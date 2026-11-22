// Hand-written clients of the user console (slice S1.13a; contracts "Identity API", "Access API", "Audit API",
// "Workflow API" and the new user views of CR-069). The server checks every request again; the screens show its answers
// as confirmed, never assumed (spec 13 STA-06).
import { requestJson } from './client';
import type { Names } from './workflow';

export const USER_STATUSES = ['invited', 'active', 'locked', 'disabled', 'archived'] as const;
export type UserStatus = (typeof USER_STATUSES)[number];

/** The transitions an administrator may make (FR-ADM-USR-03; mirrors the server's `UserStatuses.AdminMayChange`, which decides). */
const TRANSITIONS: Record<UserStatus, readonly UserStatus[]> = {
  invited: ['archived'],
  active: ['locked', 'disabled'],
  locked: ['active', 'disabled'],
  disabled: ['active', 'archived'],
  archived: [],
};

export const transitionsFrom = (status: string): readonly UserStatus[] => TRANSITIONS[status as UserStatus] ?? [];

export interface RoleChip { roleId: string; code: string; name: Names }

export interface UserRow {
  id: string;
  username: string;
  displayName: string;
  email: string;
  status: UserStatus;
  lastLoginAt: string | null;
  defaultCompanyId: string | null;
  accessValidTo: string | null;
  hasMfa: boolean;
  roles: RoleChip[];
  companyIds: string[];
  allCompanies: boolean;
}

export interface UserPage { items: UserRow[]; next: string | null }

export interface UserFilters {
  search?: string | undefined;
  status?: string | undefined;
  roleId?: string | undefined;
  companyId?: string | undefined;
  mfa?: boolean | undefined;
  cursor?: string | null | undefined;
  limit?: number | undefined;
}

export interface UserRecord {
  id: string;
  username: string;
  email: string;
  displayName: string;
  defaultCompanyId: string | null;
  language: string;
  timeZone: string;
  lastLoginAt: string | null;
  accessValidTo: string | null;
  status: UserStatus;
  isSystem: boolean;
  rowVersion: number;
}

export interface UserWrite {
  username: string;
  email?: string;
  displayName: string;
  defaultCompanyId: string | null;
  language: string;
  timeZone: string;
  accessValidTo: string | null;
  rowVersion?: number;
}

export interface RoleLine {
  assignmentId: string;
  roleId: string;
  roleCode: string;
  roleName: Names;
  companyId: string | null;
  validFrom: string;
  validTo: string | null;
  reason: string | null;
  inForce: boolean;
}

export interface GroupLine { groupId: string; code: string; name: Names; roles: RoleLine[] }
export interface ScopeLine { resource: string; dataScopeId: string; scopeCode: string; scopeKind: string; scopeName: Names; roleCode: string; source: string }
export interface DelegationLine { id: string; fromUserId: string; fromName: string; toUserId: string; toName: string; validFrom: string; validTo: string; status: string; reason: string | null }
export interface MfaLine { id: string; kind: string; label: string | null; status: string; createdAt: string; lastUsedAt: string | null }
export interface DeviceLine { id: string; label: string | null; createdAt: string; expiresAt: string; lastUsedAt: string | null }

export interface UserAccess {
  userId: string;
  roles: RoleLine[];
  groups: GroupLine[];
  scopes: ScopeLine[];
  delegations: DelegationLine[];
  mfaDevices: MfaLine[];
  trustedDevices: DeviceLine[];
}

export interface SessionLine { id: string; issuedAt: string; lastSeenAt: string; expiresAt: string; ipAddress: string | null; userAgent: string | null; isCurrent: boolean }
export interface Person { id: string; displayName: string; username: string }
export interface RoleOption { id: string; code: string; nameI18n: Names }
export interface OpenTask { taskId: string; instanceId: string; documentType: string; documentId: string; companyId: string; title: Names; stepKey: string; receivedAt: string; dueAt: string | null; overdue: boolean }
export interface TaskResult { taskId: string; outcome: string; status: string | null; error: string | null }
export interface ReassignResult { done: number; refused: number; results: TaskResult[] }
export interface AuditPage<T> { items: T[]; next: string | null }
export interface SecurityEvent { id: string; occurredAt: string; eventType: string; actor: string | null; subject: string | null; severity: string; details: Record<string, unknown> }
export interface ChangeEntry { id: string; occurredAt: string; actorUserId: string | null; actorName: string | null; action: string; entityType: string; source: string | null }
export interface LimitUse { key: string; max: number | null; used: number | null; percent: number | null }
export interface LicenceLimits { limits: LimitUse[] }
export interface StatusAnswer { openWorkflowTasks?: number }
/** The answer of a change that waits for a second administrator (202). */
export interface PendingAnswer { outcome: 'pending'; requestId: string }

