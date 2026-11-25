// The leave ledger screen (slice S1.33b; FR-HRM-LVE-10, FR-HRM-LVE-14, FR-XM-05, FR-XM-06): the running balance equals the server's balance (a mismatch is a visible warning), a request is a link,
// reversal sends the right request and is not offered for a reversal, an adjustment from a recompute difference is prefilled and settles its period, and nothing is posted without a click.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import LeaveLedgerScreen from './leave-ledger';
import { COMPANY, json, posts, problem, sent, serve, shell, show, type Body } from './leave-test-kit';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const READ = ['hrm.leave_ledger.read'];
const ALL = [...READ, 'hrm.leave_ledger.post', 'hrm.leave_ledger.recompute'];
const TYPE = { id: 't-al', companyId: COMPANY, code: 'AL', nameI18n: { en: 'Annual leave', ur: 'سالانہ رخصت' }, isPaid: true, counting: 'working_days', unit: 'day', requiresDocument: false, isCompOff: false, isUnpaidDeduction: false,
  source: 'tenant', colorToken: 'success', isActive: true, isDeleted: false, rowVersion: 1 };
const entry = (id: string, date: string, kind: string, quantity: number, extra: Record<string, unknown> = {}) => ({
  id, employmentId: 'e-1', leaveTypeId: 't-al', leaveYear: 2026, entryDate: date, kind, quantity, expiresOn: null, sourceType: 'hrm.leave_manual', sourceId: null, reversesEntryId: null, reversedById: null,
  reason: null, period: null, explanation: `hrm.leave.entry.${kind}`, createdAt: `${date}T08:00:00Z`, ...extra,
});
const ENTRIES = [
  entry('g1', '2026-01-01', 'grant', 20, { sourceType: 'hrm.leave_accrual', period: 'Y2026', explanation: 'hrm.leave.entry.automatic_grant' }),
  entry('u1', '2026-03-02', 'usage', -7.5, { sourceType: 'hrm.leave_request', sourceId: 'r-77' }),
  entry('a1', '2026-03-05', 'adjustment', 0.1, { reason: 'Typing mistake', reversedById: 'a2' }),
  entry('a2', '2026-03-06', 'adjustment', -0.1, { reason: 'Undo', reversesEntryId: 'a1', sourceType: 'hrm.leave_reversal', explanation: 'hrm.leave.entry.reversal' }),
];
const line = (balance: number, entries = ENTRIES) => ({ leaveTypeId: 't-al', code: 'AL', name: { en: 'Annual leave', ur: 'سالانہ رخصت' }, unit: 'day', isActive: true, leaveYear: 2026, yearStart: '2026-01-01', yearEnd: '2026-12-31',
  balance, totals: {}, negativeLimit: 0, noPolicy: null, entries });
const balances = (balance = 12.5) => ({ employmentId: 'e-1', companyId: COMPANY, employeeNo: 'E-1', asOf: '2026-11-25', balances: [line(balance)] });
const RECOMPUTE = {
  employmentId: 'e-1', employeeNo: 'E-1', asOf: '2026-11-25', horizonFrom: '2026-01-01', checked: 11, changed: false,
  differences: [{ leaveTypeId: 't-al', leaveTypeCode: 'AL', leaveYear: 2026, period: 'M2026-03', kind: 'differs', expected: 1.75, posted: 1.5, date: '2026-03-31', entryId: 'x', explanation: 'hrm.leave.recompute.differs', policy: null, entries: [] }],
};

function rig(options: { balance?: number; extra?: (method: string, url: string, body: Body) => Response | null } = {}) {
  serve((method, url, body) => {
    const custom = options.extra?.(method, url, body);
    if (custom) return custom;
    if (url.includes('/leave-balances')) return json(balances(options.balance));
    if (url.includes('/leave-recompute')) return json(RECOMPUTE);
    if (url.startsWith('/api/hrm/leave-ledger?')) return json({ items: ENTRIES.map((e) => ({ ...e, companyId: COMPANY })), next: null });
    if (url.includes('/api/hrm/leave-types')) return json({ items: [TYPE], next: null });
    if (url.startsWith('/api/hrm/employments/e-1')) return json({ id: 'e-1', companyId: COMPANY, employeeNo: 'E-1', status: 'active' });
    if (url.startsWith('/api/hrm/employments?')) return json({ items: [{ id: 'e-1', employeeNo: 'E-1', status: 'active', companyId: COMPANY }], next: null });
    return json({});
  });
}
const open = (permissions = ALL, writable = true) => show(<LeaveLedgerScreen />, shell(permissions, writable), '/?employment=e-1');
const row = (id: string) => document.querySelector(`tr[data-entry="${id}"]`) as HTMLElement;

