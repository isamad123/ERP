// Approval routes (slice S1.15c Build item 10; FR-KRN-WFL-01 to -03, -10; FR-XM-32): the routes with their versions and the requests running on each, the steps in
// order with their conditions as sentences, a new draft from the active version, editing and saving, the engine's check and refusals shown on the step, activation
// (and waiting for a second administrator), and the read-only and denied states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import RoutesScreen from './routes';
import type { RouteDefinition, RouteDocumentType, RouteJson, RouteVersion } from '../../api/routes';
import { json, shell, show } from './access-test-support';

const READ = ['wf.workflow_definitions.read'];
const WRITE = [...READ, 'wf.workflow_definitions.update'];

const type: RouteDocumentType = { code: 'hrm.leave_request', module: 'hrm', name: { en: 'Leave request', ur: 'چھٹی کی درخواست' }, bulkApproval: true, requiresApproval: false, decidePermission: null,
  fields: [{ name: 'days', type: 'number', label: { en: 'Days' }, choices: [] }, { name: 'grade', type: 'choice', label: { en: 'Grade' }, choices: ['a', 'b'] }, { name: 'manager', type: 'user', label: { en: 'Manager' }, choices: [] }] };
const route = (): RouteJson => ({ steps: [
  { key: 'manager', name: { en: 'Manager', ur: 'منیجر' }, approver: { kind: 'manager', levels: 1 }, bulk_approval: false },
  { key: 'hr', name: { en: 'HR', ur: 'ایچ آر' }, condition: { all: [{ field: 'days', op: 'gt', value: '5' }] }, approver: { kind: 'role', role: 'hr_admin' }, time_limit_working_days: 3, escalation: { kind: 'role', role: 'tenant_admin' }, bulk_approval: false },
] });
const version = (id: string, n: number, status: RouteVersion['status'], running = 0, extra: Partial<RouteVersion> = {}): RouteVersion => ({ id, definitionId: 'd1', version: n, status, route: route(), activatedAt: status === 'draft' ? null : '2026-11-01T08:00:00Z', activatedBy: null, rowVersion: 1, runningInstances: running, ...extra });
const definition: RouteDefinition = { id: 'd1', code: 'leave', documentType: 'hrm.leave_request', name: { en: 'Leave approval', ur: 'چھٹی کی منظوری' }, status: 'active', activeVersionId: 'v1', activeVersion: 1, requiresApproval: false, rowVersion: 3, ready: true, problems: [] };

interface Server { sent: { method: string; url: string; body: unknown }[]; versions?: RouteVersion[]; answer?: (method: string, url: string, body: unknown) => Response | undefined }

function serve(s: Server) {
  const versions = s.versions ?? [version('v1', 1, 'active', 4)];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      const body = init?.body ? JSON.parse(String(init.body)) : null;
      s.sent.push({ method, url: path, body });
      return s.answer?.(method, path, body) ?? json({ ...versions[0], id: 'v2' }, 201);
    }
    if (path === '/api/wf/document-types') return json([type]);
    if (path === '/api/wf/definitions') return json([definition]);
    if (path === '/api/wf/definitions/d1') return json({ definition, versions });
    if (path === '/api/iam/roles') return json({ items: [{ id: 'r1', code: 'hr_admin', nameI18n: { en: 'HR administrator' } }, { id: 'r2', code: 'tenant_admin', nameI18n: { en: 'Tenant administrator' } }] });
    if (path === '/api/iam/people-lookup') return json([{ id: 'u9', displayName: 'Imran Manager', username: 'imran' }]);
    if (path.startsWith('/api/iam/users/')) return json({ id: 'u1', displayName: 'Sara Khan' });
    return json({ title: 'errors.data.not_found' }, 404);
  }));
}

