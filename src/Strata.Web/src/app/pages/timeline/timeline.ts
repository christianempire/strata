import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { EChartDirective, chartTheme, tooltipStyle } from '../../charts/echart';
import { Api, errorText } from '../../core/api';
import { bytes, deltaBytes, stamp, toIso } from '../../core/format';
import { ChangeEvent, ChangeKind, VolumeSeries } from '../../core/models';
import { otherSeriesColour, seriesColours } from '../../core/series';
import { Shell, volumeKey, volumeName } from '../../core/state';
import { KindChip } from '../../ui/kind-chip';
import { PathCell } from '../../ui/path-cell';
import {
  BytesPipe,
  DeltaBytesPipe,
  DeltaClassPipe,
  DeltaFilesPipe,
  ExactBytesPipe,
  FeedStampPipe,
  GroupedPipe,
  Percent1Pipe,
  StampPipe,
} from '../../ui/pipes';

/** The five windows from the brief, and how finely each is sampled. */
const RANGES = [
  { key: '1H', label: '1H', ms: 3_600_000, buckets: 60 },
  { key: '24H', label: '24H', ms: 86_400_000, buckets: 96 },
  { key: '7D', label: '7D', ms: 7 * 86_400_000, buckets: 168 },
  { key: '30D', label: '30D', ms: 30 * 86_400_000, buckets: 180 },
  { key: '1Y', label: '1Y', ms: 365 * 86_400_000, buckets: 183 },
] as const;

type RangeKey = (typeof RANGES)[number]['key'];

/** A brushed sub-range, in epoch milliseconds. */
interface Brush {
  from: number;
  to: number;
}

/**
 * Timeline — what grew, when, and where.
 *
 * The headline screen. A stacked area of used space by top-level directory, an events feed beneath
 * it, and a two-way link between them: brushing the chart filters the feed, clicking a feed row
 * marks it on the chart.
 *
 * The brush filters, it does not zoom. The chart keeps the full range so you never lose your place,
 * and the totals in the right rail recompute against the brushed window — which is how the brush
 * earns its keep: drag over one spike and read exactly what it cost.
 */
@Component({
  selector: 'strata-timeline',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    EChartDirective,
    PathCell,
    KindChip,
    BytesPipe,
    ExactBytesPipe,
    DeltaBytesPipe,
    DeltaClassPipe,
    DeltaFilesPipe,
    GroupedPipe,
    StampPipe,
    FeedStampPipe,
    Percent1Pipe,
  ],
  templateUrl: './timeline.html',
  styleUrl: './timeline.css',
})
export class Timeline {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly shell = inject(Shell);
  protected readonly ranges = RANGES;

  protected readonly range = signal<RangeKey>('7D');
  protected readonly series = signal<VolumeSeries | null>(null);
  protected readonly events = signal<ChangeEvent[]>([]);
  protected readonly selected = signal<ChangeEvent | null>(null);
  protected readonly brush = signal<Brush | null>(null);
  protected readonly loading = signal(true);
  protected readonly failure = signal<string | null>(null);

  private readonly queryVolume = toSignal(this.route.queryParamMap);

  protected readonly volume = this.shell.volume;

  protected readonly volumeLabel = computed(() => {
    const volume = this.volume();

    return volume ? volumeName(volume) : '';
  });

  protected readonly window = computed(() => {
    const spec = RANGES.find((r) => r.key === this.range())!;
    const to = new Date();

    return { from: new Date(to.getTime() - spec.ms), to, buckets: spec.buckets };
  });

  /**
   * The feed, filtered to the brush.
   *
   * Filtered client-side rather than re-fetched. The feed for a window is at most a few hundred
   * rows, it is already in memory, and a brush drag that issued a request per settle would put the
   * database on the critical path of a mouse gesture.
   */
  protected readonly feed = computed(() => {
    const brushed = this.brush();
    const all = this.events();

    if (!brushed) {
      return all;
    }

    return all.filter((e) => {
      const at = new Date(e.whenUtc).getTime();

      return at >= brushed.from && at <= brushed.to;
    });
  });

