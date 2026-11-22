// The role screens (slice S1.13b Build items 6 and 7; FR-ADM-ROL-01, -02, -03, FR-ADM-NAV-03): the list shows counts and
// shipped marks from the server; create and copy send the right body; a permission change asks for a reason and an answer
// that waits for a second administrator is shown as such; the Tenant Admin role is read-only; two roles are compared side
// by side; the catalogue groups and searches; refusals show the permission-denied and error states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import type { CatalogEntry, RoleRecord, RoleSummary } from '../../api/access';
import { groupCatalogue } from './roles-catalogue';
import RolesScreen from './roles';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';
const ADMIN = ['iam.roles.read', 'iam.roles.manage', 'iam.permissions.read', 'aud.history.view'];

const role = (id: string, code: string, name: string, extra: Partial<RoleRecord> = {}): RoleRecord => ({ id, code, nameI18n: { en: name }, descriptionI18n: null, source: 'custom', baselineCode: null, isActive: true, rowVersion: 1, ...extra });
const summary = (r: RoleRecord, permissions: string[], userCount: number): RoleSummary => ({
  id: r.id, code: r.code, name: r.nameI18n, source: r.source, baselineCode: r.baselineCode, isActive: r.isActive, rowVersion: r.rowVersion, isPrivileged: false, permissions, dataScopes: [], fieldPolicies: [], userCount,
});
const entry = (code: string, sensitive = false, roles: string[] = []): CatalogEntry => {
  const [module = '', resource = '', action = ''] = code.split('.');
  return { code, module, resource: `${module}.${resource}`, action, descriptionKey: `permissions.${code}`, isSensitive: sensitive, roles };
};

const APPROVER = role('r1', 'approver', 'Approver', { source: 'baseline', baselineCode: 'approver' });
const CLERK = role('r2', 'clerk', 'Clerk');
const ADMINROLE = role('r3', 'tenant_admin', 'Tenant Admin', { source: 'baseline', baselineCode: 'tenant_admin' });
const CATALOG = [entry('iam.users.read', false, ['approver']), entry('iam.users.create', true), entry('wf.workflow_tasks.read', false, ['clerk'])];

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
  roles?: RoleRecord[];
  perms?: Record<string, string[]>;
  pending?: boolean;
  posted: { method: string; url: string; body: unknown }[];
}

function serve(s: Server) {
  const all = s.roles ?? [APPROVER, CLERK, ADMINROLE];
  const perms = s.perms ?? { r1: ['iam.users.read'], r2: ['iam.users.read', 'wf.workflow_tasks.read'], r3: ['iam.users.read', 'iam.roles.manage', 'iam.roles.assign'] };
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.split('?')[0] ?? url;
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      s.posted.push({ method, url: path, body: init?.body ? JSON.parse(String(init.body)) : null });
      if (path === '/api/iam/roles') return json({ outcome: 'done', id: 'new-role', requestId: null }, 201);
      if (path.endsWith('/permissions') && s.pending) return json({ outcome: 'pending', id: null, requestId: 'req-1' }, 202);
      return json({ outcome: 'done', id: null, requestId: null });
    }
    if (path === '/api/iam/roles') return json({ items: all, next: null });
    if (path === '/api/iam/permission-catalog') return json(CATALOG);
    if (path === '/api/iam/roles/compare') {
      const q = new URL(url, 'http://x').searchParams;
      return json({ left: q.get('left'), right: q.get('right'), onlyLeft: [], onlyRight: ['wf.workflow_tasks.read'], both: ['iam.users.read'] });
    }
    const m = /^\/api\/iam\/roles\/([^/]+)\/summary$/.exec(path);
    const found = m ? all.find((r) => r.id === m[1]) : undefined;
    if (found) return json(summary(found, perms[found.id] ?? [], found.id === 'r1' ? 3 : 0));
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
              <Routes><Route path="/admin/roles/*" element={<RolesScreen />} /></Routes>
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

