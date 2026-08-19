import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';

import { Api, errorText } from '../../core/api';
import { AttributedChange, Snapshot } from '../../core/models';
import { seriesColour } from '../../core/series';
import { Shell, volumeKey, volumeName } from '../../core/state';
import { KindChip } from '../../ui/kind-chip';
import { PathCell } from '../../ui/path-cell';
import {
  BytesPipe,
  DeltaBytesPipe,
  DeltaClassPipe,
  DeltaFilesPipe,
  ExactBytesPipe,
  GroupedPipe,
  Percent1Pipe,
  StampPipe,
} from '../../ui/pipes';

/** How the ranked list is ordered. */
const SORTS = [
  { key: 'net', label: 'Net change' },
  { key: 'growth', label: 'Growth' },
  { key: 'shrinkage', label: 'Shrinkage' },
  { key: 'churn', label: 'Churn' },
] as const;

type SortKey = (typeof SORTS)[number]['key'];

/** A row of the ranked list, with what an expansion of it found. */
interface Row {
  change: AttributedChange;
  colour: string;
  expanded: boolean;
  children: AttributedChange[] | null;
  loadingChildren: boolean;
}

/**
 * Compare — what exactly changed between these two moments?
 *
 * One row per change, not one row per file: 1,204 files appearing under one game folder is one row,
 * at the folder that explains it. The engine collapses at the shallowest directory accounting for
 * 90% of its subtree's change, and expanding a row runs the same diff scoped to that path, which is
 * literally how that total was reached.
 *
 * The `remainder` rows are the reason the list adds up. Whatever a directory's change is that its
 * named children do not account for gets its own row — "elsewhere in C:\" — drawn hatched, never
 * folded into a parent and never dropped.
 */
@Component({
  selector: 'strata-compare',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    RouterLink,
    PathCell,
    KindChip,
    BytesPipe,
    ExactBytesPipe,
    DeltaBytesPipe,
    DeltaClassPipe,
    DeltaFilesPipe,
    GroupedPipe,
    StampPipe,
    Percent1Pipe,
  ],
  templateUrl: './compare.html',
  styleUrl: './compare.css',
})
export class Compare {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly shell = inject(Shell);
  protected readonly sorts = SORTS;

  protected readonly snapshots = signal<Snapshot[]>([]);
  protected readonly fromId = signal<number | null>(null);
  protected readonly toId = signal<number | null>(null);
  protected readonly changes = signal<AttributedChange[]>([]);
  protected readonly expanded = signal<Set<number>>(new Set());
  protected readonly children = signal<Map<number, AttributedChange[]>>(new Map());
  protected readonly loadingChildren = signal<Set<number>>(new Set());
  protected readonly sort = signal<SortKey>('net');
  protected readonly picked = signal<AttributedChange | null>(null);
  protected readonly loading = signal(true);
  protected readonly failure = signal<string | null>(null);

  private readonly params = toSignal(this.route.queryParamMap);

  protected readonly volume = this.shell.volume;

  protected readonly from = computed(() => this.snapshots().find((s) => s.id === this.fromId()) ?? null);
  protected readonly to = computed(() => this.snapshots().find((s) => s.id === this.toId()) ?? null);

  /** How far apart the two moments are, and how many ticks fell between them. */
  protected readonly span = computed(() => {
    const from = this.from();
    const to = this.to();

    if (!from || !to) {
      return null;
    }

    const ms = Math.abs(new Date(to.takenUtc).getTime() - new Date(from.takenUtc).getTime());
    const hours = ms / 3_600_000;
    const between = this.snapshots().filter((s) => {
      const at = new Date(s.takenUtc).getTime();

      return at > new Date(from.takenUtc).getTime() && at < new Date(to.takenUtc).getTime();
    }).length;

    return {
      label:
        hours >= 24
          ? `${Math.floor(hours / 24)} days ${Math.round(hours % 24)} h`
          : `${hours.toFixed(1)} h`,
      between,
    };
  });

  protected readonly totals = computed(() => {
    let written = 0;
    let deleted = 0;
    let churn = 0;
    let filesAdded = 0;
    let filesRemoved = 0;
    let attributed = 0;
    let unattributed = 0;

    for (const change of this.changes()) {
      if (change.deltaBytes > 0) {
        written += change.deltaBytes;
      } else {
        deleted += -change.deltaBytes;
      }

      churn += change.churnBytes;

      if (change.fileCountDelta > 0) {
        filesAdded += change.fileCountDelta;
      } else {
        filesRemoved += -change.fileCountDelta;
      }

      if (change.kind === 'remainder') {
        unattributed += Math.abs(change.deltaBytes);
      } else {
        attributed += Math.abs(change.deltaBytes);
      }
    }

    const moved = attributed + unattributed;

    return {
      written,
      deleted: -deleted,
      net: written - deleted,

      /**
       * Write activity that the net change does not show.
       *
       * Not the sum of the rows' churn, which for a two-snapshot comparison is a lower bound equal
       * to each row's own delta and would only restate the net. This is the part *above* that — and
       * for a snapshot diff it is usually zero, honestly, because a directory that wrote and
       * deleted the same 50 GB between two measurements is indistinguishable from one that did
       * nothing. Only the Timeline, which reads the journal, sees the real figure.
       */
      churn: Math.max(0, churn - (written + deleted)),
      churnIsBlind: churn <= written + deleted,
      cycled: this.changes().filter((c) => c.churnBytes > Math.abs(c.deltaBytes)).length,
      filesAdded,
      filesRemoved,
      attributed,
      unattributed,

      // The sentence the remainder rows make possible: "94.7% of the change is attributed to a
      // specific path". Without them this number could not honestly be computed at all.
      attributionShare: moved > 0 ? attributed / moved : 1,
    };
  });

