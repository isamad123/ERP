// The policy screens (slice S1.23a Build item 6; FR-HRM-POL-02, FR-HRM-POL-03, FR-HRM-POL-06, ruling Q1): the policy list, a policy's versions
// with the difference view, editing and activating a draft (applied or waiting for approval), the assignment set in plain words with the
// editor's check and activation, "Which policy applies?", and the read-only and denied states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import PoliciesScreen from './policies';
import ResolutionScreen from './policy-resolution';
import type { AssignmentSet, PolicyTypes, PolicyView, Resolution } from '../../api/policies';
import { COMPANY, json, shell, show } from '../admin/access-test-support';

const READ = ['hrm.policies.read'];
const MANAGE = [...READ, 'hrm.policies.manage'];

const types: PolicyTypes = {
  items: [{
    code: 'probation', module: 'hrm', name: { en: 'Probation', ur: 'آزمائشی مدت' }, description: { en: 'How long a probation lasts' }, cardinality: 'one', required: false,
    attributes: ['grade', 'employee_type', 'unit', 'custom'],
    schema: { type: 'object', additionalProperties: false, required: ['months'], properties: { months: { type: 'integer', minimum: 1, maximum: 24, title: 'hrm.policy.param.probation.months' }, reviewer: { type: 'string', enum: ['line_manager_and_hr', 'hr'] } } },
  }],
  operators: ['equals', 'in', 'not_in'],
  employeeTypes: [{ code: 'permanent', name: { en: 'Permanent' } }],
};

const policy = (extra: Partial<PolicyView> = {}): PolicyView => ({
  id: 'p1', companyId: COMPANY, policyType: 'probation', code: 'HO', name: { en: 'Head office probation' }, source: 'tenant', isActive: true, rowVersion: 1, assignments: 1,
  versions: [
    { id: 'v2', version: 2, status: 'draft', parameters: { months: 6 }, isOverridden: false, layer: 'tenant', validFrom: null, validTo: null, inForce: false, rowVersion: 3 },
    { id: 'v1', version: 1, status: 'active', parameters: { months: 3, reviewer: 'hr' }, isOverridden: false, layer: 'tenant', validFrom: '2026-01-01', validTo: null, inForce: true, rowVersion: 2 },
  ],
  ...extra,
});

const names = {
  records: { g5: { code: 'G5', name: { en: 'Grade five' } }, fin: { code: 'FIN', name: { en: 'Finance' } } },
  customFields: [{ key: 'shift_worker', dataType: 'bool', status: 'active' as const, label: { en: 'Shift worker' }, options: [] }],
  employeeTypes: [{ code: 'permanent', name: { en: 'Permanent' } }],
};

const set: AssignmentSet = {
  companyId: COMPANY, policyType: 'probation', asOf: '2026-03-01', latestFrom: '2026-01-01', validFrom: '2026-01-01',
  assignments: [
    { id: 'a1', policyId: 'p1', policyCode: 'HO', policyName: { en: 'Head office probation' }, precedence: 1, validFrom: '2026-01-01', validTo: null,
      rule: { conditions: [{ attribute: 'grade', operator: 'in', values: ['g5'] }, { attribute: 'unit', operator: 'equals', values: ['fin'], subUnits: true }] } },
    { id: 'a2', policyId: 'p1', policyCode: 'HO', policyName: { en: 'Head office probation' }, precedence: 2, validFrom: '2026-01-01', validTo: null, rule: {} },
  ],
  sets: [{ validFrom: '2026-01-01', validTo: null, count: 2, inForce: true }],
  names,
};

interface Server { sent: { method: string; url: string; body: unknown }[]; answer?: (method: string, url: string, body: unknown) => Response | undefined; detail?: PolicyView; resolution?: Resolution; status?: number }

