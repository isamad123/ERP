// The administrators' delegation screen (slice S1.13b; FR-ADM-ROL-09): list with names and states, create and revoke bodies, the
// server's refusal in plain words, and the permission-denied state.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import DelegationsScreen from './delegations';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';
const ADMIN = ['iam.delegations.read', 'iam.delegations.create', 'iam.delegations.update', 'iam.users.read'];

const rows = [
  { id: 'd1', fromUserId: 'u1', toUserId: 'u2', validFrom: '2026-12-01T00:00:00Z', validTo: '2099-12-31T00:00:00Z', reason: 'Leave', status: 'active', rowVersion: 1 },
  { id: 'd2', fromUserId: 'u1', toUserId: 'u2', validFrom: '2026-01-01T00:00:00Z', validTo: '2026-01-31T00:00:00Z', reason: 'Old', status: 'revoked', rowVersion: 2 },
];

function shell(permissions: string[] = ADMIN): ShellValue {
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

interface Server { posted: { method: string; url: string; body: unknown }[]; failCreate?: Response }

function serve(s: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.split('?')[0] ?? url;
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      s.posted.push({ method, url: path, body: init?.body ? JSON.parse(String(init.body)) : null });
      if (path === '/api/iam/delegations' && s.failCreate) return s.failCreate;
      return json({ id: 'd-new' }, 201);
    }
    if (path === '/api/iam/delegations') return json({ items: rows, next: null });
    if (path === '/api/iam/user-overview') return json({ items: [
      { id: 'u1', username: 'amna', displayName: 'Amna Requester', email: 'a@x.test', status: 'active', lastLoginAt: null, defaultCompanyId: COMPANY, accessValidTo: null, hasMfa: false, roles: [], companyIds: [], allCompanies: false },
      { id: 'u2', username: 'bilal', displayName: 'Bilal Approver', email: 'b@x.test', status: 'active', lastLoginAt: null, defaultCompanyId: COMPANY, accessValidTo: null, hasMfa: false, roles: [], companyIds: [], allCompanies: false },
    ], next: null });
    if (path === '/api/iam/people-lookup') return json([{ id: 'p1', displayName: 'Sara Khan', username: 'sara' }, { id: 'p2', displayName: 'Omar Ali', username: 'omar' }]);
    return json({ status: 404, title: 'errors.data.not_found' }, 404);
  }));
}

function show(value = shell()) {
  render(
    <MemoryRouter initialEntries={['/admin/delegations']}>
      <I18nRoot locale="en">
        <ToastProvider>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <ShellValueProvider value={value}>
              <Routes><Route path="/admin/delegations/*" element={<DelegationsScreen />} /></Routes>
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

async function fillCreate(dialog: HTMLElement) {
  await userEvent.type(within(dialog).getByRole('combobox', { name: /^Person who delegates/ }), 'sar');
  await userEvent.click(await screen.findByRole('option', { name: /Sara Khan/ }, { timeout: 3000 }));
  await userEvent.type(within(dialog).getByRole('combobox', { name: /^Person who receives/ }), 'oma');
  await userEvent.click(await screen.findByRole('option', { name: /Omar Ali/ }, { timeout: 3000 }));
  const dates = within(dialog).getAllByRole('group', { name: /day/ });
  for (const [i, value] of ['12012026', '12312026'].entries()) {
    await userEvent.click(within(dates[i] as HTMLElement).getAllByRole('spinbutton')[0] as HTMLElement);
    await userEvent.keyboard(value);
  }
  await userEvent.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'On leave');
}

describe('FR-ADM-ROL-09 delegation list', () => {
  it('shows names, dates and the state of each delegation', async () => {
    serve({ posted: [] });
    show();
    const grid = await screen.findByRole('grid', { name: 'Delegations' });
    expect((await within(grid).findAllByText('Amna Requester')).length).toBe(2);
    expect(within(grid).getByText('Active')).toBeTruthy();
    expect(within(grid).getByText('Revoked')).toBeTruthy();
  });

  it('shows the permission-denied state', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ status: 403, title: 'errors.access.forbidden' }, 403)));
    show();
    expect(await screen.findByText('You are not allowed to see delegations.')).toBeTruthy();
  });

  it('offers neither create nor revoke without the change permissions', async () => {
    serve({ posted: [] });
    show(shell(['iam.delegations.read']));
    await screen.findByRole('grid', { name: 'Delegations' });
    expect(screen.queryByRole('button', { name: 'New delegation' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Revoke' })).toBeNull();
  });
});

describe('FR-ADM-ROL-09 create and revoke', () => {
  it('revokes an active delegation with a reason', async () => {
    const s: Server = { posted: [] };
    serve(s);
    show();
    await userEvent.click(await screen.findByRole('button', { name: 'Revoke' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(/stop going to/)).toBeTruthy();
    await userEvent.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Back early');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Revoke' }));
    await waitFor(() => expect(s.posted).toEqual([{ method: 'POST', url: '/api/iam/delegations/d1/revoke', body: { reason: 'Back early' } }]));
  });

  it('creates a delegation with both people, the days and the reason', async () => {
    const s: Server = { posted: [] };
    serve(s);
    show();
    await userEvent.click(await screen.findByRole('button', { name: 'New delegation' }));
    const dialog = await screen.findByRole('alertdialog');
    await fillCreate(dialog);
    await userEvent.click(within(dialog).getByRole('button', { name: 'New delegation' }));
    await waitFor(() => expect(s.posted).toEqual([{ method: 'POST', url: '/api/iam/delegations', body: { fromUserId: 'p1', toUserId: 'p2', from: '2026-12-01', to: '2026-12-31', reason: 'On leave' } }]));
  });

  it('keeps the dialog open and shows the server\'s refusal in words', async () => {
    const s: Server = { posted: [], failCreate: json({ status: 400, title: 'errors.workflow.delegation_overlap' }, 400) };
    serve(s);
    show();
    await userEvent.click(await screen.findByRole('button', { name: 'New delegation' }));
    const dialog = await screen.findByRole('alertdialog');
    await fillCreate(dialog);
    await userEvent.click(within(dialog).getByRole('button', { name: 'New delegation' }));
    await waitFor(() => expect(s.posted).toHaveLength(1));
    expect(screen.getByRole('alertdialog')).toBeTruthy();
    expect(within(screen.getByRole('alertdialog')).getByText(/\S/, { selector: '[role="alert"], .erp-field-error, [slot="errorMessage"]' })).toBeTruthy();
  });
});
