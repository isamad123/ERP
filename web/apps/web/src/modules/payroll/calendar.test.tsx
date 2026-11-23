// The payroll calendar screen (slice S1.34a Build items 2 and 3): FR-HRM-PAY-01 generating the periods of a tax year (the weekday of a
// weekly group, the server's refusal in plain words), FR-HRM-PAY-38 the calendar with the days left to the cut-off, changing the dates of a
// period that has not opened, the readiness checklist that never shows an unavailable item as passed, and the read-only and denied states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import CalendarScreen from './calendar';
import type { CalendarPeriod, GroupOption, Readiness } from '../../api/pay';
import { json, shell, show } from '../admin/access-test-support';

const READ = ['hrm.pay_periods.read', 'hrm.pay_groups.read', 'hrm.pay_readiness.read'];
const WRITE = [...READ, 'hrm.pay_periods.generate', 'hrm.pay_periods.update'];

const group = (id: string, code: string, frequency: GroupOption['frequency'] = 'monthly', isActive = true): GroupOption => ({ id, code, nameI18n: { en: code }, frequency, isActive });
const period = (id: string, no: number, extra: Partial<CalendarPeriod> = {}): CalendarPeriod => ({
  id, companyId: 'c1', payGroupId: 'g1', payGroupCode: 'MONTHLY', periodNo: no, taxYear: 2026, startDate: '2026-07-01', endDate: '2026-07-31', cutOffDate: '2026-07-28',
  paymentDate: '2026-08-05', status: 'future', daysToCutOff: 4, daysToPayment: 12, rowVersion: 1, ...extra,
});
const readiness = (items: Readiness['items'], ready = false): Readiness => ({ periodId: 'p1', payGroupId: 'g1', ready, summary: { passed: 0, failed: 0, notAvailable: 0 }, items });

interface Server {
  sent: { method: string; url: string; body: unknown }[];
  groups?: GroupOption[];
  periods?: CalendarPeriod[];
  readiness?: Readiness;
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
      return s.answer?.(method, path, body) ?? json({ id: 'p1', rowVersion: 2, payGroupId: 'g1', taxYear: 2026, count: 12 }, 201);
    }
    if (path === '/api/hrm/pay-groups') return json({ items: s.groups ?? [group('g1', 'MONTHLY'), group('g2', 'WEEKLY', 'weekly')], next: null });
    if (path === '/api/hrm/pay-calendar') return s.denied ? json({ title: 'errors.access.forbidden' }, 403) : json({ items: s.periods ?? [period('p1', 1), period('p2', 2, { startDate: '2026-08-01', endDate: '2026-08-31', status: 'closed', daysToCutOff: -40 })], more: false });
    if (path.endsWith('/readiness')) return json(s.readiness ?? readiness([]));
    return json({ title: 'errors.data.not_found' }, 404);
  }));
}

const open = (permissions = WRITE) => show('/payroll/calendar', '/payroll/calendar', <CalendarScreen />, shell(permissions));

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-HRM-PAY-38 the payroll calendar', () => {
  it('lists each period with its dates, the days left to the cut-off and its status', async () => {
    serve({ sent: [] });
    open();
    const table = await screen.findByRole('table', { name: 'Payroll calendar' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);
    expect(within(rows[0]!).getByText('MONTHLY')).toBeTruthy();
    expect(within(rows[0]!).getByText('in 4 days')).toBeTruthy();
    expect(within(rows[0]!).getByText('Not opened')).toBeTruthy();
    expect(within(rows[0]!).getByText('2026 no. 1')).toBeTruthy(); // no thousands separator in a year
    expect(within(rows[1]!).getByText('Closed')).toBeTruthy();
    expect(within(rows[1]!).queryByText(/ago|in \d+ days|today/)).toBeNull(); // nothing to count down for a closed period
  });

  it('offers the dates change only for a period that has not opened, and only to a person who may change them', async () => {
    serve({ sent: [] });
    open();
    const table = await screen.findByRole('table', { name: 'Payroll calendar' });
    const [first, second] = within(table).getAllByRole('row').slice(1);
    expect(within(first!).getByRole('button', { name: 'Change the dates of period 1 of MONTHLY' })).toBeTruthy();
    expect(within(second!).queryByRole('button', { name: /Change the dates/ })).toBeNull();
    cleanup();
    serve({ sent: [] });
    open(READ);
    await screen.findByRole('table', { name: 'Payroll calendar' });
    expect(screen.queryByRole('button', { name: 'Generate periods' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Change the dates/ })).toBeNull();
    expect(screen.getAllByRole('button', { name: /Readiness of period/ })).toHaveLength(2);
  });

  it('says plainly when the person may not see the calendar, and when there are no periods yet', async () => {
    serve({ sent: [], denied: true });
    open();
    expect(await screen.findByText('You may not see the payroll calendar.')).toBeTruthy();
    cleanup();
    serve({ sent: [], periods: [] });
    open();
    expect(await screen.findByText('No pay periods yet')).toBeTruthy();
    expect(screen.getByText('Generate the periods of a tax year for a pay group to see its calendar.')).toBeTruthy();
    cleanup();
    serve({ sent: [], periods: [] });
    open(READ);
    expect(await screen.findByText('Ask a payroll administrator to generate the periods of a tax year.')).toBeTruthy();
  });
});

