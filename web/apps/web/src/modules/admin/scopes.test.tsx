// Data scopes (slice S1.13b; FR-ADM-ROL-06, FR-ADM-NAV-03): creation per kind sends the definition the engine reads, attaching
// sends only the changed row with a reason and shows a pending answer as waiting, the test shows what the server counted.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import ScopesScreen from './scopes';
import { COMPANY, json, shell, show } from './access-test-support';

const ADMIN = ['iam.data_scopes.read', 'iam.roles.read', 'iam.roles.manage', 'iam.access.preview'];
interface Posted { method: string; url: string; body: unknown }

function serve(posted: Posted[], options: { pending?: boolean; fail?: number } = {}) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.split('?')[0] ?? url;
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      posted.push({ method, url: path, body: init?.body ? JSON.parse(String(init.body)) : null });
      if (path === '/api/iam/data-scopes') return json({ outcome: 'done', id: 's9', requestId: null }, 201);
      return options.pending ? json({ outcome: 'pending', id: null, requestId: 'q1' }, 202) : json({ outcome: 'done', id: 'r1', requestId: null });
    }
    if (options.fail) return json({ status: options.fail, title: 'errors.access.forbidden' }, options.fail);
    if (path === '/api/iam/data-scopes') return json({ items: [{ id: 's1', code: 'own_branch', nameI18n: { en: 'Own branch' }, kind: 'branches', definition: { branch_ids: ['b1', 'b2'] } }], next: null });
    if (path === '/api/iam/roles') return json({ items: [{ id: 'r1', code: 'hr_officer', nameI18n: { en: 'HR officer' }, isActive: true }], next: null });
    if (path === '/api/iam/roles/r1/summary') return json({ id: 'r1', code: 'hr_officer', name: { en: 'HR officer' }, source: 'custom', baselineCode: null, isActive: true, rowVersion: 1, isPrivileged: false, permissions: [], dataScopes: [{ resource: 'hrm.employees', dataScopeId: 's1', code: 'own_branch', kind: 'branches' }], fieldPolicies: [], userCount: 2 });
    if (path === '/api/iam/permission-catalog') return json([{ code: 'hrm.employees.read', module: 'hrm', resource: 'hrm.employees', action: 'read', descriptionKey: 'x', isSensitive: false, roles: [] }]);
    if (path === '/api/iam/people-lookup') return json([{ id: 'p2', displayName: 'Sara Khan', username: 'sara' }]);
    if (path === '/api/iam/data-scope-preview') return json({ resource: 'hrm.employees', userId: 'p2', count: 7, firstPage: ['id-1', 'id-2'] });
    if (path === '/api/org/branches') return json({ items: [{ id: 'b1', code: 'KHI', nameI18n: { en: 'Karachi' } }] });
    return json({ status: 404, title: 'errors.data.not_found' }, 404);
  }));
}

const open = (path = '/admin/data-scopes', value = shell(ADMIN)) => show('/admin/data-scopes/*', path, <ScopesScreen />, value);

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-ADM-ROL-06 data scope list', () => {
  it('lists scopes with kind in plain words and what they hold', async () => {
    serve([]);
    open();
    expect(await screen.findByText('Own branch')).toBeTruthy();
    expect(screen.getByText('Branches')).toBeTruthy();
    expect(screen.getByText('2 branches')).toBeTruthy();
  });

  it('shows the permission-denied state when the server refuses', async () => {
    serve([], { fail: 403 });
    open();
    expect(await screen.findByText('You may not see data scopes.')).toBeTruthy();
  });

  it('shows the error state with a retry on a server failure', async () => {
    serve([], { fail: 500 });
    open();
    expect(await screen.findByText('The data scopes could not be loaded. Try again.')).toBeTruthy();
  });

  it('offers creation only to someone who may manage roles', async () => {
    serve([]);
    open('/admin/data-scopes', shell(['iam.data_scopes.read']));
    await screen.findByText('Own branch');
    expect(screen.queryByRole('button', { name: 'New data scope' })).toBeNull();
  });
});

