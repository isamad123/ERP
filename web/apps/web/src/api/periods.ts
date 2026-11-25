// Hand-written client of the attendance period lock (slice S1.31; contract "Attendance period lock API"): the lock console's pay periods with
// their attendance state, HR's lock request after the cut-off date, the request to cancel the locking run and reopen, the punches received after
// the lock, and retroactive adjustments with their impact. The server checks every request again (permissions, data scope, FR-XM-32); the
// screens show its answers as confirmed, never assumed (spec 13 STA-06). No pay amount is part of these answers (GHR-06).
import { requestJson } from './client';

const enc = encodeURIComponent;

export type LockState = 'open' | 'lock_requested' | 'locked' | 'reopen_requested';
export type AdjustmentKind = 'attendance' | 'leave_cancellation' | 'overtime';
export type AdjustmentStatus = 'submitted' | 'approved' | 'rejected' | 'withdrawn' | 'applied';
export const DAY_RESULTS = ['present', 'absent', 'late', 'half_day', 'on_leave', 'holiday', 'weekly_off', 'on_duty', 'incomplete'] as const;
export type DayResult = (typeof DAY_RESULTS)[number];

export interface LockPeriod {
  payPeriodId: string;
  companyId: string;
  payGroupId: string;
  payGroupCode: string | null;
  periodNo: number;
  taxYear: number;
  startDate: string;
  endDate: string;
  cutOffDate: string;
  payStatus: string;
  periodId: string | null;
  state: LockState;
  lockKind: 'payroll' | 'manual' | null;
  lockedAt: string | null;
  lockedByEmployeeNo: string | null;
  lockedByRunId: string | null;
  lockReason: string | null;
  lockRequestedByEmployeeNo: string | null;
  unprocessedAccepted: number | null;
  exceptionsAccepted: number | null;
  reopenReason: string | null;
  reopenedAt: string | null;
  adjustmentsWaiting: number;
  adjustmentsCarriedIn: number;
}

export interface LockPeriodDetail {
  period: LockPeriod;
  openItems: { unprocessedDays: number; openExceptions: number };
  canRequestLock: boolean;
  lockRefusal: string | null;
  canRequestReopen: boolean;
  reopenRefusal: string | null;
}

export interface LateEvent { id: string; employmentId: string; employeeNo: string | null; eventDate: string; eventAt: string; direction: string; source: string; receivedAt: string }

export interface DaySummary { result: DayResult | null; workedMinutes: number; lateMinutes: number; earlyMinutes: number; overtimeMinutes: number; unpaidLeave: number; paidLeave: number }

export interface AdjustedDayInput { date: string; result?: DayResult; workedMinutes?: number; lateMinutes?: number; earlyMinutes?: number; overtimeMinutes?: number }

export interface AdjustmentInput { employmentId: string; kind: AdjustmentKind; leaveRequestId?: string; days?: AdjustedDayInput[]; reason?: string }

export interface Impact {
  employmentId: string;
  employeeNo: string;
  kind: AdjustmentKind;
  fromDate: string | null;
  toDate: string | null;
  canSubmit: boolean;
  refusal: string | null;
  days: { date: string; locked: boolean; payPeriodId: string | null; old: DaySummary; new: DaySummary }[];
  leave: { requestNo: string; leaveTypeCode: string; unit: string; givenBack: number; balanceBefore: number; balanceAfter: number; yearClosed: boolean; entryDate: string | null } | null;
  overtime: { id: string; workDate: string; status: string; minutes: number; approvedMinutes: number | null }[];
  penalties: { id: string; workDate: string; kind: string; action: string; status: string }[];
  payPeriods: { payPeriodId: string; startDate: string; endDate: string; payStatus: string; role: 'locked' | 'target' }[];
  modules: string[];
}

export interface AdjustmentRow {
  id: string;
  employmentId: string;
  employeeNo: string | null;
  kind: AdjustmentKind;
  fromDate: string;
  toDate: string;
  reason: string;
  status: AdjustmentStatus;
  targetPayPeriodId: string;
  createdAt: string;
}

export interface AdjustmentDetail extends AdjustmentRow {
  targetPayPeriod: { id: string; startDate: string; endDate: string } | null;
  originalPayPeriod: { id: string; startDate: string; endDate: string } | null;
  days: { date: string; old: DaySummary; new: DaySummary | null }[];
  history: { status: AdjustmentStatus; at: string; comment?: string | null }[];
}

export const periodsApi = {
  list: (companyId: string | null) => requestJson<{ items: LockPeriod[] }>('GET', `/api/hrm/attendance/periods${companyId ? `?companyId=${enc(companyId)}` : ''}`),
  get: (payPeriodId: string) => requestJson<LockPeriodDetail>('GET', `/api/hrm/attendance/periods/${enc(payPeriodId)}`),
  requestLock: (payPeriodId: string, body: { reason: string; acceptOpenItems: boolean }) =>
    requestJson<{ instanceId: string }>('POST', `/api/hrm/attendance/periods/${enc(payPeriodId)}/lock-request`, { body }),
  requestReopen: (payPeriodId: string, body: { reason: string }) =>
    requestJson<{ instanceId: string }>('POST', `/api/hrm/attendance/periods/${enc(payPeriodId)}/reopen-request`, { body }),
  lateEvents: (payPeriodId: string) => requestJson<{ total: number; lockedAt: string | null; items: LateEvent[] }>('GET', `/api/hrm/attendance/periods/${enc(payPeriodId)}/late-events`),
  adjustments: (companyId: string | null) => requestJson<{ items: AdjustmentRow[] }>('GET', `/api/hrm/attendance/adjustments${companyId ? `?companyId=${enc(companyId)}` : ''}`),
  adjustment: (id: string) => requestJson<AdjustmentDetail>('GET', `/api/hrm/attendance/adjustments/${enc(id)}`),
  impact: (body: AdjustmentInput) => requestJson<Impact>('POST', '/api/hrm/attendance/adjustments/impact', { body }),
  submit: (body: AdjustmentInput) => requestJson<{ id: string; instanceId: string }>('POST', '/api/hrm/attendance/adjustments', { body }),
};
