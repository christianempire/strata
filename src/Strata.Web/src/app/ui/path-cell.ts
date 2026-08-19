import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { splitPath } from '../core/format';

/**
 * A path that truncates from the left and keeps its tail.
 *
 * Paths in this app are long — the worst real one on the target machine is 118 characters through
 * six levels of a Unity project — and everything that identifies a change is at the end. So the head
 * shrinks and ellipsizes while the tail never shrinks at all.
 *
 * The head is laid out `direction: rtl` so the browser's own ellipsis lands at the *start* of it,
 * which is where the uninteresting part is. The full path is on the title, always.
 *
 * That trick has one trap and Windows paths walk straight into it. An RTL paragraph resolves the
 * neutral characters at each end of a run to the paragraph direction, and a path segment ends in a
 * backslash — so `C:\Users\chris\AppData\` renders as `\C:\Users\chris\AppData`, which looks
 * like a malformed path rather than a rendering artefact. Bracketing the head with LEFT-TO-RIGHT
 * MARK gives those separators a strong LTR neighbour on both sides and they stay where they were
 * written.
 */
@Component({
  selector: 'strata-path',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="path" [title]="path()">
      @if (parts().head) {
        <span class="path-head">{{ LRM }}{{ parts().head }}{{ LRM }}</span>
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

  /** U+200E LEFT-TO-RIGHT MARK. See the note above; without it the separators move. */
  protected readonly LRM = '\u200E';

  protected readonly parts = computed(() => splitPath(this.path(), this.tail()));
}
