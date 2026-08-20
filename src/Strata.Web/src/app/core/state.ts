import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval, startWith, switchMap } from 'rxjs';

import { Api, errorText } from './api';
import { ScanStatus, Volume, VolumeScanStatus } from './models';

/**
 * How often the scanner's state is re-read.
 *
 * Two seconds while something is running, so a progress bar moves; thirty when nothing is, because
 * the answer is "idle" and asking more often is a background process this tool exists to avoid
 * being. The status endpoint reads process memory rather than the database, so neither rate costs
 * the scan anything.
 */
const BUSY_POLL_MS = 2000;
const IDLE_POLL_MS = 30_000;

/**
 * The two things every screen needs: which volume, and what the scanner is doing.
 *
 * A single root service rather than per-component state, because the volume picker is in the app
 * shell and the answer has to survive a route change — clicking from Timeline to Explorer while
 * looking at D: should not land on C:.
 */
@Injectable({ providedIn: 'root' })
export class Shell {
  private readonly api = inject(Api);
  private readonly destroyRef = inject(DestroyRef);

  readonly volumes = signal<Volume[]>([]);
  readonly status = signal<ScanStatus | null>(null);

  /** Drive letter, or a GUID path for a volume with no letter. What the API's `volume` takes. */
  readonly selected = signal<string | null>(null);

  readonly failure = signal<string | null>(null);
  readonly loaded = signal(false);

  readonly volume = computed(() => {
    const key = this.selected();

    return this.volumes().find((v) => volumeKey(v) === key) ?? null;
  });

  /**
   * Volumes worth offering in a picker: attached and scannable, or detached with history.
   *
   * System partitions are left out. They have no path to browse and nothing a person is looking for,
   * and on this machine they were two thirds as many buttons as there are real disks. Settings still
   * lists them, because that is the screen for checking what the scanner is touching.
   */
  readonly listed = computed(() =>
    this.volumes().filter(
      (v) => !isSystemPartition(v) && ((v.attached && v.scannable) || v.snapshotCount > 0),
    ),
  );

  /** The ones left out of the pickers, so a screen can say they exist rather than vanish them. */
  readonly systemPartitions = computed(() => this.volumes().filter(isSystemPartition));

  readonly scanning = computed(() => this.status()?.state === 'scanning');

  /** The scanner's standing on the volume currently being looked at. */
  readonly volumeScanner = computed(() => scannerFor(this.status(), this.selected()));

  /** True until at least one volume has a baseline. The first-run state hangs off this. */
  readonly noHistory = computed(
    () => this.loaded() && !this.volumes().some((v) => v.baselineUtc !== null),
  );

  private polling = false;

  /** Called once by the shell. Idempotent, because a second call would double the poll. */
  start(): void {
    if (this.polling) {
      return;
    }

    this.polling = true;
    this.refreshVolumes();

    // A fixed interval rather than one that reschedules itself off the last answer: the poll is
    // cheap and always finishes, and a self-rescheduling timer that loses a response stops forever.
    interval(BUSY_POLL_MS)
      .pipe(
        startWith(0),
        switchMap(() => this.api.scanStatus()),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (status) => {
          const was = this.status();

          this.status.set(status);
          this.failure.set(null);

          // A scan that has just finished changed the thing every screen is reading. Volumes carry
          // the baseline and snapshot counts the first-run and undercount states turn on.
          if (was?.state === 'scanning' && status.state !== 'scanning') {
            this.refreshVolumes();
          }
        },
        error: (failure) => this.failure.set(errorText(failure)),
      });

    interval(IDLE_POLL_MS)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.refreshVolumes());
  }

  refreshVolumes(): void {
    this.api.volumes().subscribe({
      next: (volumes) => {
        this.volumes.set(volumes);
        this.loaded.set(true);
        this.failure.set(null);

        const current = this.selected();

        if (!current || !volumes.some((v) => volumeKey(v) === current)) {
          this.selected.set(defaultVolume(volumes));
        }
      },
      error: (failure) => {
        this.failure.set(errorText(failure));
        this.loaded.set(true);
      },
    });
  }

  select(key: string): void {
    this.selected.set(key);
  }
}

/** What the API's `volume` parameter should be for this volume. */
export function volumeKey(volume: Volume): string {
  return volume.driveLetter ?? volume.guidPath;
}

