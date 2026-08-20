import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { Api, errorText } from '../../core/api';
import { toIso } from '../../core/format';
import { HistorySize, ScanLog, ScanTier, Settings } from '../../core/models';
import { Shell, isSystemPartition, volumeKey, volumeName } from '../../core/state';
import {
  BytesPipe,
  ExactBytesPipe,
  ClockPipe,
  DurationPipe,
  GroupedPipe,
  StampPipe,
} from '../../ui/pipes';

/** The interval presets, in seconds. Below 30 the API refuses, and rightly. */
const INTERVALS = [
  { seconds: 300, label: '5 min' },
  { seconds: 900, label: '15 min' },
  { seconds: 1800, label: '30 min' },
  { seconds: 3600, label: '60 min' },
] as const;

/**
 * How soon to come back to a volume that is behind.
 *
 * A separate cadence from the scan interval, and a much shorter one, because a tick that hit its
 * record cap has left records in a journal that is a fixed-size circular buffer. Draining 50,000
 * records every 15 minutes is 55 a second, which is less than a development volume writes — so at
 * the ordinary interval catching up can only ever lose, and eventually the buffer wraps past the
 * cursor and the history is gone.
 */
const CATCH_UP_INTERVALS = [
  { seconds: 30, label: '30 s' },
  { seconds: 60, label: '1 min' },
  { seconds: 300, label: '5 min' },
] as const;

/**
 * The retention tiers offered, as day counts.
 *
 * Presets rather than free entry because the tiers have to stay in order — a full-resolution window
 * longer than the hourly window would mean the hourly tier never applied to anything — and a form
 * that lets you type an invalid pair and then rejects it is a worse form than one that cannot.
 */
const FULL_DAYS = [1, 3, 7, 14] as const;
const HOURLY_DAYS = [7, 30, 90] as const;
const DAILY_DAYS = [90, 365, 730] as const;

const LOG_FILTERS = ['all', 'ran', 'deferred', 'failed'] as const;

type LogFilter = (typeof LOG_FILTERS)[number];

/**
 * Settings — what is this thing doing to my machine?
 *
 * Half controls and half evidence. The scan log is the half that matters: every tick, whether it ran
 * or deferred, why, and how long it took. It is the screen where somebody checks that the promise
 * the whole project rests on is actually being kept, so it shows the deferrals rather than only the
 * successes.
 */
@Component({
  selector: 'strata-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    BytesPipe,
    ExactBytesPipe,
    GroupedPipe,
    ClockPipe,
    StampPipe,
    DurationPipe,
  ],
  templateUrl: './settings.html',
  styleUrl: './settings.css',
})
export class SettingsPage {
  private readonly api = inject(Api);
  protected readonly shell = inject(Shell);

  protected readonly intervals = INTERVALS;
  protected readonly catchUpIntervals = CATCH_UP_INTERVALS;
  protected readonly fullDays = FULL_DAYS;
  protected readonly hourlyDays = HOURLY_DAYS;
  protected readonly dailyDays = DAILY_DAYS;

  protected readonly history = signal<HistorySize | null>(null);
  protected readonly logFilters = LOG_FILTERS;

  protected readonly settings = signal<Settings | null>(null);
  protected readonly log = signal<ScanLog | null>(null);
  protected readonly filter = signal<LogFilter>('all');
  protected readonly saving = signal(false);
  protected readonly saved = signal(false);
  protected readonly failure = signal<string | null>(null);
  protected readonly queued = signal<string | null>(null);

  /** Edited locally so a slider does not send a request per pixel. */
  protected readonly cpu = signal(70);
  protected readonly newExclusion = signal('');

  protected readonly status = this.shell.status;

  protected readonly summary = computed(() => this.log()?.summary ?? null);

  protected readonly ticks = computed(() => this.log()?.ticks ?? []);

  /** CPU time as a share of one core over the window the log covers. */
  protected readonly cpuShare = computed(() => {
    const summary = this.summary();
    const ticks = this.ticks();

    if (!summary || ticks.length === 0) {
      return null;
    }

    const oldest = new Date(ticks[ticks.length - 1].whenUtc).getTime();
    const span = Date.now() - oldest;

    return span > 0 ? summary.totalMs / span : null;
  });

  constructor() {
    this.api.settings().subscribe({
      next: (settings) => {
        this.settings.set(settings);
        this.cpu.set(settings.cpuThresholdPercent);
      },
      error: (failure) => this.failure.set(errorText(failure)),
    });

    this.loadHistory();

    effect(() => {
      const which = this.filter();

      // Re-read when a tick lands, so the log a person is watching stays current.
      this.shell.status();

      untracked(() => this.loadLog(which));
    });
  }

