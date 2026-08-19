import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { zoneLabel } from './core/format';
import { Shell, volumeKey } from './core/state';
import { ClockPipe } from './ui/pipes';
import { FirstRun } from './pages/first-run';

/**
 * The app shell: the top bar, the nav, and the two states that gate every screen behind them.
 *
 * The bar is fixed at the top and holds exactly three things — where you are, what the scanner is
 * doing, and which zone the times are in. The zone is stamped once here rather than on every row,
 * which is the rule from the palette.
 */
@Component({
  selector: 'strata-root',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, FirstRun, ClockPipe],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  protected readonly shell = inject(Shell);
  protected readonly zone = zoneLabel();

  protected readonly status = this.shell.status;

  /**
   * Whether the whole app is behind the first-run screen.
   *
   * Only when there is genuinely nothing to show: no volume has ever been scanned. A volume with
   * history and a scan running is *not* this state — the running scan takes a 2px line under the
   * top bar and the stale figures stay on screen with their timestamp, which is state 02 rather
   * than state 01.
   */
  protected readonly firstRun = computed(() => this.shell.noHistory());

  /** The status pill's word and colour. Deferral is a decision with a reason, never an error. */
  protected readonly pill = computed(() => {
    const status = this.status();

    if (!status) {
      return { text: 'connecting', cls: 'faint' };
    }

    switch (status.state) {
      case 'scanning':
        return { text: this.scanningLabel(), cls: 'pos' };
      case 'deferred':
        return { text: 'deferred', cls: 'warn' };
      case 'paused':
        return { text: 'paused', cls: 'warn' };
      default:
        return { text: 'idle', cls: 'dim' };
    }
  });

  /** The 2px progress line under the bar. Null when nothing measurable is running. */
  protected readonly progress = computed(() => this.status()?.activity?.percent ?? null);

  protected readonly deferral = computed(() => this.status()?.deferral ?? '');

  constructor() {
    this.shell.start();
  }

  protected volumeKeyOf = volumeKey;

  private scanningLabel(): string {
    const activity = this.status()?.activity;

    if (!activity) {
      return 'scanning';
    }

    return activity.percent === null
      ? `scanning ${activity.volume}`
      : `scanning ${activity.volume} · ${activity.percent.toFixed(0)}%`;
  }
}
