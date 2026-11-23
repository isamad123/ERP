// Hand-written client of the HR policy framework (slice S1.23a Build items 1 to 6; FR-HRM-POL-02, FR-HRM-POL-03, FR-HRM-POL-06). Reads of
// the rows stay the generated `/api/hrm/policies`; every write goes through these purpose-built endpoints, which check the permission,
// the company's data scope and the rules again (the screens show the server's answers, never assume them).
import { requestJson } from './client';
import type { Names } from './workflow';

const enc = encodeURIComponent;

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };
export type JsonObject = { [key: string]: Json };

/** The JSON Schema subset of policy parameters (server `PolicySchema`). `title` is a server text key. */
export interface ParamSchema {
  type: 'object' | 'array' | 'string' | 'integer' | 'number' | 'boolean';
  title?: string;
  description?: string;
  properties?: Record<string, ParamSchema>;
  required?: string[];
  additionalProperties?: boolean;
  enum?: Json[];
  minimum?: number;
  maximum?: number;
  minLength?: number;
  maxLength?: number;
  minItems?: number;
  maxItems?: number;
  items?: ParamSchema;
}

export interface CatalogueCode { code: string; name: Names }

export interface PolicyType {
  code: string;
  module: string;
  name: Names;
  description: Names;
  cardinality: 'one' | 'many';
  required: boolean;
  attributes: Attribute[];
  schema: ParamSchema;
}

export interface PolicyTypes { items: PolicyType[]; operators: Operator[]; employeeTypes: CatalogueCode[] }

export type Layer = 'pack' | 'tenant';
export type VersionStatus = 'draft' | 'active' | 'retired';

export interface PolicyVersion {
  id: string;
  policyId?: string;
  version: number;
  status: VersionStatus;
  parameters: JsonObject;
  isOverridden: boolean;
  layer: Layer;
  validFrom: string | null;
  validTo: string | null;
  inForce: boolean;
  rowVersion: number;
}

export interface PolicyView {
  id: string;
  companyId: string;
  policyType: string;
  code: string;
  name: Names;
  source: Layer;
  isActive: boolean;
  rowVersion: number;
  /** Assignments that use the policy today or later. */
  assignments: number;
  versions: PolicyVersion[];
}

/** A row of the generated list. */
export interface PolicyRow { id: string; companyId: string; code: string; nameI18n: Names; policyType: string; source: Layer; isActive: boolean; rowVersion: number }

export type Attribute = 'location' | 'branch' | 'grade' | 'employee_type' | 'unit' | 'custom';
export const ATTRIBUTES: readonly Attribute[] = ['location', 'branch', 'grade', 'employee_type', 'unit', 'custom'];
export type Operator = 'equals' | 'in' | 'not_in';
export const OPERATORS: readonly Operator[] = ['equals', 'in', 'not_in'];

export interface Condition { attribute: Attribute; field?: string; operator: Operator; values: Json[]; subUnits?: boolean }
export interface Rule { conditions?: Condition[] }

export interface CustomFieldInfo { key: string; dataType: string; status: 'active' | 'retired'; label: Names; options: { code: string; label: Names }[] }
export interface NamedRecord { code: string; name: Names; rank?: number }
export interface RuleNames { records: Record<string, NamedRecord>; customFields: CustomFieldInfo[]; employeeTypes: CatalogueCode[] }

export interface AssignmentRow { id: string; policyId: string; policyCode: string; policyName: Names; precedence: number; rule: Rule; validFrom: string; validTo: string | null }
export interface SetSummary { validFrom: string; validTo: string | null; count: number; inForce: boolean }

export interface AssignmentSet {
  companyId: string;
  policyType: string;
  asOf: string;
  latestFrom: string | null;
  validFrom: string | null;
  assignments: AssignmentRow[];
  sets: SetSummary[];
  names: RuleNames;
}

export interface AssignmentInput { policyId: string; precedence: number; rule: Rule }
export interface SetRequest { companyId: string; policyType: string; effectiveFrom: string | null; basedOn: string | null; assignments: AssignmentInput[]; reason?: string }

export interface UnmatchedEmployee { employmentId: string; employeeNo: string; name: string; reason: string }
export interface Unmatched { count: number; items: UnmatchedEmployee[] }
export interface SetCheck { valid: boolean; errors: Record<string, string[]>; latestFrom: string | null; unmatched: Unmatched | null }
export type SetAnswer = { status: 'applied'; effectiveFrom: string; assignments: string[]; unmatched: Unmatched | null } | { status: 'pending'; requestId: string; unmatched: Unmatched | null };
export type ActivateAnswer = { status: 'applied'; policy: PolicyView } | { status: 'pending'; requestId: string };

