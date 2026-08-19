import { Pipe, PipeTransform } from '@angular/core';

import * as fmt from '../core/format';

/**
 * The formatting rules as pipes.
 *
 * Pure, so they run once per changed value rather than once per change-detection pass — which
 * matters on the events feed, where a few hundred rows each format four numbers.
 */

@Pipe({ name: 'bytes', standalone: true, pure: true })
export class BytesPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return value === null || value === undefined ? '—' : fmt.bytes(value);
  }
}

@Pipe({ name: 'deltaBytes', standalone: true, pure: true })
export class DeltaBytesPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return value === null || value === undefined ? '—' : fmt.deltaBytes(value);
  }
}

@Pipe({ name: 'exactBytes', standalone: true, pure: true })
export class ExactBytesPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return value === null || value === undefined ? '' : fmt.exactBytes(value);
  }
}

@Pipe({ name: 'grouped', standalone: true, pure: true })
export class GroupedPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return value === null || value === undefined ? '—' : fmt.count(value);
  }
}

@Pipe({ name: 'deltaFiles', standalone: true, pure: true })
export class DeltaFilesPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return value === null || value === undefined ? '—' : fmt.deltaFiles(value);
  }
}

@Pipe({ name: 'clock', standalone: true, pure: true })
export class ClockPipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return value ? fmt.clock(value) : '—';
  }
}

@Pipe({ name: 'stamp', standalone: true, pure: true })
export class StampPipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return value ? fmt.stamp(value) : '—';
  }
}

@Pipe({ name: 'feedStamp', standalone: true, pure: true })
export class FeedStampPipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return value ? fmt.feedStamp(value) : '—';
  }
}

/** The hover every absolute timestamp carries: full local form plus the UTC offset. */
@Pipe({ name: 'stampZone', standalone: true, pure: true })
export class StampZonePipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return value ? fmt.stampWithZone(value) : '';
  }
}

@Pipe({ name: 'duration', standalone: true, pure: true })
export class DurationPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return value === null || value === undefined ? '—' : fmt.duration(value);
  }
}

@Pipe({ name: 'ago', standalone: true, pure: true })
export class AgoPipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return value ? fmt.ago(value) : '—';
  }
}

@Pipe({ name: 'percent1', standalone: true, pure: true })
export class Percent1Pipe implements PipeTransform {
  transform(value: number | null | undefined, decimals = 1): string {
    return value === null || value === undefined ? '—' : fmt.percent(value, decimals);
  }
}

/** The class that colours a delta: growth amber, shrinkage cyan, no change neither. */
@Pipe({ name: 'deltaClass', standalone: true, pure: true })
export class DeltaClassPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    if (!value) {
      return 'dim';
    }

    return value > 0 ? 'pos' : 'neg';
  }
}

export const FORMAT_PIPES = [
  BytesPipe,
  DeltaBytesPipe,
  ExactBytesPipe,
  GroupedPipe,
  DeltaFilesPipe,
  ClockPipe,
  StampPipe,
  FeedStampPipe,
  StampZonePipe,
  DurationPipe,
  AgoPipe,
  Percent1Pipe,
  DeltaClassPipe,
] as const;
