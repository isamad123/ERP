// The attendance lock console (slice S1.31 Build item 7): FR-XM-26 and FR-HRM-ATT-17 the periods with their state, who locked and why and the adjustments,
// HR's lock request (open items accepted only when said so), FR-HRM-ATT-21 the reopen request and the server's refusal shown in plain words, the punches
// received after the lock, GHR-04 the impact of an adjustment before it can be sent, and the loading, empty, error and denied states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import PeriodsScreen, { AdjustmentsScreen } from './periods';
import type { AdjustmentRow, Impact, LockPeriod, LockPeriodDetail } from '../../api/periods';
import { json, shell, show } from '../admin/access-test-support';

const READ = ['hrm.attendance_periods.read', 'hrm.attendance_events.read', 'hrm.attendance_adjustments.read'];
const HR = [...READ, 'hrm.attendance_periods.lock', 'hrm.attendance_periods.reopen', 'hrm.attendance_adjustments.submit'];

const period = (id: string, extra: Partial<LockPeriod> = {}): LockPeriod => ({
  payPeriodId: id, companyId: 'c1', payGroupId: 'g1', payGroupCode: 'MONTHLY', periodNo: 10, taxYear: 2026, startDate: '2026-10-01', endDate: '2026-10-31', cutOffDate: '2026-10-25',
  payStatus: 'open', periodId: null, state: 'open', lockKind: null, lockedAt: null, lockedByEmployeeNo: null, lockedByRunId: null, lockReason: null, lockRequestedByEmployeeNo: null,
  unprocessedAccepted: null, exceptionsAccepted: null, reopenReason: null, reopenedAt: null, adjustmentsWaiting: 0, adjustmentsCarriedIn: 0, ...extra,
});
const locked = period('p2', { periodNo: 9, startDate: '2026-09-01', endDate: '2026-09-30', state: 'locked', lockKind: 'manual', lockedAt: '2026-09-27T07:00:00Z', lockedByEmployeeNo: 'E-7', lockReason: 'Payroll prepared', adjustmentsWaiting: 1 });

interface Server {
  sent: { method: string; url: string; body: unknown }[];
  periods?: LockPeriod[];
  detail?: (id: string) => LockPeriodDetail;
  adjustments?: AdjustmentRow[];
  impact?: Impact;
  answer?: (method: string, url: string, body: unknown) => Response | undefined;
  denied?: boolean;
}

function serve(s: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      const body = init?.body ? JSON.parse(String(init.body)) : null;
      s.sent.push({ method, url: path, body });
      if (path.endsWith('/impact')) return json(s.impact);
      return s.answer?.(method, path, body) ?? json({ instanceId: 'i1', id: 'a1' }, 202);
    }
    if (path === '/api/hrm/attendance/periods') return s.denied ? json({ title: 'errors.access.forbidden' }, 403) : json({ items: s.periods ?? [period('p1'), locked] });
    if (path.endsWith('/late-events')) return json({ total: 1, lockedAt: '2026-09-27T07:00:00Z', items: [{ id: 'e1', employmentId: 'm1', employeeNo: 'E-1', eventDate: '2026-09-15', eventAt: '2026-09-15T04:00:00Z', direction: 'in', source: 'web', receivedAt: '2026-09-28T04:00:00Z' }] });
    if (path.startsWith('/api/hrm/attendance/periods/')) return json(s.detail?.(path.split('/')[5] ?? '') ?? null);
    if (path === '/api/hrm/attendance/adjustments') return json({ items: s.adjustments ?? [] });
    if (path === '/api/hrm/employments') return json({ items: [{ id: 'm1', employeeNo: 'E-1', status: 'active', companyId: 'c1' }] });
    return json({ title: 'errors.data.not_found' }, 404);
  }));
}

const open = (path: string, permissions = HR, writable = true) => show(path.startsWith('/time/periods') ? '/time/periods/*' : '/time/adjustments/*', path,
  path.startsWith('/time/periods') ? <PeriodsScreen /> : <AdjustmentsScreen />, shell(permissions, writable));

