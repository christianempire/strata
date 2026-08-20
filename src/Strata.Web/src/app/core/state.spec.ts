import { ScanStatus, VolumeScanStatus } from './models';
import { STALE_AFTER_MS, freshness, scannerFor } from './state';

/**
 * The freshness rules behind the dated-reading note.
 *
 * Explorer showed a folder that had been deleted the day before, at its full 16.84 GiB, for 27
 * hours. It was not wrong — it was faithfully showing the last state Strata knew — but nothing on
 * screen said when that state was true. These are the rules that decide when a reading gets a date
 * on it, and a failure here is a screen that looks current and is not.
 */
describe('state', () => {
  const NOW = Date.parse('2026-08-20T19:26:00Z');

  function scanner(overrides: Partial<VolumeScanStatus> = {}): VolumeScanStatus {
    return {
      volume: 'C: (OS-M2-1TB)',
      key: 'C:',
      lastTickUtc: '2026-08-20T19:20:00Z',
      nextTickUtc: '2026-08-20T19:35:00Z',
      deferral: '',
      needsBaseline: false,
      catchingUp: false,
      consecutiveDeferrals: 0,
      ...overrides,
    };
  }

  describe('scannerFor', () => {
    const status = {
      volumes: [scanner(), scanner({ key: 'D:', volume: 'D: (Storage-M2-1TB)' })],
    } as ScanStatus;

    it('finds a volume by the key the rest of the API uses', () => {
      expect(scannerFor(status, 'D:')?.volume).toBe('D: (Storage-M2-1TB)');
    });

    it('answers null rather than guessing', () => {
      expect(scannerFor(status, 'E:')).toBeNull();
      expect(scannerFor(status, null)).toBeNull();
      expect(scannerFor(null, 'C:')).toBeNull();
    });

    it('survives a status from a build that did not report volumes', () => {
      expect(scannerFor({} as ScanStatus, 'C:')).toBeNull();
    });
  });

  describe('freshness', () => {
    it('says nothing about a volume that is keeping up', () => {
      // Six minutes old at a fifteen-minute interval is a scanner working, not a stale reading, and
      // dating every screen that refreshes on a timer would be noise.
      expect(freshness(scanner(), NOW)?.stale).toBe(false);
    });

    it('is stale after an hour, which is four ticks that did not run', () => {
      const justUnder = freshness(
        scanner({ lastTickUtc: new Date(NOW - STALE_AFTER_MS + 1000).toISOString() }),
        NOW,
      );

      const justOver = freshness(
        scanner({ lastTickUtc: new Date(NOW - STALE_AFTER_MS).toISOString() }),
        NOW,
      );

      expect(justUnder?.stale).toBe(false);
      expect(justOver?.stale).toBe(true);
    });

    it('reports the incident', () => {
      // The real numbers: last successful tick 2026-08-19T16:26:18, noticed 27 hours later.
      const age = freshness(
        scanner({
          lastTickUtc: '2026-08-19T16:26:18Z',
          deferral: 'journal reset or cursor trimmed; a full baseline is needed',
          needsBaseline: true,
          consecutiveDeferrals: 101,
        }),
        NOW,
      );

      expect(age?.stale).toBe(true);
      expect(age?.ageMs).toBe(27 * 60 * 60 * 1000 - 18_000);
      expect(age?.atUtc).toBe('2026-08-19T16:26:18Z');
      expect(age?.needsBaseline).toBe(true);
      expect(age?.reason).toContain('a full baseline is needed');
    });

    it('carries the reason so the screen does not have to guess at one', () => {
      const age = freshness(
        scanner({
          lastTickUtc: '2026-08-19T16:26:18Z',
          deferral: 'deferred: Fullscreen (borderless fullscreen)',
        }),
        NOW,
      );

      expect(age?.reason).toBe('deferred: Fullscreen (borderless fullscreen)');
      expect(age?.needsBaseline).toBe(false);
    });

    it('says nothing about a volume that has never ticked', () => {
      // There is no earlier reading for this one to be older than, and the no-baseline case has its
      // own wording. An age of "56 years" from a null date would be worse than silence.
      expect(freshness(scanner({ lastTickUtc: null }), NOW)).toBeNull();
      expect(freshness(null, NOW)).toBeNull();
    });

    it('never reports a reading from the future as negatively old', () => {
      // Clock skew between the browser and the host is a real thing and must not print "-3 minutes".
      const age = freshness(scanner({ lastTickUtc: new Date(NOW + 60_000).toISOString() }), NOW);

      expect(age?.ageMs).toBe(0);
      expect(age?.stale).toBe(false);
    });
  });
});
