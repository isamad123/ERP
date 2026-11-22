// Hand-written clients of the access console (slice S1.13b; contracts "Access API", "Identity API", "Delegation API",
// "Audit API" and the new views of CR-069). The server checks every request again; the screens show its answers as
// confirmed, never assumed (spec 13 STA-06).
import { requestJson, send, problemOf } from './client';
import type { Names } from './workflow';
import type { RoleLine, SecurityEvent } from './users';

export const ACCESS_LEVELS = ['hidden', 'masked', 'read_only', 'editable'] as const;
export type AccessLevel = (typeof ACCESS_LEVELS)[number];
export const SCOPE_KINDS = ['own', 'direct_team', 'subtree', 'org_units', 'branches', 'companies', 'all'] as const;
export type ScopeKind = (typeof SCOPE_KINDS)[number];

/** The answer of a change that may wait for a second administrator (200 or 201 `done`, 202 `pending`). */
export interface AccessAnswer { outcome: 'done' | 'pending'; id: string | null; requestId: string | null }

export interface CatalogEntry { code: string; module: string; resource: string; action: string; descriptionKey: string; isSensitive: boolean; roles: string[] }
export interface RoleRecord {
  id: string;
  code: string;
  nameI18n: Names;
  descriptionI18n: Names | null;
  source: string;
  baselineCode: string | null;
  isActive: boolean;
  rowVersion: number;
}
export interface RoleScopeView { resource: string; dataScopeId: string; code: string; kind: string }
export interface RoleFieldView { resource: string; field: string; access: AccessLevel }
export interface RoleSummary {
  id: string;
  code: string;
  name: Names;
  source: string;
  baselineCode: string | null;
  isActive: boolean;
  rowVersion: number;
  isPrivileged: boolean;
  permissions: string[];
  dataScopes: RoleScopeView[];
  fieldPolicies: RoleFieldView[];
  userCount: number;
}
export interface RoleComparison { left: string; right: string; onlyLeft: string[]; onlyRight: string[]; both: string[] }
export interface DataScopeRecord { id: string; code: string; nameI18n: Names; kind: ScopeKind; definition: Record<string, unknown>; isActive?: boolean }
export interface ScopePreview { resource: string; userId: string; count: number; firstPage: string[] }
export interface FieldPreviewItem { field: string; fieldClass: string | null; access: AccessLevel }
export interface FieldPreview { resource: string; roleId: string; fields: FieldPreviewItem[] }
export interface GroupRecord { id: string; code: string; nameI18n: Names; isActive: boolean; rowVersion: number }
export interface GroupDetail {
  id: string;
  code: string;
  name: Names;
  isActive: boolean;
  rowVersion: number;
  members: { id: string; displayName: string; username: string }[];
  roles: RoleLine[];
}
export interface DelegationRow {
  id: string;
  fromUserId: string;
  toUserId: string;
  validFrom: string;
  validTo: string;
  documentTypes: string[] | null;
  companyIds: string[] | null;
  reason: string | null;
  status: string;
  rowVersion: number;
}
export interface Page<T> { items: T[]; next?: string | null }

export interface PermissionChange {
  id: string;
  occurredAt: string;
  actorUserId: string | null;
  actorName: string | null;
  entityType: string;
  entityId: string;
  action: string;
  source: string | null;
  fields: string[];
  roleCode: string | null;
}
export interface UnusedPermission { code: string; module: string; resource: string; action: string; isSensitive: boolean }
export interface InactiveUser { id: string; displayName: string; username: string; status: string; lastLoginAt: string | null }
export interface RoleUsage { inactiveDays: number; unusedPermissions: UnusedPermission[]; inactiveUsers: InactiveUser[] }

export interface ChangeFilters {
  userId?: string | undefined;
  entityType?: string | undefined;
  entityId?: string | undefined;
  from?: string | undefined;
  to?: string | undefined;
  action?: string | undefined;
  source?: string | undefined;
  cursor?: string | null | undefined;
  limit?: number | undefined;
}
export interface AuditChange {
  id: string;
  occurredAt: string;
  actorUserId: string | null;
  actorKind: string;
  actorName: string | null;
  entityType: string;
  entityId: string;
  action: string;
  source: string | null;
  changes: { field: string; before?: unknown; after?: unknown; hidden?: boolean }[];
}

export function query(values: Record<string, string | number | boolean | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(values)) {
    if (value !== undefined && value !== null && value !== '') params.set(name, String(value));
  }
  const text = params.toString();
  return text ? `?${text}` : '';
}

const id = (value: string) => encodeURIComponent(value);
const withSignal = (signal?: AbortSignal) => (signal ? { signal } : {});

