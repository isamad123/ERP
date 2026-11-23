// Hand-written client of the route (approval route) editor (slice S1.15c Build item 10; contract "Workflow definition API", S1.10).
// A route is a list of steps in the stored format (`RouteFormat`, snake_case); the editor reads and writes it as it is, and the
// server's check is the only judge of whether it can be activated. The screens show its answers as confirmed, never assumed.
import { requestJson } from './client';
import type { Names } from './workflow';

const enc = encodeURIComponent;

export interface RouteField { name: string; type: string; label: Names; choices: string[] }
export interface RouteDocumentType { code: string; module: string; name: Names; fields: RouteField[]; bulkApproval: boolean; requiresApproval: boolean; decidePermission: string | null }
export interface RouteProblem { path: string; error: string }
export interface RouteDefinition {
  id: string; code: string; documentType: string; name: Names; status: string; activeVersionId: string | null; activeVersion: number | null;
  requiresApproval: boolean; rowVersion: number; ready: boolean; problems: RouteProblem[];
}

/** One approver in the stored format. */
export interface ApproverJson { kind: string; user_id?: string; role?: string; field?: string; levels?: number; position?: string }
export interface ConditionJson { field?: string; op?: string; value?: unknown; all?: ConditionJson[]; any?: ConditionJson[] }
export interface StepJson {
  key: string; name: Names; condition?: ConditionJson | null; approver: ApproverJson; fallback?: ApproverJson | null;
  time_limit_working_days?: number | null; escalation?: ApproverJson | null; bulk_approval?: boolean;
}
export interface RouteJson { steps: StepJson[] }

export interface RouteVersion { id: string; definitionId: string; version: number; status: 'draft' | 'active' | 'retired'; route: RouteJson; activatedAt: string | null; activatedBy: string | null; rowVersion: number; runningInstances: number }
export interface RouteDetail { definition: RouteDefinition; versions: RouteVersion[] }
export interface RouteCheck { ready: boolean; problems: RouteProblem[] }
export interface Activation { outcome: 'done' | 'pending'; version: RouteVersion | null; requestId: string | null }

export const routesApi = {
  types: () => requestJson<RouteDocumentType[]>('GET', '/api/wf/document-types'),
  list: () => requestJson<RouteDefinition[]>('GET', '/api/wf/definitions'),
  get: (id: string) => requestJson<RouteDetail>('GET', `/api/wf/definitions/${enc(id)}`),
  newDraft: (definitionId: string, body: { copyOf?: string; route?: RouteJson }) => requestJson<RouteVersion>('POST', `/api/wf/definitions/${enc(definitionId)}/versions`, { body }),
  save: (versionId: string, route: RouteJson, rowVersion: number) => requestJson<RouteVersion>('PUT', `/api/wf/versions/${enc(versionId)}`, { body: { route, rowVersion } }),
  check: (versionId: string) => requestJson<RouteCheck>('POST', `/api/wf/versions/${enc(versionId)}/check`),
  activate: (versionId: string, rowVersion: number, reason: string) => requestJson<Activation>('POST', `/api/wf/versions/${enc(versionId)}/activate`, { body: { rowVersion, reason } }),
  deactivate: (definitionId: string, reason: string, rowVersion: number) => requestJson<RouteDefinition>('POST', `/api/wf/definitions/${enc(definitionId)}/deactivate`, { body: { reason, rowVersion } }),
};