/**
 * A volume Windows keeps unmounted: the EFI system partition, WinRE recovery, and their like.
 *
 * Identified by having no drive letter, which is the property that actually matters rather than a
 * proxy for it. Windows leaves these without a mount point on purpose so they cannot casually be
 * written to, and File Explorer does not show them either — a volume with no path is not one anyone
 * browses to, and a treemap of it has nowhere to start.
 *
 * Not identified by size or filesystem: on this machine one is a 790 MB NTFS recovery partition and
 * the other a 96 MB FAT32 ESP, and neither of those facts generalises. A future data volume mounted
 * only at a folder path would be caught by this too, which is the one false positive it can produce
 * — and it is still listed in Settings, so it is out of the way rather than hidden.
 */
export function isSystemPartition(volume: Volume): boolean {
  return volume.driveLetter === null;
}

/**
 * What to call a volume on screen.
 *
 * Recovery and EFI partitions have neither a drive letter nor a label, and Windows really does
 * enumerate them. Falling through to a shortened GUID keeps them identifiable without letting a
 * 48-character path into a table column — and beats the blank button an unguarded
 * `driveLetter ?? label` produces.
 */
export function volumeName(volume: Volume): string {
  if (volume.driveLetter) {
    return volume.driveLetter;
  }

  if (volume.label) {
    return volume.label;
  }

  const guid = /Volume\{([0-9a-f]{8})/i.exec(volume.guidPath);

  return guid ? `{${guid[1]}}` : 'unnamed';
}

/**
 * Which volume to open on.
 *
 * The one with history and the most recent activity, because that is the one with something to say.
 * Falling back to the first attached volume rather than to nothing, so a first run has somewhere to
 * point the "scan this" button.
 */
function defaultVolume(volumes: Volume[]): string | null {
  const withHistory = volumes
    .filter((v) => v.baselineUtc !== null)
    .sort((a, b) => (b.lastSnapshotUtc ?? '').localeCompare(a.lastSnapshotUtc ?? ''));

  if (withHistory.length > 0) {
    return volumeKey(withHistory[0]);
  }

  const attached = volumes.filter((v) => v.attached && v.scannable);

  return attached.length > 0 ? volumeKey(attached[0]) : null;
}

/**
 * How old a reading may be before its age is worth stating.
 *
 * One hour, which at the default fifteen-minute interval is four consecutive ticks that did not run
 * — deliberately the same count at which the scheduler stops waiting and forces the issue. Below it
 * the screen is showing the ordinary lag of a scanner that ticks on a timer, and dating every
 * reading would be noise; above it something is wrong and the reading has a date on it.
 */
export const STALE_AFTER_MS = 60 * 60 * 1000;

/** The scanner's row for one volume, or null when it is not being ticked. */
export function scannerFor(
  status: ScanStatus | null,
  key: string | null,
): VolumeScanStatus | null {
  if (!status || !key) {
    return null;
  }

  return status.volumes?.find((v) => v.key === key) ?? null;
}

/** How old a volume's recorded state is, and what to say about it. */
export interface Freshness {
  /** When the volume last ticked. Never null: a volume that never has gets no Freshness at all. */
  atUtc: string;
  ageMs: number;

  /** Whether the age is worth putting on screen. */
  stale: boolean;

  /** Why it has not ticked since. Empty when the last attempt ran. */
  reason: string;

  /** Nothing will tick until a baseline runs, so the age will keep growing on its own. */
  needsBaseline: boolean;
}

/**
 * What to say about the age of a volume's recorded state.
 *
 * Explorer answers "where are the bytes right now?" from `dir_totals`, which is only as fresh as the
 * last successful tick. It used to give no hint of its age, so a 27-hour-old answer looked exactly
 * like a current one — including a 16.84 GiB folder that had been deleted the day before. The house
 * rule the rest of the UI follows applies here too: a stale reading is stated with its timestamp,
 * not hidden and not silently refreshed.
 */
export function freshness(
  volume: VolumeScanStatus | null,
  nowMs: number = Date.now(),
): Freshness | null {
  if (!volume) {
    return null;
  }

  // Never ticked. Not stale — there is no earlier reading for this one to be older than — and the
  // no-baseline case has its own wording elsewhere.
  if (!volume.lastTickUtc) {
    return null;
  }

  const ageMs = Math.max(0, nowMs - Date.parse(volume.lastTickUtc));

  return {
    atUtc: volume.lastTickUtc,
    ageMs,
    stale: ageMs >= STALE_AFTER_MS,
    reason: volume.deferral,
    needsBaseline: volume.needsBaseline,
  };
}