const detail = (p: LockPeriod, extra: Partial<LockPeriodDetail> = {}): LockPeriodDetail => ({
  period: p, openItems: { unprocessedDays: 12, openExceptions: 2 }, canRequestLock: p.state === 'open', lockRefusal: null, canRequestReopen: false, reopenRefusal: null, ...extra,
});

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-XM-26 FR-HRM-ATT-17 the attendance periods', () => {
  it('lists each period with its state, who locked and why, and its adjustments', async () => {
    serve({ sent: [] });
    open('/time/periods');
    const table = await screen.findByRole('table', { name: 'Attendance periods' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);
    expect(within(rows[0]!).getByText('Open')).toBeTruthy();
    expect(within(rows[1]!).getByText('Locked')).toBeTruthy();
    expect(within(rows[1]!).getByText(/HR before payroll, E-7 on/)).toBeTruthy();
    expect(within(rows[1]!).getByText('Payroll prepared')).toBeTruthy();
    expect(within(rows[1]!).getByText('1 waiting, 0 carried in')).toBeTruthy();
    expect(within(rows[0]!).getByText('2026 no. 10')).toBeTruthy();
  });

  it('asks for HR\'s lock with a reason, accepting the open items only when ticked', async () => {
    const sent: Server['sent'] = [];
    serve({ sent, detail: () => detail(period('p1')) });
    const user = userEvent.setup();
    open('/time/periods/p1');
    await user.click(await screen.findByRole('button', { name: 'Ask to lock' }));
    const dialog = await screen.findByRole('dialog', { name: /Ask to lock period/ });
    await user.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'Payroll of October');
    await user.click(within(dialog).getByRole('checkbox', { name: 'Lock with 12 employee days not processed and 2 open exceptions' }));
    await user.click(within(dialog).getByRole('button', { name: 'Send for approval' }));
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toEqual({ method: 'POST', url: '/api/hrm/attendance/periods/p1/lock-request', body: { reason: 'Payroll of October', acceptOpenItems: true } });
    expect(await screen.findByText('The lock waits for approval.')).toBeTruthy();
  });

  it('shows the server\'s refusal of a lock in plain words and offers no button', async () => {
    const sent: Server['sent'] = [];
    serve({ sent, detail: () => detail(period('p1'), { canRequestLock: false, lockRefusal: 'errors.hrm.attendance.lock.before_cut_off' }) });
    open('/time/periods/p1');
    expect(await screen.findByText(/HR may lock a period only after its cut-off date/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Ask to lock' })).toBeNull();
  });

  it('FR-HRM-ATT-21 offers the reopen of a payroll lock and lists the punches received after the lock', async () => {
    const sent: Server['sent'] = [];
    const runLock = { ...locked, lockKind: 'payroll' as const, lockedByRunId: 'r1' };
    serve({ sent, periods: [runLock], detail: () => detail(runLock, { canRequestReopen: true, openItems: { unprocessedDays: 0, openExceptions: 0 } }) });
    const user = userEvent.setup();
    open('/time/periods/p2');
    const punches = await screen.findByRole('table', { name: 'Punches received after the lock' });
    expect(within(punches).getByText('E-1')).toBeTruthy();
    expect(screen.getByText(/1 punches arrived after the lock/)).toBeTruthy();
    await user.click(screen.getByRole('button', { name: 'Ask to cancel the run and reopen' }));
    const dialog = await screen.findByRole('dialog', { name: /Reopen period/ });
    await user.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'Device punches missing');
    await user.click(within(dialog).getByRole('button', { name: 'Send for approval' }));
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toEqual({ method: 'POST', url: '/api/hrm/attendance/periods/p2/reopen-request', body: { reason: 'Device punches missing' } });
  });

  it('an HR lock explains that it never reopens', async () => {
    serve({ sent: [], detail: () => detail(locked, { reopenRefusal: 'errors.hrm.attendance.lock.use_adjustment' }) });
    open('/time/periods/p2');
    expect(await screen.findByText(/A period locked by HR never reopens/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /reopen/ })).toBeNull();
  });

  it('offers no request to a reader, and shows the empty, error and denied states', async () => {
    serve({ sent: [], detail: () => detail(period('p1')) });
    open('/time/periods/p1', READ);
    expect(await screen.findByText(/12 employee days not processed/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Ask to lock' })).toBeNull();
    cleanup();
    serve({ sent: [], periods: [] });
    open('/time/periods');
    expect(await screen.findByText('No pay periods have started yet')).toBeTruthy();
    cleanup();
    serve({ sent: [], denied: true });
    open('/time/periods');
    expect(await screen.findByText('You may not see attendance periods.')).toBeTruthy();
    cleanup();
    serve({ sent: [] });
    open('/time/periods', ['hrm.attendance_events.read']);
    expect(await screen.findByText('You may not see attendance periods.')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });
});

describe('FR-XM-26 GHR-04 attendance adjustments', () => {
  const impact: Impact = {
    employmentId: 'm1', employeeNo: 'E-1', kind: 'attendance', fromDate: '2026-09-14', toDate: '2026-09-16', canSubmit: true, refusal: null,
    days: ['2026-09-14', '2026-09-15', '2026-09-16'].map((date) => ({ date, locked: true, payPeriodId: 'p2', old: { result: 'absent', workedMinutes: 0, lateMinutes: 0, earlyMinutes: 0, overtimeMinutes: 0, unpaidLeave: 0, paidLeave: 0 }, new: { result: 'present', workedMinutes: 480, lateMinutes: 0, earlyMinutes: 0, overtimeMinutes: 0, unpaidLeave: 0, paidLeave: 0 } })),
    leave: null, overtime: [], penalties: [],
    payPeriods: [{ payPeriodId: 'p2', startDate: '2026-09-01', endDate: '2026-09-30', payStatus: 'open', role: 'locked' }, { payPeriodId: 'p1', startDate: '2026-10-01', endDate: '2026-10-31', payStatus: 'open', role: 'target' }],
    modules: ['attendance', 'payroll'],
  };

  it('lists adjustments with their kind, days and status', async () => {
    serve({ sent: [], adjustments: [{ id: 'a1', employmentId: 'm1', employeeNo: 'E-1', kind: 'leave_cancellation', fromDate: '2026-09-15', toDate: '2026-09-16', reason: 'Came to work', status: 'approved', targetPayPeriodId: 'p1', createdAt: '2026-10-02T05:00:00Z' }] });
    open('/time/adjustments');
    const table = await screen.findByRole('table', { name: 'Attendance adjustments' });
    const row = within(table).getAllByRole('row')[1]!;
    expect(within(row).getByText('Leave cancelled')).toBeTruthy();
    expect(within(row).getByText('Approved')).toBeTruthy();
    expect(within(row).getByText('Came to work')).toBeTruthy();
  });

  it('shows the impact of a change before it can be sent, then sends it', async () => {
    const sent: Server['sent'] = [];
    serve({ sent, impact });
    const user = userEvent.setup();
    open('/time/adjustments');
    await user.click(await screen.findByRole('button', { name: 'New adjustment' }));
    const dialog = await screen.findByRole('dialog', { name: 'New adjustment' });
    expect(within(dialog).getByRole('button', { name: 'Send for approval' }).hasAttribute('disabled') || within(dialog).getByRole('button', { name: 'Send for approval' }).getAttribute('aria-disabled') === 'true').toBe(true);
    await user.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'Device lost the punches');
    await user.click(within(dialog).getByRole('button', { name: 'Show the impact' }));
    const panel = await within(dialog).findByRole('region', { name: 'Impact' });
    expect(within(panel).getByText('Affects: attendance, payroll')).toBeTruthy();
    expect(within(panel).getByText(/Locked period/)).toBeTruthy();
    expect(within(panel).getByText(/Carried into/)).toBeTruthy();
    expect(within(panel).getAllByRole('row')).toHaveLength(4);
    await user.click(within(dialog).getByRole('button', { name: 'Send for approval' }));
    await waitFor(() => expect(sent.filter((x) => x.url === '/api/hrm/attendance/adjustments')).toHaveLength(1));
    expect(await screen.findByText('The adjustment waits for approval.')).toBeTruthy();
  });

  it('offers no new adjustment to a reader or in read-only mode, and shows the empty state', async () => {
    serve({ sent: [] });
    open('/time/adjustments', READ);
    expect(await screen.findByText('No adjustments yet')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'New adjustment' })).toBeNull();
    cleanup();
    serve({ sent: [] });
    open('/time/adjustments', HR, false);
    expect(await screen.findByText('No adjustments yet')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'New adjustment' })).toBeNull();
  });
});
