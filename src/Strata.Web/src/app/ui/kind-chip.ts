import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { ChangeKind } from '../core/models';

/**
 * How a change is described, as a glyph and a word.
 *
 * The glyph is the point. Colour is the second signal and never the first: amber and cyan separate
 * on the deutan and protan axes and survive greyscale, but the triangle, the diamond and the sign
 * each carry the meaning on their own, so the palette could be thrown away without losing the
 * reading.
 *
 * `remainder` is deliberately the only one drawn in --fg-3 on --bg-2: it is not a directory's own
 * change, and an estimate is never rendered in the same ink as a measurement.
 */
const KINDS: Record<ChangeKind, { glyph: string; label: string; cls: string }> = {
  grew: { glyph: '▲', label: 'grew', cls: 'chip-pos' },
  shrank: { glyph: '▼', label: 'shrank', cls: 'chip-neg' },
  appeared: { glyph: '◆', label: 'appeared', cls: 'chip-pos' },
  vanished: { glyph: '◇', label: 'vanished', cls: 'chip-neg' },
  moved: { glyph: '→', label: 'moved', cls: '' },
  remainder: { glyph: '▨', label: 'elsewhere', cls: '' },
};

@Component({
  selector: 'strata-kind',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="chip" [class]="info().cls">{{ info().glyph }} {{ info().label }}</span>`,
})
export class KindChip {
  readonly kind = input.required<ChangeKind>();

  protected readonly info = computed(() => KINDS[this.kind()] ?? KINDS.grew);
}

/** The same table, for templates that want the glyph without the chip. */
export function kindGlyph(kind: ChangeKind): string {
  return (KINDS[kind] ?? KINDS.grew).glyph;
}

export function kindLabel(kind: ChangeKind): string {
  return (KINDS[kind] ?? KINDS.grew).label;
}
