// The approval inbox (slice S1.12d Build items 20 to 22; FR-KRN-WFL-08, FR-HRM-SSP-02, FR-KRN-IAM-14, FR-XM-32; spec 13
// STA-06): only the actions the server lists are offered; a task without any shows no decision button; a decision is
// reported once the server has answered; bulk approval previews the count and reports one result per task; a change
// that cannot be approved shows no approve button, and the person's own change none at all.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import type { InboxItem, PendingChanges, TaskDetail } from '../../api/workflow';
import ApprovalsScreen from './approvals';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const COMPANY = '00000000-0000-7000-8000-000000000001';

function item(taskId: string, title: string, bulkApproval: boolean, extra: Partial<InboxItem> = {}): InboxItem {
  return {
    taskId, instanceId: `i-${taskId}`, documentType: 'test.expense', documentId: `d-${taskId}`, companyId: COMPANY, title: { en: title }, link: `/test/expenses/d-${taskId}`,
    stepKey: 'one', stepName: { en: 'Manager' }, requesterUserId: 'u-req', requesterName: 'Amna Requester', receivedAt: '2026-11-20T05:00:00Z', dueAt: null, queue: false,
    roleId: null, waitingForInformation: false, module: 'test', actingForUserId: null, actingForName: null, overdue: false, escalationLevel: 0, bulkApproval, ...extra,
  };
}

function detail(actions: string[]): TaskDetail {
  return {
    task: { id: 't1', stepKey: 'one', stepName: { en: 'Manager' }, status: 'pending', dueAt: null, infoRequestedAt: null, claimedAt: null, escalationLevel: 0 },
    request: { id: 'i-t1', documentType: 'test.expense', documentId: 'd-t1', companyId: COMPANY, status: 'running', requesterUserId: 'u-req', startedAt: '2026-11-20T05:00:00Z', title: { en: 'Expense claim of 1250' }, link: '/test/expenses/d-t1' },
    history: [{ id: 'h1', taskId: null, event: 'started', actorId: 'u-req', actingForUserId: null, at: '2026-11-20T05:00:00Z', details: {}, actorName: 'Amna Requester', actingForName: null }],
    context: {
      module: 'test', documentTypeName: { en: 'Expense claim' }, requesterName: 'Amna Requester', actingForName: 'Sara Khan', queue: false, bulkApproval: true,
      fields: [{ name: 'amount', type: 'money', label: { en: 'Amount' }, value: '1250.5000', displayName: null, displayNames: null }],
      actions: actions as TaskDetail['context']['actions'], reasonRequired: ['reject', 'return', 'reassign'],
    },
  };
}