describe('FR-ADM-ROL-02 FR-ADM-ROL-03 FR-ADM-NAV-03 role list', () => {
  it('lists name, shipped or custom, status, people and permission counts', async () => {
    serve({ posted: [] });
    show('/admin/roles');
    const grid = await screen.findByRole('grid', { name: 'Roles' });
    expect(await within(grid).findByText('Approver')).toBeTruthy();
    expect(within(grid).getAllByText('Shipped').length).toBe(2);
    expect(within(grid).getByText('Custom')).toBeTruthy();
    expect(within(grid).getByText('Protected')).toBeTruthy();
    const approver = within(grid).getAllByRole('row').find((r) => within(r).queryByText('Approver'));
    await waitFor(() => expect(within(approver!).getByText('3')).toBeTruthy()); // the approver's people
    expect(within(approver!).getByText('1')).toBeTruthy(); // and its permissions
    expect(within(grid).getAllByText('Active').length).toBe(3);
  });

  it('creates a role as a copy of a shipped role and sends the baseline code', async () => {
    const s: Server = { posted: [] };
    serve(s);
    show('/admin/roles');
    await userEvent.click(await screen.findByRole('button', { name: 'Create role' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create a role' });
    await userEvent.type(within(dialog).getByRole('textbox', { name: /^Code/ }), 'my_approver');
    await userEvent.type(within(dialog).getByRole('textbox', { name: /^Name in English/ }), 'My approver');
    await userEvent.click(within(dialog).getByRole('button', { name: /Start from/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'A copy of a shipped role' }));
    await userEvent.click(within(dialog).getByRole('button', { name: /Role to copy/ }));
    await userEvent.click(await screen.findByRole('option', { name: /Approver/ }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create role' }));
    await waitFor(() => expect(s.posted).toHaveLength(1));
    expect(s.posted[0]).toEqual({ method: 'POST', url: '/api/iam/roles', body: { code: 'my_approver', name: { en: 'My approver' }, copyFromBaseline: 'approver' } });
  });

  it('offers no create button to someone who may only read roles', async () => {
    serve({ posted: [] });
    show('/admin/roles', shell(['iam.roles.read']));
    await screen.findByRole('grid', { name: 'Roles' });
    expect(screen.queryByRole('button', { name: 'Create role' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Permission catalogue' })).toBeNull();
  });

  it('shows the permission-denied state when the server refuses the list', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ status: 403, title: 'errors.access.forbidden' }, 403)));
    show('/admin/roles');
    expect(await screen.findByText('You are not allowed to see roles.')).toBeTruthy();
  });

  it('shows an error state with a retry when the list cannot be loaded', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ status: 500, title: 'errors.server' }, 500)));
    show('/admin/roles');
    expect(await screen.findByText('The roles could not be loaded.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });
});

describe('FR-ADM-ROL-02 role permissions', () => {
  it('asks for a reason, sends the full permission list and says when a second administrator must approve', async () => {
    const s: Server = { posted: [], pending: true };
    serve(s);
    show('/admin/roles/r2');
    await userEvent.click(await screen.findByRole('checkbox', { name: /^iam\.users\.create/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Save permissions' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(/1 permissions are added and 0 removed/)).toBeTruthy();
    await userEvent.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Needs to create users');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save permissions' }));
    await waitFor(() => expect(s.posted).toHaveLength(1));
    expect(s.posted[0]).toEqual({ method: 'PUT', url: '/api/iam/roles/r2/permissions', body: { permissions: ['iam.users.create', 'iam.users.read', 'wf.workflow_tasks.read'], reason: 'Needs to create users' } });
    expect(await screen.findByText('The change is waiting for a second administrator. Find it under Approvals.')).toBeTruthy();
  });

  it('selects all permissions of a resource at once', async () => {
    serve({ posted: [] });
    show('/admin/roles/r1');
    await userEvent.click(await screen.findByRole('checkbox', { name: /^Select all in iam\.users/ }));
    expect((screen.getByRole('checkbox', { name: /^iam\.users\.create/ }) as HTMLInputElement).checked).toBe(true);
    expect(screen.getByText('2 permissions selected')).toBeTruthy();
  });

  it('shows the protected Tenant Admin role read-only with no save and no archive', async () => {
    serve({ posted: [] });
    show('/admin/roles/r3');
    expect(await screen.findByText('Protected role')).toBeTruthy();
    expect((await screen.findByRole('checkbox', { name: /^iam\.users\.read/ }) as HTMLInputElement).disabled).toBe(true);
    expect(screen.queryByRole('button', { name: 'Save permissions' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Archive' })).toBeNull();
  });

  it('shows a role not found state for an unknown role', async () => {
    serve({ posted: [] });
    show('/admin/roles/nope');
    expect(await screen.findByText('Role not found')).toBeTruthy();
  });
});

describe('FR-ADM-ROL-02 compare two roles', () => {
  it('shows only-left, both and only-right columns', async () => {
    serve({ posted: [] });
    show('/admin/roles/compare?left=r1&right=r2');
    const both = await screen.findByRole('region', { name: 'In both' });
    expect(within(both).getByText('iam.users.read')).toBeTruthy();
    const right = screen.getByRole('region', { name: 'Only in Clerk' });
    expect(within(right).getByText('wf.workflow_tasks.read')).toBeTruthy();
    expect(within(screen.getByRole('region', { name: 'Only in Approver' })).getByText('None')).toBeTruthy();
  });
});

describe('FR-ADM-ROL-01 permission catalogue', () => {
  it('groups permissions by module and resource and marks sensitive ones in words', async () => {
    serve({ posted: [] });
    show('/admin/roles/catalogue');
    expect(await screen.findByRole('region', { name: 'iam' })).toBeTruthy();
    expect(screen.getByRole('region', { name: 'wf' })).toBeTruthy();
    expect(screen.getByText('Sensitive')).toBeTruthy();
    expect(await screen.findByText('Approver')).toBeTruthy(); // held by (role name from the role list)
  });

  it('searches by code and shows an empty state', async () => {
    serve({ posted: [] });
    show('/admin/roles/catalogue');
    await screen.findByRole('region', { name: 'wf' });
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search permissions' }), 'workflow');
    expect(screen.queryByRole('region', { name: 'iam' })).toBeNull();
    await userEvent.clear(screen.getByRole('searchbox', { name: 'Search permissions' }));
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search permissions' }), 'zzz');
    expect(await screen.findByText('No permission matches')).toBeTruthy();
  });

  it('shows the permission-denied state', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ status: 403, title: 'errors.access.forbidden' }, 403)));
    show('/admin/roles/catalogue');
    expect(await screen.findByText('You are not allowed to see the permission catalogue.')).toBeTruthy();
  });

  it('groups entries by module, then resource, in code order', () => {
    const groups = groupCatalogue(CATALOG, '');
    expect(groups.map((g) => g.module)).toEqual(['iam', 'wf']);
    expect(groups[0]?.resources[0]?.entries.map((e) => e.code)).toEqual(['iam.users.create', 'iam.users.read']);
  });
});