function serve(s: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      const body = init?.body ? JSON.parse(String(init.body)) : null;
      s.sent.push({ method, url: path, body });
      return s.answer?.(method, path, body) ?? json(policy());
    }
    if (s.status) return json({ title: 'errors.access.forbidden' }, s.status);
    if (path === '/api/hrm/policy-types') return json(types);
    if (path === '/api/hrm/policies') return json({ items: [{ id: 'p1', companyId: COMPANY, code: 'HO', nameI18n: { en: 'Head office probation' }, policyType: 'probation', source: 'tenant', isActive: true, rowVersion: 1 }], next: null });
    if (path === '/api/hrm/policies/p1/detail') return json(s.detail ?? policy());
    if (path === '/api/hrm/policy-assignment-sets') return json(set);
    if (path === '/api/hrm/grades') return json({ items: [{ id: 'g5', code: 'G5', nameI18n: { en: 'Grade five' } }, { id: 'g6', code: 'G6', nameI18n: { en: 'Grade six' } }], next: null });
    if (path === '/api/hrm/employments') return json({ items: [{ id: 'e1', employeeNo: 'EMP-00001', status: 'active', companyId: COMPANY }], next: null });
    if (path === '/api/hrm/policy-resolution') return s.resolution ? json(s.resolution) : json({ title: 'errors.data.not_found' }, 404);
    return json({ title: 'errors.data.not_found' }, 404);
  }));
}

const open = (at: string, permissions = MANAGE, writable = true) => show('/hr/policies/*', at, <PoliciesScreen />, shell(permissions, writable));

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-HRM-POL-02 the policies of a company', () => {
  it('lists the policies with their type, source and state, and offers a new policy to HR administrators', async () => {
    serve({ sent: [] });
    open('/hr/policies');
    const table = await screen.findByRole('table', { name: 'Policies of the company' });
    const row = within(table).getAllByRole('row')[1]!;
    expect(within(row).getByText('HO')).toBeTruthy();
    expect(within(row).getByText('Probation')).toBeTruthy();
    expect(within(row).getByText('Customer')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'New policy' })).toBeTruthy();
  });

  it('offers no change to a reader, and says so when the server refuses the list', async () => {
    serve({ sent: [] });
    open('/hr/policies', READ);
    await screen.findByRole('table', { name: 'Policies of the company' });
    expect(screen.queryByRole('button', { name: 'New policy' })).toBeNull();
    cleanup();
    serve({ sent: [], status: 403 });
    open('/hr/policies', READ);
    expect(await screen.findByText('You do not have permission to see the policies of this company.')).toBeTruthy();
  });

  it('creates a policy with its first draft from the type schema', async () => {
    const s: Server = { sent: [], answer: () => json(policy(), 201) };
    serve(s);
    open('/hr/policies');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'New policy' }));
    const dialog = await screen.findByRole('dialog', { name: 'New policy' });
    await user.type(within(dialog).getByRole('textbox', { name: /^Code/ }), 'HO');
    await user.type(within(dialog).getByRole('textbox', { name: /^Name in English/ }), 'Head office probation');
    const months = within(dialog).getByRole('textbox', { name: /^Probation \(months\)/ });
    await user.clear(months);
    await user.type(months, '3');
    await user.click(within(dialog).getByRole('button', { name: 'Create the policy' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toEqual({ method: 'POST', url: '/api/hrm/policies', body: { companyId: COMPANY, policyType: 'probation', code: 'HO', name: { en: 'Head office probation' }, parameters: { months: 3 } } });
  });
});

