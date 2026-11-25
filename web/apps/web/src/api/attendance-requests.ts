// Hand-written client of the attendance request services (slice S1.30a; FR-HRM-ATT-11, ATT-14, GHR-09) and the exception queue. The lists of the
// requests themselves are the generated reads (@erp/generated/api/hrm); a request is decided and withdrawn through the shared approval endpoints.
import { requestJson } from './client';

export interface ExceptionRow {
  id: string; companyId: string; employmentId: string; employeeNo: string | null; workDate: string; kind: string; status: string; attendanceDayId: string | null; createdAt: string; ageDays: number; rowVersion: number;
}
export interface UnmappedItem { id: string; companyId: string; deviceId: string; deviceUserNo: string; punchedAt: string; direction: string; kind: 'unmapped' }
export interface ExceptionQueue { status: string; items: ExceptionRow[]; more: boolean; unmapped: { count: number; items: UnmappedItem[] } | null }
export interface ExceptionFilter { companyId?: string; status?: string; kind?: string; minAgeDays?: string; unitId?: string; from?: string; to?: string }

export interface RegularizationBody {
  employmentId?: string; exceptionId?: string; workDate?: string; requestedInTime?: string; requestedInDayOffset?: number; requestedOutTime?: string; requestedOutDayOffset?: number; reason: string;
}
export interface DutyBody { employmentId?: string; kind: string; startDate: string; endDate: string; startTime?: string; endTime?: string; purpose: string }
export interface Requested { id: string; employmentId: string; status: string; instanceId: string; onBehalf: boolean; rowVersion: number }

const query = (values: Record<string, string | undefined>) => {
  const q = Object.entries(values).filter(([, v]) => v !== undefined && v !== '').map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`).join('&');
  return q ? `?${q}` : '';
};

export const attendanceRequestsApi = {
  exceptions: (filter: ExceptionFilter, signal?: AbortSignal) => requestJson<ExceptionQueue>('GET', '/api/hrm/attendance/exceptions' + query({ ...filter, limit: '200' }), { signal }),
  regularize: (body: RegularizationBody) => requestJson<Requested>('POST', '/api/hrm/attendance/regularization-requests', { body }),
  duty: (body: DutyBody) => requestJson<Requested>('POST', '/api/hrm/attendance/duty-requests', { body }),
  withdraw: (instanceId: string, reason: string) => requestJson<unknown>('POST', `/api/wf/instances/${instanceId}/withdraw`, { body: { reason } }),
};