describe('FR-ADM-ROL-06 create a data scope', () => {
  it('sends company ids for a companies scope', async () => {
    const posted: Posted[] = [];
    serve(posted);
    open('/admin/data-scopes/new');
    await userEvent.type(await screen.findByRole('textbox', { name: /^Code/ }), 'all_c');
    await userEvent.type(screen.getByRole('textbox', { name: /^Name \(English\)/ }), 'All companies');
    await userEvent.click(screen.getByRole('button', { name: /Kind/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'Companies' }));
    await userEvent.click(await screen.findByRole('checkbox', { name: /First company/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Create scope' }));
    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]?.body).toEqual({ code: 'all_c', name: { en: 'All companies' }, kind: 'companies', definition: { company_ids: [COMPANY] } });
  });

  it('sends branch ids for a branches scope and an empty definition for own records', async () => {
    const posted: Posted[] = [];
    serve(posted);
    open('/admin/data-scopes/new');
    await userEvent.type(await screen.findByRole('textbox', { name: /^Code/ }), 'mine');
    await userEvent.type(screen.getByRole('textbox', { name: /^Name \(English\)/ }), 'Mine');
    expect((screen.getByRole('button', { name: 'Create scope' }) as HTMLButtonElement).disabled).toBe(false);
    await userEvent.click(screen.getByRole('button', { name: 'Create scope' }));
    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]?.body).toMatchObject({ kind: 'own', definition: {} });

    cleanup();
    posted.length = 0;
    open('/admin/data-scopes/new');
    await userEvent.type(await screen.findByRole('textbox', { name: /^Code/ }), 'khi');
    await userEvent.type(screen.getByRole('textbox', { name: /^Name \(English\)/ }), 'Karachi');
    await userEvent.click(screen.getByRole('button', { name: /Kind/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'Branches' }));
    await userEvent.click(await screen.findByRole('checkbox', { name: /Karachi \(KHI\)/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Create scope' }));
    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]?.body).toMatchObject({ kind: 'branches', definition: { branch_ids: ['b1'] } });
  });
});

describe('FR-ADM-ROL-06 attach to a role', () => {
  it('removes one row with a reason and says when a second administrator must approve', async () => {
    const posted: Posted[] = [];
    serve(posted, { pending: true });
    open();
    await userEvent.click(await screen.findByRole('tab', { name: 'Attach to a role' }));
    await userEvent.click(await screen.findByRole('button', { name: /Role/ }));
    await userEvent.click(await screen.findByRole('option', { name: /HR officer/ }));
    const row = await screen.findByRole('row', { name: /hrm\.employees/ });
    await userEvent.click(within(row).getByRole('button', { name: 'Remove' }));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.type(within(dialog).getByRole('textbox'), 'No longer needed');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]).toMatchObject({ method: 'PUT', url: '/api/iam/roles/r1/data-scopes', body: { scopes: [{ resource: 'hrm.employees', dataScopeId: null }], reason: 'No longer needed' } });
    expect(await screen.findByText('Waiting for a second administrator to approve this change.')).toBeTruthy();
  });
});

describe('FR-ADM-ROL-06 test what a person would see', () => {
  it('shows the count and the first ids the server returned', async () => {
    serve([]);
    open();
    await userEvent.click(await screen.findByRole('tab', { name: 'Test' }));
    await userEvent.type(await screen.findByRole('combobox', { name: /^Person/ }), 'sar');
    await userEvent.click(await screen.findByRole('option', { name: /Sara Khan/ }, { timeout: 3000 }));
    await userEvent.click(screen.getByRole('button', { name: /Resource/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'hrm.employees' }));
    await userEvent.click(screen.getByRole('button', { name: 'Run the test' }));
    expect(await screen.findByText('7 records visible to Sara Khan')).toBeTruthy();
    expect(screen.getByText('id-1')).toBeTruthy();
    expect(screen.getByText('id-2')).toBeTruthy();
  });
});
