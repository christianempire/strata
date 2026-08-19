import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  AttributedChange,
  ChangeEvent,
  ScanAccepted,
  ScanLog,
  ScanStatus,
  ScanTier,
  Settings,
  Snapshot,
  TreeNode,
  Volume,
  VolumeSeries,
} from './models';

/** Anything the API takes as a query parameter. Undefined and null mean "not asked for". */
type Query = Record<string, string | number | boolean | undefined | null>;

/**
 * The localhost API, one method per endpoint.
 *
 * Deliberately thin: no caching, no retry, no interceptor. Everything it talks to is in the same
 * process on the same machine, everything small is under 20 ms, and a layer that hid a failure
 * behind a stale value would be hiding the one thing the UI has to be honest about.
 *
 * Relative URLs, because the app is served by the same host that serves the API. That is also what
 * keeps the browser's same-origin rule doing the work of access control: there is no CORS header
 * anywhere, so no page from anywhere else can read a disk through this.
 */
@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  volumes(): Observable<Volume[]> {
    return this.http.get<Volume[]>('/api/volumes');
  }

  snapshots(query: {
    volume?: string;
    from?: string;
    to?: string;
    limit?: number;
  } = {}): Observable<Snapshot[]> {
    return this.http.get<Snapshot[]>('/api/snapshots', { params: params(query) });
  }

  events(query: {
    volume?: string;
    from?: string;
    to?: string;
    minBytes?: number;
    limit?: number;
  } = {}): Observable<ChangeEvent[]> {
    return this.http.get<ChangeEvent[]>('/api/events', { params: params(query) });
  }

  /**
   * A directory tree, current or historical.
   *
   * `volume` reads current state; `snapshot` reads history as it stood when that snapshot was
   * taken. Drill down a level at a time — that is the access pattern the storage is tuned for, and
   * the whole of C: at unbounded depth is 571k nodes and 107 MB.
   */
  tree(query: {
    volume?: string;
    snapshot?: number;
    path?: string;
    depth?: number;
    limit?: number;
  }): Observable<TreeNode[]> {
    return this.http.get<TreeNode[]>('/api/tree', { params: params(query) });
  }

  diff(query: {
    from: number;
    to: number;
    path?: string;
    minBytes?: number;
    limit?: number;
  }): Observable<AttributedChange[]> {
    return this.http.get<AttributedChange[]>('/api/diff', { params: params(query) });
  }

  /**
   * Used space over time, split by top-level directory.
   *
   * One request rather than a tree walk per snapshot: a week at a fifteen-minute cadence is 672
   * snapshots, and the storage is keyed so that one directory's whole window is a single range read.
   */
  series(query: {
    volume: string;
    from?: string;
    to?: string;
    buckets?: number;
    top?: number;
  }): Observable<VolumeSeries> {
    return this.http.get<VolumeSeries>('/api/series', { params: params(query) });
  }

  scanLog(query: {
    volume?: string;
    from?: string;
    to?: string;
    result?: string;
    limit?: number;
  } = {}): Observable<ScanLog> {
    return this.http.get<ScanLog>('/api/scan-log', { params: params(query) });
  }

  scanStatus(): Observable<ScanStatus> {
    return this.http.get<ScanStatus>('/api/scan/status');
  }

  /** Answers 202. The scan has not happened when this returns, and a baseline will not for minutes. */
  scan(volume: string, tier: ScanTier): Observable<ScanAccepted> {
    return this.http.post<ScanAccepted>('/api/scan', { volume, tier });
  }

  settings(): Observable<Settings> {
    return this.http.get<Settings>('/api/settings');
  }

  /** A patch, not a replacement. Send only what is changing; omitted fields are left alone. */
  saveSettings(patch: Partial<Settings>): Observable<Settings> {
    return this.http.put<Settings>('/api/settings', patch);
  }
}

function params(query: Query): HttpParams {
  let result = new HttpParams();

  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && value !== '') {
      result = result.set(key, String(value));
    }
  }

  return result;
}

/**
 * The message out of a failed request.
 *
 * The API wraps every failure as `{ "error": "..." }` and the messages are written to be read, so
 * showing one beats "something went wrong". A request that never reached the host has no body at
 * all, and that case has its own sentence because it means the tray agent is not running — which is
 * a different problem with a different fix.
 */
export function errorText(failure: unknown): string {
  const wrapped = failure as { error?: { error?: string }; status?: number; message?: string };

  if (wrapped?.error?.error) {
    return wrapped.error.error;
  }

  if (wrapped?.status === 0) {
    return 'No answer from Strata on localhost:5177. The tray agent may not be running.';
  }

  return wrapped?.message ?? 'The request failed.';
}
