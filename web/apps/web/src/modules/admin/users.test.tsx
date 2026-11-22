// The user console (slice S1.13a; FR-ADM-USR-01 to -05, -08, -09, FR-KRN-IAM-18): the list shows what the server sent and the
// seat bar only to licence readers; the create form refuses at the limit before anything is sent; the record offers only the
// allowed transitions; disabling a person with open approvals shows the count and reassigns to the chosen person; a refused
// username says "already used to sign in"; a bulk change shows the count first and one result per person.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import { transitionsFrom, type UserAccess, type UserRecord, type UserRow } from '../../api/users';
import UsersScreen from './users';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';
const ADMIN = ['iam.users.read', 'iam.users.create', 'iam.users.update', 'iam.users.change_status', 'iam.users.unlock', 'iam.users.reset_mfa', 'iam.users.revoke_sessions',
  'iam.users.read_sessions', 'iam.users.send_invitation', 'iam.roles.assign', 'wf.workflow_tasks.reassign', 'cfg.licence.read'];

const row = (id: string, name: string, status: UserRow['status'], extra: Partial<UserRow> = {}): UserRow => ({
  id, username: name.toLowerCase().replace(' ', '.'), displayName: name, email: `${name.toLowerCase().replace(' ', '.')}@x.test`, status, lastLoginAt: null, defaultCompanyId: COMPANY,
  accessValidTo: null, hasMfa: false, roles: [{ roleId: 'r1', code: 'approver', name: { en: 'Approver' } }], companyIds: [COMPANY], allCompanies: false, ...extra,
});

const record = (id: string, name: string, status: UserRecord['status']): UserRecord => ({
  id, username: name.toLowerCase().replace(' ', '.'), email: 'x@x.test', displayName: name, defaultCompanyId: COMPANY, language: 'en', timeZone: 'Asia/Karachi', lastLoginAt: null,
  accessValidTo: null, status, isSystem: false, rowVersion: 3,
});

const emptyAccess = (id: string): UserAccess => ({ userId: id, roles: [], groups: [], scopes: [], delegations: [], mfaDevices: [], trustedDevices: [] });

function shell(permissions: string[] = ADMIN, writable = true): ShellValue {
  const grants = permissions.map((code) => ({ code, companies: null }));
  return {
    me: { userId: 'u-me', displayName: 'Me', language: 'en', timeZone: 'Asia/Karachi', defaultCompanyId: COMPANY, companies: [{ id: COMPANY, code: 'C1', name: { en: 'First company' } }],
      permissions: grants, mfa: { required: false, methods: [], enrolBy: null }, numbers: { digits: 'western', grouping: 'international' } },
    capabilities: { modules: [], features: [], flags: [], state: 'active', nextState: null, nextStateOn: null, warnings: [] },
    facts: accessFacts([], grants, 'active'),
    can: (code: string) => permissions.includes(code),
    writable,
    activeCompany: null,
    setActiveCompany: () => undefined,
  };
}

interface Server {
  rows?: UserRow[];
  record?: UserRecord;
  seats?: { used: number; max: number };
  openTasks?: number;
  failCreate?: Response;
  posted: { method: string; url: string; body: unknown }[];
}

function serve(s: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.split('?')[0] ?? url;
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      s.posted.push({ method, url: path, body: init?.body ? JSON.parse(String(init.body)) : null });
      if (path === '/api/iam/users' && s.failCreate) return s.failCreate;
      if (path === '/api/iam/users') return json({ ...record('new-id', 'New Person', 'invited') }, 201);
      if (path.endsWith('/status')) return json(s.openTasks ? { openWorkflowTasks: s.openTasks } : undefined, s.openTasks ? 200 : 204);
      if (path.endsWith('/reassign-open-tasks')) return json({ done: s.openTasks ?? 0, refused: 0, results: [] });
      if (path.startsWith('/api/iam/users/') && method === 'PUT') return json(record('u1', 'Updated', 'active'));
      return new Response(null, { status: 204 });
    }
    if (path === '/api/iam/user-overview') return json({ items: s.rows ?? [], next: null });
    if (path === '/api/cfg/licence') return json({ limits: s.seats ? [{ key: 'users.named', max: s.seats.max, used: s.seats.used, percent: Math.floor((s.seats.used * 100) / s.seats.max) }] : [] });
    if (path === '/api/iam/roles') return json({ items: [{ id: 'r1', code: 'approver', nameI18n: { en: 'Approver' } }], next: null });
    if (path === '/api/iam/people-lookup') return json([{ id: 'p2', displayName: 'Sara Khan', username: 'sara' }]);
    if (path.endsWith('/open-tasks')) return json(Array.from({ length: s.openTasks ?? 0 }, (_, i) => ({ taskId: `t${i}`, instanceId: 'i', documentType: 'd', documentId: 'd', companyId: COMPANY, title: { en: 'x' }, stepKey: 's', receivedAt: '2026-11-20T00:00:00Z', dueAt: null, overdue: false })));
    if (path.endsWith('/access')) return json(emptyAccess('u1'));
    if (path.endsWith('/sessions')) return json([]);
    if (s.record && path === `/api/iam/users/${s.record.id}`) return json(s.record);
    return json({ status: 404, title: 'errors.data.not_found' }, 404);
  }));
}