export interface ConditionEvaluation { attribute: Attribute; field: string | null; operator: Operator; values: Json[]; subUnits: boolean; actual: Json[]; met: boolean }
export type EvaluationOutcome = 'matched' | 'not_matched' | 'not_reached' | 'no_version';
export interface AssignmentEvaluation { assignmentId: string; policyId: string; policyCode: string; policyName: Names | null; precedence: number; outcome: EvaluationOutcome; conditions: ConditionEvaluation[] }
export interface PolicyMatch {
  policyId: string; policyCode: string; policyName: Names; versionId: string; version: number; validFrom: string; validTo: string | null;
  parameters: JsonObject; layer: Layer; assignmentId: string; precedence: number;
}
export type NoPolicyReason = 'no_assignment_row' | 'no_rule_matched' | 'no_version_in_force';
export interface Resolution {
  employee: { employmentId: string; employeeNo: string; name: string; companyId: string };
  policyType: string;
  cardinality: 'one' | 'many';
  asOf: string;
  outcome: 'matched' | 'no_policy';
  reason: NoPolicyReason | null;
  matches: PolicyMatch[];
  evaluations: AssignmentEvaluation[];
  attributes: {
    employeeType: string; status: string; hasAssignment: boolean; orgUnitId: string | null; branchId: string | null; locationId: string | null; gradeId: string | null;
    custom: JsonObject | null; gradeDefaults: Json;
  };
  names: RuleNames;
}

export interface EmploymentOption { id: string; employeeNo: string; status: string; companyId: string }

export const policiesApi = {
  types: () => requestJson<PolicyTypes>('GET', '/api/hrm/policy-types'),
  list: async (companyId: string): Promise<PolicyRow[]> => {
    const rows: PolicyRow[] = [];
    let after: string | null = null;
    for (let page = 0; page < 10; page += 1) {
      const answer: { items: PolicyRow[]; next: string | null } = await requestJson('GET', `/api/hrm/policies?companyId=${enc(companyId)}&limit=200&sort=code${after ? `&after=${enc(after)}` : ''}`);
      rows.push(...answer.items);
      after = answer.next;
      if (!after) break;
    }
    return rows;
  },
  detail: (id: string) => requestJson<PolicyView>('GET', `/api/hrm/policies/${enc(id)}/detail`),
  create: (body: { companyId: string; policyType: string; code: string; name: Names; parameters: JsonObject }) => requestJson<PolicyView>('POST', '/api/hrm/policies', { body }),
  rename: (id: string, name: Names, rowVersion: number) => requestJson<PolicyView>('PUT', `/api/hrm/policies/${enc(id)}/name`, { body: { name, rowVersion } }),
  newDraft: (id: string, parameters?: JsonObject) => requestJson<PolicyVersion>('POST', `/api/hrm/policies/${enc(id)}/versions`, { body: parameters ? { parameters } : {} }),
  retire: (id: string, effectiveFrom: string, rowVersion: number) => requestJson<PolicyView>('POST', `/api/hrm/policies/${enc(id)}/retire`, { body: { effectiveFrom, rowVersion } }),
  editDraft: (versionId: string, parameters: JsonObject, rowVersion: number) => requestJson<PolicyVersion>('PUT', `/api/hrm/policy-versions/${enc(versionId)}/parameters`, { body: { parameters, rowVersion } }),
  deleteDraft: (versionId: string, rowVersion: number) => requestJson<undefined>('DELETE', `/api/hrm/policy-versions/${enc(versionId)}/draft?rowVersion=${rowVersion}`),
  activate: (versionId: string, effectiveFrom: string, rowVersion: number, reason?: string) =>
    requestJson<ActivateAnswer>('POST', `/api/hrm/policy-versions/${enc(versionId)}/activate`, { body: { effectiveFrom, rowVersion, ...(reason ? { reason } : {}) } }),
  set: (companyId: string, policyType: string, asOf?: string | null) =>
    requestJson<AssignmentSet>('GET', `/api/hrm/policy-assignment-sets?companyId=${enc(companyId)}&policyType=${enc(policyType)}${asOf ? `&asOf=${enc(asOf)}` : ''}`),
  check: (body: SetRequest) => requestJson<SetCheck>('POST', '/api/hrm/policy-assignment-sets/check', { body }),
  activateSet: (body: SetRequest) => requestJson<SetAnswer>('POST', '/api/hrm/policy-assignment-sets/activate', { body }),
  resolve: (employmentId: string, policyType: string, date?: string | null) =>
    requestJson<Resolution>('GET', `/api/hrm/policy-resolution?employmentId=${enc(employmentId)}&policyType=${enc(policyType)}${date ? `&date=${enc(date)}` : ''}`),
  /** The company's current employments by number (the generated list has no search; at most 200 are offered, filtered as the person types). */
  employments: async (companyId: string): Promise<EmploymentOption[]> =>
    (await requestJson<{ items: EmploymentOption[] }>('GET', `/api/hrm/employments?companyId=${enc(companyId)}&limit=200&sort=employeeNo`)).items,
};
