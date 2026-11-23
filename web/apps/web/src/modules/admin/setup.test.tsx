// The set-up guide and readiness checklist (slice S1.18b; FR-ADM-ONB-01 to -04; S1.18 rulings Q1, Q4): the steps with their state and where the person resumes, ticking,
// skipping and re-opening, the checklist with its messages and links, go-live with and without a reason, and the read-only and denied states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import SetupScreen from './setup';
import type { ModuleReadiness, ReadinessOverview, SetupStatus, SetupTask } from '../../api/setup';
import { COMPANY, json, shell, show } from './access-test-support';

const READ = ['cfg.setup_tasks.read'];
const WRITE = [...READ, 'cfg.setup_tasks.update', 'cfg.setup_tasks.go_live'];

const task = (taskKey: string, extra: Partial<SetupTask> = {}): SetupTask => ({ moduleId: 'kernel', taskKey, kind: 'step', titleKey: `setup.steps.${taskKey.replace('step_', '')}`, required: false, level: 'company',
  link: '/organization/companies', status: 'pending', manual: true, messageKey: null, messageArguments: null, note: null, completedBy: null, completedAt: null, order: 10, ...extra });
const item = (taskKey: string, extra: Partial<SetupTask> = {}): SetupTask => task(taskKey, { kind: 'item', titleKey: `setup.items.${taskKey}`, required: true, manual: false, ...extra });

const STEPS = [
  task('step_company_profile', { status: 'done', manual: false, order: 10 }),
  task('step_languages_formats', { status: 'done', order: 20 }),
  task('step_modules', { level: 'customer', order: 30 }),
  task('step_users', { level: 'customer', order: 50 }),
];
const kernelModule = (items: SetupTask[], extra: Partial<ModuleReadiness> = {}): ModuleReadiness => {
  const required = items.filter((i) => i.required);
  return { moduleId: 'kernel', nameKey: 'setup.modules.kernel', requiredTotal: required.length, requiredDone: required.filter((i) => i.status === 'done').length, optionalTotal: 0, optionalDone: 0,
    isLive: false, liveAt: null, liveNote: null, goLiveActions: [], items, ...extra };
};

interface Server { sent: { method: string; url: string; body: unknown }[]; overview: ReadinessOverview; answer?: (url: string, body: unknown) => Response | undefined }

function serve(s: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      const body = init?.body ? JSON.parse(String(init.body)) : null;
      s.sent.push({ method, url: path, body });
      return s.answer?.(path, body) ?? json(task('x', { status: 'done' }));
    }
    if (path === '/api/cfg/readiness') return json(s.overview);
    return json({ title: 'errors.data.not_found' }, 404);
  }));
}

const overview = (steps: SetupTask[], modules: ModuleReadiness[] = []): ReadinessOverview => ({ companyId: COMPANY, steps, modules });
const open = (permissions = WRITE, at = '/setup') => show('/setup', at, <SetupScreen />, shell(permissions));

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-ADM-ONB-01 and -02 the set-up guide', () => {
  it('lists the steps with their state and resumes at the first step still to do', async () => {
    serve({ sent: [], overview: overview(STEPS) });
    open();
    const list = await screen.findByRole('list');
    const rows = within(list).getAllByRole('listitem');
    expect(rows).toHaveLength(4);
    expect(within(rows[0]!).getByText('Done')).toBeTruthy();
    expect(within(rows[2]!).getByText('To do')).toBeTruthy();
    // The pane is the third step: modules.
    expect(await screen.findByRole('heading', { name: 'Modules to use' })).toBeTruthy();
    expect(screen.getByText('2 of 4 steps finished')).toBeTruthy();
  });

  it('marks a manual step as done through the readiness API with the company for a company step only', async () => {
    const s: Server = { sent: [], overview: overview(STEPS) };
    serve(s);
    open(WRITE, '/setup?step=step_users');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Mark as done' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ method: 'POST', url: '/api/cfg/readiness/kernel/tasks/step_users/complete', body: { companyId: null } });
  });

  it('skips a step with a note, and a company step carries the chosen company', async () => {
    const s: Server = { sent: [], overview: overview([task('step_languages_formats')]) };
    serve(s);
    open();
    const user = userEvent.setup();
    await user.type(await screen.findByRole('textbox', { name: /Why are you skipping/ }), 'later');
    await user.click(screen.getByRole('button', { name: 'Skip this step' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ url: '/api/cfg/readiness/kernel/tasks/step_languages_formats/skip', body: { companyId: COMPANY, note: 'later' } });
  });

  it('re-opens a step that was skipped', async () => {
    const s: Server = { sent: [], overview: overview([task('step_modules', { level: 'customer', status: 'skipped', note: 'not now' })]) };
    serve(s);
    open();
    expect(await screen.findByText('Skipped: not now')).toBeTruthy();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Do this step again' }));
    await waitFor(() => expect(s.sent[0]?.url).toBe('/api/cfg/readiness/kernel/tasks/step_modules/reopen'));
  });

  it('shows what the system found for a step it decides itself and offers a new check instead of a tick', async () => {
    serve({ sent: [], overview: overview([task('step_company_profile', { manual: false, messageKey: 'setup.messages.registration_missing' })]) });
    open();
    expect(await screen.findByText('No current tax or registration number is recorded.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Check again' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Mark as done' })).toBeNull();
    expect(screen.getByRole('link', { name: 'Open the screen' }).getAttribute('href')).toBe('/organization/companies');
  });

  it('FR-ADM-ONB-04 offers the import centre on the structure and users steps', async () => {
    serve({ sent: [], overview: overview([task('step_structure', { link: '/organization/org-units' })]) });
    open();
    expect((await screen.findByRole('link', { name: 'Open the import centre' })).getAttribute('href')).toBe('/admin/imports?type=org_units');
  });

  it('says so when every step is done or skipped and offers the checklist', async () => {
    serve({ sent: [], overview: overview(STEPS.map((s) => ({ ...s, status: 'done' as SetupStatus }))) });
    open();
    expect(await screen.findByText(/All steps are done or skipped/)).toBeTruthy();
  });

  it('a person who may only read sees the guide without buttons that change it', async () => {
    serve({ sent: [], overview: overview(STEPS) });
    open(READ);
    expect(await screen.findByText(/look at the set-up guide but not change it/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Mark as done' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Skip this step' })).toBeNull();
  });

  it('shows a denied message for a refused read', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ title: 'errors.access.forbidden' }, 403)));
    open(READ);
    expect(await screen.findByText('You may not see the set-up guide.')).toBeTruthy();
  });
});

