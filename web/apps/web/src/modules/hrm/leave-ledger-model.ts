// Pure rules of the leave ledger screen (slice S1.33b; FR-HRM-LVE-10, -14, FR-XM-05, -06): the running balance worked out from the entries (exact: quantities have at most four decimal
// places, so they are summed as whole units of 1/10000), the check against the balance the server returns, which entries may be reversed, where an entry's source points, the filters,
// and the adjustment a recompute difference suggests. Nothing here decides a leave rule; it only shows what the server answered.
import type { LedgerEntry } from '../../api/leave';
import { REVERSIBLE_KINDS, type RecomputeDifference } from '../../api/leave-ledger';

const SCALE = 10000;

export const toUnits = (value: number | string): number => Math.round(Number(value) * SCALE);

/** Whole units back to an exact decimal text without float artefacts ("7.5", "-0.25", "12"). */
export function unitsToText(units: number): string {
  const sign = units < 0 ? '-' : '';
  const abs = Math.abs(units);
  const integer = Math.floor(abs / SCALE);
  const fraction = String(abs % SCALE).padStart(4, '0').replace(/0+$/, '');
  return `${sign}${integer}${fraction ? `.${fraction}` : ''}`;
}

export const decimalText = (value: number | string): string => unitsToText(toUnits(value));

/** The server's order: date, then when it was made, then id. */
export function sortEntries<T extends Pick<LedgerEntry, 'entryDate' | 'createdAt' | 'id'>>(entries: readonly T[]): T[] {
  return [...entries].sort((a, b) => a.entryDate.localeCompare(b.entryDate) || a.createdAt.localeCompare(b.createdAt) || a.id.localeCompare(b.id));
}

export interface RunningRow { entry: LedgerEntry; running: string }

/** Each entry with the balance after it (one leave type and leave year). */
export function withRunning(entries: readonly LedgerEntry[]): RunningRow[] {
  let sum = 0;
  return sortEntries(entries).map((entry) => {
    sum += toUnits(entry.quantity);
    return { entry, running: unitsToText(sum) };
  });
}

export interface BalanceCheck { computed: string; server: string; matches: boolean }

/** The balance worked out in the screen against the one the server returned: any difference is shown as a warning, never hidden. */
export function checkBalance(entries: readonly LedgerEntry[], serverBalance: number): BalanceCheck {
  const computed = entries.reduce((sum, e) => sum + toUnits(e.quantity), 0);
  const server = toUnits(serverBalance);
  return { computed: unitsToText(computed), server: unitsToText(server), matches: computed === server };
}

/** Only an entry that is not itself a reversal, not already reversed, and of a kind a person may correct, offers "Reverse" (the server checks again). */
export function isReversible(entry: Pick<LedgerEntry, 'kind' | 'reversesEntryId' | 'reversedById'>): boolean {
  return entry.reversesEntryId === null && entry.reversedById === null && REVERSIBLE_KINDS.includes(entry.kind);
}

export type SourceLink = { type: 'request'; href: string } | { type: 'text'; key: 'manual' | 'accrual' | 'reversal' | 'settlement' | 'yearEnd' | 'compOff' | 'transfer' | 'penalty' | 'request' | 'other' };

/** Leave requests (`hrm.leave_request`) open the request; every other source is named in words. */
export function sourceOf(entry: Pick<LedgerEntry, 'sourceType' | 'sourceId'>): SourceLink {
  const type = entry.sourceType ?? '';
  if ((type === 'hrm.leave_request' || type === 'hrm.leave_requests') && entry.sourceId) return { type: 'request', href: `/time/leave/requests/${encodeURIComponent(entry.sourceId)}` };
  if (type === 'hrm.leave_request' || type === 'hrm.leave_requests') return { type: 'text', key: 'request' };
  if (type === 'hrm.leave_manual') return { type: 'text', key: 'manual' };
  if (type === 'hrm.leave_accrual') return { type: 'text', key: 'accrual' };
  if (type === 'hrm.leave_reversal') return { type: 'text', key: 'reversal' };
  if (type.startsWith('hrm.leave_settlement')) return { type: 'text', key: 'settlement' };
  if (type === 'hrm.leave_year_end') return { type: 'text', key: 'yearEnd' };
  if (type === 'hrm.attendance.comp_off') return { type: 'text', key: 'compOff' };
  if (type === 'hrm.leave_transfer') return { type: 'text', key: 'transfer' };
  if (type === 'hrm.attendance_penalty') return { type: 'text', key: 'penalty' };
  return { type: 'text', key: 'other' };
}

export interface LedgerFilter { kind: string; from: string; to: string }
export const NO_FILTER: LedgerFilter = { kind: '', from: '', to: '' };

/** Filters hide rows only; the running balance of the rows left is still the one after every earlier entry. */
export function matches(entry: Pick<LedgerEntry, 'kind' | 'entryDate'>, f: LedgerFilter): boolean {
  return (!f.kind || entry.kind === f.kind) && (!f.from || entry.entryDate >= f.from) && (!f.to || entry.entryDate <= f.to);
}

/** The adjustment a recompute difference suggests: the signed difference (what the policy calls for less what is posted), and the period it settles. Null when nothing can be suggested. */
export function adjustmentFor(d: Pick<RecomputeDifference, 'kind' | 'expected' | 'posted' | 'period' | 'date' | 'leaveTypeId'>): { leaveTypeId: string; quantity: string; settlesPeriod: string | null; entryDate: string | null } | null {
  if (d.kind === 'year_moved') return null;
  if (d.kind !== 'below_allowed' && !d.period) return null;
  const quantity = toUnits(d.expected ?? 0) - toUnits(d.posted ?? 0);
  if (quantity === 0) return null;
  return { leaveTypeId: d.leaveTypeId, quantity: unitsToText(quantity), settlesPeriod: d.kind === 'below_allowed' ? null : d.period, entryDate: d.date };
}

/** A quantity typed by a person: a decimal with at most four places, not zero. */
export function parseQuantity(text: string): number | null {
  const trimmed = text.trim().replace(/^\+/, '');
  if (!/^-?\d{1,4}(\.\d{1,4})?$/.test(trimmed) && !/^-?\.\d{1,4}$/.test(trimmed)) return null;
  const n = Number(trimmed);
  return n === 0 || Math.abs(n) > 9999 ? null : n;
}
