// Hand-written client of the leave reports (slice S1.33b Build item 9; FR-HRM-LVE-10, FR-HRM-RPT-05, FR-HRM-GHR-06). The server limits every answer to the caller's data scope;
// ids of units and leave types are resolved by the screen. No answer carries pay or salary.
import { requestJson } from './client';

export interface LeaveReportInfo { code: string; exportType: string; columns: string[] }
export interface LeaveReportFilter {
  companyId?: string | undefined; unitId?: string | undefined; employmentId?: string | undefined; leaveTypeId?: string | undefined; leaveYear?: string | undefined;
  from?: string | undefined; to?: string | undefined; search?: string | undefined; status?: string | undefined;
}
export type LeaveReportRow = Record<string, string | number | boolean | null>;
export type LeaveReportGroup = Record<string, string | number | null>;
export interface LeaveReportPage {
  code: string; from: string | null; to: string | null; items: LeaveReportRow[]; more: boolean; next: number | null; total: number;
  summary: { groups: LeaveReportGroup[] };
}

export const LEAVE_REPORT_CODES = ['balance_summary', 'leave_taken', 'pending_requests'] as const;
export type LeaveReportCode = (typeof LEAVE_REPORT_CODES)[number];

const query = (values: Record<string, string | number | undefined | null>) => {
  const q = Object.entries(values).filter(([, v]) => v !== undefined && v !== null && v !== '').map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`).join('&');
  return q ? `?${q}` : '';
};

const BASE = '/api/hrm/leave/reports';

export const leaveReportsApi = {
  reports: (signal?: AbortSignal) => requestJson<{ reports: LeaveReportInfo[] }>('GET', BASE, { signal }),
  report: (code: string, filter: LeaveReportFilter, paging: { limit: number; offset: number }, signal?: AbortSignal) =>
    requestJson<LeaveReportPage>('GET', `${BASE}/${encodeURIComponent(code)}${query({ ...filter, ...paging })}`, { signal }),
};
