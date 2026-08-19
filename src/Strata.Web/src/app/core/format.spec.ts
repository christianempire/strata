import { bytes, count, deltaBytes, deltaFiles, duration, leaf, splitPath, MINUS } from './format';

/**
 * The number and unit rules, which are the part of this app most worth testing.
 *
 * Every rule here comes from the palette spec and every failure is a wrong number on screen rather
 * than a crash — the kind that looks like a scanner bug. The figures used are the real ones from the
 * design brief, so a regression shows up as a mockup that no longer matches.
 */
describe('format', () => {
  const KB = 1024;
  const MB = 1024 * KB;
  const GB = 1024 * MB;
  const TB = 1024 * GB;

  describe('bytes', () => {
    it('uses binary units with Windows labels', () => {
      // 1 GB is 1024^3, written GB and not GiB, so Strata and File Explorer agree on screen.
      expect(bytes(GB)).toBe('1.0 GB');
      expect(bytes(1_000_000_000)).toBe('954 MB');
    });

    it('keeps three significant figures', () => {
      expect(bytes(550 * GB)).toBe('550 GB');
      expect(bytes(92.5 * GB)).toBe('92.5 GB');
      expect(bytes(16.78 * GB)).toBe('16.78 GB');
    });

    it('does not round a figure into meaninglessness', () => {
      // 1.5 GB must not become "2 GB", and 92.5 must not become "93".
      expect(bytes(1.5 * GB)).toBe('1.5 GB');
      expect(bytes(92.5 * GB)).not.toBe('93 GB');
    });

    it('writes whole bytes without a fraction', () => {
      expect(bytes(0)).toBe('0 B');
      expect(bytes(512)).toBe('512 B');
    });

    it('carries a unit on every figure', () => {
      for (const value of [0, 900, 5 * KB, 5 * MB, 5 * GB, 5 * TB]) {
        expect(bytes(value)).toMatch(/ (B|KB|MB|GB|TB|PB)$/);
      }
    });

    it('writes a negative with a minus sign rather than a hyphen', () => {
      expect(bytes(-16.78 * GB)).toBe(MINUS + '16.78 GB');
      expect(bytes(-GB).startsWith('-')).toBe(false);
    });
  });

  describe('deltaBytes', () => {
    it('always carries a sign, because direction is the point', () => {
      expect(deltaBytes(32 * GB)).toBe('+32.0 GB');
      expect(deltaBytes(-16.78 * GB)).toBe(MINUS + '16.78 GB');
    });

    it('writes no change as zero rather than as +0', () => {
      expect(deltaBytes(0)).toBe('0 B');
    });
  });

  describe('count', () => {
    it('groups rather than abbreviating', () => {
      // 3,053,173 files — not 3.1M. The exact figure is the content.
      expect(count(3_053_173)).toBe('3,053,173');
      expect(count(571_055)).toBe('571,055');
    });
  });

  describe('deltaFiles', () => {
    it('signs a file-count delta and names the unit', () => {
      expect(deltaFiles(1204)).toBe('+1,204 files');
      expect(deltaFiles(-15)).toBe(MINUS + '15 files');
    });

    it('says file rather than files for one', () => {
      expect(deltaFiles(1)).toBe('+1 file');
      expect(deltaFiles(-1)).toBe(MINUS + '1 file');
    });
  });

  describe('duration', () => {
    it('keeps milliseconds under a second', () => {
      // A 1.8 s tick and a 180 ms tick are different claims about the scanner.
      expect(duration(180)).toBe('180 ms');
      expect(duration(1800)).toBe('1.8 s');
    });

    it('switches to minutes past a minute', () => {
      expect(duration(208_000)).toBe('3 m 28 s');
      expect(duration(211_000)).toBe('3 m 31 s');
    });

    it('has a dash for no duration rather than a zero', () => {
      expect(duration(0)).toBe('—');
    });
  });

  describe('splitPath', () => {
    it('protects the tail, which is what identifies a change', () => {
      const path =
        'C:\\Users\\chris\\Documents\\My Programming Projects\\aerisetta\\warrior-of-lust\\unity\\Library\\PlayerDataCache\\Win642\\Data';

      const { head, tail } = splitPath(path);

      expect(tail).toBe('PlayerDataCache\\Win642\\Data');
      expect(head + tail).toBe(path);
    });

    it('leaves a short path whole', () => {
      expect(splitPath('C:\\Users')).toEqual({ head: '', tail: 'C:\\Users' });
    });
  });

  describe('leaf', () => {
    it('takes the last segment', () => {
      expect(leaf('C:\\WINDOWS\\WinSxS')).toBe('WinSxS');
    });

    it('ignores a trailing separator', () => {
      expect(leaf('C:\\WINDOWS\\')).toBe('WINDOWS');
    });
  });
});