describe('FR-HRM-PAY-01 generating the periods of a tax year', () => {
  it('sends the tax year, and the weekday only for a weekly group', async () => {
    const s: Server = { sent: [] };
    serve(s);
    open();
    const user = userEvent.setup();
    await screen.findByRole('table', { name: 'Payroll calendar' });
    await user.click(screen.getByRole('button', { name: 'Generate periods' }));
    const dialog = await screen.findByRole('dialog', { name: 'Generate pay periods' });
    expect(within(dialog).queryByText('Week starts on')).toBeNull(); // the first group is monthly
    await user.type(within(dialog).getByRole('textbox', { name: /Tax year/ }), '2026');
    await user.click(within(dialog).getByRole('button', { name: 'Generate' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toEqual({ method: 'POST', url: '/api/hrm/pay-groups/g1/periods/generate', body: { taxYear: 2026 } });
  });

  it('asks for the weekday of a weekly group and sends it with the tax year', async () => {
    const s: Server = { sent: [], groups: [group('g2', 'WEEKLY', 'weekly')] };
    serve(s);
    open();
    const user = userEvent.setup();
    await screen.findByRole('table', { name: 'Payroll calendar' });
    await user.click(screen.getByRole('button', { name: 'Generate periods' }));
    const dialog = await screen.findByRole('dialog', { name: 'Generate pay periods' });
    expect(await within(dialog).findByText('Week starts on')).toBeTruthy();
    await user.type(within(dialog).getByRole('textbox', { name: /Tax year/ }), '2026');
    await user.click(within(dialog).getByRole('button', { name: 'Generate' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toEqual({ method: 'POST', url: '/api/hrm/pay-groups/g2/periods/generate', body: { taxYear: 2026, weekStartsOn: 'monday' } });
  });

  it('shows the server\'s refusal in plain words and keeps the dialog open', async () => {
    const s: Server = { sent: [], answer: () => json({ title: 'errors.hrm.pay.periods_exist', status: 409 }, 409) };
    serve(s);
    open();
    const user = userEvent.setup();
    await screen.findByRole('table', { name: 'Payroll calendar' });
    await user.click(screen.getByRole('button', { name: 'Generate periods' }));
    const dialog = await screen.findByRole('dialog', { name: 'Generate pay periods' });
    await user.type(within(dialog).getByRole('textbox', { name: /Tax year/ }), '2026');
    await user.click(within(dialog).getByRole('button', { name: 'Generate' }));
    expect(await within(dialog).findByText('Periods already exist for this pay group and tax year.')).toBeTruthy();
  });
});

describe('FR-HRM-PAY-38 the readiness checklist', () => {
  it('shows an item whose slice does not exist yet as not available and lists who is missing', async () => {
    const s: Server = {
      sent: [],
      readiness: readiness([
        { code: 'attendance_locked', labelKey: 'hrm.pay.readiness.attendance_locked', slice: 'S1.31', requirement: 'FR-XM-26', status: 'not_available', count: 0, employees: [] },
        { code: 'bank_data_complete', labelKey: 'hrm.pay.readiness.bank_data_complete', slice: 'S1.34a', requirement: 'FR-HRM-PAY-38', status: 'failed', count: 2,
          employees: [{ employmentId: 'e1', employeeNo: 'EMP-00001' }, { employmentId: 'e2', employeeNo: 'EMP-00002' }] },
      ]),
    };
    serve(s);
    open();
    const user = userEvent.setup();
    const table = await screen.findByRole('table', { name: 'Payroll calendar' });
    await user.click(within(within(table).getAllByRole('row')[1]!).getByRole('button', { name: 'Readiness of period 1 of MONTHLY' }));
    const dialog = await screen.findByRole('dialog', { name: 'Readiness of period 1 of MONTHLY' });
    expect(await within(dialog).findByText('Not ready: something is missing or cannot be checked yet.')).toBeTruthy();
    expect(within(dialog).getByText('Not available')).toBeTruthy();
    expect(within(dialog).getByText(/comes with slice S1\.31; until then it is never counted as ready/)).toBeTruthy();
    expect(within(dialog).queryByText('Ready')).toBeNull(); // never passed
    expect(within(dialog).getByText('Missing')).toBeTruthy();
    expect(within(dialog).getByText(/2 employees are missing this:/)).toBeTruthy();
    expect(within(dialog).getByText(/EMP-00001, EMP-00002/)).toBeTruthy();
  });
});

describe('FR-HRM-PAY-38 changing the dates of a period', () => {
  it('sends both dates with the row version read', async () => {
    const s: Server = { sent: [], answer: () => json({ id: 'p1', rowVersion: 2 }) };
    serve(s);
    open();
    const user = userEvent.setup();
    await screen.findByRole('table', { name: 'Payroll calendar' });
    await user.click(screen.getByRole('button', { name: 'Change the dates of period 1 of MONTHLY' }));
    const dialog = await screen.findByRole('dialog', { name: 'Dates of period 1 of MONTHLY' });
    await user.click(within(dialog).getByRole('button', { name: 'Save dates' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toEqual({ method: 'PUT', url: '/api/hrm/pay-periods/p1', body: { cutOffDate: '2026-07-28', paymentDate: '2026-08-05', rowVersion: 1 } });
  });
});
