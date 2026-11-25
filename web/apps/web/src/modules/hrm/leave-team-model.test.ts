// The pure helpers of the team leave calendar (slice S1.33a; FR-HRM-LVE-15).
import { describe, expect, it } from 'vitest';
import type { CalendarPerson } from '../../api/leave-calendar';
import { cellFor, colourOf, coverageData, coverageIndex, daysOfMonth, holidayIndex, monthOf, monthRange, moveFocus, shiftMonth, weekdayOf } from './leave-team-model';

const PERSON: CalendarPerson = {
  employmentId: 'e1', companyId: 'c1', employeeNo: 'E-1', firstName: 'Sara', lastName: 'Khan', unitId: null, calendarId: 'cal',
  leave: [
    { date: '2026-03-14', leaveTypeId: 't1', requestId: 'r1', quantity: 1, status: 'approved', kind: 'leave' },
    { date: '2026-03-15', leaveTypeId: 't1', requestId: 'r1', quantity: 0.5, status: 'submitted', kind: 'leave' },
    { date: '2026-03-23', leaveTypeId: 't1', requestId: 'r2', quantity: 1, status: 'approved', kind: 'sandwich' },
  ],
  offDays: [{ date: '2026-03-21', kind: 'weekly_off' }, { date: '2026-03-23', kind: 'holiday' }],
};
const HOLIDAYS = holidayIndex([{ calendarId: 'cal', items: [{ date: '2026-03-23', name: { en: 'Pakistan Day' }, kind: 'public' }] }]);

describe('FR-HRM-LVE-15 leave calendar model', () => {
  it('works out the days of a month and moves between months across a year', () => {
    expect(daysOfMonth({ year: 2026, month: 2 })).toHaveLength(28);
    expect(daysOfMonth({ year: 2028, month: 2 })).toHaveLength(29);
    expect(monthRange({ year: 2026, month: 11 })).toEqual({ from: '2026-11-01', to: '2026-11-30' });
    expect(shiftMonth(monthOf('2026-12-05'), 1)).toEqual({ year: 2027, month: 1 });
    expect(shiftMonth(monthOf('2026-01-05'), -1)).toEqual({ year: 2025, month: 12 });
    expect(weekdayOf('2026-11-25')).toBe(3);
  });

  it('FR-HRM-LVE-15 a missing or unknown colour token is neutral', () => {
    expect(colourOf('success')).toBe('success');
    expect(colourOf(null)).toBe('neutral');
    expect(colourOf('#ff0000')).toBe('neutral');
  });

  it('FR-HRM-LVE-15 classifies a cell: approved, waiting half day, sandwich, weekly off, holiday, nothing', () => {
    const approved = cellFor(PERSON, '2026-03-14', HOLIDAYS);
    expect(approved.kind).toBe('leave');
    expect(approved.leaves[0]).toMatchObject({ half: false, waiting: false, sandwich: false });
    expect(cellFor(PERSON, '2026-03-15', HOLIDAYS).leaves[0]).toMatchObject({ half: true, waiting: true });
    expect(cellFor(PERSON, '2026-03-23', HOLIDAYS).leaves[0]).toMatchObject({ sandwich: true });
    expect(cellFor(PERSON, '2026-03-21', HOLIDAYS).kind).toBe('off');
    expect(cellFor({ ...PERSON, leave: [], offDays: [] }, '2026-03-23', HOLIDAYS).kind).toBe('holiday');
    expect(cellFor(PERSON, '2026-03-16', HOLIDAYS).kind).toBe('none');
  });

  it('FR-HRM-LVE-05 coverage data comes from the row of the person\'s company and day, and an empty team has none', () => {
    const index = coverageIndex([{ companyId: 'c1', date: '2026-03-14', teamSize: 8, away: 3, percent: 37.5, warn: true }, { companyId: 'c1', date: '2026-03-15', teamSize: 0, away: 0, percent: 0, warn: false }]);
    expect(coverageData(index.get('c1|2026-03-14'))).toEqual({ away: 3, teamSize: 8, percent: 37.5, warn: true });
    expect(coverageData(index.get('c1|2026-03-15'))).toBeNull();
    expect(coverageData(index.get('c2|2026-03-14'))).toBeNull();
  });

  it('moves the focus by arrows, Home and End and stays inside the grid', () => {
    expect(moveFocus({ r: 0, c: 0 }, 'left', 3, 5)).toEqual({ r: 0, c: 0 });
    expect(moveFocus({ r: 0, c: 0 }, 'down', 3, 5)).toEqual({ r: 1, c: 0 });
    expect(moveFocus({ r: 2, c: 4 }, 'right', 3, 5)).toEqual({ r: 2, c: 4 });
    expect(moveFocus({ r: 1, c: 2 }, 'end', 3, 5)).toEqual({ r: 1, c: 4 });
    expect(moveFocus({ r: 1, c: 2 }, 'home', 3, 5)).toEqual({ r: 1, c: 0 });
    expect(moveFocus({ r: 0, c: 1 }, 'up', 3, 5)).toEqual({ r: 0, c: 1 });
  });
});
