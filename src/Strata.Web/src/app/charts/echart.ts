import {
  Directive,
  ElementRef,
  NgZone,
  OnDestroy,
  computed,
  effect,
  inject,
  input,
  output,
} from '@angular/core';
import * as echarts from 'echarts/core';
import { BarChart, LineChart, TreemapChart } from 'echarts/charts';
import {
  DatasetComponent,
  GridComponent,
  MarkLineComponent,
  TooltipComponent,
  BrushComponent,
  ToolboxComponent,
} from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';

import { token } from '../core/series';

/**
 * Only the pieces two charts and a treemap actually use.
 *
 * The full ECharts bundle is 1 MB of JavaScript for a page that draws a stacked area and a treemap.
 * Registering by hand keeps it to what is drawn, which matters here for the same reason everything
 * else in this project is measured: the app is served off a local disk by a process whose entire
 * promise is to stay out of the way.
 */
echarts.use([
  LineChart,
  BarChart,
  TreemapChart,
  GridComponent,
  TooltipComponent,
  DatasetComponent,
  MarkLineComponent,
  BrushComponent,
  ToolboxComponent,
  CanvasRenderer,
]);

export type EChartOption = Parameters<echarts.ECharts['setOption']>[0];

/**
 * An ECharts instance bound to the element it sits on.
 *
 * A directive rather than a wrapper component, so the host owns its own layout and the chart is not
 * one more box inside another box. Everything ECharts does is outside Angular's zone: it animates
 * on requestAnimationFrame and emits mousemove events per pixel, and letting either drive change
 * detection turns a chart hover into an application-wide re-render.
 */
@Directive({
  selector: '[strataChart]',
  standalone: true,
})
export class EChartDirective implements OnDestroy {
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly zone = inject(NgZone);

  readonly option = input.required<EChartOption>({ alias: 'strataChart' });

  /**
   * Replace the option outright rather than merging it.
   *
   * Needed whenever a series list can shrink: merged updates leave the old series in place, and a
   * volume with fewer top-level directories than the last one would keep the extra bands.
   */
  readonly replace = input(false, { alias: 'strataChartReplace' });

  readonly chartClick = output<unknown>();
  readonly chartBrush = output<unknown>();
  readonly chartReady = output<echarts.ECharts>();

  private chart: echarts.ECharts | null = null;
  private observer: ResizeObserver | null = null;

  constructor() {
    effect(() => {
      const option = this.option();
      const replace = this.replace();

      this.zone.runOutsideAngular(() => this.render(option, replace));
    });
  }

  ngOnDestroy(): void {
    this.observer?.disconnect();
    this.chart?.dispose();
  }

  private render(option: EChartOption, replace: boolean): void {
    const element = this.host.nativeElement as HTMLElement;

    if (!this.chart) {
      // The plot area is the app ground rather than the panel: the palette gives chart plot areas
      // --bg-0, so the panel frames the data rather than tinting it. The canvas stays transparent
      // and the surrounding element carries the colour, which is set in each chart's own option.
      this.chart = echarts.init(element, undefined, { renderer: 'canvas' });

      // Panels resize with the window and with a drawer opening beside them. ECharts does not
      // watch its own element, so without this a chart keeps the width it was born at.
      this.observer = new ResizeObserver(() => this.chart?.resize());
      this.observer.observe(element);

      this.chart.on('click', (event) => this.zone.run(() => this.chartClick.emit(event)));
      this.chart.on('brushEnd', (event) => this.zone.run(() => this.chartBrush.emit(event)));

      this.zone.run(() => this.chartReady.emit(this.chart!));
    }

    this.chart.setOption(option, { notMerge: replace });
  }
}

/**
 * The chart chrome every plot in this app shares.
 *
 * Gridlines in --grid, the axis line in --rule-strong, labels in --fg-3 at 10px. Read from the
 * tokens at call time rather than written as hex, so the palette keeps one home and a light theme
 * would need no change here.
 */
export function chartTheme() {
  return {
    grid: token('--grid'),
    rule: token('--rule'),
    ruleStrong: token('--rule-strong'),
    fg1: token('--fg-1'),
    fg2: token('--fg-2'),
    fg3: token('--fg-3'),
    bg0: token('--bg-0'),
    bg1: token('--bg-1'),
    bg2: token('--bg-2'),
    pos: token('--pos'),
    neg: token('--neg'),
    other: token('--other'),
    mono: "'JetBrains Mono', Consolas, monospace",
  };
}

/** The tooltip box, styled as a panel rather than as ECharts' default white card. */
export function tooltipStyle() {
  const theme = chartTheme();

  return {
    backgroundColor: theme.bg1,
    borderColor: theme.ruleStrong,
    borderWidth: 1,
    padding: [6, 9] as [number, number],
    textStyle: { color: theme.fg1, fontSize: 11, fontFamily: theme.mono },
    extraCssText: 'border-radius:0;box-shadow:none;',
  };
}