const open = (permissions = WRITE, at = '/settings/approval-routes?route=d1') => show('/settings/approval-routes', at, <RoutesScreen />, shell(permissions));

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-KRN-WFL-10 routes, versions and running requests', () => {
  it('lists the routes with their active version', async () => {
    serve({ sent: [] });
    open(WRITE, '/settings/approval-routes');
    const list = await screen.findByRole('list', { name: 'Approval routes' });
    expect(within(list).getByText('Leave approval')).toBeTruthy();
    expect(within(list).getByText('Active version 1')).toBeTruthy();
  });

  it('shows each version with the requests still running on it, and says they stay on it', async () => {
    serve({ sent: [], versions: [version('v1', 1, 'retired', 2), version('v2', 2, 'active', 4)] });
    open();
    const versions = await screen.findByRole('list', { name: 'Versions' });
    expect(within(versions).getByText('2 requests are running on it')).toBeTruthy();
    expect(within(versions).getByText('4 requests are running on it')).toBeTruthy();
    expect(screen.getByText('Requests already running stay on the version they started with.')).toBeTruthy();
  });

  it('FR-KRN-WFL-01 shows the steps top to bottom with who decides and when the step applies, in plain words', async () => {
    serve({ sent: [] });
    open();
    expect(await screen.findByText(/The line manager, 1 level up\. Always/)).toBeTruthy();
    expect(await screen.findByText(/Anyone holding the role HR administrator\. When Days is more than 5/)).toBeTruthy();
    const steps = screen.getAllByRole('region', { name: /^Step \d/ });
    expect(steps.map((s) => s.getAttribute('aria-label'))).toEqual(['Step 1', 'Step 2']);
  });

  it('an active version is read-only: no editing, and a draft is started from it', async () => {
    const s: Server = { sent: [], answer: () => json(version('v2', 2, 'draft'), 201) };
    serve(s);
    open();
    const user = userEvent.setup();
    expect(await screen.findByText(/cannot be edited/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Save draft' })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Remove step/ })).toBeNull();
    await user.click(screen.getByRole('button', { name: 'New draft from version 1' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ method: 'POST', url: '/api/wf/definitions/d1/versions', body: { copyOf: 'v1' } });
  });
});

describe('FR-KRN-WFL-01 to -03 editing a draft', () => {
  const draftServer = (extra: Partial<Server> = {}): Server => ({ sent: [], versions: [version('v1', 1, 'active', 4), version('v2', 2, 'draft')], ...extra });

  it('adds a step, builds a condition on a declared field and saves the route in the stored form', async () => {
    const s = draftServer({ answer: () => json(version('v2', 2, 'draft', 0, { rowVersion: 2 })) });
    serve(s);
    open();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Add a step' }));
    const third = await screen.findByRole('region', { name: 'Step 3' });
    await user.type(within(third).getByRole('textbox', { name: /^Step name in English/ }), 'Director');
    await user.type(within(third).getByRole('textbox', { name: /^Step name in Urdu/ }), 'ڈائریکٹر');
    await user.click(within(third).getByRole('button', { name: 'Add a condition' }));
    expect(within(third).getByText(/When Days is …/)).toBeTruthy();
    await user.type(within(third).getByRole('textbox', { name: 'Value' }), '10');
    expect(within(third).getByText(/When Days is 10/)).toBeTruthy();
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ method: 'PUT', url: '/api/wf/versions/v2', body: { rowVersion: 1 } });
    const saved = (s.sent[0]!.body as { route: RouteJson }).route;
    expect(saved.steps).toHaveLength(3);
    expect(saved.steps[2]).toMatchObject({ key: 'step_3', name: { en: 'Director', ur: 'ڈائریکٹر' }, condition: { all: [{ field: 'days', op: 'eq', value: '10' }] } });
  });

  it('FR-KRN-WFL-02 picks a named person for a step, shows the name, saves the id, and is not "changed" again once the server has the same route', async () => {
    const versions = [version('v1', 1, 'active', 4), version('v2', 2, 'draft')];
    const s = draftServer({ versions, answer: (_m, _u, body) => {
      const saved = (body as { route: RouteJson }).route;
      versions[1] = { ...versions[1]!, route: saved, rowVersion: 2 };
      return json(versions[1]);
    } });
    serve(s);
    open();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Add a step' }));
    const third = await screen.findByRole('region', { name: 'Step 3' });
    await user.type(within(third).getByRole('textbox', { name: /^Step name in English/ }), 'Director');
    await user.type(within(third).getByRole('textbox', { name: /^Step name in Urdu/ }), 'ڈائریکٹر');
    await user.click(within(third).getByRole('button', { name: /Who decides/ }));
    await user.click(await screen.findByRole('option', { name: 'A named person' }));
    await user.type(within(third).getByRole('combobox', { name: /^Person/ }), 'Imr');
    await user.click(await screen.findByRole('option', { name: /Imran Manager/ }));
    expect(await within(third).findByText('Imran Manager')).toBeTruthy();
    expect(within(third).getByRole('button', { name: 'Choose another person' })).toBeTruthy();
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect((s.sent[0]!.body as { route: RouteJson }).route.steps[2]?.approver).toEqual({ kind: 'user', user_id: 'u9' });
    await waitFor(() => expect((screen.getByRole('button', { name: 'Check' }) as HTMLButtonElement).disabled).toBe(false));
    expect(screen.queryByText('You have changes that are not saved.')).toBeNull();
  });

  it('moves and removes steps, and warns when every step has a condition', async () => {
    serve(draftServer());
    open();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Remove step 1' }));
    expect(await screen.findByText('At least one step must have no condition, so every request reaches someone.')).toBeTruthy();
    expect(screen.getAllByRole('region', { name: /^Step \d/ })).toHaveLength(1);
    expect(screen.getByText('You have changes that are not saved.')).toBeTruthy();
  });

  it('shows the engine\'s check on the step it names, and says a ready version can be activated', async () => {
    let ready = false;
    const s = draftServer({ answer: () => json(ready ? { ready: true, problems: [] } : { ready: false, problems: [{ path: 'steps[1].approver', error: 'errors.workflow.approver_cannot_decide' }, { path: 'steps', error: 'errors.workflow.route_needs_unconditional_step' }] }) });
    serve(s);
    open();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Check' }));
    const second = await screen.findByRole('region', { name: 'Step 2' });
    expect(await within(second).findByText(/The approver of this step is not allowed to decide this document type/)).toBeTruthy();
    expect(screen.getByText(/At least one step must have no condition/, { selector: 'li' })).toBeTruthy();
    ready = true;
    await user.click(screen.getByRole('button', { name: 'Check' }));
    expect(await screen.findByText('No problems: this version can be activated.')).toBeTruthy();
  });

  it('shows the problems of a refused save on the steps', async () => {
    const s = draftServer({ answer: () => json({ title: 'errors.workflow.route_invalid', details: ['steps[0].name: errors.workflow.step_name_required'] }, 400) });
    serve(s);
    open();
    const user = userEvent.setup();
    const first = await screen.findByRole('region', { name: 'Step 1' });
    await user.clear(within(first).getByRole('textbox', { name: /^Step name in English/ }));
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(await within(first).findByText(/Every step needs a name in English and Urdu/)).toBeTruthy();
  });

  it('activates a version after a confirmation that says what happens to running requests', async () => {
    const s = draftServer({ answer: () => json({ outcome: 'done', version: version('v2', 2, 'active'), requestId: null }) });
    serve(s);
    open();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Activate' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Activate version 2?' });
    expect(within(dialog).getByText(/Requests already running stay on the version they started with: 4 are running now/)).toBeTruthy();
    await user.click(within(dialog).getByRole('button', { name: 'Activate version 2' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ method: 'POST', url: '/api/wf/versions/v2/activate', body: { rowVersion: 1 } });
  });

  it('says when the activation waits for a second administrator', async () => {
    const s = draftServer({ answer: () => json({ outcome: 'pending', version: null, requestId: 'q1' }, 202) });
    serve(s);
    open();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Activate' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Activate version 2?' });
    await user.click(within(dialog).getByRole('button', { name: 'Activate version 2' }));
    expect(await screen.findByText(/Waiting for a second administrator/)).toBeTruthy();
  });
});

describe('FR-KRN-WFL-01 permissions and states', () => {
  it('shows no editing to a person who may only read', async () => {
    serve({ sent: [], versions: [version('v1', 1, 'active', 4), version('v2', 2, 'draft')] });
    open(READ);
    await screen.findByRole('region', { name: 'Step 1' });
    expect(screen.queryByRole('button', { name: 'Save draft' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Add a step' })).toBeNull();
    expect(screen.queryByRole('button', { name: /^New draft/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Deactivate the route' })).toBeNull();
  });

  it('shows a denied state when the server refuses', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ title: 'errors.access.forbidden' }, 403)));
    open();
    expect(await screen.findByText('You may not see the approval routes.')).toBeTruthy();
  });
});
