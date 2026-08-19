import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval, startWith, switchMap } from 'rxjs';

import { Api, errorText } from './api';
import { ScanStatus, Volume } from './models';

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

  /** Volumes worth offering in the picker: attached and scannable, or detached with history. */
  readonly listed = computed(() =>
    this.volumes().filter((v) => (v.attached && v.scannable) || v.snapshotCount > 0),
  );

  readonly scanning = computed(() => this.status()?.state === 'scanning');

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
