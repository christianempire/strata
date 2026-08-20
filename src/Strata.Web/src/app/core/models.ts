/**
 * The API's shapes, as TypeScript.
 *
 * Mirrors the records in `Strata.Host/Api/Contracts.cs` and `Strata.Storage/ReadModel.cs`. The API
 * serialises camelCase with enums as names and no wrapper envelope, so these are the responses
 * verbatim rather than something a mapping layer produces.
 *
 * Sizes are always `number` bytes and never pre-formatted. Timestamps are ISO-8601 UTC strings and
 * are turned into local time at the point of display, never before.
 */

export type ScanTier = 'full' | 'incremental';

export type VolumeKind = 'unknown' | 'fixed' | 'removable' | 'network' | 'cdRom' | 'ramDisk';

export type ChangeKind = 'grew' | 'shrank' | 'appeared' | 'vanished' | 'moved' | 'remainder';

export type ScannerState = 'idle' | 'scanning' | 'deferred' | 'paused';

export type ScanOutcome = 'ran' | 'deferred' | 'failed';

/** How a running scan's percentage is measured. `none` means there is no percentage. */
export type ProgressBasis = 'none' | 'entries' | 'bytes';

/**
 * Which part of a baseline is running.
 *
 * A baseline is three pieces of work and only the middle one has progress counters. Clearing the
 * previous scan of C: takes about nineteen seconds before a single directory is enumerated, so a
 * display that assumed one long read showed "0%, 0 entries/s" for the first sixth of it.
 */
export type ScanPhase = 'preparing' | 'reading' | 'writing';

export interface Volume {
  id: number | null;
  guidPath: string;
  driveLetter: string | null;
  label: string;
  fileSystem: string;
  kind: VolumeKind;
  diskNumber: number | null;
  totalBytes: number;
  freeBytes: number;
  usedBytes: number;

  /**
   * What Strata accounts for. Expected to sit below `usedBytes`: unreadable directories, filesystem
   * metadata and hardlinks counted once all account for part of the gap. Both are reported so the
   * difference is visible rather than looking like a bug.
   */
  accountedBytes: number;

  attached: boolean;
  scannable: boolean;
  supportsUsnJournal: boolean;

  /** Null means no baseline, and Tier 2 cannot run until there is one. */
  baselineUtc: string | null;
  lastSnapshotUtc: string | null;
  snapshotCount: number;
}

export interface Snapshot {
  id: number;
  volumeId: number;
  takenUtc: string;
  tier: ScanTier;
  durationMs: number;
  totalBytes: number;
  totalOnDisk: number;
  freeBytes: number;
  fileCount: number;
  dirCount: number;
  inaccessibleCount: number;
  hardLinkCount: number;
  hardLinkBytes: number;
  reparseCount: number;
}

/**
 * One directory in a tree walk.
 *
 * Carries a parent link and a leaf name rather than a path. The caller already knows the path it
 * asked for, and every other path is that plus the names on the way down — sending materialised
 * paths would have cost roughly 90 MB of strings on a full walk of C:.
 */
export interface TreeNode {
  dirId: number;
  parentId: number;
  name: string;
  depth: number;
  subtreeBytes: number;
  subtreeOnDisk: number;
  subtreeFiles: number;
  subtreeDirs: number;
  hasChildren: boolean;
}

export interface ChangeEvent {
  id: number;
  volumeId: number;
  driveLetter: string | null;
  snapshotId: number;
  whenUtc: string;
  path: string;
  deltaBytes: number;

  /** Bytes written and removed regardless of net effect. A build directory's real story. */
  churnBytes: number;
  fileDelta: number;
  kind: ChangeKind;
}

/**
 * One attributed change from the diff engine.
 *
 * On a `remainder` row, `path` is the directory the leftovers are *inside* — displayed as
 * "elsewhere in {path}" — not a directory that changed by this amount.
 */
export interface AttributedChange {
  directoryId: number;
  path: string;
  kind: ChangeKind;
  deltaBytes: number;
  churnBytes: number;
  fileCountDelta: number;
  depth: number;
  movedFrom: string | null;
  absoluteDelta: number;
}

export interface Settings {
  intervalSeconds: number;
  cpuThresholdPercent: number;
  maxRecordsPerTick: number;

