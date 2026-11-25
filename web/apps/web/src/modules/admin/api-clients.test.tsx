// The API clients screen (slice S1.27c; FR-ADM-SEC-02, FR-KRN-INT-07): the list never shows a secret, a new key is shown once with a copy button and is gone
// when the person confirms, a key waiting for a second person says it does not work yet, revoke and rotate ask first, and a person without the manage
// permission sees no action that changes anything.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import type { ApiClientSecret, ApiClientView } from '../../api/api-clients';
import ApiClientsScreen from './api-clients';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';
const PUSH = 'hrm.attendance_devices.push';

function shell(permissions: string[]): ShellValue {
  const grants = permissions.map((code) => ({ code, companies: null }));
  return {
    me: { userId: 'u-me', displayName: 'Me', language: 'en', timeZone: 'Asia/Karachi', defaultCompanyId: COMPANY, companies: [{ id: COMPANY, code: 'C1', name: { en: 'First company' } }],
      permissions: grants, mfa: { required: false, methods: [], enrolBy: null }, numbers: { digits: 'western', grouping: 'international' } },
    capabilities: { modules: [], features: [], flags: [], state: 'active', nextState: null, nextStateOn: null, warnings: [] },
    facts: accessFacts([], grants, 'active'),
    can: (code: string) => permissions.includes(code),
    writable: true,
    activeCompany: null,
    setActiveCompany: () => undefined,
  };
}

const client = (extra: Partial<ApiClientView> = {}): ApiClientView => ({
  id: 'c1', clientId: 'ak_0123456789abcdef01234567', name: 'Lahore gateway', scopes: [PUSH], ipAllow: ['10.1.2.0/24'], rateLimitPerMinute: 60, expiresAt: null, lastUsedAt: '2026-11-25T06:00:00Z',
  status: 'active', previousSecretValidUntil: null, revokedAt: null, createdAt: '2026-11-20T06:00:00Z', ...extra,
});

interface Sent { method: string; path: string; body: Record<string, unknown> | null }
const sent: Sent[] = [];

function serve(items: ApiClientView[], handler?: (path: string, method: string) => Response | undefined) {
  sent.length = 0;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    sent.push({ method, path, body: typeof init?.body === 'string' ? JSON.parse(init.body) : null });
    return handler?.(path, method)
      ?? (path === '/api/iam/api-clients' && method === 'GET' ? json(items)
        : path === '/api/iam/api-clients/scope-options' ? json([{ code: PUSH, module: 'hrm', descriptionKey: `permissions.${PUSH}` }])
          : json({ status: 404, title: 'errors.data.not_found' }, 404));
  }));
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const queryClient = () => new QueryClient({ defaultOptions: { queries: { retry: false } } });

function show(permissions: string[]) {
  return render(
    <MemoryRouter initialEntries={['/admin/api-clients']}><I18nRoot locale="en"><ToastProvider><QueryClientProvider client={queryClient()}>
      <ShellValueProvider value={shell(permissions)}><ApiClientsScreen /></ShellValueProvider>
    </QueryClientProvider></ToastProvider></I18nRoot></MemoryRouter>,
  );
}