function show(path: string, value = shell()) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <I18nRoot locale="en">
        <ToastProvider>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <ShellValueProvider value={value}>
              <Routes><Route path="/admin/users/*" element={<UsersScreen />} /></Routes>
            </ShellValueProvider>
          </QueryClientProvider>
        </ToastProvider>
      </I18nRoot>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('FR-ADM-USR-03 allowed transitions', () => {
  it('offers only the moves the status rules allow and never activation of an invited person', () => {
    expect(transitionsFrom('invited')).toEqual(['archived']);
    expect(transitionsFrom('active')).toEqual(['locked', 'disabled']);
    expect(transitionsFrom('locked')).toEqual(['active', 'disabled']);
    expect(transitionsFrom('disabled')).toEqual(['active', 'archived']);
    expect(transitionsFrom('archived')).toEqual([]);
  });
});

describe('FR-ADM-USR-01 FR-ADM-USR-08 user list', () => {
  it('lists name, username, status, roles and second-step state, with the seat bar for licence readers only', async () => {
    serve({ rows: [row('u1', 'Amna Requester', 'active', { hasMfa: true }), row('u2', 'Bilal Locked', 'locked')], seats: { used: 9, max: 10 }, posted: [] });
    show('/admin/users');
    const grid = await screen.findByRole('grid', { name: 'Users' });
    expect(await within(grid).findByText('Amna Requester')).toBeTruthy();
    expect(within(grid).getAllByText('Approver').length).toBe(2);
    expect(within(grid).getByText('Locked')).toBeTruthy();
    expect(await screen.findByText('9 of 10 named users')).toBeTruthy();
    expect(screen.getByText('90 percent of the named users are in use')).toBeTruthy();
  });

  it('shows no seat bar to someone who may not read the licence', async () => {
    serve({ rows: [row('u1', 'Amna Requester', 'active')], seats: { used: 9, max: 10 }, posted: [] });
    show('/admin/users', shell(ADMIN.filter((p) => p !== 'cfg.licence.read')));
    await screen.findByText('Amna Requester');
    expect(screen.queryByText('9 of 10 named users')).toBeNull();
  });

  it('shows the permission-denied state when the server refuses the list', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ status: 403, title: 'errors.access.forbidden' }, 403)));
    show('/admin/users');
    expect(await screen.findByText('You are not allowed to see users.')).toBeTruthy();
  });
});

describe('FR-ADM-USR-02 FR-KRN-IAM-18 create and invite', () => {
  it('refuses in plain words at the user limit and sends nothing', async () => {
    const s: Server = { seats: { used: 10, max: 10 }, posted: [] };
    serve(s);
    show('/admin/users/new');
    expect(await screen.findByText('The licence has no free named user place left, so nobody new can be added. Disable someone who no longer needs access, or ask your provider for more places.')).toBeTruthy();
    const submit = screen.getByRole('button', { name: 'Create and send invitation' });
    expect((submit as HTMLButtonElement).disabled).toBe(true);
    expect(s.posted).toEqual([]);
  });

  it('says "already used to sign in" when a username clashes with another person\'s e-mail, and keeps the form', async () => {
    const s: Server = { posted: [], failCreate: json({ status: 400, title: 'errors.data.rule_violated', detail: 'ck_users_username_email_clash' }, 400) };
    serve(s);
    show('/admin/users/new');
    await userEvent.type(await screen.findByRole('textbox', { name: /^Name/ }), 'New Person');
    await userEvent.type(screen.getByRole('textbox', { name: /^Username/ }), 'clash@x.test');
    await userEvent.type(screen.getByRole('textbox', { name: /^E-mail/ }), 'new@x.test');
    await userEvent.click(screen.getByRole('button', { name: 'Create and send invitation' }));
    expect(await screen.findAllByText(/Already used to sign in/)).not.toHaveLength(0);
    expect(s.posted.filter((p) => p.url === '/api/iam/users')).toHaveLength(1);
    expect(s.posted.some((p) => p.url.endsWith('/invitation'))).toBe(false);
  });

  it('creates the person and sends the invitation', async () => {
    const s: Server = { posted: [] };
    serve(s);
    show('/admin/users/new');
    await userEvent.type(await screen.findByRole('textbox', { name: /^Name/ }), 'New Person');
    await userEvent.type(screen.getByRole('textbox', { name: /^Username/ }), 'new.person');
    await userEvent.type(screen.getByRole('textbox', { name: /^E-mail/ }), 'new@x.test');
    await userEvent.click(screen.getByRole('button', { name: 'Create and send invitation' }));
    await waitFor(() => expect(s.posted.map((p) => p.url)).toEqual(['/api/iam/users', '/api/iam/users/new-id/invitation']));
    expect(s.posted[0]?.body).toMatchObject({ username: 'new.person', email: 'new@x.test', displayName: 'New Person', language: 'en' });
  });
});

