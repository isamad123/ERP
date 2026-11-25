// Hand-written client of the team leave calendar (slice S1.33a Build item 4; FR-HRM-LVE-15, LVE-05). The server limits the people to the data scope of the caller's
// leave-request read grant, answers at most 62 days, and pages the people (limit default 50, most 200).
import { requestJson } from './client';

export type Names = Record<string, string>;
export type LeaveDayStatus = 'submitted' | 'approved' | 'cancel_requested';
export type LeaveDayKind = 'leave' | 'sandwich';

export interface CalendarCompany { companyId: string; today: string; limitPercent: number }
export interface CalendarType { id: string; code: string; nameI18n: Names; colorToken: string | null; unit: string }
export interface CalendarLeaveDay { date: string; leaveTypeId: string; requestId: string; quantity: number; status: LeaveDayStatus; kind: LeaveDayKind }
export interface CalendarOffDay { date: string; kind: string }
export interface CalendarPerson {
  employmentId: string; companyId: string; employeeNo: string; firstName: string | null; lastName: string | null; unitId: string | null; calendarId: string | null;
  leave: CalendarLeaveDay[]; offDays: CalendarOffDay[];
}
export interface CalendarHoliday { calendarId: string; items: { date: string; name: Names; kind: string }[] }
export interface CalendarCoverage { companyId: string; date: string; teamSize: number; away: number; percent: number; warn: boolean }
export interface LeaveCalendar {
  from: string; to: string; companies: CalendarCompany[]; types: CalendarType[]; total: number; more: boolean;
  people: CalendarPerson[]; holidays: CalendarHoliday[]; coverage: CalendarCoverage[];
}
export interface LeaveCalendarFilter {
  companyId?: string | undefined; from: string; to: string; unitId?: string | undefined; leaveTypeId?: string | undefined; limit?: number | undefined; offset?: number | undefined;
}
export interface LeaveTypeChoice { id: string; code: string; nameI18n: Names; colorToken?: string | null }

const query = (values: Record<string, string | number | undefined | null>) => {
  const q = Object.entries(values).filter(([, v]) => v !== undefined && v !== null && v !== '').map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`).join('&');
  return q ? `?${q}` : '';
};

export const leaveCalendarApi = {
  calendar: (filter: LeaveCalendarFilter, signal?: AbortSignal) => requestJson<LeaveCalendar>('GET', `/api/hrm/leave-calendar${query({ ...filter })}`, { signal }),
  /** The leave types of the customer (names for the filter; the calendar answer carries only the types used in the range). */
  leaveTypes: (signal?: AbortSignal) => requestJson<{ items: LeaveTypeChoice[] }>('GET', '/api/hrm/leave-types?limit=200', { signal }),
  units: (signal?: AbortSignal) => requestJson<{ items: { id: string; code: string; nameI18n: Names }[] }>('GET', '/api/org/org-units?limit=200', { signal }),
};
