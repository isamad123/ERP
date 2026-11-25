// Pure rules of the leave ledger screen (slice S1.33b): the running balance is exact, the check against the server balance, which entries may be reversed (FR-XM-05, FR-XM-06), the source
// link, the filters and the adjustment a recompute difference suggests (FR-HRM-LVE-10, FR-HRM-LVE-14).
import { describe, expect, it } from 'vitest';
import type { LedgerEntry } from '../../api/leave';
import { adjustmentFor, checkBalance, decimalText, isReversible, matches, parseQuantity, sortEntries, sourceOf, unitsToText, withRunning } from './leave-ledger-model';

const entry = (id: string, date: string, quantity: number, extra: Partial<LedgerEntry> = {}): LedgerEntry => ({
  id, leaveTypeId: 't', leaveYear: 2026, entryDate: date, kind: 'adjustment', quantity, expiresOn: null, sourceType: 'hrm.leave_manual', sourceId: null, reversesEntryId: null, reversedById: null,
  reason: null, period: null, explanation: 'hrm.leave.entry.adjustment', createdAt: `${date}T00:00:00Z`, ...extra,
});

describe('FR-HRM-LVE-10 running balance', () => {
  it('FR-HRM-LVE-10 adds exactly, without float artefacts, in date then creation order', () => {
    const rows = withRunning([entry('b', '2026-02-01', 0.2), entry('a', '2026-01-01', 0.1), entry('c', '2026-03-01', -0.3)]);
    expect(rows.map((r) => r.entry.id)).toEqual(['a', 'b', 'c']);
    expect(rows.map((r) => r.running)).toEqual(['0.1', '0.3', '0']);
  });
  it('FR-HRM-LVE-10 the last running balance equals the server balance, and a difference is reported', () => {
    const entries = [entry('a', '2026-01-01', 20), entry('b', '2026-03-02', -7.5)];
    expect(checkBalance(entries, 12.5)).toEqual({ computed: '12.5', server: '12.5', matches: true });
    expect(checkBalance(entries, 12)).toEqual({ computed: '12.5', server: '12', matches: false });
  });
  it('FR-HRM-LVE-10 same-day entries keep the order they were made in', () => {
    const sorted = sortEntries([entry('b', '2026-01-01', 1, { createdAt: '2026-01-01T10:00:00Z' }), entry('a', '2026-01-01', 1, { createdAt: '2026-01-01T09:00:00Z' })]);
    expect(sorted.map((e) => e.id)).toEqual(['a', 'b']);
  });
  it('writes exact decimal text', () => {
    expect(unitsToText(-2500)).toBe('-0.25');
    expect(decimalText(12)).toBe('12');
    expect(decimalText(0.30000000000000004)).toBe('0.3');
  });
});

describe('FR-XM-05 and FR-XM-06 correction by reversal', () => {
  it('FR-XM-06 offers a reversal only for an entry that is not a reversal, not already reversed and of a kind a person may correct', () => {
    expect(isReversible(entry('a', '2026-01-01', 1))).toBe(true);
    expect(isReversible(entry('a', '2026-01-01', 1, { reversedById: 'x' }))).toBe(false);
    expect(isReversible(entry('a', '2026-01-01', -1, { reversesEntryId: 'z' }))).toBe(false);
    expect(isReversible(entry('a', '2026-01-01', -1, { kind: 'usage' }))).toBe(false);
    expect(isReversible(entry('a', '2026-01-01', 1, { kind: 'carry_forward' }))).toBe(false);
  });
});

describe('source documents and filters', () => {
  it('FR-HRM-LVE-10 a leave request is a link, every other source is named in words', () => {
    expect(sourceOf({ sourceType: 'hrm.leave_request', sourceId: 'r1' })).toEqual({ type: 'request', href: '/time/leave/requests/r1' });
    expect(sourceOf({ sourceType: 'hrm.leave_year_end', sourceId: 's' })).toEqual({ type: 'text', key: 'yearEnd' });
    expect(sourceOf({ sourceType: 'hrm.leave_accrual', sourceId: null })).toEqual({ type: 'text', key: 'accrual' });
    expect(sourceOf({ sourceType: 'hrm.attendance.comp_off', sourceId: 'c' })).toEqual({ type: 'text', key: 'compOff' });
    expect(sourceOf({ sourceType: 'hrm.leave_transfer', sourceId: 't' })).toEqual({ type: 'text', key: 'transfer' });
    expect(sourceOf({ sourceType: 'hrm.leave_settlement:Y2026', sourceId: null })).toEqual({ type: 'text', key: 'settlement' });
    expect(sourceOf({ sourceType: null, sourceId: null })).toEqual({ type: 'text', key: 'other' });
  });
  it('filters by kind and dates', () => {
    const e = entry('a', '2026-03-02', 1, { kind: 'usage' });
    expect(matches(e, { kind: 'usage', from: '2026-03-01', to: '2026-03-31' })).toBe(true);
    expect(matches(e, { kind: 'grant', from: '', to: '' })).toBe(false);
    expect(matches(e, { kind: '', from: '2026-03-03', to: '' })).toBe(false);
    expect(matches(e, { kind: '', from: '', to: '2026-03-01' })).toBe(false);
  });
});

describe('FR-HRM-LVE-14 adjustment from a difference', () => {
  const base = { leaveTypeId: 't', date: '2026-03-31' };
  it('FR-HRM-LVE-14 is the signed difference between what the policy calls for and what is posted, settling that period', () => {
    expect(adjustmentFor({ ...base, kind: 'differs', expected: 1.75, posted: 1.5, period: 'M2026-03' })).toEqual({ leaveTypeId: 't', quantity: '0.25', settlesPeriod: 'M2026-03', entryDate: '2026-03-31' });
    expect(adjustmentFor({ ...base, kind: 'missing', expected: 20, posted: null, period: 'Y2026' })?.quantity).toBe('20');
    expect(adjustmentFor({ ...base, kind: 'unexpected', expected: null, posted: 2, period: 'Y2026' })?.quantity).toBe('-2');
  });
  it('FR-HRM-LVE-14 suggests nothing where the quantity is unknown or the difference is zero', () => {
    expect(adjustmentFor({ ...base, kind: 'year_moved', expected: null, posted: null, period: null })).toBeNull();
    expect(adjustmentFor({ ...base, kind: 'differs', expected: 1, posted: 1, period: 'Y2026' })).toBeNull();
    expect(adjustmentFor({ ...base, kind: 'differs', expected: 2, posted: 1, period: null })).toBeNull();
  });
  it('a below-the-limit difference has no period to settle', () => {
    expect(adjustmentFor({ ...base, kind: 'below_allowed', expected: -2, posted: -5, period: null })).toEqual({ leaveTypeId: 't', quantity: '3', settlesPeriod: null, entryDate: '2026-03-31' });
  });
  it('reads a typed quantity: at most four places, not zero, at most 9999', () => {
    expect(parseQuantity('-2.5')).toBe(-2.5);
    expect(parseQuantity('+3')).toBe(3);
    expect(parseQuantity('0')).toBeNull();
    expect(parseQuantity('1.23456')).toBeNull();
    expect(parseQuantity('10000')).toBeNull();
    expect(parseQuantity('abc')).toBeNull();
  });
});