describe('FR-ADM-USR-03 FR-ADM-USR-05 account actions', () => {
  it('offers only what the status allows: a locked person can be unlocked or disabled, never locked again', async () => {
    serve({ record: record('u1', 'Bilal Locked', 'locked'), posted: [] });
    show('/admin/users/u1');
    const actions = await screen.findByRole('group', { name: 'Account actions' });
    const names = within(actions).getAllByRole('button').map((b) => b.textContent);
    expect(names).toContain('Unlock');
    expect(names).toContain('Disable');
    expect(names).not.toContain('Lock');
    expect(names).not.toContain('Activate');
  });

  it('asks for a reason, states the effect, and sends the reason with the unlock', async () => {
    const s: Server = { record: record('u1', 'Bilal Locked', 'locked'), posted: [] };
    serve(s);
    show('/admin/users/u1');
    await userEvent.click(await screen.findByRole('button', { name: 'Unlock' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(/can sign in again/)).toBeTruthy();
    const confirm = within(dialog).getByRole('button', { name: 'Unlock' });
    expect((confirm as HTMLButtonElement).disabled).toBe(true);
    await userEvent.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Verified by phone');
    await userEvent.click(confirm);
    await waitFor(() => expect(s.posted).toEqual([{ method: 'POST', url: '/api/iam/users/u1/unlock', body: { reason: 'Verified by phone' } }]));
  });

  it('disabling a person with two open approvals shows the count and reassigns them to the chosen person', async () => {
    const s: Server = { record: record('u1', 'Amna Requester', 'active'), openTasks: 2, posted: [] };
    serve(s);
    show('/admin/users/u1');
    await userEvent.click(await screen.findByRole('button', { name: 'Disable' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(await within(dialog).findByText('2 approvals are waiting for this person')).toBeTruthy();
    await userEvent.type(within(dialog).getByRole('combobox', { name: /^Reassign to/ }), 'sar');
    await userEvent.click(await screen.findByRole('option', { name: /Sara Khan/ }, { timeout: 3000 }));
    await userEvent.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Left the company');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Disable' }));
    await waitFor(() => expect(s.posted.map((p) => p.url)).toEqual(['/api/iam/users/u1/status', '/api/wf/users/u1/reassign-open-tasks']));
    expect(s.posted[0]?.body).toEqual({ status: 'disabled', reason: 'Left the company' });
    expect(s.posted[1]?.body).toEqual({ toUserId: 'p2', reason: 'Left the company' });
  });

  it('shows no account action to someone who may not change users', async () => {
    serve({ record: record('u1', 'Amna Requester', 'active'), posted: [] });
    show('/admin/users/u1', shell(['iam.users.read']));
    await screen.findByRole('heading', { name: 'Amna Requester' });
    expect(screen.queryByRole('group', { name: 'Account actions' })).toBeNull();
  });
});

describe('FR-ADM-USR-09 bulk actions', () => {
  it('shows the count first, then one result per person with the failure listed', async () => {
    const s: Server = { rows: [row('u1', 'Amna Requester', 'active'), row('u2', 'Bilal Locked', 'locked')], posted: [] };
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      const path = url.split('?')[0] ?? url;
      if ((init?.method ?? 'GET') === 'POST') {
        s.posted.push({ method: 'POST', url: path, body: JSON.parse(String(init?.body)) });
        return path === '/api/iam/users/u2/status' ? json({ status: 400, title: 'errors.user.transition_not_allowed' }, 400) : new Response(null, { status: 204 });
      }
      if (path === '/api/iam/user-overview') return json({ items: s.rows, next: null });
      if (path === '/api/cfg/licence') return json({ limits: [] });
      if (path === '/api/iam/roles') return json({ items: [], next: null });
      return json({ status: 404, title: 'errors.data.not_found' }, 404);
    }));
    show('/admin/users');
    const grid = await screen.findByRole('grid', { name: 'Users' });
    await within(grid).findByText('Amna Requester');
    await userEvent.click(within(grid).getAllByRole('checkbox')[0]!); // the header box selects every row on the page
    await userEvent.click(await screen.findByRole('button', { name: /Actions for 2 selected/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Change several users' });
    await userEvent.click(within(dialog).getByRole('button', { name: /What to do/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'Disable' }));
    await userEvent.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Project ended');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Continue' }));
    expect(await within(dialog).findByText('2 users will be disabled and signed out everywhere.')).toBeTruthy();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Change 2 users' }));
    expect(await within(dialog).findByText('1 done, 1 not done.')).toBeTruthy();
    const results = within(dialog).getByRole('list', { name: 'Result for each user' });
    expect(within(results).getAllByRole('listitem')).toHaveLength(2);
    expect(within(results).getByText(/not done:/)).toBeTruthy();
  });
});