function query(values: Record<string, string | number | boolean | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(values)) {
    if (value !== undefined && value !== null && value !== '') params.set(name, String(value));
  }
  const text = params.toString();
  return text ? `?${text}` : '';
}

const id = (value: string) => encodeURIComponent(value);
const base = (userId: string) => `/api/iam/users/${id(userId)}`;

export type AccountAction = 'unlock' | 'force-password-change' | 'reset-mfa' | 'revoke-sessions' | 'password-reset-link';

export const users = {
  list: (filters: UserFilters, signal?: AbortSignal) =>
    requestJson<UserPage>('GET', `/api/iam/user-overview${query({ ...filters })}`, signal ? { signal } : {}),
  get: (userId: string) => requestJson<UserRecord>('GET', base(userId)),
  access: (userId: string) => requestJson<UserAccess>('GET', `${base(userId)}/access`),
  create: (body: UserWrite) => requestJson<UserRecord>('POST', '/api/iam/users', { body }),
  update: (userId: string, body: UserWrite) => requestJson<UserRecord>('PUT', base(userId), { body }),
  invite: (userId: string) => requestJson<undefined>('POST', `${base(userId)}/invitation`, { body: {} }),
  setStatus: (userId: string, status: UserStatus, reason: string) => requestJson<StatusAnswer | undefined>('POST', `${base(userId)}/status`, { body: { status, reason } }),
  act: (userId: string, action: AccountAction, reason: string) => requestJson<undefined>('POST', `${base(userId)}/${action}`, { body: { reason } }),
  changeEmail: (userId: string, email: string, reason: string) => requestJson<PendingAnswer | undefined>('POST', `${base(userId)}/email`, { body: { email, reason } }),
  sessions: (userId: string) => requestJson<SessionLine[]>('GET', `${base(userId)}/sessions`),
  revokeSession: (userId: string, sessionId: string, reason: string) => requestJson<undefined>('POST', `${base(userId)}/sessions/${id(sessionId)}/revoke`, { body: { reason } }),
  removeDevice: (userId: string, deviceId: string, reason: string) => requestJson<undefined>('POST', `${base(userId)}/trusted-devices/${id(deviceId)}/remove`, { body: { reason } }),
  openTasks: (userId: string) => requestJson<OpenTask[]>('GET', `/api/wf/users/${id(userId)}/open-tasks`),
  reassignTasks: (userId: string, toUserId: string, reason: string) => requestJson<ReassignResult>('POST', `/api/wf/users/${id(userId)}/reassign-open-tasks`, { body: { toUserId, reason } }),
  assignRole: (body: { userId: string; roleId: string; companyId: string | null; validFrom: string | null; validTo: string | null; reason: string }) =>
    requestJson<{ outcome: string; id: string | null; requestId: string | null }>('POST', '/api/iam/user-roles', { body }),
  endRole: (assignmentId: string, endDate: string | null, reason: string) =>
    requestJson<{ outcome: string; id: string | null; requestId: string | null }>('POST', `/api/iam/user-roles/${id(assignmentId)}/end`, { body: { endDate, reason } }),
  roles: async () => (await requestJson<{ items: RoleOption[] }>('GET', '/api/iam/roles?limit=200')).items,
  people: (search: string, signal?: AbortSignal) => requestJson<Person[]>('GET', `/api/iam/people-lookup${query({ search })}`, signal ? { signal } : {}),
  licence: () => requestJson<LicenceLimits>('GET', '/api/cfg/licence'),
  signIns: (subject: string, limit = 20) => requestJson<AuditPage<SecurityEvent>>('GET', `/api/aud/security-events${query({ subject, limit })}`),
  activity: (userId: string, limit = 20) => requestJson<AuditPage<ChangeEntry>>('GET', `/api/aud/changes${query({ entityType: 'iam.users', entityId: userId, limit })}`),
};

/** The seat figures of the licence (named users), or null when the licence has none. */
export function seatsOf(licence: LicenceLimits | undefined): { used: number; max: number; percent: number } | null {
  const line = licence?.limits.find((l) => l.key === 'users.named');
  if (!line || line.max === null || line.used === null) return null;
  return { used: line.used, max: line.max, percent: line.percent ?? Math.floor((line.used * 100) / Math.max(line.max, 1)) };
}

/** True when the problem is the named-users limit of the licence (409 `errors.licence.limit_reached`, detail names the limit). */
export function isUserLimit(key: string, detail: string | null): boolean {
  return key === 'errors.licence.limit_reached' && (detail?.endsWith('users.named') ?? false);
}
