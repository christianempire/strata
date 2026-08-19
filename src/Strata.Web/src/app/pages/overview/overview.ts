import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';

import { Api } from '../../core/api';
import { bytes, toIso } from '../../core/format';
import { ChangeEvent, Snapshot, Volume } from '../../core/models';
import { seriesColour } from '../../core/series';
import { Shell, volumeKey, volumeName } from '../../core/state';
import { KindChip } from '../../ui/kind-chip';
import { PathCell } from '../../ui/path-cell';
import { SparkPoint, Sparkline } from '../../ui/sparkline';
import {
  AgoPipe,
  BytesPipe,
  ClockPipe,
  DeltaBytesPipe,
  DeltaClassPipe,
  DeltaFilesPipe,
  ExactBytesPipe,
  FeedStampPipe,
  GroupedPipe,
  Percent1Pipe,
  StampPipe,
} from '../../ui/pipes';

/** A volume with everything the card needs, worked out once. */
interface VolumeCard {
  volume: Volume;
  key: string;
  colour: string;

  /**
   * Bytes Windows says are used that Strata cannot account for. Drawn hatched, never folded in.
   *
   * Zero on a volume with no baseline. Nothing has been measured there, so there is nothing to be
   * short *of* — calling an unscanned disk an 855 GB undercount would be the loudest possible way
   * to report "we have not looked yet".
   */
  unaccounted: number;

  /** How many directories the last scan could not read. The elevation story, when there is one. */
  unreadable: number;

  scanned: boolean;

  accountedShare: number;
  unaccountedShare: number;
  freeShare: number;
  coverage: number;
  freeShareOfCapacity: number;

  /** Low free space is the one condition on this screen that is an alert rather than a note. */
  lowSpace: boolean;

  fidelity: { text: string; cls: string };
  spark: SparkPoint[];
  thirtyDayChange: number | null;
  snapshot: Snapshot | null;
}

/**
 * Overview — is everything fine, and if not, which disk?
 *
 * Volumes grouped by physical disk, because "which disk" is the question and two volumes on one SSD
 * share its endurance and its free space problems. Each card reconciles what Strata accounts for
 * against what Windows reports rather than showing one number and hoping.
 */
@Component({
  selector: 'strata-overview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    Sparkline,
    PathCell,
    KindChip,
    BytesPipe,
    ExactBytesPipe,
    DeltaBytesPipe,
    DeltaClassPipe,
    DeltaFilesPipe,
    GroupedPipe,
    ClockPipe,
    StampPipe,
    FeedStampPipe,
    AgoPipe,
    Percent1Pipe,
  ],
  templateUrl: './overview.html',
  styleUrl: './overview.css',
})
export class Overview {
  private readonly api = inject(Api);
  protected readonly shell = inject(Shell);

  private readonly snapshots = signal<Map<string, Snapshot[]>>(new Map());
  protected readonly recent = signal<ChangeEvent[]>([]);
  protected readonly loading = signal(true);

  protected readonly attached = computed(() =>
    this.shell.volumes().filter((v) => v.attached && (v.scannable || v.snapshotCount > 0)),
  );

  /** History for hardware that is not plugged in. Reported rather than dropped: it is not stale. */
  protected readonly detached = computed(() =>
    this.shell.volumes().filter((v) => !v.attached && v.snapshotCount > 0),
  );

  protected readonly cards = computed(() => this.attached().map((v) => this.card(v)));

  /** Grouped by physical disk, with volumes Windows could not place on one collected at the end. */
  protected readonly disks = computed(() => {
    const groups = new Map<number | null, VolumeCard[]>();

    for (const card of this.cards()) {
      const disk = card.volume.diskNumber;
      const existing = groups.get(disk);

      if (existing) {
        existing.push(card);
      } else {
        groups.set(disk, [card]);
      }
    }

    return [...groups.entries()]
      .sort((a, b) => (a[0] ?? 999) - (b[0] ?? 999))
      .map(([disk, cards]) => ({
        disk,
        label: disk === null ? 'Unplaced' : `Disk ${disk}`,
        cards,

        // One badge per disk, showing its worst news: an undercount outranks reduced fidelity,
        // which outranks everything being fine.
        badge: worstBadge(cards),
      }));
  });

  protected readonly totals = computed(() => {
    const cards = this.cards();

    return {
      volumes: cards.length,
      disks: new Set(cards.map((c) => c.volume.diskNumber)).size,
      files: cards.reduce((sum, c) => sum + (c.snapshot?.fileCount ?? 0), 0),
      directories: cards.reduce((sum, c) => sum + (c.snapshot?.dirCount ?? 0), 0),
    };
  });

  /** Net across every volume in the window, which is the sentence under the change list. */
  protected readonly recentNet = computed(() =>
    this.recent().reduce((sum, e) => sum + e.deltaBytes, 0),
  );

  protected readonly scanner = computed(() => this.shell.status());

  constructor() {
    this.load();
  }

  protected keyOf = volumeKey;
  protected nameOf = volumeName;

