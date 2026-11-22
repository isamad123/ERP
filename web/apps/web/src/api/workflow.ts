// Hand-written clients of the approval inbox (slice S1.12d Build items 20 to 22): the workflow inbox, its counts, task
// details with the actions the engine allows, decisions, claim and release, bulk approval (contract "Workflow API") and
// the pending maker-checker requests with their decisions (contract "Access-control API", S1.06; list S1.12d). The
// server checks every request again; the screen shows its answers as confirmed, never assumed (spec 13 STA-06).
import { requestJson } from './client';

export type Names = Record<string, string>;

/** A name in the person's language, else English, else any. */
export function nameOf(names: Names | null | undefined, locale: string): string {
  if (!names) return '';
  return names[locale] ?? names.en ?? Object.values(names)[0] ?? '';
}

export interface InboxItem {
  taskId: string;
  instanceId: string;
  documentType: string;
  documentId: string;
  companyId: string;
  title: Names;
  link: string;
  stepKey: string;
  stepName: Names;
  requesterUserId: string;
  requesterName: string | null;
  receivedAt: string;
  dueAt: string | null;
  queue: boolean;
  roleId: string | null;
  waitingForInformation: boolean;
  module: string;
  actingForUserId: string | null;
  actingForName: string | null;
  overdue: boolean;
  escalationLevel: number;
  bulkApproval: boolean;
}

export interface InboxTypeCount {
  documentType: string;
  module: string;
  name: Names;
  count: number;
  overdue: number;
}

export interface InboxCountsFull {
  total: number;
  overdue: number;
  documentTypes: InboxTypeCount[];
  changeApprovals: number;
}

export interface InboxFilters {
  module?: string | undefined;
  documentType?: string | undefined;
  companyId?: string | undefined;
  requesterUserId?: string | undefined;
  step?: string | undefined;
  waiting?: boolean | undefined;
  overdue?: boolean | undefined;
  receivedFrom?: string | undefined;
  receivedTo?: string | undefined;
  sort?: 'received' | 'due' | undefined;
}

export type TaskAction = 'approve' | 'reject' | 'return' | 'ask' | 'reassign' | 'claim' | 'release';

export interface FieldValue {
  name: string;
  type: string;
  label: Names;
  value: unknown;
  displayName: string | null;
  displayNames: Names | null;
}

export interface HistoryEntry {
  id: string;
  taskId: string | null;
  event: string;
  actorId: string | null;
  actingForUserId: string | null;
  at: string;
  details: Record<string, unknown>;
  actorName: string | null;
  actingForName: string | null;
}

export interface TaskDetail {
  task: {
    id: string;
    stepKey: string;
    stepName: Names;
    status: string;
    dueAt: string | null;
    infoRequestedAt: string | null;
    claimedAt: string | null;
    escalationLevel: number;
  };
  request: {
    id: string;
    documentType: string;
    documentId: string;
    companyId: string;
    status: string;
    requesterUserId: string;
    startedAt: string;
    title: Names;
    link: string;
  };
  history: HistoryEntry[];
  context: {
    module: string;
    documentTypeName: Names;
    requesterName: string | null;
    actingForName: string | null;
    queue: boolean;
    bulkApproval: boolean;
    fields: FieldValue[];
    actions: TaskAction[];
    reasonRequired: TaskAction[];
  };
}

export interface DecisionResult {
  instanceId: string;
  status: string;
  openTaskIds: string[];
}

export interface BulkResult {
  done: number;
  refused: number;
  results: { taskId: string; outcome: 'approved' | 'refused'; status: string | null; error: string | null }[];
}

export interface ChangeDetail {
  label: string;
  type: 'text' | 'date' | 'names' | 'items' | 'key';
  text: string | null;
  date: string | null;
  names: Names | null;
  items: string[] | null;
}

export interface ChangeRequest {
  id: string;
  kind: string;
  requestedBy: string;
  requestedByName: string | null;
  requestedAt: string;
  expiresAt: string;
  reason: string | null;
  subject: Names | null;
  details: ChangeDetail[];
  canApprove: boolean;
  approveProblem: string | null;
}

export interface PendingChanges {
  toDecide: ChangeRequest[];
  mine: ChangeRequest[];
}

function query(values: Record<string, string | boolean | undefined>): string {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(values)) {
    if (value !== undefined && value !== '') params.set(name, String(value));
  }
  const text = params.toString();
  return text ? `?${text}` : '';
}

const id = (value: string) => encodeURIComponent(value);

export const workflow = {
  inbox: (filters: InboxFilters) => requestJson<InboxItem[]>('GET', `/api/wf/inbox${query({ ...filters })}`),
  counts: () => requestJson<InboxCountsFull>('GET', '/api/wf/inbox/counts'),
  task: (taskId: string) => requestJson<TaskDetail>('GET', `/api/wf/tasks/${id(taskId)}`),
  decide: (taskId: string, action: 'approve' | 'reject' | 'return', comment: string | null) =>
    requestJson<DecisionResult>('POST', `/api/wf/tasks/${id(taskId)}/${action}`, { body: { comment } }),
  ask: (taskId: string, question: string) => requestJson<DecisionResult>('POST', `/api/wf/tasks/${id(taskId)}/ask`, { body: { question } }),
  claim: (taskId: string) => requestJson<DecisionResult>('POST', `/api/wf/tasks/${id(taskId)}/claim`, { body: {} }),
  release: (taskId: string) => requestJson<DecisionResult>('POST', `/api/wf/tasks/${id(taskId)}/release`, { body: {} }),
  bulkApprove: (taskIds: string[], comment: string | null) => requestJson<BulkResult>('POST', '/api/wf/tasks/bulk-approve', { body: { taskIds, comment } }),
};

export const changes = {
  pending: () => requestJson<PendingChanges>('GET', '/api/iam/maker-checker-requests/pending'),
  approve: (requestId: string, reason: string | null) => requestJson<{ outcome: string }>('POST', `/api/iam/maker-checker-requests/${id(requestId)}/approve`, { body: { reason } }),
  reject: (requestId: string, reason: string) => requestJson<{ outcome: string }>('POST', `/api/iam/maker-checker-requests/${id(requestId)}/reject`, { body: { reason } }),
};
