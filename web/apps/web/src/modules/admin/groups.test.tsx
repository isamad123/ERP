// The group screens (slice S1.13b; FR-ADM-USR-06, FR-ADM-ROL-04, FR-ADM-NAV-03): the list and its create body, members added
// and removed, a role given to the group with its reason, the "waiting for a second administrator" answer, ended roles greyed,
// the permission-denied state.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import type { GroupDetail } from '../../api/access';
import GroupsScreen from './groups';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';
const ADMIN = ['iam.user_groups.read', 'iam.user_groups.manage', 'iam.roles.read', 'iam.users.read'];

const detail: GroupDetail = {
  id: 'g1', code: 'finance-approvers', name: { en: 'Finance approvers' }, isActive: true, rowVersion: 2,
  members: [{ id: 'm1', displayName: 'Amna Requester', username: 'amna' }],
  roles: [
    { assignmentId: 'a1', roleId: 'r1', roleCode: 'approver', roleName: { en: 'Approver' }, companyId: null, validFrom: '2026-01-01', validTo: null, reason: 'Needed', inForce: true },
    { assignmentId: 'a2', roleId: 'r2', roleCode: 'viewer', roleName: { en: 'Viewer' }, companyId: COMPANY, validFrom: '2025-01-01', validTo: '2025-12-31', reason: null, inForce: false },
  ],
};

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

interface Server { posted: { method: string; url: string; body: unknown }[]; pending?: boolean; failMember?: boolean }

function serve(s: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.split('?')[0] ?? url;
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      s.posted.push({ method, url: path, body: init?.body ? JSON.parse(String(init.body)) : null });
      if (path === '/api/iam/user-groups') return json({ outcome: 'done', id: 'g-new', requestId: null }, 201);
      if (path.endsWith('/roles') && s.pending) return json({ outcome: 'pending', id: null, requestId: 'req1' }, 202);
      return json({ outcome: 'done', id: null, requestId: null });
    }
    if (path === '/api/iam/user-groups') return json({ items: [{ id: 'g1', code: 'finance-approvers', nameI18n: { en: 'Finance approvers' }, isActive: true, rowVersion: 2 }], next: null });
    if (path === '/api/iam/user-groups/g1/detail') return json(detail);
    if (path === '/api/iam/roles') return json({ items: [{ id: 'r1', code: 'approver', nameI18n: { en: 'Approver' } }], next: null });
    if (path === '/api/iam/people-lookup') return json([{ id: 'p2', displayName: 'Sara Khan', username: 'sara' }]);
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
              <Routes><Route path="/admin/groups/*" element={<GroupsScreen />} /></Routes>
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

describe('FR-ADM-USR-06 group list', () => {
  it('lists the groups and creates one with its names', async () => {
    const s: Server = { posted: [] };
    serve(s);
    show('/admin/groups');
    const grid = await screen.findByRole('grid', { name: 'Groups' });
    expect(await within(grid).findByText('Finance approvers')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'New group' }));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.type(within(dialog).getByRole('textbox', { name: /^Code/ }), 'hr-team');
    await userEvent.type(within(dialog).getByRole('textbox', { name: /^Name \(English\)/ }), 'HR team');
    await userEvent.click(within(dialog).getByRole('button', { name: 'New group' }));
    await waitFor(() => expect(s.posted).toEqual([{ method: 'POST', url: '/api/iam/user-groups', body: { code: 'hr-team', name: { en: 'HR team' } } }]));
  });

  it('shows no create button to someone who may not manage groups', async () => {
    serve({ posted: [] });
    show('/admin/groups', shell(['iam.user_groups.read']));
    await screen.findByRole('grid', { name: 'Groups' });
    expect(screen.queryByRole('button', { name: 'New group' })).toBeNull();
  });

  it('shows the permission-denied state when the server refuses', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ status: 403, title: 'errors.access.forbidden' }, 403)));
    show('/admin/groups');
    expect(await screen.findByText('You are not allowed to see groups.')).toBeTruthy();
  });
});

describe('FR-ADM-USR-06 members', () => {
  it('adds a person chosen from the lookup and removes one after confirmation', async () => {
    const s: Server = { posted: [] };
    serve(s);
    show('/admin/groups/g1');
    expect(await screen.findByText('1 members')).toBeTruthy();
    await userEvent.type(screen.getByRole('combobox', { name: /^Add a person/ }), 'sar');
    await userEvent.click(await screen.findByRole('option', { name: /Sara Khan/ }, { timeout: 3000 }));
    await userEvent.click(screen.getByRole('button', { name: 'Add to group' }));
    await waitFor(() => expect(s.posted).toContainEqual({ method: 'POST', url: '/api/iam/user-groups/g1/members', body: { userId: 'p2' } }));
    await userEvent.click(screen.getByRole('button', { name: 'Remove' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(/loses the roles it gives/)).toBeTruthy();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(s.posted).toContainEqual({ method: 'POST', url: '/api/iam/user-groups/g1/members/m1/remove', body: {} }));
  });
});

describe('FR-ADM-ROL-04 roles of a group', () => {
  it('greys ended assignments and offers "end" only for those in force', async () => {
    serve({ posted: [] });
    show('/admin/groups/g1');
    await userEvent.click(await screen.findByRole('tab', { name: 'Roles' }));
    const ended = (await screen.findByText('Viewer')).closest('tr');
    expect(ended?.className).toContain('erp-ended');
    expect(within(ended as HTMLElement).queryByRole('button', { name: 'End' })).toBeNull();
    const live = screen.getByText('Approver').closest('tr') as HTMLElement;
    expect(within(live).getByRole('button', { name: 'End' })).toBeTruthy();
  });

  it('gives a role with a reason, and says when a second administrator must approve', async () => {
    const s: Server = { posted: [], pending: true };
    serve(s);
    show('/admin/groups/g1');
    await userEvent.click(await screen.findByRole('tab', { name: 'Roles' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Give a role' }));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.click(within(dialog).getByRole('button', { name: /^Role/ }));
    await userEvent.click(await screen.findByRole('option', { name: /Approver/ }));
    await userEvent.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Quarter close');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Give a role' }));
    await waitFor(() => expect(s.posted).toEqual([{ method: 'POST', url: '/api/iam/user-groups/g1/roles', body: { roleId: 'r1', companyId: null, validFrom: null, validTo: null, reason: 'Quarter close' } }]));
    expect(await screen.findByText(/waiting for a second administrator/)).toBeTruthy();
  });
});

describe('FR-ADM-NAV-03 group record', () => {
  it('shows the rename and archive actions to managers only', async () => {
    serve({ posted: [] });
    show('/admin/groups/g1', shell(['iam.user_groups.read']));
    await screen.findByRole('heading', { name: 'Finance approvers' });
    expect(screen.queryByRole('group', { name: 'Group actions' })).toBeNull();
  });
});
