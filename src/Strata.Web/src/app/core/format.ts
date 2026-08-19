/**
 * Number, unit and time rules from the palette spec.
 *
 * Pure functions with no Angular in them, because they are the part of this app most worth testing
 * and least worth mocking a component to reach. Every rule here is stated in the design and none of
 * it is a preference:
 *
 * - Binary sizes with Windows labels. 1 GB is 1024^3 bytes, written GB and not GiB, so Strata and
 *   File Explorer agree on screen. Stated once in Settings and never re-explained.
 * - Never rounded into meaninglessness. Three significant figures minimum, a unit on every figure,
 *   no bare numbers.
 * - Counts are grouped, not abbreviated: 3,053,173 files, never 3.1M.
 * - Deltas carry a sign, written with U+2212 MINUS SIGN rather than a hyphen.
 * - UTC in, local out.
 */

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];

/** U+2212. A hyphen is not a minus sign, and at 32px next to tabular figures it shows. */
export const MINUS = '−';

/**
 * A byte count with its unit.
 *
 * Three significant figures **minimum**, which is the rule as the palette states it and not the same
 * thing as exactly three. Under 100 the figure keeps two decimal places, so the real deletion from
 * the brief reads −16.78 GB rather than −16.8 GB; at 100 and above the decimals are past the point
 * of meaning anything and 550 GB does not gain one. Bytes are whole: a fraction of a byte is not a
 * thing.
 */
export function bytes(value: number): string {
  const negative = value < 0;
  let n = Math.abs(value);
  let unit = 0;

  while (n >= 1024 && unit < UNITS.length - 1) {
    n /= 1024;
    unit++;
  }

  let text: string;

  if (unit === 0) {
    text = String(Math.round(n));
  } else if (n >= 100) {
    text = n.toFixed(0);
  } else {
    // Trailing zeroes past the figure are noise, but one decimal always stays: a column of
    // "32 GB" beside "16.78 GB" stops aligning on the decimal, which is the whole point of
    // tabular numerals here.
    text = n.toFixed(2).replace(/0$/, '').replace(/\.$/, '.0');
  }

  return (negative ? MINUS : '') + text + ' ' + UNITS[unit];
}

/** A byte count with an explicit sign, for deltas where direction is the point. */
export function deltaBytes(value: number): string {
  if (value === 0) {
    return '0 B';
  }

  return (value > 0 ? '+' : '') + bytes(value);
}

/** The exact figure, for the hover that every rounded number carries. */
export function exactBytes(value: number): string {
  return count(value) + ' bytes';
}

/** Grouped, never abbreviated. */
export function count(value: number): string {
  const negative = value < 0;
  const text = Math.abs(Math.round(value)).toLocaleString('en-US');

  return (negative ? MINUS : '') + text;
}

/** A file-count delta, which always carries its sign: "+1,204 files", "−15 files". */
export function deltaFiles(value: number): string {
  const unit = Math.abs(value) === 1 ? 'file' : 'files';

  return (value >= 0 ? '+' : '') + count(value) + ' ' + unit;
}

export function percent(fraction: number, decimals = 1): string {
  return (fraction * 100).toFixed(decimals) + '%';
}

// --- time ---------------------------------------------------------------------------------------

const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function pad(n: number): string {
  return n < 10 ? '0' + n : String(n);
}

/** Wall clock only: "14:07". For a feed whose rows are all from today. */
export function clock(utc: string | Date): string {
  const d = toDate(utc);

  return pad(d.getHours()) + ':' + pad(d.getMinutes());
}

