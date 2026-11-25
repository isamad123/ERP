// Hand-written client of the live attendance dashboard and the attendance reports (slice S1.30b; FR-HRM-ATT-18, ATT-19, SSP-02, RPT-05).
// The server limits every answer to the caller's data scope and returns ids for units and locations; the screens resolve their names.
import { requestJson } from './client';

export interface Counts {
  headcount: number; scheduled: number; present: number; late: number; absent: number; onLeave: number; onDuty: number;
  notYetPunched: number; notDue: number; notScheduled: number; noPolicy: number;
}
export interface Dashboard {
  asOf: string;
  refreshSeconds: number;
  companies: { companyId: string; date: string }[];
  totals: Counts;
  byUnit: { unitId: string; counts: Counts }[];
  byLocation: { locationId: string; counts: Counts }[];
}
export interface DashboardFilter { companyId?: string | undefined; unitId?: string | undefined; locationId?: string | undefined }

export const PEOPLE_STATUSES = ['present', 'late', 'absent', 'on_leave', 'on_duty', 'not_yet_punched', 'not_due', 'not_scheduled', 'no_policy'] as const;
export type PeopleStatus = (typeof PEOPLE_STATUSES)[number];
export interface PersonToday {
  employmentId: string; companyId: string; employeeNo: string; firstName: string; lastName: string; unitId: string | null; branchId: string | null;
  locationId: string | null; status: string; shiftStart: string | null; shiftEnd: string | null; firstPunch: string | null;
}
export interface PeoplePage { status: string; total: number; more: boolean; items: PersonToday[] }

export interface ReportInfo { code: string; exportType: string; columns: string[] }
export interface ReportFilter {
  companyId?: string | undefined; from?: string | undefined; to?: string | undefined; unitId?: string | undefined; branchId?: string | undefined; locationId?: string | undefined;
  employmentId?: string | undefined; search?: string | undefined; status?: string | undefined; kind?: string | undefined; minAgeDays?: string | undefined;
}
export type ReportRow = Record<string, string | number | boolean | null>;
export interface ReportPage { code: string; from: string | null; to: string | null; items: ReportRow[]; more: boolean; next: number | null; total: number | null; summary: Record<string, number> }

export const REPORT_CODES = ['muster_daily', 'muster_monthly', 'absenteeism', 'late_early', 'punch_log', 'device_sync', 'exception_ageing'] as const;

const query = (values: Record<string, string | number | undefined | null>) => {
  const q = Object.entries(values).filter(([, v]) => v !== undefined && v !== null && v !== '').map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`).join('&');
  return q ? `?${q}` : '';
};

const BASE = '/api/hrm/attendance';

export const attendanceReportsApi = {
  dashboard: (filter: DashboardFilter, signal?: AbortSignal) => requestJson<Dashboard>('GET', `${BASE}/dashboard${query({ ...filter })}`, { signal }),
  people: (filter: DashboardFilter & { status: string; limit: number; offset: number }, signal?: AbortSignal) =>
    requestJson<PeoplePage>('GET', `${BASE}/dashboard/people${query({ ...filter })}`, { signal }),
  reports: (signal?: AbortSignal) => requestJson<{ reports: ReportInfo[] }>('GET', `${BASE}/reports`, { signal }),
  report: (code: string, filter: ReportFilter, paging: { limit: number; offset: number }, signal?: AbortSignal) =>
    requestJson<ReportPage>('GET', `${BASE}/reports/${encodeURIComponent(code)}${query({ ...filter, ...paging })}`, { signal }),
  /** The units of the organization (names only; the dashboard answers carry ids). */
  units: (signal?: AbortSignal) => requestJson<{ items: { id: string; code: string; nameI18n: Record<string, string> }[] }>('GET', '/api/org/org-units?limit=200', { signal }),
};
