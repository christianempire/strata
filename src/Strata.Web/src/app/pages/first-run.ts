import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';

import { Api, errorText } from '../core/api';
import { Shell, volumeKey } from '../core/state';
import { BytesPipe, DurationPipe, GroupedPipe } from '../ui/pipes';

/**
 * State 01 — first run, no baseline yet.
 *
 * The rule from the mockups: progress is only ever shown against a known denominator, and entries
 * per second, the path being read and a falling time remaining are what separate "working" from
 * "frozen". A bar alone does not. Screens that genuinely cannot work yet are listed with what they
 * are waiting for rather than being hidden.
 *
 * A cold baseline of C: is 211 seconds measured, so this screen is on display for three and a half
 * minutes and has to hold up for all of it.
 */
@Component({
  selector: 'strata-first-run',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BytesPipe, GroupedPipe, DurationPipe],
  templateUrl: './first-run.html',
  styleUrl: './first-run.css',
})
export class FirstRun {
  private readonly api = inject(Api);
  protected readonly shell = inject(Shell);

  protected readonly queueing = signal(false);
  protected readonly failure = signal<string | null>(null);

  protected readonly activity = computed(() => this.shell.status()?.activity ?? null);
  protected readonly queued = computed(() => this.shell.status()?.queued ?? []);

  /** Volumes worth offering a first scan of. */
  protected readonly candidates = computed(() =>
    this.shell.volumes().filter((v) => v.attached && v.scannable),
  );

  /**
   * The percentage and what it is a percentage of.
   *
   * The API names its own denominator, and this screen says which one it got. On a genuine first
   * run there is no previous scan to count entries against, so the figure is bytes read against
   * what Windows reports used — rougher than entries, and labelled as such rather than presented
   * as the same claim.
   */
  protected readonly basisNote = computed(() => {
    const activity = this.activity();

    if (!activity) {
      return '';
    }

    switch (activity.basis) {
      case 'entries':
        return 'of the entries the last scan found';
      case 'bytes':
        return 'of what Windows reports used — there is no previous scan to count entries against';
      default:
        return '';
    }
  });

  /**
   * What the scan is doing, when it is not the part with a percentage.
   *
   * Both of these are real work with no denominator: clearing C:'s previous 3.1 million rows takes
   * about nineteen seconds, and draining the writer takes several more. Saying so beats a bar
   * sitting at zero, which is the difference between "working" and "frozen" that this whole screen
   * exists to hold.
   */
  protected readonly phaseNote = computed(() => {
    switch (this.activity()?.phase) {
      case 'preparing':
        return 'Clearing the previous scan of this volume';
      case 'writing':
        return 'Writing the snapshot';
      default:
        return 'Reading';
    }
  });

  protected readonly reading = computed(() => this.activity()?.phase === 'reading');

  /** What already works while the first scan runs, and what is still waiting. */
  protected readonly readiness = computed(() => {
    const volumes = this.shell.volumes();
    const snapshots = volumes.reduce((total, v) => total + v.snapshotCount, 0);

    return [
      { ready: true, text: 'Volume totals and free space' },
      {
        ready: snapshots > 0,
        text: 'Explorer, for the part already read',
      },
      {
        ready: snapshots >= 2,
        text: 'Timeline — needs a second snapshot, one tick after this scan lands',
      },
      {
        ready: snapshots >= 2,
        text: 'Compare — needs two snapshots, about half an hour away',
      },
    ];
  });

  protected scan(volume: string): void {
    this.queueing.set(true);
    this.failure.set(null);

    this.api.scan(volume, 'full').subscribe({
      next: () => {
        this.queueing.set(false);

        // The status poll picks the scan up within two seconds; nothing else needs doing here.
      },
      error: (failure) => {
        this.queueing.set(false);
        this.failure.set(errorText(failure));
      },
    });
  }

  protected keyOf = volumeKey;
}
