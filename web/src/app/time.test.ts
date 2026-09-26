import { describe, expect, it } from 'vitest';
import { moscowTime } from './time';

describe('Moscow time', () => {
  it('shows a UTC instant in Moscow time with the label', () => {
    // 20:59 UTC is 23:59 in Moscow (UTC+3, no daylight saving)
    expect(moscowTime('2026-10-03T20:59:00Z', new Date('2026-09-26T12:00:00Z'))).toBe(
      '3 октября, 23:59 МСК',
    );
  });

  it('moves to the next Moscow day across midnight', () => {
    expect(moscowTime('2026-10-03T21:30:00Z', new Date('2026-09-26T12:00:00Z'))).toBe(
      '4 октября, 00:30 МСК',
    );
  });

  it('names the year only when it is not the current one', () => {
    expect(moscowTime('2027-01-05T09:00:00Z', new Date('2026-09-26T12:00:00Z'))).toBe(
      '5 января 2027 г., 12:00 МСК',
    );
  });
});
