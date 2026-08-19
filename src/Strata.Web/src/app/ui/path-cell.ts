import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { splitPath } from '../core/format';

/**
 * A path that truncates from the left and keeps its tail.
 *
 * Paths in this app are long — the worst real one on the target machine is 118 characters through
 * six levels of a Unity project — and everything that identifies a change is at the end. So the head
 * shrinks and ellipsizes while the tail never shrinks at all.
 *
 * The head is reversed with `direction: rtl` so the browser's own ellipsis lands at the *start* of
 * it, which is where the uninteresting part is. The full path is on the title, always.
 */
@Component({
  selector: 'strata-path',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="path" [title]="path()">
      @if (parts().head) {
        <span class="path-head">{{ parts().head }}</span>
      }
      <span class="path-tail">{{ parts().tail }}</span>
    </span>
  `,
  styles: [
    `
      :host {
        display: block;
        min-width: 0;
        overflow: hidden;
      }
    `,
  ],
})
export class PathCell {
  readonly path = input.required<string>();

  /** How many trailing segments are protected from truncation. */
  readonly tail = input(3);

  protected readonly parts = computed(() => splitPath(this.path(), this.tail()));
}