export const access = {
  catalog: () => requestJson<CatalogEntry[]>('GET', '/api/iam/permission-catalog'),
  roles: async () => (await requestJson<Page<RoleRecord>>('GET', '/api/iam/roles?limit=200')).items,
  summary: (roleId: string) => requestJson<RoleSummary>('GET', `/api/iam/roles/${id(roleId)}/summary`),
  compare: (left: string, right: string) => requestJson<RoleComparison>('GET', `/api/iam/roles/compare${query({ left, right })}`),
  createRole: (body: { code: string; name: Names; description?: Names; copyFromRoleId?: string; copyFromBaseline?: string }) => requestJson<AccessAnswer>('POST', '/api/iam/roles', { body }),
  renameRole: (roleId: string, name: Names, description: Names | undefined, rowVersion: number) =>
    requestJson<AccessAnswer>('PUT', `/api/iam/roles/${id(roleId)}`, { body: { name, description, rowVersion } }),
  archiveRole: (roleId: string, reason: string) => requestJson<AccessAnswer>('POST', `/api/iam/roles/${id(roleId)}/archive`, { body: { reason } }),
  setPermissions: (roleId: string, permissions: string[], reason: string) => requestJson<AccessAnswer>('PUT', `/api/iam/roles/${id(roleId)}/permissions`, { body: { permissions, reason } }),
  setScopes: (roleId: string, scopes: { resource: string; dataScopeId: string | null }[], reason: string) => requestJson<AccessAnswer>('PUT', `/api/iam/roles/${id(roleId)}/data-scopes`, { body: { scopes, reason } }),
  setFields: (roleId: string, fields: { resource: string; field: string; access: AccessLevel | null }[], reason: string) => requestJson<AccessAnswer>('PUT', `/api/iam/roles/${id(roleId)}/field-policies`, { body: { fields, reason } }),
  fieldPreview: (roleId: string, resource: string) => requestJson<FieldPreview>('GET', `/api/iam/roles/${id(roleId)}/field-preview${query({ resource })}`),

  scopes: async () => (await requestJson<Page<DataScopeRecord>>('GET', '/api/iam/data-scopes?limit=200')).items,
  createScope: (body: { code: string; name: Names; kind: ScopeKind; definition: Record<string, unknown> }) => requestJson<AccessAnswer>('POST', '/api/iam/data-scopes', { body }),
  scopePreview: (userId: string, resource: string, limit = 20) => requestJson<ScopePreview>('GET', `/api/iam/data-scope-preview${query({ userId, resource, limit })}`),

  groups: async () => (await requestJson<Page<GroupRecord>>('GET', '/api/iam/user-groups?limit=200')).items,
  group: (groupId: string) => requestJson<GroupDetail>('GET', `/api/iam/user-groups/${id(groupId)}/detail`),
  createGroup: (code: string, name: Names) => requestJson<AccessAnswer>('POST', '/api/iam/user-groups', { body: { code, name } }),
  renameGroup: (groupId: string, name: Names, rowVersion: number) => requestJson<AccessAnswer>('PUT', `/api/iam/user-groups/${id(groupId)}`, { body: { name, rowVersion } }),
  archiveGroup: (groupId: string, reason: string) => requestJson<AccessAnswer>('POST', `/api/iam/user-groups/${id(groupId)}/archive`, { body: { reason } }),
  addMember: (groupId: string, userId: string) => requestJson<AccessAnswer>('POST', `/api/iam/user-groups/${id(groupId)}/members`, { body: { userId } }),
  removeMember: (groupId: string, userId: string) => requestJson<AccessAnswer>('POST', `/api/iam/user-groups/${id(groupId)}/members/${id(userId)}/remove`, { body: {} }),
  assignGroupRole: (groupId: string, body: { roleId: string; companyId: string | null; validFrom: string | null; validTo: string | null; reason: string }) =>
    requestJson<AccessAnswer>('POST', `/api/iam/user-groups/${id(groupId)}/roles`, { body }),
  endGroupRole: (assignmentId: string, endDate: string | null, reason: string) => requestJson<AccessAnswer>('POST', `/api/iam/user-group-roles/${id(assignmentId)}/end`, { body: { endDate, reason } }),

  delegations: async () => (await requestJson<Page<DelegationRow>>('GET', '/api/iam/delegations?limit=200')).items,
  createDelegation: (body: { fromUserId: string; toUserId: string; from: string; to: string; reason: string; documentTypes?: string[]; companyIds?: string[] }) =>
    requestJson<{ id: string }>('POST', '/api/iam/delegations', { body }),
  revokeDelegation: (delegationId: string, reason: string) => requestJson<unknown>('POST', `/api/iam/delegations/${id(delegationId)}/revoke`, { body: { reason } }),

  permissionChanges: (filters: { from?: string | undefined; to?: string | undefined; cursor?: string | null | undefined; limit?: number }, signal?: AbortSignal) =>
    requestJson<Page<PermissionChange>>('GET', `/api/iam/reports/permission-changes${query({ ...filters })}`, withSignal(signal)),
  roleUsage: () => requestJson<RoleUsage>('GET', '/api/iam/reports/role-usage'),

  changes: (filters: ChangeFilters, signal?: AbortSignal) => requestJson<Page<AuditChange>>('GET', `/api/aud/changes${query({ ...filters })}`, withSignal(signal)),
  securityEvents: (filters: Record<string, string | number | null | undefined>, signal?: AbortSignal) =>
    requestJson<Page<SecurityEvent>>('GET', `/api/aud/security-events${query({ ...filters })}`, withSignal(signal)),
};

/** The audit export (CSV) for the same filters as the search: saved as a file by the browser, never shown in the page. */
export async function downloadChangeExport(filters: ChangeFilters): Promise<void> {
  const response = await send('GET', `/api/aud/export${query({ ...filters, cursor: undefined, limit: undefined })}`);
  if (!response.ok) throw await problemOf(response);
  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement('a');
  link.href = url;
  link.download = 'change-log.csv';
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

/** True for an answer that is waiting for a second administrator. */
export const isPending = (answer: { outcome?: string } | undefined) => answer?.outcome === 'pending';