  /** Seconds between ticks on a volume whose last tick hit the record cap. */
  catchUpIntervalSeconds: number;

  eventFloorBytes: number;
  snapshotMaxDepth: number;
  snapshotSizeFloorBytes: number;
  historyBandBytes: number;
  scanWorkers: number | null;
  backgroundIo: boolean;
  detectBorderlessFullscreen: boolean;
  includeStrataDatabase: boolean;
  excludedEventPaths: string[];
  excludedDirectoryNames: string[];
  paused: boolean;
}

export interface ScanActivity {
  volume: string;
  tier: ScanTier;

  /** Only `reading` has meaningful counters; the others report null percent and basis `none`. */
  phase: ScanPhase;
  startedUtc: string;
  elapsedMs: number;
  directoriesCompleted: number;
  filesSeen: number;
  bytesSeen: number;
  currentPath: string;
  currentDepth: number;
  entriesPerSecond: number;
  basis: ProgressBasis;

  /** Null when `basis` is `none`. */
  percent: number | null;
  expected: number | null;

  /** Null until 2% is done. A time remaining that lurches is worse than none at all. */
  remainingMs: number | null;
}

/**
 * Where one volume's incremental scanning stands.
 *
 * The machine-wide `deferral` cannot say that C: is stuck while D: and E: are fine, which is most of
 * why a volume once went 27 hours without a tick and nothing on screen said so.
 */
export interface VolumeScanStatus {
  /** How the volume is named on screen. */
  volume: string;

  /** Drive letter or GUID path — the same key the rest of the API takes. */
  key: string;

  /** When this volume last ticked successfully, which is how old its recorded state is. */
  lastTickUtc: string | null;

  nextTickUtc: string | null;

  /** Why the last attempt on this volume did not run. Empty when it did. */
  deferral: string;

  /** The journal cannot be resumed, so nothing ticks until a baseline runs. */
  needsBaseline: boolean;

  /** The last tick hit its record cap, so this volume is on the short interval. */
  catchingUp: boolean;

  consecutiveDeferrals: number;
}

export interface ScanStatus {
  state: ScannerState;
  activity: ScanActivity | null;
  queued: { volume: string; tier: ScanTier }[];

  /** When anything last happened anywhere. For one volume's freshness, use `volumes`. */
  lastTickUtc: string | null;

  /** Null while a scan is running or the scanner is paused, when there is no such time. */
  nextTickUtc: string | null;
  deferral: string;

  /** One row per volume the scheduler is ticking. */
  volumes: VolumeScanStatus[];
}

export interface ScanLogEntry {
  id: number;

  /** Rows produced by one scheduler cycle share this, so a tick groups exactly. */
  cycleUtc: string;
  whenUtc: string;
  volumeId: number | null;
  volume: string;
  tier: ScanTier;
  result: ScanOutcome;
  reason: string;
  durationMs: number;
  records: number;
  events: number;
}

export interface ScanLogSummary {
  ran: number;
  deferred: number;
  failed: number;

  /** Median over the ticks that ran. Deferrals have a duration of zero and would drag it to nothing. */
  medianMs: number;
  worstMs: number;
  worstVolume: string;
  totalMs: number;
}

export interface ScanLog {
  summary: ScanLogSummary;
  ticks: ScanLogEntry[];
}

export interface ScanAccepted {
  id: number;
  volume: string;
  tier: ScanTier;

  /** Roughly how long this will take, so a caller knows whether to wait or walk away. */
  expect: string;
}

/** One top-level directory's size at each bucket edge of a Timeline window. */
export interface DirectorySeries {
  dirId: number;
  name: string;

  /** Same length as the response's `buckets`. */
  bytes: number[];
  latest: number;
}

/**
 * The Timeline's stacked area.
 *
 * Every array is indexed by `buckets`. `used` is what the OS reported at each edge, so the
 * difference between it and the sum of the bands is the part Strata could not account for — drawn
 * as a hatched top band rather than quietly left out.
 */
export interface VolumeSeries {
  fromUtc: string;
  toUtc: string;
  buckets: string[];
  series: DirectorySeries[];

  /** Everything past the twelfth directory, summed. The one desaturated band. */
  other: number[];
  used: number[];

  /** False means nothing was measured in this window: an empty range, not a failure. */
  measured: boolean;
}