function shell(writable = true, permissions: string[] = ['wf.workflow_tasks.read', 'wf.workflow_tasks.update', 'iam.maker_checker.approve']): ShellValue {
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

interface Server { inbox?: InboxItem[]; task?: TaskDetail; changes?: PendingChanges; posted: { url: string; body: unknown }[] }

function serve(server: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.split('?')[0];
    if (init?.method === 'POST') {
      server.posted.push({ url: path ?? url, body: init.body ? JSON.parse(String(init.body)) : null });
      if (path?.endsWith('/bulk-approve')) {
        return json({ done: 1, refused: 1, results: [{ taskId: 't1', outcome: 'approved', status: 'approved', error: null }, { taskId: 't2', outcome: 'refused', status: null, error: 'errors.workflow.task_not_pending' }] });
      }
      return json({ instanceId: 'i-t1', status: 'running', openTaskIds: ['t9'] });
    }
    if (path === '/api/wf/inbox/counts') return json({ total: server.inbox?.length ?? 0, overdue: 0, documentTypes: [{ documentType: 'test.expense', module: 'test', name: { en: 'Expense claim' }, count: server.inbox?.length ?? 0, overdue: 0 }], changeApprovals: server.changes?.toDecide.length ?? 0 });
    if (path === '/api/wf/inbox') return json(server.inbox ?? []);
    if (path?.startsWith('/api/wf/tasks/')) return server.task ? json(server.task) : json({ status: 404, title: 'errors.workflow.task_not_found' }, 404);
    if (path === '/api/iam/maker-checker-requests/pending') return json(server.changes ?? { toDecide: [], mine: [] });
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
              <Routes><Route path="/approvals/*" element={<ApprovalsScreen />} /></Routes>
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

describe('FR-KRN-WFL-08 FR-XM-32 approval inbox', () => {
  it('offers exactly the actions the server allows, and reports a decision only after the server answered', async () => {
    const server: Server = { inbox: [item('t1', 'Expense claim of 1250', true)], task: detail(['approve', 'reject', 'ask']), posted: [] };
    serve(server);
    show('/approvals/tasks/t1');
    const panel = await screen.findByRole('complementary', { name: 'Request details' });
    const decide = await within(panel).findByRole('group', { name: 'Your decision' });
    expect(within(decide).getAllByRole('button').map((b) => b.textContent)).toEqual(['Approve', 'Reject', 'Ask for information']);
    expect(within(panel).getByText('1,250.5000')).toBeTruthy(); // the exact decimal text
    expect(within(panel).getAllByText(/For .Sara Khan./).length).toBeGreaterThan(0);

    await userEvent.click(within(decide).getByRole('button', { name: 'Reject' }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Reject' }));
    expect(await within(dialog).findByText('Give a reason.')).toBeTruthy(); // nothing sent without the reason
    expect(server.posted).toEqual([]);
    await userEvent.type(within(dialog).getByRole('textbox'), 'Missing receipt');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Reject' }));
    await waitFor(() => expect(server.posted).toEqual([{ url: '/api/wf/tasks/t1/reject', body: { comment: 'Missing receipt' } }]));
    expect(await screen.findByText('Rejected. Expense claim of 1250 is closed.')).toBeTruthy();
  });

  it('shows no decision button when the server allows none, nor in read-only', async () => {
    serve({ inbox: [item('t1', 'Expense claim of 1250', false)], task: detail([]), posted: [] });
    show('/approvals/tasks/t1');
    const panel = await screen.findByRole('complementary', { name: 'Request details' });
    expect(await within(panel).findByText('There is nothing for you to decide on this request now.')).toBeTruthy();
    expect(within(panel).queryByRole('button', { name: 'Approve' })).toBeNull();
  });

  it('hides decisions while the licence is read-only', async () => {
    serve({ inbox: [item('t1', 'Expense claim of 1250', true)], task: detail(['approve', 'reject']), posted: [] });
    show('/approvals/tasks/t1', shell(false));
    const panel = await screen.findByRole('complementary', { name: 'Request details' });
    expect(await within(panel).findByText('The licence is read-only: requests can be read but not decided.')).toBeTruthy();
    expect(within(panel).queryByRole('button', { name: 'Approve' })).toBeNull();
  });

  it('previews a bulk approval and lists one result per task', async () => {
    const server: Server = { inbox: [item('t1', 'Claim A', true), item('t2', 'Claim B', true), item('t3', 'Claim C', false)], posted: [] };
    serve(server);
    show('/approvals');
    await screen.findByText('Claim A');
    await userEvent.click(screen.getByRole('checkbox', { name: /Select all/i }));
    await userEvent.click(await screen.findByRole('button', { name: 'Approve selected' }));
    const dialog = await screen.findByRole('dialog', { name: 'Approve several requests' });
    expect(within(dialog).getByText(/2 requests will be approved/)).toBeTruthy();
    expect(within(dialog).getByText('Claim C')).toBeTruthy(); // decided on its own
    await userEvent.click(within(dialog).getByRole('button', { name: 'Approve 2 requests' }));
    await waitFor(() => expect(server.posted).toEqual([{ url: '/api/wf/tasks/bulk-approve', body: { taskIds: ['t1', 't2'], comment: null } }]));
    expect(await within(dialog).findByText('Approved: 1. Not approved: 1.')).toBeTruthy();
    expect(within(dialog).getByText('This task has already been decided.')).toBeTruthy();
  });
});

describe('FR-KRN-IAM-14 approvals of changes', () => {
  const change = (id: string, canApprove: boolean, problem: string | null) => ({
    id, kind: 'cfg.setting.change', requestedBy: 'u-other', requestedByName: 'Bilal Admin', requestedAt: '2026-11-20T05:00:00Z', expiresAt: '2026-11-27T05:00:00Z',
    reason: 'month end', subject: { en: 'Approval test' }, canApprove, approveProblem: problem,
    details: [{ label: 'changes.detail.newValue', type: 'text' as const, text: '42', date: null, names: null, items: null }],
  });

  it('offers approve only where approving would pass, and no decision on the person\'s own change', async () => {
    serve({ changes: { toDecide: [change('c1', true, null), change('c2', false, 'errors.access.company_not_covered')], mine: [change('c3', false, 'errors.maker_checker.own_request')] }, posted: [] });
    show('/approvals/changes');
    const first = await screen.findAllByRole('listitem', { name: /Change the setting .Approval test./ });
    expect(first).toHaveLength(3);
    const [approvable, refused, mine] = first as [HTMLElement, HTMLElement, HTMLElement];
    expect(within(approvable).getByRole('button', { name: 'Approve' })).toBeTruthy();
    expect(within(refused).queryByRole('button', { name: 'Approve' })).toBeNull();
    expect(within(refused).getByRole('button', { name: 'Reject' })).toBeTruthy();
    expect(within(mine).queryAllByRole('button')).toEqual([]);
    expect(within(mine).getByText('Waiting for a second person')).toBeTruthy();
    expect(within(approvable).getByText('42')).toBeTruthy();
  });
});
