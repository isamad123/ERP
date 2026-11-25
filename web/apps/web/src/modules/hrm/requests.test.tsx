// Attendance request screens (slice S1.30a; FR-HRM-ATT-11, ATT-14, GHR-09): the requests are listed with who made them and their status, a request made
// by someone else says so, the correction form sends clock times and the exception (never an instant or a person's id typed by hand), a refusal is shown in
// the server's words, withdrawing goes through the approval endpoint, the exception queue filters and opens the form for its day, and a person without the
// permission is told so and nothing is sent.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import ExceptionsScreen from './exceptions';
import RequestsScreen from './requests';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';

function shell(permissions: string[], writable = true): ShellValue {
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

const sent: { method: string; url: string; body: unknown }[] = [];

function serve(handler: (method: string, url: string, body: unknown) => Response) {
  sent.length = 0;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
    sent.push({ method: init?.method ?? 'GET', url, body });
    return handler(init?.method ?? 'GET', url, body);
  }));
}

function show(element: React.ReactNode, value: ShellValue) {
  render(
    <MemoryRouter>
      <I18nRoot locale="en">
        <ToastProvider>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <ShellValueProvider value={value}>{element}</ShellValueProvider>
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


const REG = {
  id: 'r1', companyId: COMPANY, employmentId: 'e1', exceptionId: 'x1', workDate: '2026-11-18', requestedIn: null, requestedOut: '2026-11-18T13:00:00Z', reason: 'Forgot to punch out', fileIds: [],
  workflowInstanceId: 'w1', status: 'submitted', createdAt: '2026-11-19T04:00:00Z', createdBy: 'u-me', updatedAt: '2026-11-19T04:00:00Z', updatedBy: 'u-me', rowVersion: 2,
};
const DUTY = {
  id: 'd1', companyId: COMPANY, employmentId: 'e1', kind: 'field_duty', startAt: '2026-11-23T00:00:00Z', endAt: '2026-11-25T19:00:00Z', purpose: 'Site survey', workflowInstanceId: 'w2', status: 'approved',
  createdAt: '2026-11-19T04:00:00Z', createdBy: 'u-hr', updatedAt: '2026-11-19T04:00:00Z', updatedBy: 'u-hr', rowVersion: 3,
};
const EXCEPTION = { id: 'x1', companyId: COMPANY, employmentId: 'e1', employeeNo: 'E-1', workDate: '2026-11-18', kind: 'missing_out', status: 'open', attendanceDayId: null, createdAt: '2026-11-19T01:00:00Z', ageDays: 7, rowVersion: 1 };
const READ = ['hrm.regularization_requests.read', 'hrm.duty_requests.read', 'hrm.regularization_requests.create', 'hrm.duty_requests.create', 'hrm.attendance_exceptions.read'];

function requests(items: { reg?: unknown[]; duty?: unknown[] }, post?: (body: unknown) => Response) {
  serve((method, url, body) => {
    if (method === 'POST') return post ? post(body) : json({ id: 'new', status: 'submitted', instanceId: 'w9', onBehalf: false });
    if (url.includes('/regularization-requests')) return json({ items: items.reg ?? [], next: null });
    if (url.includes('/duty-requests')) return json({ items: items.duty ?? [], next: null });
    return json({});
  });
}

describe('requests screen', () => {
  it('FR-HRM-ATT-11 lists corrections with their status and lets the maker withdraw a waiting one through the approval endpoint', async () => {
    requests({ reg: [REG] });
    show(<RequestsScreen />, shell(READ));
    expect(await screen.findByText('Forgot to punch out')).toBeTruthy();
    expect(screen.getByText('Waiting')).toBeTruthy();
    expect(screen.getByText('You')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw' }));
    expect(await screen.findByText('The request was withdrawn.')).toBeTruthy();
    const post = sent.find((c) => c.method === 'POST')!;
    expect(post.url).toBe('/api/wf/instances/w1/withdraw');
  });

  it('GHR-09 FR-HRM-ATT-14 a duty request made by someone else for the person says so and cannot be withdrawn by them', async () => {
    requests({ duty: [DUTY] });
    show(<RequestsScreen />, shell(['hrm.duty_requests.read']));
    expect(await screen.findByText('Site survey')).toBeTruthy();
    expect(screen.getByText('Someone else for this person')).toBeTruthy();
    expect(screen.getByText('Field duty')).toBeTruthy();
    expect(screen.getByText('Approved')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Withdraw' })).toBeNull();
  });

  it('FR-HRM-ATT-11 the correction form cannot be sent until a time and a reason are given', async () => {
    requests({ reg: [] });
    show(<RequestsScreen />, shell(READ));
    expect(await screen.findByText('No requests yet')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Ask for a correction' }));
    const send = await screen.findByRole('button', { name: 'Send request' });
    expect((send as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByLabelText(/Employee number/)).toBeTruthy();
  });

  it('FR-HRM-ATT-11 a person without the permission is told so and nothing is sent', () => {
    requests({});
    show(<RequestsScreen />, shell([]));
    expect(screen.getByText('You may not see attendance requests.')).toBeTruthy();
    expect(sent.length).toBe(0);
  });

  it('FR-HRM-ATT-11 shows the empty state when nothing was asked yet', async () => {
    requests({ duty: [] });
    show(<RequestsScreen />, shell(['hrm.duty_requests.read']));
    expect(await screen.findByText('No requests yet')).toBeTruthy();
  });

  it('FR-HRM-ATT-11 an error loading is shown with its reason', async () => {
    serve(() => json({ title: 'errors.data.not_found', status: 404 }, 404));
    show(<RequestsScreen />, shell(['hrm.regularization_requests.read']));
    expect(await screen.findByText('The requests could not be loaded.')).toBeTruthy();
  });
});

describe('exception queue screen', () => {
  const queue = (items: unknown[], unmapped: unknown = null) => serve((method, url) => (url.includes('/attendance/exceptions') ? json({ status: 'open', items, more: false, unmapped }) : method === 'POST' ? json({ id: 'n', status: 'submitted', instanceId: 'w9', onBehalf: false }) : json({})));

  it('FR-HRM-ATT-11 lists exceptions with their kind and age and asks the server with the filters chosen', async () => {
    queue([EXCEPTION], { count: 2, items: [] });
    show(<ExceptionsScreen />, shell(READ));
    expect(await screen.findByRole('table')).toBeTruthy();
    expect(screen.getByText('7 days')).toBeTruthy();
    expect(screen.getByText(/2 device punches belong to nobody yet/)).toBeTruthy();
    await userEvent.type(screen.getByLabelText(/At least this many days old/), '3');
    await vi.waitFor(() => expect(sent.some((c) => c.url.includes('minAgeDays=3'))).toBe(true));
    expect(sent[0]?.url).toContain('/api/hrm/attendance/exceptions?companyId=');
  });

  it('FR-HRM-ATT-11 "Ask for a correction" opens the form for that exception without asking for the day or the person', async () => {
    queue([EXCEPTION]);
    show(<ExceptionsScreen />, shell(READ));
    await userEvent.click(await screen.findByRole('button', { name: 'Ask for a correction' }));
    expect(await screen.findByRole('button', { name: 'Send request' })).toBeTruthy();
    expect(screen.queryByLabelText(/Employee number/)).toBeNull();
    expect(screen.queryByText('Day to correct')).toBeNull();
  });

  it('FR-HRM-ATT-11 the form sends the exception, clock times and the reason, and shows a refusal in the server\'s words', async () => {
    serve((method, url) => (method === 'POST'
      ? json({ title: 'errors.hrm.attendance.request.period_locked', status: 409 }, 409)
      : url.includes('/attendance/exceptions') ? json({ status: 'open', items: [EXCEPTION], more: false, unmapped: null }) : json({})));
    show(<ExceptionsScreen />, shell(READ));
    await userEvent.click(await screen.findByRole('button', { name: 'Ask for a correction' }));
    await userEvent.type(await screen.findByLabelText(/Time out/), '18:00');
    await userEvent.type(screen.getByLabelText(/Reason/), 'Forgot to punch out');
    await userEvent.click(screen.getByRole('button', { name: 'Send request' }));
    expect(await screen.findByText(/locked attendance period/)).toBeTruthy();
    const post = sent.find((c) => c.method === 'POST')!;
    expect(post.url).toBe('/api/hrm/attendance/regularization-requests');
    expect(post.body).toEqual({ exceptionId: 'x1', employmentId: 'e1', requestedOutTime: '18:00:00', requestedOutDayOffset: 0, reason: 'Forgot to punch out' });
  });

  it('FR-HRM-ATT-11 a person without the permission is told so; an empty queue invites nothing', async () => {
    queue([]);
    show(<ExceptionsScreen />, shell(READ));
    expect(await screen.findByText('No exceptions')).toBeTruthy();
    cleanup();
    show(<ExceptionsScreen />, shell([]));
    expect(screen.getByText('You may not see attendance exceptions.')).toBeTruthy();
  });
});