  /** The ranked rows, with any expansion spliced in under its parent. */
  protected readonly rows = computed<Row[]>(() => {
    const rank = this.ranker();
    const expanded = this.expanded();
    const children = this.children();
    const loading = this.loadingChildren();

    const ordered = [...this.changes()].sort((a, b) => rank(b) - rank(a));
    const rows: Row[] = [];

    for (const change of ordered) {
      rows.push({
        change,
        colour: seriesColour(topSegment(change.path)),
        expanded: expanded.has(change.directoryId),
        children: children.get(change.directoryId) ?? null,
        loadingChildren: loading.has(change.directoryId),
      });
    }

    return rows;
  });

  /** The two directories that cycled the most bytes for the least net effect. */
  protected readonly churners = computed(() =>
    [...this.changes()]
      .filter((c) => c.churnBytes > Math.abs(c.deltaBytes) * 2 && c.kind !== 'remainder')
      .sort((a, b) => b.churnBytes - a.churnBytes)
      .slice(0, 3),
  );

  constructor() {
    effect(() => {
      const wanted = this.params()?.get('volume');

      if (wanted && untracked(() => this.shell.selected()) !== wanted) {
        this.shell.select(wanted);
      }
    });

    effect(() => {
      const key = this.shell.selected();

      this.shell.status();

      if (key) {
        untracked(() => this.loadSnapshots(key));
      }
    });

    effect(() => {
      const from = this.fromId();
      const to = this.toId();

      if (from !== null && to !== null && from !== to) {
        untracked(() => this.loadDiff(from, to));
      }
    });
  }

  protected setFrom(value: number): void {
    this.fromId.set(Number(value));
    this.reset();
  }

  protected setTo(value: number): void {
    this.toId.set(Number(value));
    this.reset();
  }

  protected selectVolume(key: string): void {
    this.shell.select(key);
    this.reset();

    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { volume: key },
      replaceUrl: true,
    });
  }

  protected setSort(key: SortKey): void {
    this.sort.set(key);
  }

  protected pick(change: AttributedChange): void {
    this.picked.set(this.picked()?.directoryId === change.directoryId ? null : change);
  }

  /**
   * Expands a row into how its total was reached.
   *
   * Runs the same diff scoped to that path rather than holding a tree in memory. That is exactly
   * what the engine does when it descends, so an expansion is the next level of the same answer
   * rather than a second, differently-computed one.
   */
  protected toggle(change: AttributedChange): void {
    if (change.kind === 'remainder') {
      // A remainder has no children by definition: it is what the children did not account for.
      return;
    }

    const open = new Set(this.expanded());

    if (open.has(change.directoryId)) {
      open.delete(change.directoryId);
      this.expanded.set(open);
      return;
    }

    open.add(change.directoryId);
    this.expanded.set(open);

    if (this.children().has(change.directoryId)) {
      return;
    }

    const from = this.fromId();
    const to = this.toId();

    if (from === null || to === null) {
      return;
    }

    const busy = new Set(this.loadingChildren());
    busy.add(change.directoryId);
    this.loadingChildren.set(busy);

    this.api.diff({ from, to, path: change.path, minBytes: 1024 * 1024, limit: 50 }).subscribe({
      next: (found) => {
        const map = new Map(this.children());

        // Drop the row that is the scope itself; it is the row being expanded.
        map.set(
          change.directoryId,
          found.filter((c) => c.path !== change.path),
        );

        this.children.set(map);
        this.clearBusy(change.directoryId);
      },
      error: () => this.clearBusy(change.directoryId),
    });
  }

  protected keyOf = volumeKey;
  protected nameOf = volumeName;

  protected topOf = topSegment;

  private clearBusy(id: number): void {
    const busy = new Set(this.loadingChildren());
    busy.delete(id);
    this.loadingChildren.set(busy);
  }

  private reset(): void {
    this.expanded.set(new Set());
    this.children.set(new Map());
    this.picked.set(null);
  }

  private ranker(): (change: AttributedChange) => number {
    switch (this.sort()) {
      case 'growth':
        return (c) => c.deltaBytes;
      case 'shrinkage':
        return (c) => -c.deltaBytes;
      case 'churn':
        return (c) => c.churnBytes;
      default:
        return (c) => c.absoluteDelta;
    }
  }

  private loadSnapshots(volume: string): void {
    this.api.snapshots({ volume, limit: 500 }).subscribe({
      next: (rows) => {
        this.snapshots.set(rows);

        // Default to the widest comparison the history supports: oldest against newest. That is the
        // question someone opens this screen with, and narrowing it is one dropdown away.
        if (rows.length >= 2) {
          this.toId.set(rows[0].id);
          this.fromId.set(rows[rows.length - 1].id);
        } else {
          this.fromId.set(null);
          this.toId.set(null);
          this.changes.set([]);
          this.loading.set(false);
        }
      },
      error: (failure) => {
        this.failure.set(errorText(failure));
        this.loading.set(false);
      },
    });
  }

  private loadDiff(from: number, to: number): void {
    this.loading.set(true);
    this.failure.set(null);

    this.api.diff({ from, to, minBytes: 1024 * 1024, limit: 300 }).subscribe({
      next: (changes) => {
        this.changes.set(changes);
        this.loading.set(false);
      },
      error: (failure) => {
        this.failure.set(errorText(failure));
        this.changes.set([]);
        this.loading.set(false);
      },
    });
  }
}

/** The top-level directory a path sits under, which is what colours the row. */
function topSegment(path: string): string {
  const parts = path.split('\\').filter((p) => p.length > 0);

  return parts.length >= 2 ? parts[1] : (parts[0] ?? '');
}