describe('FR-ADM-ONB-03 the readiness checklist', () => {
  const items = [
    item('location', { messageKey: 'setup.messages.location_missing', link: '/organization/locations' }),
    item('numbering', { status: 'done', link: '/settings/numbering' }),
    task('notification_rules', { kind: 'item', titleKey: 'setup.items.notification_rules', manual: true, level: 'customer' }),
  ];

  it('shows each item with its state, the message about what is missing and the link that fixes it', async () => {
    serve({ sent: [], overview: overview(STEPS, [kernelModule(items)]) });
    open(WRITE, '/setup?view=checklist');
    const table = await screen.findByRole('table', { name: 'Core platform' });
    expect(within(table).getByText('The company has no location yet.')).toBeTruthy();
    expect(within(table).getByRole('link', { name: 'Fix: At least one location' }).getAttribute('href')).toBe('/organization/locations');
    expect(screen.getByText('1 of 2 required items done')).toBeTruthy();
    expect(screen.getByText('Set-up mode')).toBeTruthy();
  });

  it('refuses to go live with open required items until a reason is given, then sends it', async () => {
    const s: Server = { sent: [], overview: overview(STEPS, [kernelModule(items)]) };
    serve(s);
    open(WRITE, '/setup?view=checklist');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Take Core platform live' }));
    const dialog = await screen.findByRole('dialog', { name: 'Take Core platform live' });
    expect(within(dialog).getByText(/1 required items are still open/)).toBeTruthy();
    const confirm = within(dialog).getByRole('button', { name: 'Go live' });
    expect((confirm as HTMLButtonElement).disabled).toBe(true);
    await user.type(within(dialog).getByRole('textbox', { name: /Reason for going live/ }), 'Pilot only');
    await user.click(confirm);
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ url: '/api/cfg/readiness/kernel/go-live', body: { companyId: COMPANY, overrideReason: 'Pilot only' } });
  });

  it('goes live without a reason when every required item is done', async () => {
    const done = [item('location', { status: 'done' }), item('numbering', { status: 'done' })];
    const s: Server = { sent: [], overview: overview(STEPS, [kernelModule(done)]) };
    serve(s);
    open(WRITE, '/setup?view=checklist');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Take Core platform live' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).queryByRole('textbox')).toBeNull();
    await user.click(within(dialog).getByRole('button', { name: 'Go live' }));
    await waitFor(() => expect(s.sent[0]?.body).toMatchObject({ companyId: COMPANY, overrideReason: null }));
  });

  it('shows a refusal of the server beside the dialog', async () => {
    const s: Server = { sent: [], overview: overview(STEPS, [kernelModule(items)]), answer: () => json({ title: 'errors.setup.already_live' }, 409) };
    serve(s);
    open(WRITE, '/setup?view=checklist');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Take Core platform live' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByRole('textbox'), 'x');
    await user.click(within(dialog).getByRole('button', { name: 'Go live' }));
    expect(await within(dialog).findByRole('alert')).toBeTruthy();
  });

  it('shows a module that is live with the reason of an override, and no go-live button', async () => {
    const live = kernelModule(items, { isLive: true, liveAt: '2026-11-23T10:00:00Z', liveNote: 'Pilot only' });
    serve({ sent: [], overview: overview(STEPS, [live]) });
    open(WRITE, '/setup?view=checklist');
    expect(await screen.findByText(/Reason: Pilot only/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Take Core platform live' })).toBeNull();
  });

  it('ticks a manual item through the readiness API without a company (a customer-level item)', async () => {
    const s: Server = { sent: [], overview: overview(STEPS, [kernelModule(items)]) };
    serve(s);
    open(WRITE, '/setup?view=checklist');
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Mark as done' }));
    await waitFor(() => expect(s.sent[0]).toMatchObject({ url: '/api/cfg/readiness/kernel/tasks/notification_rules/complete', body: { companyId: null } }));
  });

  it('a person without the go-live right sees the checklist but no go-live button', async () => {
    serve({ sent: [], overview: overview(STEPS, [kernelModule(items)]) });
    open([...READ, 'cfg.setup_tasks.update'], '/setup?view=checklist');
    await screen.findByRole('table', { name: 'Core platform' });
    expect(screen.queryByRole('button', { name: 'Take Core platform live' })).toBeNull();
  });
});
