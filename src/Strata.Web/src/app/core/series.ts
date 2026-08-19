/**
 * Which of the twelve series hues a directory gets.
 *
 * Assigned by a stable hash of the name rather than by rank, which is the rule the palette states
 * and the reason for it: `Users` keeps its colour on the Timeline, in the treemap and in Compare,
 * and it keeps it across a re-scan that reorders the list. A rank-assigned ramp repaints the whole
 * chart when the third-largest directory becomes the fourth, and the eye reads that as a change in
 * the data.
 */

/** The twelve hues, in ramp order. Read from the tokens so the palette has one home. */
const SERIES_TOKENS = [
  '--s1', '--s2', '--s3', '--s4', '--s5', '--s6',
  '--s7', '--s8', '--s9', '--s10', '--s11', '--s12',
];

let resolved: string[] | null = null;
let otherColour = '#4b4743';

/**
 * Resolves the ramp out of the CSS custom properties, once.
 *
 * ECharts takes concrete colours rather than `var(--s1)`, so the tokens have to be read at chart
 * init. Doing it through getComputedStyle rather than duplicating the hex values here means the
 * palette still has exactly one definition.
 */
function ramp(): string[] {
  if (resolved) {
    return resolved;
  }

  const style = getComputedStyle(document.documentElement);

  resolved = SERIES_TOKENS.map((token) => style.getPropertyValue(token).trim() || '#888');
  otherColour = style.getPropertyValue('--other').trim() || otherColour;

  return resolved;
}

/**
 * The desaturated band everything past the twelfth series folds into.
 *
 * The only unsaturated colour in the chart, so "everything else" never looks like a named directory.
 */
export function otherSeriesColour(): string {
  ramp();

  return otherColour;
}

/**
 * FNV-1a over the lower-cased name.
 *
 * Any stable hash would do. This one is four lines, has no dependency, and spreads short similar
 * strings — which is what directory names at one level of one volume are — across its range well
 * enough that adjacent top-level directories do not collide in practice.
 */
function hash(name: string): number {
  let h = 0x811c9dc5;

  for (let i = 0; i < name.length; i++) {
    h ^= name.charCodeAt(i) | 0x20;
    h = Math.imul(h, 0x01000193);
  }

  return h >>> 0;
}

/** The colour for a directory name, stable for the life of that name. */
export function seriesColour(name: string): string {
  const colours = ramp();

  return colours[hash(name) % colours.length];
}

/**
 * Colours for a set of names, pulled apart where the hash collided.
 *
 * The hash is stable but not injective, and two adjacent bands in the same colour is the one thing
 * the ramp exists to prevent. So a collision within a single chart is nudged to the next free hue —
 * a local repair that leaves every other name's colour exactly where it was.
 */
export function seriesColours(names: string[]): Map<string, string> {
  const colours = ramp();
  const taken = new Set<number>();
  const result = new Map<string, string>();

  for (const name of names) {
    let index = hash(name) % colours.length;
    let tries = 0;

    while (taken.has(index) && tries < colours.length) {
      index = (index + 1) % colours.length;
      tries++;
    }

    // Past twelve names every hue is taken; the caller has already folded the tail into "other",
    // so anything arriving here has earned a band and takes the collision.
    taken.add(index);
    result.set(name, colours[index]);
  }

  return result;
}

/** A token's current value, for the handful of places a chart needs one. */
export function token(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}
