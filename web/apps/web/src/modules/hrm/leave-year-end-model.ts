// Pure rules of the year-end screen (slice S1.33b; FR-HRM-LVE-11): the preview lines of a run as the screen shows them (filter by type, status and employee number, totals per
// type worked out from the lines), and what each status means. No leave rule is decided here: the lines come from the job, and confirming checks them again on the server.
import { toUnits, unitsToText } from './leave-ledger-model';
import type { YearEndLine } from '../../api/leave-ledger';

export interface LineFilter { search: string; leaveTypeId: string; status: string }
export const NO_LINE_FILTER: LineFilter = { search: '', leaveTypeId: '', status: '' };

export function filterLines(lines: readonly YearEndLine[], f: LineFilter): YearEndLine[] {
  const search = f.search.trim().toLowerCase();
  return lines.filter((l) => (!search || l.employeeNo.toLowerCase().includes(search)) && (!f.leaveTypeId || l.leaveTypeId === f.leaveTypeId) && (!f.status || l.status === f.status));
}

export interface TypeTotals { leaveTypeId: string; code: string; unit: string; lines: number; closing: string; carryForward: string; encash: string; lapse: string }

/** Carry-forward includes the lots carried with their own expiry (as the job's totals do); only lines that `ok` count, the others change nothing. */
export const carriedOf = (l: YearEndLine): number => toUnits(l.carryForward) + (l.lots ?? []).reduce((s, x) => s + toUnits(x.quantity), 0);

export function totalsByType(lines: readonly YearEndLine[]): TypeTotals[] {
  const map = new Map<string, { code: string; unit: string; lines: number; closing: number; carry: number; encash: number; lapse: number }>();
  for (const l of lines) {
    const row = map.get(l.leaveTypeId) ?? { code: l.code, unit: l.unit, lines: 0, closing: 0, carry: 0, encash: 0, lapse: 0 };
    row.lines += 1;
    row.closing += toUnits(l.closing);
    if (l.status === 'ok') { row.carry += carriedOf(l); row.encash += toUnits(l.encash); row.lapse += toUnits(l.lapse); }
    map.set(l.leaveTypeId, row);
  }
  return [...map.entries()].map(([leaveTypeId, r]) => ({ leaveTypeId, code: r.code, unit: r.unit, lines: r.lines, closing: unitsToText(r.closing), carryForward: unitsToText(r.carry), encash: unitsToText(r.encash), lapse: unitsToText(r.lapse) }))
    .sort((a, b) => a.code.localeCompare(b.code));
}