/** "Tue 11 Aug 14:07", 24-hour, in the machine's local zone. */
export function stamp(utc: string | Date): string {
  const d = toDate(utc);

  return `${DAYS[d.getDay()]} ${d.getDate()} ${MONTHS[d.getMonth()]} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** "Wed 19 13:58" — the feed form, which drops the month because the range implies it. */
export function feedStamp(utc: string | Date): string {
  const d = toDate(utc);

  return `${DAYS[d.getDay()]} ${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** The UTC offset, stamped once in the top bar rather than on every row. */
export function zoneLabel(): string {
  // getTimezoneOffset is minutes *behind* UTC, so its sign is the opposite of the one written.
  const minutes = -new Date().getTimezoneOffset();
  const sign = minutes < 0 ? MINUS : '+';
  const abs = Math.abs(minutes);

  return `UTC${sign}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`;
}

/** What any absolute timestamp carries on hover: the full local form and its UTC offset. */
export function stampWithZone(utc: string | Date): string {
  return `${stamp(utc)} ${zoneLabel()}`;
}

/**
 * A duration, at the resolution the number deserves.
 *
 * Milliseconds under a second, because a 1.8 s tick and a 180 ms tick are different claims about
 * the scanner. Minutes and seconds above a minute, because "208.0 s" is a number nobody reads.
 */
export function duration(ms: number): string {
  if (ms <= 0) {
    return '—';
  }

  if (ms < 1000) {
    return Math.round(ms) + ' ms';
  }

  const seconds = ms / 1000;

  if (seconds < 60) {
    return seconds.toFixed(1) + ' s';
  }

  const whole = Math.round(seconds);

  return Math.floor(whole / 60) + ' m ' + pad(whole % 60) + ' s';
}

/** How long ago, for a last-seen date rather than a timestamp. */
export function ago(utc: string | Date): string {
  const elapsed = Date.now() - toDate(utc).getTime();
  const days = Math.floor(elapsed / 86_400_000);

  if (days >= 1) {
    return days === 1 ? '1 day ago' : `${days} days ago`;
  }

  const hours = Math.floor(elapsed / 3_600_000);

  if (hours >= 1) {
    return hours === 1 ? '1 hour ago' : `${hours} hours ago`;
  }

  const minutes = Math.max(0, Math.floor(elapsed / 60_000));

  return minutes <= 1 ? 'just now' : `${minutes} minutes ago`;
}

/**
 * Parses a timestamp from the API.
 *
 * The API sends ISO-8601 with an explicit Z, so this is only defensive: a value that somehow lost
 * its zone would otherwise be read as local and land an hour out, which is the kind of error that
 * looks like a scanner bug rather than a parsing one.
 */
export function toDate(value: string | Date): Date {
  if (value instanceof Date) {
    return value;
  }

  const hasZone = /(?:Z|[+-]\d{2}:?\d{2})$/.test(value);

  return new Date(hasZone ? value : value + 'Z');
}

/** ISO-8601 UTC, which is what every endpoint's `from` and `to` want. */
export function toIso(date: Date): string {
  return date.toISOString();
}

// --- paths --------------------------------------------------------------------------------------

/**
 * Splits a path so the meaningful tail survives truncation.
 *
 * Paths here are long — the worst real case on the target machine is 118 characters — and the part
 * that identifies a change is always at the end. The head ellipsizes, the tail never does.
 */
export function splitPath(path: string, tailSegments = 3): { head: string; tail: string } {
  const parts = path.split('\\');

  if (parts.length <= tailSegments) {
    return { head: '', tail: path };
  }

  return {
    head: parts.slice(0, -tailSegments).join('\\') + '\\',
    tail: parts.slice(-tailSegments).join('\\'),
  };
}

/** The leaf name, or the whole thing when there is no separator. */
export function leaf(path: string): string {
  const trimmed = path.replace(/\\+$/, '');
  const cut = trimmed.lastIndexOf('\\');

  return cut < 0 ? trimmed : trimmed.slice(cut + 1);
}

/** Joins a parent path to a child name without doubling the separator on a volume root. */
export function joinPath(parent: string, name: string): string {
  if (!name) {
    return parent;
  }

  return parent.endsWith('\\') ? parent + name : parent + '\\' + name;
}
