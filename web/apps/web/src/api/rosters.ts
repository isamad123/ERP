// Hand-written client of the roster, holiday and effective-shift services (slices S1.28a and S1.28b; contract "HR shift and roster API").
// Reads of rosters, roster days and shifts are the generated lists; everything that changes a roster goes through these routes. The server
// checks every request again; the screens show its answers as confirmed, never assumed.
import { requestJson } from './client';

const enc = encodeURIComponent;

export interface RosterAssign { shiftId: string; employmentIds?: string[]; everyone?: boolean; from?: string; to?: string; replace?: boolean }
export interface DayChange { employmentId: string; workDate: string; dayType: 'working' | 'weekly_off' | 'holiday'; shiftId?: string; reason?: string }
export interface RosterMessage { code: string; severity: 'error' | 'warning'; employmentId: string | null; date: string | null; detail: string | null }
export interface RosterChecks { messages: RosterMessage[] }
export interface HijriDate { year: number; month: number; day: number }
export interface HolidayRow { id: string; calendarId: string; date: string; name: Record<string, string>; kind: string; isTentative: boolean; isOptional: boolean; hijri: HijriDate | null }
export interface HolidayList { companyId: string; calendarId: string | null; year: number; hijriOffsetDays: number; holidays: HolidayRow[] }
export interface ChoiceRow { holiday: HolidayRow; chosen: boolean; open: boolean }
export interface EmploymentHolidays { employmentId: string; calendarId: string | null; year: number; hijriOffsetDays: number; optionalLimit: number; optionalChosen: number; cutoffDays: number; holidays: ChoiceRow[] }

export const rosterApi = {
  create: (body: { companyId: string; name: string; orgUnitId?: string | null; periodStart: string; periodEnd: string }) => requestJson<{ id: string; rowVersion: number }>('POST', '/api/hrm/rosters', { body }),
  assign: (id: string, body: RosterAssign) => requestJson<{ people: number; created: number; kept: number }>('POST', `/api/hrm/rosters/${id}/assign`, { body }),
  copyForward: (id: string, body: { name?: string; periodStart?: string }) => requestJson<{ id: string }>('POST', `/api/hrm/rosters/${id}/copy-forward`, { body }),
  changeDays: (id: string, body: { changes: DayChange[]; reason?: string }) => requestJson<{ changed: number }>('PUT', `/api/hrm/rosters/${id}/days`, { body }),
  checks: (id: string, signal?: AbortSignal) => requestJson<RosterChecks>('GET', `/api/hrm/rosters/${id}/checks`, { signal }),
  publish: (id: string, rowVersion: number) => requestJson<{ status: string; people: number }>('POST', `/api/hrm/rosters/${id}/publish`, { body: { rowVersion } }),
};

export const holidayApi = {
  list: (companyId: string, year: number, calendarId?: string, signal?: AbortSignal) =>
    requestJson<HolidayList>('GET', `/api/hrm/holidays?companyId=${enc(companyId)}&year=${year}${calendarId ? `&calendarId=${enc(calendarId)}` : ''}`, { signal }),
  forEmployment: (employmentId: string, year: number, signal?: AbortSignal) =>
    requestJson<EmploymentHolidays>('GET', `/api/hrm/employments/${employmentId}/holidays?year=${year}`, { signal }),
  choose: (employmentId: string, year: number, holidayIds: string[]) =>
    requestJson<EmploymentHolidays>('PUT', `/api/hrm/employments/${employmentId}/optional-holidays`, { body: { year, holidayIds } }),
};
