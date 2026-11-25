// Leave screens (slice S1.33a; FR-HRM-LVE-04 to -06, -16, GHR-09, FR-XM-32): the apply form shows what the server counts and every reason it refuses (a preview that saves nothing),
// a missing required document blocks the send before the server is asked, half days and hours follow the type, HR applying for someone says who acts for whom, My leave shows
// the balances with their entries and only the buttons the status allows, and the approval card shows the coverage warning (or says no team is measured).
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import { leaveApi } from '../../api/leave';
import LeaveScreen from './leave';
import LeaveApprovalCard from './leave-approval-card';
import { approvalCardFor, registerApprovalCard } from '../kernel/approval-cards';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';
const ME = 'u-me';
const WIDE = ['hrm.leave_requests.read', 'hrm.leave_requests.create', 'hrm.leave_requests.update', 'hrm.leave_requests.cancel'];

function shell(permissions: string[], writable = true): ShellValue {
  const grants = permissions.map((code) => ({ code, companies: null }));
  return {
    me: { userId: ME, displayName: 'Mona Me', language: 'en', timeZone: 'Asia/Karachi', defaultCompanyId: COMPANY, companies: [{ id: COMPANY, code: 'C1', name: { en: 'First company' } }],
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
type Body = Record<string, unknown> | undefined;
const serve = (handler: (method: string, url: string, body: Body) => Response) => {
  sent.length = 0;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const body = (typeof init?.body === 'string' ? JSON.parse(init.body) : init?.body ?? undefined) as Body;
    sent.push({ method: init?.method ?? 'GET', url, body });
    return handler(init?.method ?? 'GET', url, body);
  }));
};

