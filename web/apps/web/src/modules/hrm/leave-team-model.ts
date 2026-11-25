// Pure helpers of the team leave calendar (slice S1.33a; FR-HRM-LVE-15): the days of a month, what each cell shows, the coverage lookup and the keyboard moves.
// Dates are yyyy-mm-dd strings of the company's calendar; nothing here reads a clock.
import type { CalendarCoverage, CalendarHoliday, CalendarLeaveDay, CalendarPerson } from '../../api/leave-calendar';

export const COLOUR_TOKENS = ['primary', 'success', 'warning', 'danger', 'info', 'neutral'] as const;

/** The token a leave type is painted with; a missing or unknown one is neutral. */
export function colourOf(token: string | null | undefined): string {
  return (COLOUR_TOKENS as readonly string[]).includes(token ?? '') ? (token as string) : 'neutral';
}

export interface Month { year: number; month: number }

export const monthOf = (iso: string): Month => {
  const [y, m] = iso.split('-').map(Number) as [number, number];
  return { year: y, month: m };
};

export const shiftMonth = ({ year, month }: Month, by: number): Month => {
  const index = year * 12 + (month - 1) + by;
  return { year: Math.floor(index / 12), month: (index % 12) + 1 };
};

const two = (n: number) => String(n).padStart(2, '0');

export const monthRange = ({ year, month }: Month): { from: string; to: string } => {
  const last = new Date(Date.UTC(year, month, 0)).getUTCDate();
  return { from: `${year}-${two(month)}-01`, to: `${year}-${two(month)}-${two(last)}` };
};

/** Every day of the month as yyyy-mm-dd. */
export const daysOfMonth = (m: Month): string[] => {
  const { to } = monthRange(m);
  const last = Number(to.slice(8));
  return Array.from({ length: last }, (_, i) => `${m.year}-${two(m.month)}-${two(i + 1)}`);
};

/** 0 = Sunday. */
export const weekdayOf = (iso: string): number => new Date(`${iso}T12:00:00Z`).getUTCDay();

export interface DayLeave { day: CalendarLeaveDay; half: boolean; waiting: boolean; sandwich: boolean }
export type CellKind = 'leave' | 'holiday' | 'off' | 'none';
export interface Cell { kind: CellKind; leaves: DayLeave[]; offKind: string | null }

/** Holiday names of every calendar: `calendarId`, then `date`. */
export function holidayIndex(holidays: CalendarHoliday[]): Map<string, Map<string, Record<string, string>>> {
  return new Map(holidays.map((h) => [h.calendarId, new Map(h.items.map((i) => [i.date, i.name]))]));
}

/** What a person's day shows: leave first (a sandwich day is still shown as leave), else the holiday of the person's calendar, else a weekly off, else nothing. */
export function cellFor(person: CalendarPerson, date: string, holidays: Map<string, Map<string, Record<string, string>>>): Cell {
  const leaves = person.leave.filter((l) => l.date === date).map((day): DayLeave => ({ day, half: day.quantity > 0 && day.quantity < 1, waiting: day.status === 'submitted', sandwich: day.kind === 'sandwich' }));
  if (leaves.length > 0) return { kind: 'leave', leaves, offKind: null };
  if (person.calendarId && holidays.get(person.calendarId)?.has(date)) return { kind: 'holiday', leaves: [], offKind: 'holiday' };
  const off = person.offDays.find((o) => o.date === date);
  if (off) return { kind: off.kind === 'holiday' ? 'holiday' : 'off', leaves: [], offKind: off.kind };
  return { kind: 'none', leaves: [], offKind: null };
}

/** The coverage rows by `companyId|date`. */
export function coverageIndex(rows: CalendarCoverage[]): Map<string, CalendarCoverage> {
  return new Map(rows.map((r) => [`${r.companyId}|${r.date}`, r]));
}

export interface CoverageText { away: number; teamSize: number; percent: number; warn: boolean }
export const coverageData = (row: CalendarCoverage | undefined): CoverageText | null =>
  row && row.teamSize > 0 ? { away: row.away, teamSize: row.teamSize, percent: row.percent, warn: row.warn } : null;

export type Move = 'left' | 'right' | 'up' | 'down' | 'home' | 'end';

/** The next cell of the grid for an arrow, Home or End key; stays inside the grid. */
export function moveFocus(at: { r: number; c: number }, move: Move, rows: number, cols: number): { r: number; c: number } {
  const clamp = (n: number, max: number) => Math.max(0, Math.min(max - 1, n));
  switch (move) {
    case 'left': return { r: at.r, c: clamp(at.c - 1, cols) };
    case 'right': return { r: at.r, c: clamp(at.c + 1, cols) };
    case 'up': return { r: clamp(at.r - 1, rows), c: at.c };
    case 'down': return { r: clamp(at.r + 1, rows), c: at.c };
    case 'home': return { r: at.r, c: 0 };
    case 'end': return { r: at.r, c: cols - 1 };
  }
}