  /** Counts per kind, for the chips above the feed. */
  protected readonly kindCounts = computed(() => {
    const counts = new Map<ChangeKind, number>();

    for (const event of this.feed()) {
      counts.set(event.kind, (counts.get(event.kind) ?? 0) + 1);
    }

    return [...counts.entries()].map(([kind, count]) => ({ kind, count }));
  });

  /**
   * Written, deleted, net and churn over whatever window is showing.
   *
   * Churn is the smaller of written and deleted — bytes that came and went. It costs SSD endurance
   * and shows up nowhere in the net figure, which is the whole reason it is reported separately.
   */
  protected readonly totals = computed(() => {
    let written = 0;
    let deleted = 0;

    for (const event of this.feed()) {
      if (event.deltaBytes > 0) {
        written += event.deltaBytes;
      } else {
        deleted += -event.deltaBytes;
      }
    }

    return {
      written,
      deleted: -deleted,
      net: written - deleted,
      churn: Math.min(written, deleted),
    };
  });

  /** The sentence at the top: what changed, over what window, and where most of it landed. */
  protected readonly headline = computed(() => {
    const volume = this.volume();
    const totals = this.totals();
    const feed = this.feed();

    if (!volume) {
      return null;
    }

    const largest = feed.reduce<ChangeEvent | null>(
      (best, e) => (best === null || Math.abs(e.deltaBytes) > Math.abs(best.deltaBytes) ? e : best),
      null,
    );

    return {
      net: totals.net,

      // The verb already carries the sign, so the figure beside it is an absolute value. "lost
      // -13.93 GB" is a double negative, and reads as a gain on a second glance.
      verb: totals.net >= 0 ? 'gained' : 'lost',
      amount: bytes(Math.abs(totals.net)),
      since: stamp(this.window().from),
      topPath: largest ? largest.path : null,
      topShare: largest ? bytes(Math.abs(largest.deltaBytes)) : null,

      // Where the largest change went, or where it went from. Saying bytes "landed in" a directory
      // that lost them is the same double negative one clause later.
      topVerb: largest && largest.deltaBytes < 0 ? 'left' : 'landed in',
      empty: feed.length === 0,
    };
  });

  /** Colours for the bands, resolved once per response so a hash collision is repaired the same
   * way in the legend and in the chart. */
  private readonly bandColours = computed(() =>
    seriesColours((this.series()?.series ?? []).map((s) => s.name)),
  );

  protected readonly hasOther = computed(() =>
    (this.series()?.other ?? []).some((v) => v > 0),
  );

  /** The next range out, for the empty-range offer. Null when already at a year. */
  protected readonly wider = computed(() => {
    const index = RANGES.findIndex((r) => r.key === this.range());

    return index >= 0 && index < RANGES.length - 1 ? RANGES[index + 1].key : null;
  });

  /** The gap between what the OS reports used and what the bands add up to. */
  protected readonly unaccounted = computed(() => {
    const series = this.series();

    if (!series || series.buckets.length === 0) {
      return 0;
    }

    const last = series.buckets.length - 1;
    const banded =
      series.series.reduce((sum, s) => sum + s.bytes[last], 0) + (series.other[last] ?? 0);

    return Math.max(0, (series.used[last] ?? 0) - banded);
  });

  protected readonly chart = computed(() => this.buildChart());

  protected readonly brushLabel = computed(() => {
    const brushed = this.brush();

    if (!brushed) {
      return null;
    }

    return `${stamp(new Date(brushed.from))} → ${stamp(new Date(brushed.to))}`;
  });

  constructor() {
    // A volume in the URL wins on arrival, so the Overview's links land where they say they will.
    effect(() => {
      const wanted = this.queryVolume()?.get('volume');

      if (wanted && untracked(() => this.shell.selected()) !== wanted) {
        this.shell.select(wanted);
      }
    });

    effect(() => {
      const key = this.shell.selected();
      const window = this.window();

      // Re-read whenever the volume or the range changes, and whenever a scan lands: a finished
      // tick is new data on this exact chart.
      this.shell.status();

      if (key) {
        untracked(() => this.load(key, window.from, window.to, window.buckets));
      }
    });
  }

