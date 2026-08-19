import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';

import { EChartDirective, chartTheme, tooltipStyle } from '../../charts/echart';
import { Api, errorText } from '../../core/api';
import { bytes, count, joinPath, leaf } from '../../core/format';
import { TreeNode } from '../../core/models';
import { otherSeriesColour, seriesColour } from '../../core/series';
import { Shell, volumeKey } from '../../core/state';
import {
  BytesPipe,
  ExactBytesPipe,
  GroupedPipe,
  Percent1Pipe,
  StampPipe,
} from '../../ui/pipes';

/**
 * How many tiles get drawn before the tail is merged into one.
 *
 * A directory with 2,041 children — `C:\WINDOWS\WinSxS` really has that many — cannot be forty
 * tiles a pixel wide, and drawing them would say less than one tile saying "+2,001 smaller". The
 * sibling list beside it still lists every one of them.
 */
const MAX_TILES = 40;

/** A child of the current directory, with its share of the parent worked out. */
interface Child {
  node: TreeNode;
  path: string;
  share: number;
  colour: string;

  /**
   * Bytes reached from more than one path.
   *
   * A directory whose subtree is large but whose parent's total does not grow by the same amount is
   * mostly links to bytes counted elsewhere. WinSxS is the canonical case: 36.4 GB apparent and
   * close to nothing unique.
   */
  linked: boolean;
}

/**
 * Explorer — where are the bytes right now?
 *
 * A treemap with drill-down and a sibling list beside it. Reads one level at a time, which is the
 * access pattern the storage is tuned for: `depth=1` measures 2 ms against 110 ms for three levels
 * and 107 MB for the whole volume.
 */
@Component({
  selector: 'strata-explorer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [EChartDirective, BytesPipe, ExactBytesPipe, GroupedPipe, Percent1Pipe, StampPipe],
  templateUrl: './explorer.html',
  styleUrl: './explorer.css',
})
export class Explorer {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly shell = inject(Shell);

  /** The directory being looked at. Empty means the volume root. */
  protected readonly path = signal('');

  protected readonly here = signal<TreeNode | null>(null);
  protected readonly children = signal<TreeNode[]>([]);
  protected readonly loading = signal(true);
  protected readonly failure = signal<string | null>(null);
  protected readonly picked = signal<string | null>(null);

  private readonly params = toSignal(this.route.queryParamMap);

  protected readonly volume = this.shell.volume;

  protected readonly root = computed(() => this.volume()?.driveLetter ?? this.shell.selected() ?? '');

  /** The breadcrumb, root first, each segment clickable. */
  protected readonly crumbs = computed(() => {
    const root = this.root();
    const path = this.path();

    const crumbs = [{ label: root + '\\', path: '' }];

    if (!path) {
      return crumbs;
    }

    const relative = path.startsWith(root) ? path.slice(root.length) : path;
    const segments = relative.split('\\').filter((s) => s.length > 0);

    let walked = root;

    for (const segment of segments) {
      walked = joinPath(walked, segment);
      crumbs.push({ label: segment, path: walked });
    }

    return crumbs;
  });

  protected readonly parentPath = computed(() => {
    const crumbs = this.crumbs();

    return crumbs.length >= 2 ? crumbs[crumbs.length - 2].path : null;
  });

  protected readonly sorted = computed<Child[]>(() => {
    const here = this.here();
    const total = here?.subtreeBytes ?? 0;

    return [...this.children()]
      .sort((a, b) => b.subtreeBytes - a.subtreeBytes)
      .map((node) => ({
        node,
        path: joinPath(this.path() || this.root(), node.name),
        share: total > 0 ? node.subtreeBytes / total : 0,
        colour: seriesColour(node.name),

        // Heuristic, and labelled as one in the UI: a child larger than the parent it sits in can
        // only be bytes counted somewhere else as well.
        linked: total > 0 && node.subtreeBytes > total,
      }));
  });

  /** The share of the parent that the current directory holds directly, rather than in a child. */
  protected readonly ownBytes = computed(() => {
    const here = this.here();

    if (!here) {
      return 0;
    }

    const inChildren = this.children().reduce((sum, c) => sum + c.subtreeBytes, 0);

    return Math.max(0, here.subtreeBytes - inChildren);
  });

  protected readonly selected = computed(() => this.sorted().find((c) => c.path === this.picked()) ?? null);

  protected readonly drawn = computed(() => Math.min(MAX_TILES, this.sorted().length));

  protected readonly merged = computed(() => Math.max(0, this.sorted().length - MAX_TILES));

  protected readonly mergedBytes = computed(() =>
    this.sorted()
      .slice(MAX_TILES)
      .reduce((sum, c) => sum + c.node.subtreeBytes, 0),
  );

  protected readonly chart = computed(() => this.buildChart());

