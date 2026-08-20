import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { ScanStatus, VolumeScanStatus } from '../../core/models';
import { Shell } from '../../core/state';
import { Explorer } from './explorer';

/**
 * That the dated-reading note actually reaches the screen.
 *
 * The freshness rules have their own unit coverage in `core/state.spec.ts`. What that cannot catch
 * is the half of this fix that matters most: a note that is computed correctly and never rendered
 * would leave the screen exactly as misleading as it was for 27 hours. So this drives the real
 * component and reads the real DOM.
 */
describe('Explorer, on a volume that has stopped ticking', () => {
  let fixture: ComponentFixture<Explorer>;
  let shell: Shell;

  function status(volumes: VolumeScanStatus[]): ScanStatus {
    return {
      state: 'deferred',
      activity: null,
      queued: [],
      lastTickUtc: null,
      nextTickUtc: null,
      deferral: '',
      volumes,
    };
  }

  function scanner(overrides: Partial<VolumeScanStatus> = {}): VolumeScanStatus {
    return {
      volume: 'C: (OS-M2-1TB)',
      key: 'C:',
      lastTickUtc: new Date().toISOString(),
      nextTickUtc: null,
      deferral: '',
      needsBaseline: false,
      catchingUp: false,
      consecutiveDeferrals: 0,
      ...overrides,
    };
  }

  /** The state the machine was actually found in on 19 Aug, less the wall-clock drift. */
  function stuck(): VolumeScanStatus {
    return scanner({
      lastTickUtc: new Date(Date.now() - 27 * 60 * 60 * 1000).toISOString(),
      deferral: 'journal reset or cursor trimmed; a full baseline is needed',
      needsBaseline: true,
      consecutiveDeferrals: 101,
    });
  }

  function note(): HTMLElement | null {
    return fixture.nativeElement.querySelector('.stale-note');
  }

  function render(volumes: VolumeScanStatus[]): void {
    shell.status.set(status(volumes));
    shell.selected.set('C:');
    fixture.detectChanges();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Explorer],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();

    shell = TestBed.inject(Shell);
    fixture = TestBed.createComponent(Explorer);
  });

  it('says nothing while the scanner is keeping up', () => {
    render([scanner()]);

    expect(note()).toBeNull();
  });

  it('dates the reading, and names why it has not moved', () => {
    render([stuck()]);

    const text = note()?.innerText.replace(/\s+/g, ' ') ?? '';

    expect(text).toContain('not where they are now');
    expect(text).toContain('journal reset or cursor trimmed');

    // The timestamp itself, which is the whole point: a reading stated as of when it was true.
    expect(text).toMatch(/\d{4}|\d{1,2}:\d{2}/);
  });

  it('says a full scan is coming when nothing else will close the gap', () => {
    render([stuck()]);

    expect(note()?.innerText).toContain('A full scan is the only way to close the gap');
    expect(note()?.classList).toContain('needs-baseline');
  });

  it('does not claim a baseline is coming when the volume is merely behind', () => {
    render([
      scanner({
        lastTickUtc: new Date(Date.now() - 3 * 60 * 60 * 1000).toISOString(),
        deferral: 'deferred: Fullscreen (borderless fullscreen)',
      }),
    ]);

    const text = note()?.innerText ?? '';

    expect(text).toContain('borderless fullscreen');
    expect(text).not.toContain('A full scan is the only way');
    expect(note()?.classList).not.toContain('needs-baseline');
  });

  it('says nothing about a volume the scanner is not reporting on', () => {
    // A detached volume, or one from a build that did not send per-volume rows. Silence is right:
    // there is no age to state, and inventing one would be the original bug in reverse.
    render([scanner({ key: 'D:' })]);

    expect(note()).toBeNull();
  });
});