function show(element: React.ReactNode, value: ShellValue, path = '/') {
  render(
    <MemoryRouter initialEntries={[path]}>
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

const type = (id: string, code: string, name: string, extra: Record<string, unknown> = {}) => ({
  id, companyId: COMPANY, code, nameI18n: { en: name, ur: name }, isPaid: true, counting: 'working_days', unit: 'day', requiresDocument: false, isCompOff: false, isUnpaidDeduction: false,
  source: 'tenant', colorToken: 'success', isActive: true, isDeleted: false, rowVersion: 1, ...extra,
});
const TYPES = [
  type('t-annual', 'AL', 'Annual leave'),
  type('t-sick', 'SL', 'Sick leave', { requiresDocument: true, colorToken: 'danger' }),
  type('t-half', 'CL', 'Casual leave', { unit: 'half_day', colorToken: 'info' }),
  type('t-hour', 'SH', 'Short leave', { unit: 'hour', counting: 'hours', colorToken: 'warning' }),
];
const PLAN = {
  leaveTypeId: 't-annual', code: 'AL', unit: 'day', counting: 'working_days', isPaid: true,
  days: [
    { date: '2026-12-03', dayType: 'working', quantity: 1, kind: 'leave', part: 'full', reason: 'hrm.leave.day.leave' },
    { date: '2026-12-04', dayType: 'weekly_off', quantity: 1, kind: 'sandwich', part: 'full', reason: 'hrm.leave.day.sandwich_weekly_off' },
    { date: '2026-12-05', dayType: 'holiday', quantity: 0, kind: 'off', part: 'full', reason: 'hrm.leave.day.off_holiday' },
  ],
  total: 2, leaveYear: 2026, balance: 10, pending: 0, after: 8, lowest: 8, negativeLimit: 0,
};
const OK_PREVIEW = { employmentId: 'e-me', allowed: true, error: null, errors: null, details: null, documentRequired: false, plan: PLAN };
const MY_BALANCES = {
  employments: [{ employmentId: 'e-me', companyId: COMPANY, employeeNo: 'E-1' }], employmentId: 'e-me', companyId: COMPANY, employeeNo: 'E-1', asOf: '2026-11-25',
  balances: [{
    leaveTypeId: 't-annual', code: 'AL', name: { en: 'Annual leave', ur: 'سالانہ رخصت' }, unit: 'day', isActive: true, leaveYear: 2026, yearStart: '2026-01-01', yearEnd: '2026-12-31', balance: 12.5,
    totals: { grant: 20 }, negativeLimit: 0, noPolicy: null,
    entries: [{ id: 'l1', leaveTypeId: 't-annual', leaveYear: 2026, entryDate: '2026-01-01', kind: 'grant', quantity: 20, expiresOn: null, sourceType: 'automatic', sourceId: null, reversesEntryId: null, reversedById: null, reason: null, period: 'Y2026', explanation: 'hrm.leave.entry.automatic_grant', createdAt: '2026-01-01T00:00:00Z' },
      { id: 'l2', leaveTypeId: 't-annual', leaveYear: 2026, entryDate: '2026-03-02', kind: 'usage', quantity: -7.5, expiresOn: null, sourceType: 'leave_request', sourceId: 'r1', reversesEntryId: null, reversedById: null, reason: null, period: null, explanation: 'hrm.leave.entry.usage', createdAt: '2026-03-02T00:00:00Z' }],
  }],
};
const REQUEST = (id: string, status: string, extra: Record<string, unknown> = {}) => ({
  id, companyId: COMPANY, branchId: null, requestNo: `LV-${id}`, employmentId: 'e-me', leaveTypeId: 't-annual', startDate: '2026-12-03', endDate: '2026-12-04', startPart: 'full', endPart: 'full', hours: null, days: 2,
  reason: null, fileIds: [], modifiesRequestId: null, workflowInstanceId: `w-${id}`, status, custom: null, createdAt: '2026-11-20T00:00:00Z', createdBy: ME, updatedAt: '2026-11-20T00:00:00Z', updatedBy: ME, rowVersion: 3, ...extra,
});

function rig(handler?: (method: string, url: string, body: Body) => Response | null, previews: unknown = OK_PREVIEW) {
  serve((method, url, body) => {
    const custom = handler?.(method, url, body);
    if (custom) return custom;
    if (url.includes('/api/hrm/leave-types')) return json({ items: TYPES, next: null });
    if (url.endsWith('/leave-requests/preview')) return json(previews);
    if (url.includes('/api/hrm/my/leave-balances')) return json(MY_BALANCES);
    if (url.includes('/api/hrm/leave-requests?')) return json({ items: [], next: null });
    if (url.includes('/api/hrm/employments')) return json({ items: [{ id: 'e-7', employeeNo: 'E-7', status: 'active', companyId: COMPANY }], next: null });
    return json({});
  });
}

const pickType = async (name: string) => {
  await userEvent.click(await screen.findByRole('button', { name: /Leave type/ }));
  await userEvent.click(await screen.findByRole('option', { name }));
};
const typeDate = async (label: RegExp, value: string) => {
  const group = screen.getAllByRole('group', { name: label })[0] as HTMLElement;
  const [month, day, year] = within(group).getAllByRole('spinbutton') as HTMLElement[];
  await userEvent.type(month!, value.slice(0, 2));
  await userEvent.type(day!, value.slice(2, 4));
  await userEvent.type(year!, value.slice(4));
};

describe('leave client', () => {
  it('FR-HRM-LVE-04 sends each call to its endpoint with the body the server reads', async () => {
    rig();
    await leaveApi.preview({ leaveTypeId: 't', startDate: '2026-12-03' });
    await leaveApi.create({ leaveTypeId: 't', startDate: '2026-12-03', submit: false });
    await leaveApi.submit('r1', 4);
    await leaveApi.cancel('r1', 'Plans changed', 4);
    await leaveApi.change('r1', { leaveTypeId: 't', startDate: '2026-12-10' });
    await leaveApi.updateDraft('r1', { leaveTypeId: 't', startDate: '2026-12-10', rowVersion: 2 });
    await leaveApi.withdraw('w1', 'No longer needed');
    await leaveApi.details('r1');
    await leaveApi.coverage('r1');
    await leaveApi.myBalances(COMPANY);
    expect(sent.map((c) => `${c.method} ${c.url}`)).toEqual([
      'POST /api/hrm/leave-requests/preview', 'POST /api/hrm/leave-requests', 'POST /api/hrm/leave-requests/r1/submit', 'POST /api/hrm/leave-requests/r1/cancel', 'POST /api/hrm/leave-requests/r1/change',
      'PUT /api/hrm/leave-requests/r1/draft', 'POST /api/wf/instances/w1/withdraw', 'GET /api/hrm/leave-requests/r1/details', 'GET /api/hrm/leave-requests/r1/coverage', `GET /api/hrm/my/leave-balances?companyId=${COMPANY}`,
    ]);
    expect(sent[2]?.body).toEqual({ rowVersion: 4 });
    expect(sent[3]?.body).toEqual({ reason: 'Plans changed', rowVersion: 4 });
  });
});

describe('apply for leave', () => {
  it('FR-HRM-LVE-04 shows the days the server counted one by one with their explanation, the sandwich day marked, and the balance before and after', async () => {
    rig();
    show(<LeaveScreen />, shell(WIDE), '/apply');
    await pickType('Annual leave');
    await typeDate(/^From/, '12032026');
    expect(await screen.findByText('Weekly off between two leave days, counted as leave (sandwich rule)', {}, { timeout: 3000 })).toBeTruthy();
    expect(screen.getByText('Sandwich day')).toBeTruthy();
    expect(screen.getByText('Holiday, not counted')).toBeTruthy();
    const summary = screen.getByText('Balance after').closest('dl') as HTMLElement;
    expect(within(summary).getByText('10 days')).toBeTruthy();
    expect(within(summary).getAllByText('8 days').length).toBeGreaterThan(0);
    expect(screen.getByText('This leave can be sent.')).toBeTruthy();
    const preview = sent.find((c) => c.url.endsWith('/preview'))!;
    expect(preview.body).toEqual({ leaveTypeId: 't-annual', startDate: '2026-12-03', endDate: '2026-12-03' });
    expect(sent.some((c) => c.method === 'POST' && c.url === '/api/hrm/leave-requests')).toBe(false);
  });

  it('FR-HRM-LVE-04 shows every reason for a refusal in plain words and does not offer to send', async () => {
    rig(undefined, {
      ...OK_PREVIEW, allowed: false, error: 'errors.hrm.leave.below_allowed', details: { lowest: -2, allowed: 0 },
      errors: { startDate: ['errors.hrm.leave.request.notice'] }, plan: { ...PLAN, after: -2, lowest: -2 },
    });
    show(<LeaveScreen />, shell(WIDE), '/apply');
    await pickType('Annual leave');
    await typeDate(/^From/, '12032026');
    expect(await screen.findByText(/The balance would fall below what the leave policy allows\./, {}, { timeout: 3000 })).toBeTruthy();
    expect(screen.getByText(/The balance would fall to -2; the policy allows 0\./)).toBeTruthy();
    expect(screen.getByText('This leave must be asked for earlier: the leave policy requires more notice.')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Send for approval' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('FR-HRM-LVE-16 a type that needs a document blocks the send with a clear message before the server is asked', async () => {
    rig(undefined, { ...OK_PREVIEW, documentRequired: true });
    show(<LeaveScreen />, shell(WIDE), '/apply');
    await pickType('Sick leave');
    await typeDate(/^From/, '12032026');
    await screen.findByText('This leave can be sent.', {}, { timeout: 3000 });
    expect(screen.getByText(/Supporting document \(required for this leave type\)/)).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Send for approval' }));
    expect((await screen.findAllByText(/needs a supporting document\. Attach one before you send/)).length).toBeGreaterThan(0);
    expect(sent.some((c) => c.method === 'POST' && c.url === '/api/hrm/leave-requests')).toBe(false);
  });

  it('FR-HRM-LVE-16 with a document the request is saved as a draft, the file is attached, then it is sent; the camera input takes photos', async () => {
    rig((method, url) => {
      if (method === 'POST' && url === '/api/hrm/leave-requests') return json({ id: 'r9', requestNo: 'LV-9', status: 'draft', employmentId: 'e-me', rowVersion: 1 }, 201);
      if (method === 'POST' && url === '/api/doc/attachments') return json({ id: 'f1', fileName: 'note.pdf' });
      if (url === '/api/hrm/leave-requests/r9/details') return json({ id: 'r9', rowVersion: 2 });
      if (url === '/api/hrm/leave-requests/r9/submit') return json({ id: 'r9', requestNo: 'LV-9', status: 'submitted', rowVersion: 3 }, 202);
      return null;
    }, { ...OK_PREVIEW, documentRequired: true });
    show(<LeaveScreen />, shell(WIDE), '/apply');
    await pickType('Sick leave');
    await typeDate(/^From/, '12032026');
    await screen.findByText('This leave can be sent.', {}, { timeout: 3000 });
    expect(screen.getByTestId('leave-photo-input').getAttribute('capture')).toBe('environment');
    await userEvent.upload(screen.getByTestId('leave-file-input'), new File(['x'], 'note.pdf', { type: 'application/pdf' }));
    await userEvent.click(screen.getByRole('button', { name: 'Send for approval' }));
    await waitFor(() => expect(sent.some((c) => c.url === '/api/hrm/leave-requests/r9/submit')).toBe(true));
    const create = sent.find((c) => c.method === 'POST' && c.url === '/api/hrm/leave-requests')!;
    expect(create.body).toMatchObject({ leaveTypeId: 't-sick', startDate: '2026-12-03', submit: false });
    expect(sent.some((c) => c.url === '/api/doc/attachments')).toBe(true);
    expect(sent.find((c) => c.url.endsWith('/submit'))!.body).toEqual({ rowVersion: 2 });
  });

  it('FR-HRM-LVE-04 offers a part of the day only for a type taken in half days, and hours only for an hourly type', async () => {
    rig();
    show(<LeaveScreen />, shell(WIDE), '/apply');
    await pickType('Annual leave');
    expect(screen.queryByRole('button', { name: /Part of the day/ })).toBeNull();
    expect(screen.queryByLabelText(/^Hours/)).toBeNull();
    await pickType('Casual leave');
    expect(screen.getByRole('button', { name: /Part of the day/ })).toBeTruthy();
    await pickType('Short leave');
    expect(screen.queryByRole('button', { name: /Part of the day/ })).toBeNull();
    expect(screen.getByLabelText(/^Hours/)).toBeTruthy();
    expect(screen.queryByRole('group', { name: /^To/ })).toBeNull();
  });

  it('GHR-09 HR applying for an employee names who acts for whom and sends the employment', async () => {
    rig();
    show(<LeaveScreen />, shell([...WIDE, 'hrm.employments.read']), '/apply');
    await userEvent.click(await screen.findByRole('button', { name: /Employee/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'E-7' }));
    expect(await screen.findByText(/Applied by .*Mona Me.* for .*E-7/)).toBeTruthy();
    await pickType('Annual leave');
    await typeDate(/^From/, '12032026');
    await screen.findByText('This leave can be sent.', {}, { timeout: 3000 });
    expect(sent.find((c) => c.url.endsWith('/preview'))!.body).toMatchObject({ employmentId: 'e-7' });
  });

  it('FR-HRM-LVE-04 a person without the permission to apply is told so and nothing is sent', () => {
    rig();
    show(<LeaveScreen />, shell(['hrm.leave_requests.read']), '/apply');
    expect(screen.getByText('You may not use leave screens.')).toBeTruthy();
    expect(sent.length).toBe(0);
  });
});

describe('my leave', () => {
  const requests = [REQUEST('d1', 'draft'), REQUEST('s1', 'submitted'), REQUEST('a1', 'approved'), REQUEST('x1', 'rejected'), REQUEST('c1', 'cancelled')];
  const mine = () => rig((_method, url) => (url.includes('/api/hrm/leave-requests?') ? json({ items: requests, next: null }) : null));

  it('FR-HRM-LVE-04 shows the balance of each type and opens the entries behind the number', async () => {
    mine();
    show(<LeaveScreen />, shell(WIDE));
    expect((await screen.findAllByText('12.5 days')).length).toBeGreaterThan(0);
    await userEvent.click(screen.getByRole('button', { name: 'Why this number?' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('Yearly grant by the leave policy')).toBeTruthy();
    expect(within(dialog).getByText('Leave taken')).toBeTruthy();
    expect(within(dialog).getByText('-7.5')).toBeTruthy();
  });

  it('FR-HRM-LVE-06 offers only the buttons the status allows: finish and cancel a draft, withdraw a waiting request, change or cancel approved leave, nothing for the rest', async () => {
    mine();
    show(<LeaveScreen />, shell(WIDE));
    const rows = async (no: string) => within((await screen.findByText(no)).closest('tr') as HTMLElement);
    const names = (row: ReturnType<typeof within>) => row.queryAllByRole('button').map((b: HTMLElement) => b.textContent);
    expect(names(await rows('LV-d1'))).toEqual(['Finish and send', 'Cancel leave']);
    expect(names(await rows('LV-s1'))).toEqual(['Withdraw']);
    expect(names(await rows('LV-a1'))).toEqual(['Change', 'Cancel leave']);
    expect(names(await rows('LV-x1'))).toEqual([]);
    expect(names(await rows('LV-c1'))).toEqual([]);
  });

  it('FR-HRM-LVE-06 withdrawing a waiting request goes through the approval endpoint; cancelling approved leave asks for a reason and sends the row version', async () => {
    rig((_method, url) => {
      if (url.includes('/api/hrm/leave-requests?')) return json({ items: requests, next: null });
      if (url === '/api/hrm/leave-requests/a1/details') return json({ id: 'a1', rowVersion: 7 });
      if (url === '/api/hrm/leave-requests/a1/cancel') return json({ id: 'a1', status: 'cancelled', rowVersion: 8 });
      return null;
    });
    show(<LeaveScreen />, shell(WIDE));
    const waiting = within((await screen.findByText('LV-s1')).closest('tr') as HTMLElement);
    await userEvent.click(waiting.getByRole('button', { name: 'Withdraw' }));
    await waitFor(() => expect(sent.some((c) => c.url === '/api/wf/instances/w-s1/withdraw')).toBe(true));
    const approved = within((await screen.findByText('LV-a1')).closest('tr') as HTMLElement);
    await userEvent.click(approved.getByRole('button', { name: 'Cancel leave' }));
    const dialog = await screen.findByRole('dialog');
    const confirm = within(dialog).getByRole('button', { name: 'Cancel the leave' }) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    await userEvent.type(within(dialog).getByLabelText(/Reason/), 'Plans changed');
    await userEvent.click(confirm);
    await waitFor(() => expect(sent.some((c) => c.url === '/api/hrm/leave-requests/a1/cancel')).toBe(true));
    expect(sent.find((c) => c.url.endsWith('/a1/cancel'))!.body).toEqual({ reason: 'Plans changed', rowVersion: 7 });
  });

  it('FR-HRM-LVE-06 shows no button while the system is read-only', async () => {
    mine();
    show(<LeaveScreen />, shell(WIDE, false));
    const row = within((await screen.findByText('LV-a1')).closest('tr') as HTMLElement);
    expect((row.getByRole('button', { name: 'Change' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('FR-HRM-LVE-04 shows the empty state and an error state', async () => {
    rig((_method, url) => (url.includes('/api/hrm/leave-requests?') ? json({ items: [], next: null }) : null));
    show(<LeaveScreen />, shell(WIDE));
    expect(await screen.findByText('No leave requests')).toBeTruthy();
    cleanup();
    serve(() => json({ title: 'errors.data.not_found', status: 404 }, 404));
    show(<LeaveScreen />, shell(WIDE));
    expect((await screen.findAllByText('The leave information could not be loaded.')).length).toBeGreaterThan(0);
  });
});

describe('leave request page', () => {
  const DETAILS = {
    id: 'r1', requestNo: 'LV-1', companyId: COMPANY, employmentId: 'e-7', employeeNo: 'E-7', leaveTypeId: 't-annual', leaveTypeCode: 'AL', unit: 'day', counting: 'working_days', isPaid: true, status: 'submitted',
    startDate: '2026-12-03', endDate: '2026-12-05', startPart: 'full', endPart: 'full', hours: null, days: 2, reason: 'Family visit', fileIds: [], currentFileIds: [], documentRequired: false,
    modifiesRequestId: null, changes: [], workflowInstanceId: 'w1', appliedBy: 'u-hr', onBehalf: true, impact: { leaveYear: 2026, balance: 10, pending: 0, after: 8, lowest: 8 },
    dayList: [{ date: '2026-12-03', quantity: 1, kind: 'leave', isPaid: true, status: 'submitted' }, { date: '2026-12-04', quantity: 1, kind: 'sandwich', isPaid: true, status: 'submitted' }],
    entries: [], rowVersion: 4,
  };
  it('GHR-09 shows the days, the balance impact and that someone else applied on behalf of the employee, with no decision buttons', async () => {
    rig((_method, url) => (url === '/api/hrm/leave-requests/r1/details' ? json(DETAILS) : url.includes('/api/doc/attachments') ? json([]) : null));
    show(<LeaveScreen />, shell(WIDE), '/requests/r1');
    expect(await screen.findByText('Someone else, on behalf of the employee')).toBeTruthy();
    expect(screen.getByText('Sandwich day')).toBeTruthy();
    expect(screen.getByText('Balance after')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Approve|Reject/ })).toBeNull();
  });
});

describe('approval card', () => {
  const D = {
    id: 'r1', requestNo: 'LV-1', companyId: COMPANY, employmentId: 'e-7', employeeNo: 'E-7', leaveTypeId: 't-annual', leaveTypeCode: 'AL', unit: 'day', counting: 'working_days', isPaid: true, status: 'submitted',
    startDate: '2026-12-03', endDate: '2026-12-04', startPart: 'full', endPart: 'full', hours: null, days: 2, reason: null, fileIds: [], currentFileIds: [], documentRequired: false, modifiesRequestId: null,
    changes: [], workflowInstanceId: 'w1', appliedBy: 'u-hr', onBehalf: false, impact: { leaveYear: 2026, balance: 10, pending: 0, after: 8, lowest: 8 },
    dayList: [{ date: '2026-12-03', quantity: 1, kind: 'leave', isPaid: true, status: 'submitted' }, { date: '2026-12-04', quantity: 1, kind: 'sandwich', isPaid: true, status: 'submitted' }], entries: [], rowVersion: 4,
  };
  const card = (coverage: unknown, status = 200) => {
    serve((_method, url) => (url === '/api/hrm/leave-requests/r1/details' ? json(D) : url.endsWith('/coverage') ? json(coverage, status) : url.includes('/api/doc/attachments') ? json([]) : json({})));
    show(<LeaveApprovalCard documentType="hrm.leave_request" documentId="r1" companyId={COMPANY} instanceId="w1" requesterUserId="u-hr" />, shell(WIDE));
  };

  it('FR-HRM-LVE-05 shows the days, the balance before and after and the coverage warning with the colleagues away', async () => {
    card({ requestId: 'r1', limitPercent: 30, teamKnown: true, managerEmploymentId: 'm', teamSize: 8, warn: true, days: [
      { date: '2026-12-03', teamSize: 8, away: 3, percent: 38, warn: true, people: [{ employeeNo: 'E-2', firstName: 'Sara', lastName: 'Khan' }, { employeeNo: 'E-3', firstName: null, lastName: null }] },
      { date: '2026-12-04', teamSize: 8, away: 1, percent: 13, warn: false, people: [] }] });
    expect(await screen.findByText('Team coverage warning')).toBeTruthy();
    expect(screen.getByText(/3 of 8 of the team are away on .*; limit 30 percent\./)).toBeTruthy();
    expect(screen.getByText(/Away: .*Sara Khan.*E-3/)).toBeTruthy();
    expect(screen.getByText('This is a warning only. You can still decide the request.')).toBeTruthy();
    expect(screen.getByText('Sandwich day')).toBeTruthy();
    expect(screen.getByText('8 days')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Approve/ })).toBeNull();
  });

  it('FR-HRM-LVE-05 says plainly that no team is measured when the employee has no line manager', async () => {
    card({ requestId: 'r1', limitPercent: 30, teamKnown: false, managerEmploymentId: null, teamSize: 0, warn: false, days: [] });
    expect(await screen.findByText('Team not measured')).toBeTruthy();
    expect(screen.getByText(/has no line manager recorded/)).toBeTruthy();
    expect(screen.queryByText('Team coverage warning')).toBeNull();
  });

  it('FR-HRM-LVE-05 a card whose details are not available to the person says so and shows nothing else', async () => {
    serve(() => json({ title: 'errors.auth.forbidden', status: 403 }, 403));
    show(<LeaveApprovalCard documentType="hrm.leave_request" documentId="r1" companyId={COMPANY} instanceId="w1" requesterUserId="u-hr" />, shell(WIDE));
    expect(await screen.findByText(/leave details are not available to you here/)).toBeTruthy();
  });

  it('FR-HRM-LVE-05 the inbox finds the cards by document type once the HR declarations are loaded', async () => {
    await import('../hrm');
    expect(approvalCardFor('hrm.leave_request')).toBeDefined();
    expect(approvalCardFor('hrm.leave_cancellation')).toBeDefined();
    expect(approvalCardFor('kernel.other')).toBeUndefined();
    expect(typeof registerApprovalCard).toBe('function');
  });
});