  private load(): void {
    const now = new Date();
    const monthAgo = new Date(now.getTime() - 30 * 86_400_000);
    const dayAgo = new Date(now.getTime() - 86_400_000);

    // One request per volume for history plus one for the change list. Snapshots are indexed on
    // (volume_id, taken_utc) so each is a range seek, and the alternative — one unfiltered call
    // sliced client-side — would pull every volume's month to use one volume's.
    this.api
      .volumes()
      .subscribe((volumes) => {
        this.shell.volumes.set(volumes);

        const requests = volumes
          .filter((v) => v.snapshotCount > 0)
          .map((v) =>
            this.api
              .snapshots({ volume: volumeKey(v), from: toIso(monthAgo), limit: 400 })
              .pipe(
                map((rows) => [volumeKey(v), rows] as const),
                catchError(() => of([volumeKey(v), [] as Snapshot[]] as const)),
              ),
          );

        if (requests.length === 0) {
          this.loading.set(false);
          return;
        }

        forkJoin(requests).subscribe((pairs) => {
          this.snapshots.set(new Map(pairs));
          this.loading.set(false);
        });
      });

    this.api
      .events({ from: toIso(dayAgo), minBytes: 100 * 1024 * 1024, limit: 5 })
      .subscribe((events) => this.recent.set(events));
  }

  private card(volume: Volume): VolumeCard {
    const key = volumeKey(volume);
    const history = this.snapshots().get(key) ?? [];

    // Newest-first from the API; a sparkline reads left to right.
    const ordered = [...history].reverse();

    const scanned = volume.baselineUtc !== null;
    const unaccounted = scanned ? Math.max(0, volume.usedBytes - volume.accountedBytes) : 0;
    const capacity = Math.max(1, volume.totalBytes);
    const freeShareOfCapacity = volume.freeBytes / capacity;

    return {
      volume,
      key,
      colour: seriesColour(volume.driveLetter ?? volume.label ?? key),
      unaccounted,
      unreadable: ordered.length > 0 ? ordered[ordered.length - 1].inaccessibleCount : 0,
      scanned,
      accountedShare: (volume.accountedBytes / capacity) * 100,
      unaccountedShare: (unaccounted / capacity) * 100,
      freeShare: (volume.freeBytes / capacity) * 100,
      coverage: scanned && volume.usedBytes > 0 ? volume.accountedBytes / volume.usedBytes : 0,
      freeShareOfCapacity,

      // Eight percent, from the palette's alert note. Below it a volume is a problem rather than a
      // fact, and the number turns red.
      lowSpace: freeShareOfCapacity < 0.08,

      fidelity: fidelityOf(volume),

      // Used space as the OS sees it, not as Strata counts it. Every snapshot records free space,
      // so capacity minus free is the same measure across the whole window — and it is the one a
      // person recognises, because it is the number in Explorer's properties dialog.
      spark: ordered.map((s) => ({
        whenUtc: s.takenUtc,
        usedBytes: s.freeBytes > 0 ? volume.totalBytes - s.freeBytes : s.totalBytes,
      })),
      thirtyDayChange:
        ordered.length >= 2
          ? usedAt(ordered[ordered.length - 1], volume) - usedAt(ordered[0], volume)
          : null,
      snapshot: ordered.length > 0 ? ordered[ordered.length - 1] : null,
    };
  }
}

/** Used space at a snapshot, as the OS reported it at the time. */
function usedAt(snapshot: Snapshot, volume: Volume): number {
  return snapshot.freeBytes > 0 ? volume.totalBytes - snapshot.freeBytes : snapshot.totalBytes;
}

/**
 * How trustworthy this volume's history is, said plainly.
 *
 * Three states from the mockups, and none of them is an error: a journal-backed volume is fine, a
 * volume without one has coarser history and a one-command fix, and a volume with no baseline has
 * not started yet.
 */
function fidelityOf(volume: Volume): { text: string; cls: string } {
  if (!volume.baselineUtc) {
    return { text: 'no baseline yet', cls: 'faint' };
  }

  if (!volume.supportsUsnJournal) {
    return { text: 'no change journal', cls: 'warn' };
  }

  return { text: 'journal live', cls: 'dim' };
}

/**
 * One badge per disk, showing its worst news.
 *
 * Ordered so that "we have not looked" outranks "we looked and came up short" — an unscanned volume
 * has no coverage figure to be alarmed about, and reporting one as an undercount would put a red
 * number against a disk that has simply never been read.
 */
function worstBadge(cards: VolumeCard[]): { text: string; cls: string } {
  if (cards.some((c) => !c.scanned)) {
    return { text: '○ not scanned', cls: 'faint' };
  }

  const undercounted = cards.find((c) => c.unaccounted > 1024 * 1024 * 1024);

  if (undercounted) {
    return { text: `▲ undercount ${bytes(undercounted.unaccounted)}`, cls: 'alert' };
  }

  if (cards.some((c) => c.fidelity.cls === 'warn')) {
    return { text: '▲ reduced fidelity', cls: 'warn' };
  }

  return { text: '● fully accounted', cls: 'dim' };
}