describe('the API clients screen', () => {
  it('FR_ADM_SEC_02_lists_the_clients_with_status_and_limit_and_offers_actions_only_to_a_manager', async () => {
    serve([client(), client({ id: 'c2', clientId: 'ak_ffffffffffffffffffffffff', name: 'Old gateway', status: 'revoked' })]);
    show(['iam.api_clients.read', 'iam.api_clients.manage']);
    const table = within(await screen.findByRole('table'));
    expect(table.getByText('Lahore gateway')).toBeTruthy();
    expect(table.getByText('Revoked')).toBeTruthy();
    expect(table.getAllByText('Active').length).toBe(1);
    expect(table.getByText('ak_0123456789abcdef01234567')).toBeTruthy();
    expect(table.getAllByRole('button', { name: /^New secret:/ }).length).toBe(1); // only the active client
    expect(table.getAllByRole('button', { name: /^Revoke:/ }).length).toBe(1);
    expect(screen.getByRole('button', { name: 'New API client' })).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/argon2|secret_hash/i);
  });

  it('FR_ADM_SEC_02_a_reader_sees_the_list_and_the_use_but_no_button_that_changes_anything', async () => {
    serve([client()]);
    show(['iam.api_clients.read']);
    const table = within(await screen.findByRole('table'));
    expect(table.getByRole('button', { name: /^Use:/ })).toBeTruthy();
    expect(table.queryByRole('button', { name: /^New secret:/ })).toBeNull();
    expect(table.queryByRole('button', { name: /^Revoke:/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'New API client' })).toBeNull();
  });

  it('FR_ADM_SEC_02_shows_a_new_key_once_with_a_copy_button_and_forgets_it_when_the_person_has_stored_it', async () => {
    const made: ApiClientSecret = { id: 'c9', clientId: 'ak_aaaaaaaaaaaaaaaaaaaaaaaa', secret: 'S3cretValue', apiKey: 'ak_aaaaaaaaaaaaaaaaaaaaaaaa.S3cretValue', heldRequestId: null, validUntil: null };
    serve([], (path, method) => (path === '/api/iam/api-clients' && method === 'POST' ? json(made, 201) : undefined));
    const user = userEvent.setup();
    show(['iam.api_clients.read', 'iam.api_clients.manage']);
    await user.click((await screen.findAllByRole('button', { name: 'New API client' }))[0]!);
    const dialog = within(await screen.findByRole('alertdialog'));
    await user.type(dialog.getByRole('textbox', { name: /^Name/ }), 'Lahore gateway');
    await user.click(await dialog.findByRole('checkbox', { name: /^Send punches from a device/ }));
    await user.type(dialog.getByRole('textbox', { name: /^Allowed addresses/ }), '10.1.2.0/24{Enter}203.0.113.7');
    await user.click(dialog.getByRole('button', { name: 'Create key' }));

    await waitFor(() => expect(sent.find((s) => s.method === 'POST')).toBeTruthy());
    const post = sent.find((s) => s.method === 'POST')!;
    expect(post.path).toBe('/api/iam/api-clients');
    expect(post.body).toEqual({ name: 'Lahore gateway', scopes: [PUSH], ipAllow: ['10.1.2.0/24', '203.0.113.7'], rateLimitPerMinute: 60, expiresAt: null });

    const secret = within(await screen.findByRole('alertdialog', { name: 'Copy the key now' }));
    expect((secret.getByRole('textbox', { name: 'API key' }) as HTMLInputElement).value).toBe(made.apiKey);
    expect(secret.getByRole('button', { name: 'Copy key' })).toBeTruthy();
    expect(secret.queryByText(/does not work yet/)).toBeNull();
    await user.click(secret.getByRole('button', { name: 'I have stored the key' }));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
    expect(document.body.textContent).not.toContain('S3cretValue');
  });

  it('FR_ADM_SEC_02_says_a_key_that_waits_for_a_second_person_does_not_work_yet_and_links_to_the_approvals', async () => {
    const made: ApiClientSecret = { id: null, clientId: 'ak_bbbbbbbbbbbbbbbbbbbbbbbb', secret: 'Another1', apiKey: 'ak_bbbbbbbbbbbbbbbbbbbbbbbb.Another1', heldRequestId: 'h1', validUntil: null };
    serve([client()], (path, method) => (path === '/api/iam/api-clients/c1/rotate' && method === 'POST' ? json(made) : undefined));
    const user = userEvent.setup();
    show(['iam.api_clients.read', 'iam.api_clients.manage']);
    await user.click(await screen.findByRole('button', { name: /^New secret:/ }));
    const ask = within(await screen.findByRole('alertdialog'));
    expect(ask.getByText(/old one keeps working for a while/)).toBeTruthy();
    await user.click(ask.getByRole('button', { name: 'New secret' }));
    const secret = within(await screen.findByRole('alertdialog', { name: 'Copy the key now' }));
    expect(secret.getByText(/does not work yet/)).toBeTruthy();
    expect(secret.getByRole('link', { name: 'Open approvals' }).getAttribute('href')).toBe('/approvals');
    expect(sent.some((s) => s.method === 'POST' && s.path === '/api/iam/api-clients/c1/rotate')).toBe(true);
  });

  it('FR_ADM_SEC_02_asks_before_it_revokes_and_sends_nothing_when_the_person_cancels', async () => {
    serve([client()], (path, method) => (path === '/api/iam/api-clients/c1/revoke' && method === 'POST' ? json(client({ status: 'revoked' })) : undefined));
    const user = userEvent.setup();
    show(['iam.api_clients.read', 'iam.api_clients.manage']);
    await user.click(await screen.findByRole('button', { name: /^Revoke:/ }));
    const ask = within(await screen.findByRole('alertdialog'));
    expect(ask.getByText(/stops working at once/)).toBeTruthy();
    await user.click(ask.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
    expect(sent.some((s) => s.path.endsWith('/revoke'))).toBe(false);

    await user.click(await screen.findByRole('button', { name: /^Revoke:/ }));
    const again = within(await screen.findByRole('alertdialog'));
    await user.click(again.getByRole('button', { name: 'Revoke' }));
    await waitFor(() => expect(sent.some((s) => s.method === 'POST' && s.path === '/api/iam/api-clients/c1/revoke')).toBe(true));
  });

  it('FR_KRN_INT_07_shows_the_use_of_a_client_per_day_with_the_slowest_five_percent', async () => {
    serve([client()], (path) => (path === '/api/iam/api-clients/c1/usage'
      ? json({ clientId: 'c1', lastUsedAt: null, days: [{ date: '2026-11-25', calls: 1234, errors4xx: 12, errors5xx: 1, rateLimited: 3, p95Ms: 250, maxMs: 900 }] }) : undefined));
    const user = userEvent.setup();
    show(['iam.api_clients.read']);
    await user.click(await screen.findByRole('button', { name: /^Use:/ }));
    const dialog = within(await screen.findByRole('dialog'));
    const table = within(await dialog.findByRole('table'));
    expect(table.getByText('2026-11-25')).toBeTruthy();
    expect(table.getByText('1,234')).toBeTruthy();
    expect(table.getByText('250')).toBeTruthy();
  });

  it('FR_ADM_SEC_02_shows_the_empty_the_denied_and_the_error_states', async () => {
    serve([]);
    show(['iam.api_clients.read']);
    expect(await screen.findByText('No API clients yet')).toBeTruthy();
    cleanup();
    serve([], (path) => (path === '/api/iam/api-clients' ? json({ status: 403, title: 'errors.iam.api_client.forbidden' }, 403) : undefined));
    show(['iam.api_clients.read']);
    expect(await screen.findByText('You may not see API clients.')).toBeTruthy();
  });
});
