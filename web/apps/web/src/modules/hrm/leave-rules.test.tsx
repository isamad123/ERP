// The leave rules screen (slice S1.33b Build item 6; FR-HRM-LVE-03): the rules of the company's `leave` policy as grouped sections made from the policy type's schema, read-only for the version
// in force, a draft that HR edits and saves with the server's refusals beside the fields, the leave types the policy does not list, the link to Policies for activation, and "who does this apply to".
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import LeaveRulesScreen from './leave-rules';
import type { AssignmentSet, ParamSchema, PolicyTypes, PolicyVersion, PolicyView } from '../../api/policies';
import { COMPANY, json, sent, serve, shell, show } from './live-test-support';

const READ = ['hrm.policies.read', 'hrm.leave_types.read'];
const MANAGE = [...READ, 'hrm.policies.manage'];

const number = (title: string, max = 9999): ParamSchema => ({ type: 'number', minimum: 0, maximum: max, title: `hrm.policy.param.leave.${title}` });
const schema: ParamSchema = {
  type: 'object', additionalProperties: false, required: ['types'],
  properties: {
    types: {
      type: 'array', minItems: 1, maxItems: 50, title: 'hrm.policy.param.leave.types',
      items: {
        type: 'object', additionalProperties: false, required: ['leaveType', 'entitlement'],
        properties: {
          leaveType: { type: 'string', minLength: 1, maxLength: 40, title: 'hrm.policy.param.leave.leave_type' },
          entitlement: {
            type: 'object', additionalProperties: false, required: ['model'], title: 'hrm.policy.param.leave.entitlement',
            properties: {
              model: { type: 'string', enum: ['fixed_yearly', 'monthly_accrual', 'manual'], title: 'hrm.policy.param.leave.model' },
              quantity: number('quantity'),
              proRata: { type: 'string', enum: ['none', 'days'], title: 'hrm.policy.param.leave.pro_rata' },
            },
          },
          carryForward: { type: 'object', additionalProperties: false, title: 'hrm.policy.param.leave.carry_forward', properties: { cap: number('carry_forward_cap') } },
          negativeLimit: number('negative_limit'),
          maxConsecutiveDays: number('max_consecutive_days'),
          eligibility: { type: 'object', additionalProperties: false, title: 'hrm.policy.param.leave.eligibility', properties: { minServiceMonths: { type: 'integer', minimum: 0, maximum: 600, title: 'hrm.policy.param.leave.min_service_months' } } },
          sandwich: { type: 'string', enum: ['off', 'both'], title: 'hrm.policy.param.leave.sandwich' },
        },
      },
    },
  },
};
const types: PolicyTypes = {
  items: [{ code: 'leave', module: 'hrm', name: { en: 'Leave' }, description: { en: 'Leave rules' }, cardinality: 'one', required: false, attributes: ['grade'], schema }],
  operators: ['equals', 'in', 'not_in'], employeeTypes: [],
};
const ACTIVE_PARAMS = { types: [{ leaveType: 'AL', entitlement: { model: 'fixed_yearly', quantity: 14 }, carryForward: { cap: 5 }, sandwich: 'both' }] };
const version = (extra: Partial<PolicyVersion>): PolicyVersion => ({ id: 'v1', version: 1, status: 'active', parameters: ACTIVE_PARAMS, isOverridden: false, layer: 'tenant', validFrom: '2026-01-01', validTo: null, inForce: true, rowVersion: 2, ...extra });
const policy = (versions: PolicyVersion[]): PolicyView => ({ id: 'p1', companyId: COMPANY, policyType: 'leave', code: 'LV', name: { en: 'Leave policy' }, source: 'tenant', isActive: true, rowVersion: 1, assignments: 1, versions });
const DRAFT = version({ id: 'v2', version: 2, status: 'draft', inForce: false, validFrom: null, rowVersion: 5 });
const lt = (id: string, code: string, en: string, isActive = true) => ({ id, companyId: COMPANY, code, nameI18n: { en, ur: en }, isActive, isPaid: true, counting: 'working_days', unit: 'day', requiresDocument: false, isCompOff: false, isUnpaidDeduction: false, source: 'tenant', rowVersion: 1 });
const LEAVE_TYPES = [lt('t1', 'AL', 'Annual leave'), lt('t2', 'SL', 'Sick leave'), lt('t3', 'OLD', 'Old leave', false)];
const set: AssignmentSet = {
  companyId: COMPANY, policyType: 'leave', asOf: '2026-11-25', latestFrom: '2026-01-01', validFrom: '2026-01-01',
  assignments: [{ id: 'a1', policyId: 'p1', policyCode: 'LV', policyName: { en: 'Leave policy' }, precedence: 1, validFrom: '2026-01-01', validTo: null, rule: { conditions: [{ attribute: 'grade', operator: 'in', values: ['g5'] }] } }],
  sets: [{ validFrom: '2026-01-01', validTo: null, count: 1, inForce: true }],
  names: { records: { g5: { code: 'G5', name: { en: 'Grade five' } } }, customFields: [], employeeTypes: [] },
};
const UNMATCHED = { valid: false, errors: { effectiveFrom: ['errors.data.required'] }, latestFrom: '2026-01-01', unmatched: { count: 2, items: [
  { employmentId: 'e9', employeeNo: 'EMP-00009', name: 'Sara Khan', reason: 'no_rule_matched' }, { employmentId: 'e8', employeeNo: 'EMP-00008', name: 'Bilal Ahmed', reason: 'no_rule_matched' }] } };