describe('leave ledger entries', () => {
  it('FR-HRM-LVE-10 shows each entry with its running balance and the server balance on top, with no warning when they agree', async () => {
    rig();
    open();
    await screen.findByRole('heading', { name: /Annual leave/ });
    await waitFor(() => expect(row('u1')).toBeTruthy());
    expect(within(row('g1')).getAllByText('20').length).toBeGreaterThan(0);
    expect(within(row('u1')).getByText('-7.5')).toBeTruthy();
    expect(within(row('u1')).getByText('12.5')).toBeTruthy();
    expect(within(row('a2')).getByText('12.5')).toBeTruthy();
    expect(screen.getByLabelText('Annual leave: balance 12.5 days')).toBeTruthy();
    expect(screen.queryByText('The numbers do not agree')).toBeNull();
  });

  it('FR-HRM-LVE-10 warns visibly when the entries do not add up to the balance the server returned', async () => {
    rig({ balance: 13 });
    open();
    expect(await screen.findByText('The numbers do not agree')).toBeTruthy();
    expect(screen.getByText(/add up to 12\.5, but the server reports a balance of 13/)).toBeTruthy();
  });

  it('FR-HRM-LVE-10 a leave request source is a link to the request; other sources are named in words', async () => {
    rig();
    open();
    await waitFor(() => expect(row('u1')).toBeTruthy());
    const link = within(row('u1')).getByRole('link', { name: 'Open the request' });
    expect(link.getAttribute('href')).toBe('/time/leave/requests/r-77');
    expect(within(row('g1')).getByText('Automatic grant or accrual job')).toBeTruthy();
    expect(within(row('a1')).getByText('Posted by HR')).toBeTruthy();
    expect(within(row('g1')).queryByRole('link')).toBeNull();
  });

  it('FR-XM-06 marks what an entry reverses and what reversed it', async () => {
    rig();
    open();
    await waitFor(() => expect(row('a1')).toBeTruthy());
    expect(within(row('a1')).getByText(/Reversed by the entry of/)).toBeTruthy();
    expect(within(row('a2')).getByText(/Reverses the entry of/)).toBeTruthy();
  });

  it('FR-HRM-LVE-10 filters by kind without changing the running balance of the rows left', async () => {
    rig();
    open();
    await waitFor(() => expect(row('u1')).toBeTruthy());
    await userEvent.click(screen.getByRole('button', { name: /Kind of entry/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'Leave taken' }));
    await waitFor(() => expect(row('g1')).toBeNull());
    expect(within(row('u1')).getByText('12.5')).toBeTruthy();
  });

  it('FR-HRM-LVE-10 another leave year is read through a date inside it (the year\'s last entry)', async () => {
    rig();
    open();
    await userEvent.click(await screen.findByRole('button', { name: /Leave year/ }));
    await userEvent.click(await screen.findByRole('option', { name: '2026' }));
    await waitFor(() => expect(sent.some((c) => c.url.includes('/leave-balances') && c.url.includes('asOf=2026-03-06') && c.url.includes('leaveTypeId=t-al'))).toBe(true));
  });
});

