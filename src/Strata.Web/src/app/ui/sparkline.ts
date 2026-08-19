import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { bytes, stamp } from '../core/format';

export interface SparkPoint {
  whenUtc: string;
  usedBytes: number;
}

/**
 * A thirty-day used-space sparkline, as bars.
 *
 * Bars rather than a line, and no ECharts: this is thirty rectangles on a volume card, and pulling
 * in a charting library per card would cost more than the whole Overview screen.
 *
 * The scale is padded and does **not** start at zero. Used space on a nearly-full volume moves by a
 * percent or two over a month, and a zero-based axis would draw thirty identical bars — which is a
 * true picture of the ratio and a useless picture of the trend. The panel says "used, 30 days"
 * rather than implying an absolute scale, and every bar carries its exact figure on hover.
 */
@Component({
  selector: 'strata-sparkline',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="spark" [style.--spark-colour]="colour()">
      @for (bar of bars(); track bar.key) {
        <span class="bar" [class.last]="bar.last" [style.height.%]="bar.height" [title]="bar.title">
        </span>
      } @empty {
        <span class="t-meta faint">no history in this window</span>
      }
    </div>
  `,
  styles: [
    `
      .spark {
        display: flex;
        align-items: flex-end;
        gap: 2px;
        height: 34px;
      }

      .bar {
        flex: 1;
        min-width: 2px;
        min-height: 2px;
        background: var(--spark-colour, var(--s1));
        opacity: 0.75;
      }

      /* The newest reading is the one being asked about, so it is the one drawn in text ink. */
      .bar.last {
        background: var(--fg-1);
        opacity: 1;
      }
    `,
  ],
})
export class Sparkline {
  readonly points = input.required<SparkPoint[]>();
  readonly colour = input('var(--s1)');

  protected readonly bars = computed(() => {
    const points = this.points();

    if (points.length === 0) {
      return [];
    }

    const values = points.map((p) => p.usedBytes);
    const low = Math.min(...values);
    const high = Math.max(...values);

    // A flat month is a real answer — state 08 says so — so it is drawn as a flat row of bars at
    // mid height rather than as nothing, or as noise amplified by a zero-width range.
    const range = high - low;
    const pad = range === 0 ? 1 : range * 0.15;

    return points.map((point, index) => ({
      key: point.whenUtc + ':' + index,
      last: index === points.length - 1,
      height:
        range === 0
          ? 55
          : 12 + 88 * ((point.usedBytes - (low - pad)) / (range + pad * 2)),
      title: `${stamp(point.whenUtc)} · ${bytes(point.usedBytes)} used`,
    }));
  });
}
