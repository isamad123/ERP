// The access reports and the audit search (slice S1.13b Build item 11; FR-ADM-ROL-11, FR-KRN-AUD-04): what the server sends is shown,
// values are never invented, filters become query parameters, and each state (loading, empty, error, permission denied) is shown.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import ReportsScreen from './reports';
import AuditScreen from './audit';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';
const ADMIN = ['iam.users.read', 'iam.users.create', 'iam.users.update', 'iam.users.change_status', 'iam.users.unlock', 'iam.users.reset_mfa', 'iam.users.revoke_sessions',
  'iam.users.read_sessions', 'iam.users.send_invitation', 'iam.roles.assign', 'wf.workflow_tasks.reassign', 'cfg.licence.read'];

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


const calls: string[] = [];

function serve(handler: (path: string, query: URLSearchParams) => Response) {
  calls.length = 0;
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    const [path = '', q = ''] = url.split('?');
    calls.push(url);
    return handler(path, new URLSearchParams(q));
  }));
}

function show(path: string, value = shell(['aud.changes.view', 'iam.roles.read', 'iam.users.read', 'aud.security_events.view', 'aud.export', 'iam.roles.assign'])) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <I18nRoot locale="en">
        <ToastProvider>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <ShellValueProvider value={value}>
              <Routes>
                <Route path="/admin/access-reports" element={<ReportsScreen />} />
                <Route path="/admin/audit" element={<AuditScreen />} />
              </Routes>
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

const change = (id: string, extra: object = {}) => ({ id, occurredAt: '2026-11-22T08:00:00Z', actorUserId: 'a1', actorName: 'Ayesha Admin', entityType: 'iam.roles', entityId: 'e1', action: 'update', source: 'ui', fields: ['is_active'], roleCode: 'payroll_clerk', ...extra });

describe('FR-ADM-ROL-11 permission change report', () => {
  it('lists who changed which role, when and which fields (names only)', async () => {
    serve((path) => (path === '/api/iam/reports/permission-changes' ? json({ items: [change('c1')], next: null }) : json({ items: [] })));
    show('/admin/access-reports');
    expect(await screen.findByText('Ayesha Admin')).toBeTruthy();
    expect(screen.getByText('payroll_clerk')).toBeTruthy();
    expect(screen.getByText('is_active')).toBeTruthy();
    expect(screen.getAllByText('Role').length).toBeGreaterThan(1);
  });

  it('shows the empty state when nothing was changed', async () => {
    serve(() => json({ items: [], next: null }));
    show('/admin/access-reports');
    expect(await screen.findByText('No changes found')).toBeTruthy();
  });

  it('shows a plain permission-denied message when the server refuses', async () => {
    serve(() => json({ status: 403, title: 'errors.access.forbidden' }, 403));
    show('/admin/access-reports');
    expect(await screen.findByText('You do not have permission to see these reports.')).toBeTruthy();
  });
});

describe('FR-ADM-ROL-11 role usage report', () => {
  it('lists an unused permission and a person who has not signed in, with the number of days from the setting', async () => {
    serve((path) => (path === '/api/iam/reports/role-usage'
      ? json({ inactiveDays: 90, unusedPermissions: [{ code: 'hrm.employees.purge', module: 'hrm', resource: 'hrm.employees', action: 'purge', isSensitive: true }],
        inactiveUsers: [{ id: 'u9', displayName: 'Old Timer', username: 'old.timer', status: 'active', lastLoginAt: null }] })
      : json({ items: [], next: null })));
    show('/admin/access-reports');
    await userEvent.click(await screen.findByRole('tab', { name: 'Role usage' }));
    expect(await screen.findByText('hrm.employees.purge')).toBeTruthy();
    expect(screen.getByText('Old Timer')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'People who have not signed in for 90 days' })).toBeTruthy();
  });
});

describe('FR-KRN-AUD-04 audit search', () => {
  it('sends the filters of the form and shows the changes; hidden values are marked, never shown', async () => {
    serve((path) => (path === '/api/aud/changes'
      ? json({ items: [{ id: 'x1', occurredAt: '2026-11-22T08:00:00Z', actorUserId: 'a1', actorKind: 'user', actorName: 'Ayesha Admin', entityType: 'iam.users', entityId: '00000000-0000-7000-8000-000000000009', action: 'update', source: 'ui', changes: [{ field: 'salary', hidden: true }, { field: 'display_name', before: 'A', after: 'B' }] }], next: null })
      : json({ items: [], next: null })));
    show('/admin/audit?entityType=iam.users&entityId=00000000-0000-7000-8000-000000000009');
    expect(await screen.findByText('Ayesha Admin')).toBeTruthy();
    expect(screen.getByText('salary (hidden), display_name')).toBeTruthy();
    expect(calls.some((c) => c.includes('entityType=iam.users') && c.includes('entityId=00000000-0000-7000-8000-000000000009'))).toBe(true);
    expect(screen.queryByText('"B"')).toBeNull();
  });

  it('refuses a record identifier that is not an identifier before sending anything', async () => {
    serve(() => json({ items: [], next: null }));
    show('/admin/audit');
    await screen.findByText('No changes recorded');
    await userEvent.type(screen.getByRole('textbox', { name: /Record/ }), 'abc');
    await userEvent.click(screen.getByRole('button', { name: 'Search' }));
    expect(await screen.findByText('Enter a valid record identifier.')).toBeTruthy();
    expect(calls.filter((c) => c.includes('entityId=abc'))).toEqual([]);
  });

  it('offers the export only to people who may export, and shows the security events tab', async () => {
    serve((path) => (path === '/api/aud/security-events'
      ? json({ items: [{ id: 's1', occurredAt: '2026-11-22T08:00:00Z', eventType: 'auth.login_failed', actor: null, subject: 'sara', severity: 'warning', details: {} }], next: null })
      : json({ items: [], next: null })));
    show('/admin/audit', shell(['aud.changes.view', 'aud.security_events.view']));
    await screen.findByText('No changes recorded');
    expect(screen.queryByRole('button', { name: 'Export' })).toBeNull();
    await userEvent.click(screen.getByRole('tab', { name: 'Security events' }));
    expect(await screen.findByText('auth.login_failed')).toBeTruthy();
    expect(screen.getAllByText('Warning').length).toBeGreaterThan(0);
  });

  it('shows a permission-denied message when the server refuses', async () => {
    serve(() => json({ status: 403, title: 'errors.access.forbidden' }, 403));
    show('/admin/audit');
    expect(await screen.findByText('You do not have permission to search the audit log.')).toBeTruthy();
  });
});