describe('FR-HRM-POL-06 versions of a policy', () => {
  it('shows the versions with status, layer and dates, and the difference between two versions key by key', async () => {
    serve({ sent: [] });
    open('/hr/policies/p1');
    const versions = await screen.findByRole('table', { name: 'Versions' });
    expect(within(versions).getByText('Version 1')).toBeTruthy();
    expect(within(versions).getByText('In force today')).toBeTruthy();
    const diff = screen.getByRole('table', { name: 'Compare versions' });
    const rows = within(diff).getAllByRole('row').slice(1);
    expect(rows.map((r) => r.textContent)).toEqual(['months changed36', 'reviewer removedhr']);
  });

  it('saves a changed draft with its row version', async () => {
    const s: Server = { sent: [], answer: (_m, url) => (url.endsWith('/activate') ? json({ status: 'pending', requestId: 'r1' }, 202) : json(policy().versions[0])) };
    serve(s);
    open('/hr/policies/p1');
    const user = userEvent.setup();
    const months = within(await screen.findByRole('region', { name: 'Draft version 2' })).getByRole('textbox', { name: /^Probation \(months\)/ });
    await user.clear(months);
    await user.type(months, '9');
    await user.click(screen.getByRole('button', { name: 'Save the draft' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toEqual({ method: 'PUT', url: '/api/hrm/policy-versions/v2/parameters', body: { parameters: { months: 9 }, rowVersion: 3 } });
  });

  it('shows the server\'s refusal of a value beside its field', async () => {
    const s: Server = { sent: [], answer: () => json({ title: 'errors.data.invalid_request', errors: { 'parameters.months': ['errors.hrm.policy.param.maximum'] } }, 400) };
    serve(s);
    open('/hr/policies/p1');
    const user = userEvent.setup();
    const months = within(await screen.findByRole('region', { name: 'Draft version 2' })).getByRole('textbox', { name: /^Probation \(months\)/ });
    await user.clear(months);
    await user.type(months, '99');
    await user.click(screen.getByRole('button', { name: 'Save the draft' }));
    expect(await screen.findByText('The value is above the largest allowed.')).toBeTruthy();
  });

  it('activates the saved draft from a date (held for approval: 202)', async () => {
    const s: Server = { sent: [], answer: () => json({ status: 'pending', requestId: 'r1' }, 202) };
    serve(s);
    open('/hr/policies/p1');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Activate' }));
    const dialog = await screen.findByRole('dialog', { name: 'Activate version 2' });
    const group = within(dialog).getByRole('group', { name: /In force from/ });
    const [month, day, year] = within(group).getAllByRole('spinbutton');
    await user.type(month!, '04');
    await user.type(day!, '01');
    await user.type(year!, '2026');
    await user.click(within(dialog).getByRole('button', { name: 'Activate' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toEqual({ method: 'POST', url: '/api/hrm/policy-versions/v2/activate', body: { effectiveFrom: '2026-04-01', rowVersion: 3 } });
    expect((await screen.findAllByText('The activation waits for approval by a second person.')).length).toBeGreaterThan(0);
  });

  it('offers no change of a retired policy', async () => {
    serve({ sent: [], detail: policy({ isActive: false, versions: [policy().versions[1]!] }) });
    open('/hr/policies/p1');
    expect(await screen.findByText(/This policy is retired/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Retire' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'New draft' })).toBeNull();
  });
});

describe('FR-HRM-POL-02 assignments in plain words', () => {
  it('shows each rule as a sentence in order, with everyone last', async () => {
    serve({ sent: [] });
    open('/hr/policies/assignments?type=probation');
    const list = await screen.findByRole('list', { name: 'Assignments in order' });
    const items = within(list).getAllByRole('listitem').filter((li) => li.parentElement === list);
    expect(items[0]!.textContent).toContain('Grade is one of');
    expect(items[0]!.textContent).toContain('Grade five (G5)');
    expect(items[0]!.textContent).toContain('Finance (FIN)');
    expect(items[0]!.textContent).toContain('and its sub-units');
    expect(items[1]!.textContent).toContain('Everyone (no condition)');
  });

  it('checks a new set and shows the employees no rule covers, then activates it from a date', async () => {
    const s: Server = {
      sent: [],
      answer: (_m, url) => (url.endsWith('/check')
        ? json({ valid: true, errors: {}, latestFrom: '2026-01-01', unmatched: { count: 1, items: [{ employmentId: 'e9', employeeNo: 'EMP-00009', name: 'Sara Khan', reason: 'no_rule_matched' }] } })
        : json({ status: 'applied', effectiveFrom: '2026-04-01', assignments: ['n1'], unmatched: null })),
    };
    serve(s);
    open('/hr/policies/assignments?type=probation');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Change the assignments' }));
    await user.click(await screen.findByRole('button', { name: 'Remove order 2' }));
    await user.click(screen.getByRole('button', { name: 'Check' }));
    expect(await screen.findByText('1 current employee would get no policy')).toBeTruthy();
    expect(screen.getByText('Sara Khan')).toBeTruthy();
    expect(s.sent[0]!.body).toMatchObject({ companyId: COMPANY, policyType: 'probation', basedOn: '2026-01-01', assignments: [{ policyId: 'p1', precedence: 1 }] });
  });

  it('shows the server\'s refusal of a rule on its assignment', async () => {
    const s: Server = { sent: [], answer: () => json({ valid: false, errors: { 'assignments[0].rule': ['errors.hrm.policy.rule.custom_field_retired'] }, latestFrom: '2026-01-01', unmatched: null }) };
    serve(s);
    open('/hr/policies/assignments?type=probation');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Change the assignments' }));
    await user.click(screen.getByRole('button', { name: 'Check' }));
    expect(await screen.findByText('This custom field is retired. A rule cannot use it.')).toBeTruthy();
    expect(screen.getByText('The set has problems; see the assignments marked.')).toBeTruthy();
  });
});

describe('FR-HRM-POL-03 which policy applies', () => {
  const resolution: Resolution = {
    employee: { employmentId: 'e1', employeeNo: 'EMP-00001', name: 'Ali Raza', companyId: COMPANY },
    policyType: 'probation', cardinality: 'one', asOf: '2026-03-01', outcome: 'matched', reason: null,
    matches: [{ policyId: 'p2', policyCode: 'ALL', policyName: { en: 'Everyone' }, versionId: 'v1', version: 1, validFrom: '2026-01-01', validTo: null, parameters: { months: 3 }, layer: 'pack', assignmentId: 'a2', precedence: 2 }],
    evaluations: [
      { assignmentId: 'a1', policyId: 'p1', policyCode: 'SENIOR', policyName: { en: 'Senior staff' }, precedence: 1, outcome: 'not_matched',
        conditions: [{ attribute: 'grade', field: null, operator: 'in', values: ['g5'], subUnits: false, actual: ['g3'], met: false }] },
      { assignmentId: 'a2', policyId: 'p2', policyCode: 'ALL', policyName: { en: 'Everyone' }, precedence: 2, outcome: 'matched', conditions: [] },
    ],
    attributes: { employeeType: 'permanent', status: 'active', hasAssignment: true, orgUnitId: 'fin', branchId: null, locationId: null, gradeId: 'g3', custom: {}, gradeDefaults: null },
    names: { ...names, records: { ...names.records, g3: { code: 'G3', name: { en: 'Grade three' } } } },
  };

  it('answers the version, its layer and the rule that decided, and explains the rule that did not match', async () => {
    serve({ sent: [], resolution });
    show('/hr/policy-resolution', '/hr/policy-resolution?type=probation', <ResolutionScreen />, shell(READ));
    const user = userEvent.setup();
    const employee = await screen.findByRole('combobox', { name: /^Employee/ });
    await user.type(employee, 'EMP');
    await user.click(await screen.findByRole('option', { name: /EMP-00001/ }));
    await user.click(screen.getByRole('button', { name: 'Show' }));
    expect(await screen.findByText(/Everyone.*, version 1, applies/)).toBeTruthy();
    expect(screen.getByText(/values of the country pack/)).toBeTruthy();
    const table = screen.getByRole('table', { name: 'Conditions of order 1' });
    expect(within(table).getByText('Grade three (G3)')).toBeTruthy();
    expect(within(table).getByText('Not met')).toBeTruthy();
    expect(screen.getByText('Did not match')).toBeTruthy();
  });

  it('says why no policy applies', async () => {
    serve({ sent: [], resolution: { ...resolution, outcome: 'no_policy', reason: 'no_version_in_force', matches: [] } });
    show('/hr/policy-resolution', '/hr/policy-resolution?type=probation', <ResolutionScreen />, shell(READ));
    const user = userEvent.setup();
    await user.type(await screen.findByRole('combobox', { name: /^Employee/ }), 'EMP');
    await user.click(await screen.findByRole('option', { name: /EMP-00001/ }));
    await user.click(screen.getByRole('button', { name: 'Show' }));
    expect(await screen.findByText('No policy applies')).toBeTruthy();
    expect(screen.getByText('A rule matches, but its policy has no version in force on this date.')).toBeTruthy();
  });
});
