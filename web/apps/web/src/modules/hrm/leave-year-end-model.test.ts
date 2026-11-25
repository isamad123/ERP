// Pure rules of the year-end preview (slice S1.33b; FR-HRM-LVE-11): filters and totals per type, exact sums.
import { describe, expect, it } from 'vitest';
import type { YearEndLine } from '../../api/leave-ledger';
import { NO_LINE_FILTER, carriedOf, filterLines, totalsByType } from './leave-year-end-model';

const line = (employeeNo: string, leaveTypeId: string, extra: Partial<YearEndLine> = {}): YearEndLine => ({
  employmentId: `e-${employeeNo}`, employeeNo, leaveTypeId, code: leaveTypeId === 't1' ? 'AL' : 'SL', unit: 'day', yearStart: '2026-01-01', yearEnd: '2026-12-31', closing: 10, carryForward: 5, encash: 2, lapse: 3,
  carryExpiresOn: null, status: 'ok', lots: null, ...extra,
});

describe('FR-HRM-LVE-11 year-end preview rules', () => {
  const lines = [line('E-1', 't1'), line('E-2', 't1', { closing: 0.3, carryForward: 0.1, encash: 0.1, lapse: 0.1 }), line('E-12', 't2', { status: 'no_policy', carryForward: 0, encash: 0, lapse: 0 }), line('E-3', 't2', { status: 'separated', closing: 4, carryForward: 0, encash: 0, lapse: 0 })];
  it('FR-HRM-LVE-11 filters by part of the employee number, type and status', () => {
    expect(filterLines(lines, { ...NO_LINE_FILTER, search: 'e-1' }).map((l) => l.employeeNo)).toEqual(['E-1', 'E-12']);
    expect(filterLines(lines, { ...NO_LINE_FILTER, leaveTypeId: 't2' })).toHaveLength(2);
    expect(filterLines(lines, { ...NO_LINE_FILTER, status: 'separated' }).map((l) => l.employeeNo)).toEqual(['E-3']);
  });
  it('FR-HRM-LVE-11 totals per type count only the lines that will be posted, exactly', () => {
    const totals = totalsByType(lines);
    expect(totals.find((t) => t.code === 'AL')).toMatchObject({ lines: 2, closing: '10.3', carryForward: '5.1', encash: '2.1', lapse: '3.1' });
    expect(totals.find((t) => t.code === 'SL')).toMatchObject({ lines: 2, closing: '14', carryForward: '0', encash: '0', lapse: '0' });
  });
  it('carry-forward includes lots carried with their own expiry', () => {
    expect(carriedOf(line('E-9', 't1', { carryForward: 1, lots: [{ lotId: 'l', quantity: 0.5, expiresOn: '2027-03-31' }] }))).toBe(15000);
  });
});