describe('reversal and posting', () => {
  it('FR-XM-06 reverses an entry by a reason, never edits it, and does not offer a reversal of a reversal or of an entry already reversed', async () => {
    rig({ extra: (method, url) => (method === 'POST' && url === '/api/hrm/leave-ledger/g1/reverse' ? json({ id: 'new' }, 201) : null) });
    open();
    await waitFor(() => expect(row('g1')).toBeTruthy());
    expect(within(row('a2')).queryByRole('button', { name: 'Reverse' })).toBeNull();
    expect(within(row('a1')).queryByRole('button', { name: 'Reverse' })).toBeNull();
    expect(within(row('u1')).queryByRole('button', { name: 'Reverse' })).toBeNull();
    await userEvent.click(within(row('g1')).getByRole('button', { name: 'Reverse' }));
    const confirm = screen.getByRole('button', { name: 'Reverse the entry' }) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    await userEvent.type(screen.getByRole('textbox', { name: /Reason/ }), 'Granted twice');
    await userEvent.click(confirm);
    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]).toMatchObject({ url: '/api/hrm/leave-ledger/g1/reverse', body: { reason: 'Granted twice' } });
    expect(sent.some((c) => c.method === 'PUT' || c.method === 'DELETE' || c.method === 'PATCH')).toBe(false);
  });

  it('FR-XM-06 shows the server refusal of a reversal in plain words', async () => {
    rig({ extra: (method, url) => (method === 'POST' && url.endsWith('/reverse') ? problem('errors.hrm.leave.already_reversed') : null) });
    open();
    await waitFor(() => expect(row('g1')).toBeTruthy());
    await userEvent.click(within(row('g1')).getByRole('button', { name: 'Reverse' }));
    await userEvent.type(screen.getByRole('textbox', { name: /Reason/ }), 'Again');
    await userEvent.click(screen.getByRole('button', { name: 'Reverse the entry' }));
    expect(await screen.findByText('This entry has already been reversed.')).toBeTruthy();
  });

  it('FR-HRM-LVE-10 posts a grant with a reason through the form, and sends nothing until the button is pressed', async () => {
    rig({ extra: (method, url) => (method === 'POST' && url === '/api/hrm/employments/e-1/leave-ledger' ? json({ id: 'n1' }, 201) : null) });
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'Post an entry' }));
    const confirm = screen.getByRole('button', { name: 'Post the entry' }) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    await userEvent.click(screen.getByRole('button', { name: /Leave type/ }));
    await userEvent.click(await screen.findByRole('option', { name: /Annual leave/ }));
    await userEvent.type(screen.getByRole('textbox', { name: /Quantity/ }), '2');
    const group = screen.getAllByRole('group', { name: /^Date of the entry/ })[0] as HTMLElement;
    const [month, day, year] = within(group).getAllByRole('spinbutton') as HTMLElement[];
    await userEvent.type(month!, '04'); await userEvent.type(day!, '01'); await userEvent.type(year!, '2026');
    await userEvent.type(screen.getByRole('textbox', { name: /Reason/ }), 'Long service');
    expect(posts()).toHaveLength(0);
    await userEvent.click(confirm);
    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]?.body).toEqual({ leaveTypeId: 't-al', kind: 'grant', quantity: 2, entryDate: '2026-04-01', reason: 'Long service' });
  });

  it('read-only mode and a missing post permission hide or disable the writes', async () => {
    rig();
    open(READ);
    await waitFor(() => expect(row('g1')).toBeTruthy());
    expect(screen.queryByRole('button', { name: 'Post an entry' })).toBeNull();
    expect(within(row('g1')).queryByRole('button', { name: 'Reverse' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Check balances' })).toBeNull();
  });
});

describe('FR-HRM-LVE-14 recompute', () => {
  it('FR-HRM-LVE-14 lists a planted difference with its cause, period, expected and posted, says nothing was changed, and posts nothing by itself', async () => {
    rig();
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'Check balances' }));
    expect(await screen.findByText('Nothing was changed')).toBeTruthy();
    expect(screen.getByText(/HR decides and posts an adjustment/)).toBeTruthy();
    const table = screen.getByRole('table', { name: 'Differences between the ledger and the leave policies' });
    const cells = within(table).getAllByRole('cell').map((c) => c.textContent);
    expect(cells).toContain('M2026-03');
    expect(cells).toContain('The ledger has a different quantity from what the leave policy calls for.');
    expect(cells).toContain('1.75');
    expect(cells).toContain('1.5');
    expect(sent.some((c) => c.url.includes('/leave-recompute') && c.method === 'GET')).toBe(true);
    expect(posts()).toHaveLength(0);
  });

  it('FR-HRM-LVE-14 "Post an adjustment" opens the form with the type, the signed difference and the period it settles, and posts only on the second click', async () => {
    rig({ extra: (method, url) => (method === 'POST' && url === '/api/hrm/employments/e-1/leave-ledger' ? json({ id: 'n2' }, 201) : null) });
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'Check balances' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Post an adjustment' }));
    expect((screen.getByRole('textbox', { name: /Quantity/ }) as HTMLInputElement).value).toBe('0.25');
    expect(screen.getByText(/settles the period M2026-03/)).toBeTruthy();
    expect(posts()).toHaveLength(0);
    await userEvent.type(screen.getByRole('textbox', { name: /Reason/ }), 'Policy changed in March');
    await userEvent.click(screen.getByRole('button', { name: 'Post the entry' }));
    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]?.body).toEqual({ leaveTypeId: 't-al', kind: 'adjustment', quantity: 0.25, entryDate: '2026-03-31', reason: 'Policy changed in March', settlesPeriod: 'M2026-03' });
  });

  it('FR-HRM-LVE-14 says so when the ledger matches the policies', async () => {
    rig({ extra: (_m, url) => (url.includes('/leave-recompute') ? json({ ...RECOMPUTE, differences: [] }) : null) });
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'Check balances' }));
    expect(await screen.findByText('No differences')).toBeTruthy();
  });
});

describe('states', () => {
  it('shows the denied state without asking the server for any ledger', async () => {
    rig();
    open([]);
    expect(await screen.findByText('You may not read leave ledgers')).toBeTruthy();
    expect(sent.some((c) => c.url.includes('leave-balances'))).toBe(false);
  });
  it('asks for an employee first', async () => {
    rig();
    show(<LeaveLedgerScreen />, shell(ALL));
    expect(await screen.findByText('Choose an employee')).toBeTruthy();
  });
  it('shows the empty state when the employee has no entries', async () => {
    rig({ extra: (_m, url) => (url.includes('/leave-balances') ? json({ ...balances(), balances: [] }) : null) });
    open();
    expect(await screen.findByText('No leave entries')).toBeTruthy();
  });
  it('shows an error with a retry when the ledger cannot be loaded', async () => {
    rig({ extra: (_m, url) => (url.includes('/leave-balances') ? problem('errors.data.forbidden', 500) : null) });
    open();
    expect(await screen.findByText('The leave ledger could not be loaded.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });
});
