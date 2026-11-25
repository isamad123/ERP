// Hand-written client of the leave services (slice S1.33a; FR-HRM-LVE-04, -05, -06, -16, GHR-09, FR-XM-32). The lists of leave types and requests are the generated
// reads (@erp/generated/api/hrm: leaveTypes, leaveRequests); everything that works out, saves or decides goes through these endpoints, which check the rules again.
// Texts in answers (`error`, `errors`, `reason`, `explanation`) are server keys, shown through `useText().server(key)`.
import { requestJson } from './client';

const enc = encodeURIComponent;

export type LeavePart = 'full' | 'first_half' | 'second_half';

/** The body of a preview, a request, a draft change or a change of approved leave (server `LeaveRequestInput`). */
export interface LeaveBody {
  employmentId?: string;
  leaveTypeId: string;
  startDate: string;
  endDate?: string;
  startPart?: LeavePart;
  endPart?: LeavePart;
  hours?: number;
  reason?: string;
  /** false keeps a draft (to attach a document first). */
  submit?: boolean;
  rowVersion?: number;
}

export interface PlanDay { date: string; dayType: string; quantity: number; kind: 'leave' | 'sandwich' | 'off' | string; part: LeavePart | string; reason: string }

export interface LeavePlan {
  leaveTypeId: string;
  code: string;
  unit: 'day' | 'half_day' | 'hour';
  counting: string;
  isPaid: boolean;
  days: PlanDay[] | null;
  total: number | null;
  leaveYear: number | null;
  balance: number | null;
  pending: number | null;
  after: number | null;
  lowest: number | null;
  negativeLimit: number | null;
}

export interface LeavePreview {
  employmentId: string;
  allowed: boolean;
  /** A server key for the one reason that refused the leave. */
  error: string | null;
  /** Field -> server keys. */
  errors: Record<string, string[]> | null;
  /** Values that explain the refusal (`date`, `lowest`, `allowed`, `yearEnd`, `leaveYear`, `reason`). */
  details: Record<string, unknown> | null;
  documentRequired: boolean;
  plan: LeavePlan;
}

export interface LeaveSaved { id: string; requestNo: string; status: string; employmentId: string; modifiesRequestId: string | null; workflowInstanceId: string | null; rowVersion: number; plan?: LeavePlan }

export interface LedgerEntry {
  id: string;
  leaveTypeId: string;
  leaveYear: number;
  entryDate: string;
  kind: string;
  quantity: number;
  expiresOn: string | null;
  sourceType: string | null;
  sourceId: string | null;
  reversesEntryId: string | null;
  reversedById: string | null;
  reason: string | null;
  period: string | null;
  /** A server key explaining the entry. */
  explanation: string;
  createdAt: string;
}

export interface BalanceLine {
  leaveTypeId: string;
  code: string;
  name: Record<string, string>;
  unit: 'day' | 'half_day' | 'hour';
  isActive: boolean;
  leaveYear: number;
  yearStart: string;
  yearEnd: string;
  balance: number;
  totals: Record<string, number>;
  negativeLimit: number;
  noPolicy: { reason: string; message: string } | null;
  entries: LedgerEntry[];
}

export interface MyBalances {
  employments: { employmentId: string; companyId: string; employeeNo: string }[];
  employmentId: string | null;
  companyId: string | null;
  employeeNo?: string;
  needsCompany?: boolean;
  asOf: string;
  balances: BalanceLine[];
}

export interface DetailDay { date: string; quantity: number; kind: string; isPaid: boolean; status: string }

export interface LeaveDetails {
  id: string;
  requestNo: string;
  companyId: string;
  employmentId: string;
  employeeNo: string;
  leaveTypeId: string;
  leaveTypeCode: string;
  unit: 'day' | 'half_day' | 'hour';
  counting: string;
  isPaid: boolean;
  status: 'draft' | 'submitted' | 'approved' | 'rejected' | 'cancel_requested' | 'cancelled';
  startDate: string;
  endDate: string;
  startPart: LeavePart;
  endPart: LeavePart;
  hours: number | null;
  days: number;
  reason: string | null;
  fileIds: string[];
  currentFileIds: string[];
  documentRequired: boolean;
  modifiesRequestId: string | null;
  changes: { id: string; requestNo: string; status: string }[];
  workflowInstanceId: string | null;
  /** The user who applied (an id; compare with the signed-in user). */
  appliedBy: string;
  onBehalf: boolean;
  /** For a waiting request: the balance with it counted. */
  impact: { leaveYear: number | null; balance: number | null; pending: number | null; after: number | null; lowest: number | null } | null;
  dayList: DetailDay[];
  entries: LedgerEntry[];
  rowVersion: number;
}

export interface CoveragePerson { employeeNo: string; firstName: string | null; lastName: string | null }
export interface CoverageDay { date: string; teamSize: number; away: number; percent: number; warn: boolean; people: CoveragePerson[] }
export interface Coverage { requestId: string; limitPercent: number; teamKnown: boolean; managerEmploymentId: string | null; teamSize: number; days: CoverageDay[]; warn: boolean }

export const leaveApi = {
  preview: (body: LeaveBody, signal?: AbortSignal) => requestJson<LeavePreview>('POST', '/api/hrm/leave-requests/preview', { body, signal }),
  create: (body: LeaveBody) => requestJson<LeaveSaved>('POST', '/api/hrm/leave-requests', { body }),
  updateDraft: (id: string, body: LeaveBody) => requestJson<LeaveSaved>('PUT', `/api/hrm/leave-requests/${enc(id)}/draft`, { body }),
  submit: (id: string, rowVersion: number) => requestJson<LeaveSaved>('POST', `/api/hrm/leave-requests/${enc(id)}/submit`, { body: { rowVersion } }),
  cancel: (id: string, reason: string, rowVersion: number) => requestJson<{ id: string; status: string; rowVersion: number }>('POST', `/api/hrm/leave-requests/${enc(id)}/cancel`, { body: { reason, rowVersion } }),
  change: (id: string, body: LeaveBody) => requestJson<LeaveSaved>('POST', `/api/hrm/leave-requests/${enc(id)}/change`, { body }),
  details: (id: string, signal?: AbortSignal) => requestJson<LeaveDetails>('GET', `/api/hrm/leave-requests/${enc(id)}/details`, { signal }),
  coverage: (id: string, signal?: AbortSignal) => requestJson<Coverage>('GET', `/api/hrm/leave-requests/${enc(id)}/coverage`, { signal }),
  myBalances: (companyId?: string, signal?: AbortSignal) => requestJson<MyBalances>('GET', `/api/hrm/my/leave-balances${companyId ? `?companyId=${enc(companyId)}` : ''}`, { signal }),
  /** A waiting request is withdrawn through the shared approval endpoint. */
  withdraw: (instanceId: string, reason: string) => requestJson<unknown>('POST', `/api/wf/instances/${enc(instanceId)}/withdraw`, { body: { reason } }),
};