  constructor() {
    effect(() => {
      const params = this.params();
      const wanted = params?.get('volume');
      const path = params?.get('path') ?? '';

      untracked(() => {
        if (wanted && this.shell.selected() !== wanted) {
          this.shell.select(wanted);
        }

        if (path !== this.path()) {
          this.path.set(path);
        }
      });
    });

    effect(() => {
      const key = this.shell.selected();
      const path = this.path();

      // A finished scan changes what is on the disk, so the level being looked at is re-read.
      this.shell.status();

      if (key) {
        untracked(() => this.load(key, path));
      }
    });
  }

  protected go(path: string): void {
    this.picked.set(null);

    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { volume: this.shell.selected(), path: path || null },
      queryParamsHandling: 'merge',
    });
  }

  protected selectVolume(key: string): void {
    this.picked.set(null);

    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { volume: key, path: null },
    });
  }

  /**
   * A treemap click.
   *
   * Drills into the tile unless it is the merged tail, which is not a directory and has nowhere to
   * go — clicking it selects nothing rather than navigating to a name that does not exist.
   */
  protected onTileClick(raw: unknown): void {
    const name = (raw as { data?: { realPath?: string } })?.data?.realPath;

    if (name) {
      this.go(name);
    }
  }

  protected pick(child: Child): void {
    this.picked.set(this.picked() === child.path ? null : child.path);
  }

  protected keyOf = volumeKey;

  private load(volume: string, path: string): void {
    this.loading.set(true);
    this.failure.set(null);

    // depth=2 rather than 1: the extra level is what tells each child whether it has children of
    // its own, which is what the twisty on a sibling row is drawn from. Measured at 8.5 ms against
    // 2 ms for one level, which is worth it to avoid a request per row.
    this.api.tree({ volume, path: path || undefined, depth: 2, limit: 20_000 }).subscribe({
      next: (nodes) => {
        if (nodes.length === 0) {
          this.here.set(null);
          this.children.set([]);
          this.loading.set(false);
          return;
        }

        // The walk returns the requested directory first, at depth 0 relative to itself.
        const root = nodes[0];

        this.here.set(root);
        this.children.set(nodes.filter((n) => n.parentId === root.dirId && n.dirId !== root.dirId));
        this.loading.set(false);
      },
      error: (failure) => {
        this.failure.set(errorText(failure));
        this.here.set(null);
        this.children.set([]);
        this.loading.set(false);
      },
    });
  }

  /**
   * The treemap.
   *
   * Area is bytes, and the label is always drawn rather than hidden on small tiles: the palette's
   * note is that the two closest hue pairs are separated in the treemap by an always-drawn label,
   * so dropping labels would take the separation with it.
   */
  private buildChart(): Record<string, unknown> {
    const theme = chartTheme();
    const children = this.sorted();

    const tiles = children.slice(0, MAX_TILES).map((child) => ({
      name: child.node.name,
      value: child.node.subtreeBytes,
      realPath: child.path,
      itemStyle: { color: child.colour, borderColor: theme.bg0, borderWidth: 1, gapWidth: 1 },
    }));

    const tail = this.mergedBytes();

    if (tail > 0) {
      tiles.push({
        name: `+${count(this.merged())} smaller`,
        value: tail,
        realPath: '',

        // The one desaturated tile, so "everything else" never looks like a named directory.
        itemStyle: {
          color: otherSeriesColour(),
          borderColor: theme.bg0,
          borderWidth: 1,
          gapWidth: 1,
        },
      } as never);
    }

    return {
      animation: true,
      animationDuration: 260,
      animationEasing: 'cubicOut',
      backgroundColor: 'transparent',
      tooltip: {
        ...tooltipStyle(),
        formatter: (info: unknown) => {
          const node = info as { name: string; value: number };

          return `<div>${node.name}</div><div style="color:#aeaaa5">${bytes(node.value)}</div>`;
        },
      },
      series: [
        {
          type: 'treemap',
          roam: false,

          // Drill-down is the app's, not ECharts'. Its own zoom keeps a breadcrumb this screen
          // already draws, and it would navigate without the URL following.
          nodeClick: false,
          breadcrumb: { show: false },
          width: '100%',
          height: '100%',
          top: 0,
          left: 0,
          right: 0,
          bottom: 0,
          itemStyle: { borderColor: theme.bg0, borderWidth: 1, gapWidth: 1 },
          label: {
            show: true,
            color: theme.bg0,
            fontSize: 11,
            fontFamily: theme.mono,
            overflow: 'truncate',
            formatter: (info: unknown) => {
              const node = info as { name: string; value: number };

              return `${node.name}\n${bytes(node.value)}`;
            },
          },
          upperLabel: { show: false },
          data: tiles,
        },
      ],
    };
  }

  protected leafOf = leaf;
}
