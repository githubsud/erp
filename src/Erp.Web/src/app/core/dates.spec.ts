import { blankToNull, daysBetween, displayDate, monthEndIso, monthStartIso, toIso } from './dates';

describe('dates', () => {
  it('formats local dates without time-zone shifts', () => {
    expect(toIso(new Date(2026, 9, 31, 23, 59))).toBe('2026-10-31');
    expect(toIso(new Date(2026, 0, 1, 0, 0))).toBe('2026-01-01');
  });

  it('computes month boundaries including leap years', () => {
    expect(monthStartIso(2026, 2)).toBe('2026-02-01');
    expect(monthEndIso(2026, 2)).toBe('2026-02-28');
    expect(monthEndIso(2028, 2)).toBe('2028-02-29');
    expect(monthEndIso(2026, 12)).toBe('2026-12-31');
  });

  it('counts days between ISO dates', () => {
    expect(daysBetween('2026-10-17', '2026-11-08')).toBe(22);
    expect(daysBetween('2026-11-08', '2026-10-17')).toBe(-22);
  });

  it('displays dd/MM/yyyy as on Qatari documents', () => {
    expect(displayDate('2026-03-01')).toBe('01/03/2026');
    expect(displayDate(null)).toBe('');
  });

  it('turns blank form values into nulls', () => {
    expect(blankToNull({ a: '', b: 'x', c: 0 })).toEqual({ a: null, b: 'x', c: 0 });
  });
});