interface World { versions: PolicyVersion[]; policies?: number; answer?: (method: string, url: string, body: unknown) => Response | undefined; leaveTypes?: Response }

function open(world: World, permissions = MANAGE, locale: 'en' | 'ur' = 'en', writable = true) {
  const state = { versions: world.versions };
  serve((method, url, body) => {
    const custom = world.answer?.(method, url, body);
    if (custom) return custom;
    const [path = ''] = url.split('?');
    if (method === 'POST' && path === '/api/hrm/policies/p1/versions') { state.versions = [DRAFT, ...state.versions]; return json(DRAFT); }
    if (method === 'POST' && path === '/api/hrm/policy-assignment-sets/check') return json(UNMATCHED);
    if (method !== 'GET') return json(DRAFT);
    if (path === '/api/hrm/policy-types') return json(types);
    if (path === '/api/hrm/policies') return json({ items: world.policies === 0 ? [] : [{ id: 'p1', companyId: COMPANY, code: 'LV', nameI18n: { en: 'Leave policy' }, policyType: 'leave', source: 'tenant', isActive: true, rowVersion: 1 }], next: null });
    if (path === '/api/hrm/policies/p1/detail') return json(policy(state.versions));
    if (path === '/api/hrm/leave-types') return world.leaveTypes ?? json({ items: LEAVE_TYPES, next: null });
    if (path === '/api/hrm/policy-assignment-sets') return json(set);
    return json({ title: 'errors.data.not_found' }, 404);
  });
  show(<LeaveRulesScreen />, shell(permissions, writable), locale);
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-HRM-LVE-03 the rules of the version in force', () => {
  it('shows one block for each listed leave type with its rules in grouped sections, read-only, listing only what is set', async () => {
    open({ versions: [version({})] });
    const block = await screen.findByRole('region', { name: /Annual leave/ });
    expect(within(block).getByRole('heading', { name: 'How the leave is given' })).toBeTruthy();
    expect(within(block).getByRole('heading', { name: 'Carry-forward and encashment' })).toBeTruthy();
    expect(within(block).getByRole('heading', { name: 'Sandwich days and combining' })).toBeTruthy();
    expect(within(block).queryByRole('heading', { name: 'Who may take it' })).toBeNull();
    expect(within(block).queryByRole('heading', { name: 'Negative balance' })).toBeNull();
    const quantity = within(block).getByRole('textbox', { name: /^Quantity/ }) as HTMLInputElement;
    expect(quantity.value).toBe('14');
    expect(quantity.readOnly).toBe(true);
    expect((within(block).getByRole('textbox', { name: /^Most that carries forward/ }) as HTMLInputElement).value).toBe('5');
    expect(screen.queryByRole('button', { name: 'Save the draft' })).toBeNull();
    expect(screen.getByText(/This version is in force and cannot be edited/)).toBeTruthy();
  });

  it('says that leave types the policy does not list cannot be taken, and names them (retired types are left out)', async () => {
    open({ versions: [version({})] });
    expect(await screen.findByText('Employees under this policy cannot take these leave types: Sick leave.')).toBeTruthy();
    expect(screen.queryByText(/Old leave/)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Add the leave type' })).toBeNull();
  });

  it('links to Policies for activation and to the resolution screen', async () => {
    open({ versions: [version({})] });
    const activate = await screen.findByRole('link', { name: 'Activate in Policies' });
    expect(activate.getAttribute('href')).toBe(`/hr/policies/p1?company=${COMPANY}`);
    const resolution = await screen.findByRole('link', { name: 'Which policy applies to this person?' });
    expect(resolution.getAttribute('href')).toBe(`/hr/policy-resolution?company=${COMPANY}`);
  });

  it('shows the denied state without the permission', async () => {
    open({ versions: [version({})] }, ['hrm.leave_types.read']);
    expect(await screen.findByText('You may not see the leave policy of this company.')).toBeTruthy();
    expect(sent).toHaveLength(0);
  });

  it('says so when the company has no leave policy', async () => {
    open({ versions: [], policies: 0 });
    expect(await screen.findByText('This company has no leave policy yet')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Create a leave policy in Policies' })).toBeTruthy();
  });

  it('shows an error with a retry when the policy cannot be loaded', async () => {
    open({ versions: [version({})], answer: (m, url) => (m === 'GET' && url.startsWith('/api/hrm/policies/p1/detail') ? json({ title: 'errors.http.500' }, 500) : undefined) });
    expect(await screen.findByText('The leave rules could not be loaded')).toBeTruthy();
  });

  it('writes the title in Urdu when the language is Urdu', async () => {
    open({ versions: [version({})] }, MANAGE, 'ur');
    expect(await screen.findByRole('heading', { name: 'رخصت کے اصول' })).toBeTruthy();
    expect(await screen.findByRole('link', { name: 'پالیسیاں میں فعال کریں' })).toBeTruthy();
  });
});

describe('FR-HRM-LVE-03 editing a draft', () => {
  it('starts a new draft (a copy of the active version) and then lets HR edit it', async () => {
    open({ versions: [version({})] });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Start a new draft' }));
    await waitFor(() => expect(sent.some((s) => s.method === 'POST' && s.url === '/api/hrm/policies/p1/versions')).toBe(true));
    expect(sent.find((s) => s.method === 'POST' && s.url === '/api/hrm/policies/p1/versions')?.body).toEqual({});
    const block = await screen.findByRole('region', { name: /Annual leave/ });
    expect(await within(block).findByRole('button', { name: /Remove Annual leave/ })).toBeTruthy();
    expect((within(block).getByRole('textbox', { name: /^Quantity/ }) as HTMLInputElement).readOnly).toBe(false);
    expect(screen.getByRole('button', { name: 'Save the draft' })).toHaveProperty('disabled', true);
    expect(screen.getByText(/This is a draft: it changes nothing until it is activated/)).toBeTruthy();
  });

  it('saves a changed rule with the draft\'s row version and keeps every other value', async () => {
    open({ versions: [DRAFT, version({})] });
    const user = userEvent.setup();
    const block = await screen.findByRole('region', { name: /Annual leave/ });
    const quantity = await within(block).findByRole('textbox', { name: /^Quantity/ });
    await user.clear(quantity);
    await user.type(quantity, '20');
    await user.click(screen.getByRole('button', { name: 'Save the draft' }));
    await waitFor(() => expect(sent.some((s) => s.method === 'PUT')).toBe(true));
    expect(sent.find((s) => s.method === 'PUT')).toEqual({
      method: 'PUT', url: '/api/hrm/policy-versions/v2/parameters',
      body: { parameters: { types: [{ leaveType: 'AL', entitlement: { model: 'fixed_yearly', quantity: 20 }, carryForward: { cap: 5 }, sandwich: 'both' }] }, rowVersion: 5 },
    });
  });

  it('shows the server\'s refusal of a value beside its field', async () => {
    open({ versions: [DRAFT, version({})], answer: (m) => (m === 'PUT' ? json({ title: 'errors.data.invalid_request', errors: { 'parameters.types[0].entitlement.quantity': ['errors.hrm.policy.param.maximum'], 'parameters.types[0].carryForward.cap': ['errors.hrm.leave.policy.positive'] } }, 400) : undefined) });
    const user = userEvent.setup();
    const block = await screen.findByRole('region', { name: /Annual leave/ });
    const quantity = await within(block).findByRole('textbox', { name: /^Quantity/ });
    await user.clear(quantity);
    await user.type(quantity, '99999');
    await user.click(screen.getByRole('button', { name: 'Save the draft' }));
    expect(await within(block).findByText('The value is above the largest allowed.')).toBeTruthy();
    expect(within(block).getByText('The value must be more than zero.')).toBeTruthy();
    expect(screen.getByText('Check the fields marked below.')).toBeTruthy();
    expect(within(block).getByRole('textbox', { name: /^Quantity/ }).getAttribute('aria-invalid')).toBe('true');
  });

  it('adds a block for a leave type the policy does not list, with no number filled in, and removes a block', async () => {
    open({ versions: [DRAFT, version({})] });
    const user = userEvent.setup();
    await screen.findByRole('region', { name: /Annual leave/ });
    await user.click(await screen.findByRole('button', { name: 'Add the leave type' }));
    const added = await screen.findByRole('region', { name: /Sick leave/ });
    expect(within(added).getByRole('heading', { name: 'How the leave is given' })).toBeTruthy();
    expect(within(added).getByRole('button', { name: /Entitlement model/ }).textContent).not.toContain('fixed_yearly');
    expect(screen.queryByText(/cannot take these leave types/)).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Save the draft' }));
    await waitFor(() => expect(sent.some((s) => s.method === 'PUT')).toBe(true));
    const body = sent.find((s) => s.method === 'PUT')?.body as { parameters: { types: unknown[] } };
    expect(body.parameters.types[1]).toEqual({ leaveType: 'SL', entitlement: {} });
    await user.click(within(screen.getByRole('region', { name: /Annual leave/ })).getByRole('button', { name: /Remove Annual leave/ }));
    expect(screen.queryByRole('region', { name: /Annual leave/ })).toBeNull();
    expect(screen.getByText('Employees under this policy cannot take these leave types: Annual leave.')).toBeTruthy();
  });

  it('keeps a reader and a read-only system from editing a draft', async () => {
    open({ versions: [DRAFT, version({})] }, READ);
    const block = await screen.findByRole('region', { name: /Annual leave/ });
    expect((await within(block).findByRole('textbox', { name: /^Quantity/ }) as HTMLInputElement).readOnly).toBe(true);
    expect(screen.queryByRole('button', { name: 'Save the draft' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Start a new draft' })).toBeNull();
    cleanup();
    open({ versions: [version({})] }, MANAGE, 'en', false);
    await screen.findByRole('region', { name: /Annual leave/ });
    expect(screen.queryByRole('button', { name: 'Start a new draft' })).toBeNull();
  });
});

describe('FR-HRM-LVE-03 who does this apply to', () => {
  it('shows the assignment in words and the employees who would get no leave policy', async () => {
    open({ versions: [version({})] });
    const section = await screen.findByRole('region', { name: 'Who does this apply to' });
    expect((await within(section).findByText(/Grade is one of/)).textContent).toContain('Grade five (G5)');
    expect(within(section).getByText('This policy')).toBeTruthy();
    expect(await within(section).findByText('2 current employees would get no policy')).toBeTruthy();
    const list = within(section).getByRole('list', { name: 'Employees without a policy' });
    expect(within(list).getAllByRole('listitem')).toHaveLength(2);
    expect(within(list).getByText('EMP-00009')).toBeTruthy();
    const check = sent.find((s) => s.url === '/api/hrm/policy-assignment-sets/check');
    expect(check?.body).toMatchObject({ companyId: COMPANY, policyType: 'leave', effectiveFrom: null, basedOn: '2026-01-01' });
  });

  it('does not run the check for a reader and says who may see the list', async () => {
    open({ versions: [version({})] }, READ);
    expect(await screen.findByText(/Grade is one of/)).toBeTruthy();
    expect(screen.getByText('The list of employees who would get no leave policy is shown to people who manage policies.')).toBeTruthy();
    expect(sent.some((s) => s.url.endsWith('/check'))).toBe(false);
  });
});