  protected setFilter(value: LogFilter): void {
    this.filter.set(value);
  }

  protected setInterval(seconds: number): void {
    this.patch({ intervalSeconds: seconds });
  }

  protected setCatchUpInterval(seconds: number): void {
    this.patch({ catchUpIntervalSeconds: seconds });
  }

  /**
   * Sets one retention tier, pushing the coarser ones out if it would overtake them.
   *
   * The tiers must stay in order, and the API rejects a pair that is not — correctly, but a screen
   * that lets you click something and then shows an error for it is a screen that made you do the
   * work. Widening a tier widens whatever sat behind it instead.
   */
  protected setRetention(tier: 'full' | 'hourly' | 'daily', days: number): void {
    const current = this.settings();

    if (!current) {
      return;
    }

    const full = tier === 'full' ? days : current.fullResolutionDays;
    const hourly = Math.max(tier === 'hourly' ? days : current.hourlyDays, full);
    const daily = Math.max(tier === 'daily' ? days : current.dailyDays, hourly);

    this.patch({ fullResolutionDays: full, hourlyDays: hourly, dailyDays: daily });
  }

  protected toggleRetention(): void {
    const current = this.settings();

    if (current) {
      this.patch({ retentionEnabled: !current.retentionEnabled });
    }
  }

  private loadHistory(): void {
    this.api.history().subscribe({
      next: (size) => this.history.set(size),

      // A missing breakdown costs a panel, not the screen. Every other knob still works.
      error: () => this.history.set(null),
    });
  }

  protected commitCpu(): void {
    this.patch({ cpuThresholdPercent: this.cpu() });
  }

  protected togglePause(): void {
    const settings = this.settings();

    if (settings) {
      this.patch({ paused: !settings.paused });
    }
  }

  protected toggleBorderless(): void {
    const settings = this.settings();

    if (settings) {
      this.patch({ detectBorderlessFullscreen: !settings.detectBorderlessFullscreen });
    }
  }

  protected toggleBackgroundIo(): void {
    const settings = this.settings();

    if (settings) {
      this.patch({ backgroundIo: !settings.backgroundIo });
    }
  }

  protected toggleStrataDb(): void {
    const settings = this.settings();

    if (settings) {
      this.patch({ includeStrataDatabase: !settings.includeStrataDatabase });
    }
  }

  protected addExclusion(): void {
    const value = this.newExclusion().trim();
    const settings = this.settings();

    if (!value || !settings || settings.excludedEventPaths.includes(value)) {
      return;
    }

    // A list is sent whole. The patch semantics are "absent means unchanged", so clearing or
    // shortening one means sending the array you want rather than leaving it out.
    this.patch({ excludedEventPaths: [...settings.excludedEventPaths, value] });
    this.newExclusion.set('');
  }

  protected removeExclusion(pattern: string): void {
    const settings = this.settings();

    if (settings) {
      this.patch({
        excludedEventPaths: settings.excludedEventPaths.filter((p) => p !== pattern),
      });
    }
  }

  /** Queues a scan by hand. The one control on this screen that starts work rather than shaping it. */
  protected scan(volume: string, tier: ScanTier): void {
    this.queued.set(null);
    this.failure.set(null);

    this.api.scan(volume, tier).subscribe({
      next: (accepted) =>
        this.queued.set(
          `Queued ${accepted.tier} scan of ${accepted.volume}. Expect ${accepted.expect}.`,
        ),
      error: (failure) => this.failure.set(errorText(failure)),
    });
  }

  protected keyOf = volumeKey;
  protected nameOf = volumeName;
  protected isSystem = isSystemPartition;

  private patch(change: Partial<Settings>): void {
    this.saving.set(true);
    this.saved.set(false);
    this.failure.set(null);

    this.api.saveSettings(change).subscribe({
      next: (settings) => {
        this.settings.set(settings);
        this.cpu.set(settings.cpuThresholdPercent);
        this.saving.set(false);
        this.saved.set(true);
      },
      error: (failure) => {
        this.saving.set(false);

        // The API validates and explains: "intervalSeconds must be at least 30" is a better
        // message than anything this screen could invent, so it is shown verbatim.
        this.failure.set(errorText(failure));
      },
    });
  }

  private loadLog(which: LogFilter): void {
    const midnight = new Date();
    midnight.setHours(0, 0, 0, 0);

    this.api
      .scanLog({
        from: toIso(midnight),
        result: which === 'all' ? undefined : which,
        limit: 200,
      })
      .subscribe({
        next: (log) => this.log.set(log),
        error: (failure) => this.failure.set(errorText(failure)),
      });
  }
}
