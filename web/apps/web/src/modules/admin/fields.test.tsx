// Field security (slice S1.13b; FR-ADM-ROL-07, FR-ADM-NAV-03): the matrix shows the rules the server holds, a changed cell sends
// only the changed rows with a reason, a pending answer is shown as waiting, and the preview shows a masked field as dots.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import FieldsScreen from './fields';
import { json, shell, show } from './access-test-support';

const ADMIN = ['iam.roles.read', 'iam.roles.manage', 'iam.access.preview'];
interface Posted { method: string; url: string; body: unknown }

function serve(posted: Posted[], options: { pending?: boolean; fail?: number } = {}) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.split('?')[0] ?? url;
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      posted.push({ method, url: path, body: init?.body ? JSON.parse(String(init.body)) : null });
      return options.pending ? json({ outcome: 'pending', id: null, requestId: 'q1' }, 202) : json({ outcome: 'done', id: 'r1', requestId: null });
    }
    if (options.fail) return json({ status: options.fail, title: 'errors.access.forbidden' }, options.fail);
    if (path === '/api/iam/roles') return json({ items: [{ id: 'r1', code: 'hr_officer', nameI18n: { en: 'HR officer' }, isActive: true }], next: null });
    if (path === '/api/iam/roles/r1/summary') return json({ id: 'r1', code: 'hr_officer', name: { en: 'HR officer' }, source: 'custom', baselineCode: null, isActive: true, rowVersion: 1, isPrivileged: false, permissions: [], dataScopes: [], fieldPolicies: [{ resource: 'iam.users', field: 'email', access: 'masked' }], userCount: 1 });
    if (path === '/api/iam/roles/r1/field-preview') return json({ resource: 'iam.users', roleId: 'r1', fields: [{ field: 'email', fieldClass: 'personal', access: 'masked' }, { field: 'username', fieldClass: null, access: 'editable' }, { field: 'access_valid_to', fieldClass: null, access: 'hidden' }] });
    return json({ status: 404, title: 'errors.data.not_found' }, 404);
  }));
}

const open = (value = shell(ADMIN)) => show('/admin/field-security/*', '/admin/field-security', <FieldsScreen />, value);

async function choose(table: string) {
  await userEvent.click(await screen.findByRole('button', { name: /Table/ }));
  await userEvent.click(await screen.findByRole('option', { name: table }));
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-ADM-ROL-07 field matrix', () => {
  it('shows the rule the server holds and the field class', async () => {
    serve([]);
    open();
    await choose('iam.users');
    const row = await screen.findByRole('row', { name: /email/ });
    expect(within(row).getByText('personal')).toBeTruthy();
    expect(within(row).getByRole('button', { name: /Masked/ })).toBeTruthy();
  });

  it('sends only the changed row, with a reason, and shows a pending answer as waiting', async () => {
    const posted: Posted[] = [];
    serve(posted, { pending: true });
    open();
    await choose('iam.users');
    const row = await screen.findByRole('row', { name: /username/ });
    await userEvent.click(within(row).getByRole('button', { name: /Default/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'Hidden' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save 1 change' }));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.type(within(dialog).getByRole('textbox'), 'Privacy review');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]).toMatchObject({ method: 'PUT', url: '/api/iam/roles/r1/field-policies', body: { fields: [{ resource: 'iam.users', field: 'username', access: 'hidden' }], reason: 'Privacy review' } });
    expect(await screen.findByText('Waiting for a second administrator to approve this change.')).toBeTruthy();
  });

  it('offers no change to someone who may only read roles', async () => {
    serve([]);
    open(shell(['iam.roles.read', 'iam.access.preview']));
    await choose('iam.users');
    const row = await screen.findByRole('row', { name: /username/ });
    expect((within(row).getByRole('button', { name: /Default/ }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole('button', { name: /^Save/ })).toBeNull();
  });

  it('shows the permission-denied state when the server refuses', async () => {
    serve([], { fail: 403 });
    open();
    await choose('iam.users');
    expect(await screen.findByText('You may not see field security.')).toBeTruthy();
  });
});

describe('FR-ADM-ROL-07 preview the form as a role', () => {
  it('shows a masked field as dots, a hidden one struck and named, and an editable one as an input', async () => {
    serve([]);
    open();
    await choose('iam.users');
    await userEvent.click(await screen.findByRole('tab', { name: 'Preview as a role' }));
    await userEvent.click(await screen.findByRole('button', { name: /Role/ }));
    await userEvent.click(await screen.findByRole('option', { name: /HR officer/ }));
    const masked = await screen.findByRole('textbox', { name: /: Masked/ });
    expect((masked as HTMLInputElement).value).toBe('••••••');
    expect(screen.getByText('This field is not shown')).toBeTruthy();
    expect((screen.getByRole('textbox', { name: /: Editable/ }) as HTMLInputElement).readOnly).toBe(false);
  });
});
