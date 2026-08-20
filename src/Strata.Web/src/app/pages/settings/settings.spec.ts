import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { Settings as SettingsModel } from '../../core/models';
import { SettingsPage } from './settings';

/**
 * The retention tiers, which have to stay in order.
 *
 * A full-resolution window longer than the hourly window would mean the hourly tier never applied to
 * anything, and the API rejects that pair — correctly. But a screen that lets you click something
 * and then shows you an error for it has made you do the work, so widening one tier widens whatever
 * sat behind it instead. This is the only real logic on the Settings screen and it is invisible
 * until it is wrong.
 */
describe('Settings retention tiers', () => {
  let fixture: ComponentFixture<SettingsPage>;
  let http: HttpTestingController;

  function defaults(overrides: Partial<SettingsModel> = {}): SettingsModel {
    return {
      intervalSeconds: 900,
      cpuThresholdPercent: 70,
      maxRecordsPerTick: 50_000,
      catchUpIntervalSeconds: 60,
      eventFloorBytes: 1_048_576,
      snapshotMaxDepth: 8,
      snapshotSizeFloorBytes: 10_485_760,
      historyBandBytes: 0,
      scanWorkers: null,
      backgroundIo: true,
      detectBorderlessFullscreen: true,
      includeStrataDatabase: false,
      excludedEventPaths: [],
      excludedDirectoryNames: [],
      paused: false,
      retentionEnabled: true,
      fullResolutionDays: 7,
      hourlyDays: 30,
      dailyDays: 365,
      scanLogDays: 90,
      ...overrides,
    };
  }

  /** The body of the PUT the component sends when a tier button is clicked. */
  function patchFor(action: () => void): Partial<SettingsModel> {
    action();

    const request = http.expectOne(
      (r) => r.url === '/api/settings' && r.method === 'PUT',
    );

    const body = request.request.body as Partial<SettingsModel>;

    request.flush(defaults(body));

    return body;
  }

  function open(settings: SettingsModel): void {
    http.expectOne('/api/settings').flush(settings);

    // The breakdown and the log both load on open; neither is what this is about.
    http.match('/api/history').forEach((r) => r.flush({
      databaseBytes: 0, walBytes: 0, historyRows: 0, volumes: [],
    }));
    http.match((r) => r.url.startsWith('/api/scan-log')).forEach((r) =>
      r.flush({ summary: { ran: 0, deferred: 0, failed: 0, medianMs: 0, worstMs: 0, worstVolume: '', totalMs: 0 }, ticks: [] }),
    );

    fixture.detectChanges();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SettingsPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(SettingsPage);
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify({ ignoreCancelled: true });
  });

  it('sends the tier that was clicked', () => {
    open(defaults());

    const body = patchFor(() => fixture.componentInstance['setRetention']('full', 3));

    expect(body.fullResolutionDays).toBe(3);
    expect(body.hourlyDays).toBe(30);
    expect(body.dailyDays).toBe(365);
  });

  it('pushes the coarser tiers out rather than sending an invalid pair', () => {
    // Full resolution widened past the hourly window. Sending {full: 14, hourly: 7} would be
    // rejected by the API, and rightly.
    open(defaults({ hourlyDays: 7, dailyDays: 7 }));

    const body = patchFor(() => fixture.componentInstance['setRetention']('full', 14));

    expect(body.fullResolutionDays).toBe(14);
    expect(body.hourlyDays).toBe(14);
    expect(body.dailyDays).toBe(14);
  });

  it('pushes daily out when hourly overtakes it', () => {
    open(defaults({ hourlyDays: 30, dailyDays: 90 }));

    const body = patchFor(() => fixture.componentInstance['setRetention']('hourly', 90));

    expect(body.hourlyDays).toBe(90);
    expect(body.dailyDays).toBe(90);
  });

  it('never narrows a tier that was not asked about', () => {
    open(defaults());

    const body = patchFor(() => fixture.componentInstance['setRetention']('full', 1));

    expect(body.fullResolutionDays).toBe(1);
    expect(body.hourlyDays).toBe(30);
    expect(body.dailyDays).toBe(365);
  });

  it('clamps a daily tier that would fall behind hourly', () => {
    open(defaults({ hourlyDays: 90 }));

    const body = patchFor(() => fixture.componentInstance['setRetention']('daily', 90));

    expect(body.dailyDays).toBe(90);
    expect(body.hourlyDays).toBe(90);
  });

  it('toggles retention without disturbing the tiers', () => {
    open(defaults());

    const body = patchFor(() => fixture.componentInstance['toggleRetention']());

    expect(body.retentionEnabled).toBe(false);
    expect(body.fullResolutionDays).toBeUndefined();
  });
});