  protected setRange(key: RangeKey): void {
    this.range.set(key);

    // The brush is a window inside the old range and means nothing inside the new one.
    this.brush.set(null);
  }

  protected selectVolume(key: string): void {
    this.shell.select(key);
    this.selected.set(null);
    this.brush.set(null);

    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { volume: key },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  protected select(event: ChangeEvent): void {
    this.selected.set(this.selected()?.id === event.id ? null : event);
  }

  protected clearBrush(): void {
    this.brush.set(null);
  }

  /**
   * Turns an ECharts brush event into a time window.
   *
   * A brush with no area is a click, which is how a selection gets cleared — the palette's rule is
   * that brushing is the default cursor rather than a mode you enter, so there is no button to
   * un-press.
   */
  protected onBrush(raw: unknown): void {
    const areas = (raw as { areas?: { coordRange?: number[] }[] })?.areas ?? [];
    const range = areas[0]?.coordRange;

    if (!range || range.length < 2) {
      this.brush.set(null);
      return;
    }

    const buckets = this.series()?.buckets ?? [];
    const lo = Math.max(0, Math.round(range[0]));
    const hi = Math.min(buckets.length - 1, Math.round(range[1]));

    if (!buckets[lo] || !buckets[hi] || hi <= lo) {
      this.brush.set(null);
      return;
    }

    this.brush.set({
      from: new Date(buckets[lo]).getTime(),
      to: new Date(buckets[hi]).getTime(),
    });
  }

  protected explorerLink(event: ChangeEvent): Record<string, string> {
    return { volume: this.shell.selected() ?? '', path: event.path };
  }

  protected colourOf(name: string): string {
    return this.bandColours().get(name) ?? otherSeriesColour();
  }

  protected keyOf = volumeKey;
  protected nameOf = volumeName;

  private load(key: string, from: Date, to: Date, buckets: number): void {
    this.loading.set(true);
    this.failure.set(null);

    forkJoin({
      series: this.api
        .series({ volume: key, from: toIso(from), to: toIso(to), buckets })
        .pipe(catchError(() => of(null))),
      events: this.api
        .events({ volume: key, from: toIso(from), to: toIso(to), limit: 2000 })
        .pipe(catchError(() => of([] as ChangeEvent[]))),
    }).subscribe({
      next: ({ series, events }) => {
        this.series.set(series);
        this.events.set(events);
        this.loading.set(false);

        // A selection from the previous range that is not in this one would sit in the rail
        // describing something the chart no longer draws.
        const chosen = this.selected();

        if (chosen && !events.some((e) => e.id === chosen.id)) {
          this.selected.set(null);
        }
      },
      error: (failure) => {
        this.failure.set(errorText(failure));
        this.loading.set(false);
      },
    });
  }

  /**
   * The stacked area.
   *
   * Bands are ordered smallest-first so the largest directory sits at the bottom of the stack and
   * the volatile small ones ride on top, where their movement is visible instead of displacing
   * everything above them.
   */
  private buildChart(): Record<string, unknown> {
    const data = this.series();
    const theme = chartTheme();

    if (!data || data.buckets.length === 0) {
      return { series: [] };
    }

    const times = data.buckets.map((b) => new Date(b).getTime());
    const colours = this.bandColours();

    // Smallest at the bottom of the option array is ECharts' bottom of the stack.
    const ordered = [...data.series].reverse();

    const bands = ordered.map((s) => ({
      name: s.name,
      type: 'line',
      stack: 'used',
      areaStyle: { color: colours.get(s.name), opacity: 0.85 },
      lineStyle: { width: 0 },
      symbol: 'none',
      itemStyle: { color: colours.get(s.name) },
      data: s.bytes.map((v, i) => [times[i], v]),
    }));

    const otherTotal = data.other.reduce((a, b) => a + b, 0);

    if (otherTotal > 0) {
      bands.push({
        name: 'other',
        type: 'line',
        stack: 'used',
        areaStyle: { color: otherSeriesColour(), opacity: 0.85 },
        lineStyle: { width: 0 },
        symbol: 'none',
        itemStyle: { color: otherSeriesColour() },
        data: data.other.map((v, i) => [times[i], v]),
      });
    }

    // The band Strata cannot account for. Drawn, never dropped: a stack that adds up to less than
    // the disk with no explanation reads as a bug, and this is state 05 on the chart.
    const gap = data.used.map((used, i) => {
      const banded =
        data.series.reduce((sum, s) => sum + s.bytes[i], 0) + (data.other[i] ?? 0);

      return Math.max(0, used - banded);
    });

    if (gap.some((v) => v > 0)) {
      bands.push({
        name: 'unaccounted',
        type: 'line',
        stack: 'used',
        areaStyle: {
          color: theme.other,
          opacity: 0.45,

          // ECharts has no hatch fill, so decal carries the same meaning: this is estimated.
          decal: {
            symbol: 'rect',
            color: theme.fg3,
            dashArrayX: [1, 4],
            dashArrayY: [3, 0],
            rotation: -Math.PI / 4,
          },
        },
        lineStyle: { width: 0 },
        symbol: 'none',
        itemStyle: { color: theme.other },
        data: gap.map((v, i) => [times[i], v]),
      } as never);
    }

    const marked = this.selected();

    return {
      animation: false,
      backgroundColor: 'transparent',
      grid: { left: 62, right: 14, top: 12, bottom: 24 },

      // No toolbox. Mouse-down anywhere in the plot starts the brush; the directive arms it after
      // every setOption, so it survives a range or volume change.
      toolbox: { show: false },
      tooltip: {
        trigger: 'axis',
        ...tooltipStyle(),
        axisPointer: { lineStyle: { color: theme.ruleStrong } },
        formatter: (params: unknown) => this.tooltip(params),
      },
      xAxis: {
        type: 'time',
        axisLine: { lineStyle: { color: theme.ruleStrong } },
        axisTick: { show: false },
        axisLabel: { color: theme.fg3, fontSize: 10, hideOverlap: true },
        splitLine: { show: true, lineStyle: { color: theme.grid } },
      },
      yAxis: {
        type: 'value',
        axisLine: { show: false },
        axisTick: { show: false },
        axisLabel: {
          color: theme.fg3,
          fontSize: 10,
          fontFamily: theme.mono,
          formatter: (value: number) => bytes(value),
        },
        splitLine: { lineStyle: { color: theme.grid } },
      },

      // lineX, debounced rather than throttled: the feed does not reflow mid-drag, so the gesture
      // reads as one smooth selection with a single settle at the end.
      brush: {
        toolbox: [],
        xAxisIndex: 0,
        brushType: 'lineX',
        brushMode: 'single',
        throttleType: 'debounce',
        throttleDelay: 80,
        transformable: false,
        brushStyle: {
          color: 'rgba(248,172,61,0.10)',
          borderColor: theme.pos,
          borderWidth: 1,
        },
      },
      series: [
        ...bands,
        ...(marked
          ? [
              {
                name: 'marker',
                type: 'line',
                data: [],
                markLine: {
                  silent: true,
                  symbol: 'none',
                  lineStyle: { color: theme.fg1, width: 1, type: 'solid' },
                  label: {
                    color: theme.fg1,
                    fontFamily: theme.mono,
                    fontSize: 10,
                    formatter: deltaBytes(marked.deltaBytes),
                  },
                  data: [{ xAxis: new Date(marked.whenUtc).getTime() }],
                },
              },
            ]
          : []),
      ],
    };
  }

  private tooltip(params: unknown): string {
    const points = (params as { axisValue: number; seriesName: string; value: [number, number]; color: string }[]) ?? [];

    if (points.length === 0) {
      return '';
    }

    const when = stamp(new Date(points[0].axisValue));
    const rows = points
      .filter((p) => p.value[1] > 0)
      .reverse()
      .map(
        (p) =>
          `<div style="display:flex;gap:8px;align-items:center">` +
          `<span style="width:8px;height:8px;background:${p.color}"></span>` +
          `<span style="flex:1">${p.seriesName}</span>` +
          `<span>${bytes(p.value[1])}</span></div>`,
      )
      .join('');

    return `<div style="min-width:200px"><div style="color:#aeaaa5;margin-bottom:4px">${when}</div>${rows}</div>`;
  }
}
