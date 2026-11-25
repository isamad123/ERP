// The leave reports (slice S1.33b; FR-HRM-LVE-10, FR-HRM-RPT-05, FR-HRM-GHR-06): the three reports with their summary tables and rows, filters sent as query members, paging by the
// server's offset, the export of exactly the filter shown, field errors in words, the request link, unit-aware numbers, no pay column, and the empty, error and denied states, in English and Urdu.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import ur from '@erp/i18n/messages/leave-reports.ur.json';
import en from '@erp/i18n/messages/leave-reports.en.json';
import LeaveReportsScreen from './leave-reports';
import { UNIT, json, orgAnswer, sent, serve, shell, show } from './live-test-support';

const PERMS = ['hrm.leave_reports.read', 'itg.exports.run'];
const CATALOGUE = {
  reports: [
    { code: 'balance_summary', exportType: 'hrm.rpt_leave_balance_summary', columns: ['companyId', 'employmentId', 'employeeNo', 'firstName', 'lastName', 'orgUnitId', 'leaveTypeCode', 'unit', 'leaveYear', 'added', 'used', 'adjusted', 'removed', 'balance'] },
    { code: 'leave_taken', exportType: 'hrm.rpt_leave_leave_taken', columns: ['employeeNo', 'firstName', 'lastName', 'orgUnitId', 'leaveTypeCode', 'unit', 'month', 'requests', 'taken', 'unpaid'] },
    { code: 'pending_requests', exportType: 'hrm.rpt_leave_pending_requests', columns: ['requestId', 'requestNo', 'employeeNo', 'firstName', 'lastName', 'orgUnitId', 'leaveTypeCode', 'unit', 'startDate', 'endDate', 'days', 'status', 'requestedAt', 'ageDays'] },
  ],
};
const FORBIDDEN = /pay|salary|wage|amount|gross|net|money/i;
const BALANCE_ROW = { companyId: 'c', employmentId: 'e1', employeeNo: 'E-1', firstName: 'Amna', lastName: 'Khan', orgUnitId: UNIT, leaveTypeCode: 'AL', unit: 'day', leaveYear: 2026, added: 20, used: 3.5, adjusted: 0, removed: 1, balance: 15.5 };
const BALANCE = (o: Record<string, unknown> = {}) => ({
  code: 'balance_summary', from: null, to: null, items: [BALANCE_ROW], more: false, next: null, total: 1,
  summary: { groups: [
    { orgUnitId: UNIT, leaveTypeCode: 'AL', unit: 'day', people: 7, added: 140, used: 24.5, adjusted: 0, removed: 7, balance: 108.5 },
    { orgUnitId: UNIT, leaveTypeCode: 'SH', unit: 'hour', people: 3, added: 24, used: 6, adjusted: 0, removed: 0, balance: 18 },
  ] }, ...o,
});
const TAKEN = {
  code: 'leave_taken', from: '2026-01-01', to: '2026-11-30', total: 1, more: false, next: null,
  items: [{ employeeNo: 'E-1', firstName: 'Amna', lastName: 'Khan', orgUnitId: UNIT, leaveTypeCode: 'AL', unit: 'day', month: '2026-10-01', requests: 2, taken: 3, unpaid: 0 }],
  summary: { groups: [
    { leaveTypeCode: 'AL', unit: 'day', month: '2026-10-01', people: 4, requests: 5, taken: 9, unpaid: 0 },
    { leaveTypeCode: 'AL', unit: 'day', month: '2026-11-01', people: 2, requests: 2, taken: 4.5, unpaid: 1 },
  ] },
};
const PENDING = {
  code: 'pending_requests', from: null, to: null, total: 1, more: false, next: null,
  items: [{ requestId: 'r-9', requestNo: 'LV-0009', employeeNo: 'E-2', firstName: 'Bilal', lastName: 'Ali', orgUnitId: UNIT, leaveTypeCode: 'AL', unit: 'day', startDate: '2026-12-01', endDate: '2026-12-03', days: 3, status: 'submitted', requestedAt: '2026-11-20T05:00:00Z', ageDays: 5 }],
  summary: { groups: [{ leaveTypeCode: 'AL', status: 'submitted', requests: 4, oldestAgeDays: 9 }] },
};

function reports(answer: (url: string) => Response = () => json(BALANCE())) {
  serve((_m, url) => {
    const org = orgAnswer(url);
    if (org) return org;
    if (url.includes('/api/hrm/leave-types')) return json({ items: [{ id: 't1', companyId: 'c', code: 'AL', nameI18n: { en: 'Annual leave', ur: 'سالانہ چھٹی' }, unit: 'day' }, { id: 't2', companyId: 'c', code: 'SH', nameI18n: { en: 'Short leave' }, unit: 'hour' }], total: 2 });
    if (url.endsWith('/api/hrm/leave/reports')) return json(CATALOGUE);
    if (url.includes('/api/hrm/leave/reports/')) return answer(url);
    if (url.includes('/api/itg/exports/check')) return json({ sensitiveClasses: [], reasonRequired: false, needsApproval: false, approvalOn: false, rows: 1, rowsCapped: false, approvalRows: 100, hidden: [] });
    return json({});
  });
}
const lastReport = () => [...sent].reverse().find((c) => c.url.includes('/api/hrm/leave/reports/'))!;
const pick = async (name: RegExp, option: string) => {
  await userEvent.click(screen.getByRole('button', { name }));
  await userEvent.click(await screen.findByRole('option', { name: option }));
};

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('leave reports screen', () => {
  it('FR-HRM-LVE-10 balance summary shows the group table by unit and type first, never adding up different units, then the employee rows', async () => {
    reports();
    show(<LeaveReportsScreen />, shell(PERMS));
    const groups = await screen.findByRole('table', { name: 'By unit and leave type' });
    await vi.waitFor(() => expect(within(groups).getAllByText('Operations').length).toBe(2));
    expect(within(groups).getByText('Annual leave')).toBeTruthy();
    expect(within(groups).getByText('Short leave')).toBeTruthy();
    expect(within(groups).getByText('Days')).toBeTruthy();
    expect(within(groups).getByText('Hours')).toBeTruthy();
    expect(within(groups).getByText('108.5')).toBeTruthy();
    expect(within(groups).getByText('18')).toBeTruthy();
    expect(within(groups).queryByText('126.5')).toBeNull();
    const rows = screen.getByRole('table', { name: 'People' });
    expect(within(rows).getByText('E-1')).toBeTruthy();
    expect(within(rows).getByText('15.5')).toBeTruthy();
    expect(within(rows).getByText('3.5')).toBeTruthy();
    expect(groups.compareDocumentPosition(rows) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(screen.getByText('You see the people in your data scope.')).toBeTruthy();
    expect(lastReport().url).toContain('/api/hrm/leave/reports/balance_summary?');
    expect(lastReport().url).toContain('limit=100');
  });

  it('FR-HRM-LVE-10 leave taken shows a type by month table built from the groups with unpaid leave noted', async () => {
    reports((url) => json(url.includes('leave_taken') ? TAKEN : BALANCE()));
    show(<LeaveReportsScreen />, shell(PERMS));
    await screen.findByRole('table', { name: 'By unit and leave type' });
    await pick(/Report/, 'Leave taken by type and month');
    const matrix = await screen.findByRole('table', { name: 'By leave type and month' });
    expect(within(matrix).getAllByRole('columnheader').length).toBe(4);
    expect(within(matrix).getByText('Annual leave')).toBeTruthy();
    expect(within(matrix).getByText('9')).toBeTruthy();
    expect(within(matrix).getByText(/4\.5 · 1 unpaid/)).toBeTruthy();
    expect(lastReport().url).toContain('/leave_taken?');
  });

  it('FR-HRM-LVE-10 waiting requests link each request number to its page and show the waiting groups', async () => {
    reports((url) => json(url.includes('pending_requests') ? PENDING : BALANCE()));
    show(<LeaveReportsScreen />, shell(PERMS));
    await screen.findByRole('table', { name: 'By unit and leave type' });
    await pick(/Report/, 'Leave waiting for a decision');
    const link = await screen.findByRole('link', { name: 'LV-0009' });
    expect(link.getAttribute('href')).toBe('/time/leave/requests/r-9');
    const groups = screen.getByRole('table', { name: 'Waiting by leave type and status' });
    expect(within(groups).getByText('Waiting for approval')).toBeTruthy();
    expect(within(groups).getByText('9')).toBeTruthy();
    await pick(/Status/, 'Cancellation requested');
    await vi.waitFor(() => expect(lastReport().url).toContain('status=cancel_requested'));
  });

  it('FR-HRM-LVE-10 filters go to the server as query members and paging uses the offset the server gave', async () => {
    reports((url) => json(BALANCE(url.includes('offset=100') ? { items: [{ ...BALANCE_ROW, employeeNo: 'E-101' }], next: null } : { more: true, next: 100, total: 101 })));
    show(<LeaveReportsScreen />, shell(PERMS));
    await userEvent.type(await screen.findByLabelText('Employee number or name'), 'amna');
    await vi.waitFor(() => expect(lastReport().url).toContain('search=amna'));
    await userEvent.type(screen.getByLabelText('Leave year'), '2025');
    await vi.waitFor(() => expect(lastReport().url).toContain('leaveYear=2025'));
    await pick(/Leave type/, 'Short leave');
    await vi.waitFor(() => expect(lastReport().url).toContain('leaveTypeId=t2'));
    await pick(/Unit/, 'Operations');
    await vi.waitFor(() => expect(lastReport().url).toContain(`unitId=${UNIT}`));
    expect(lastReport().url).toContain('companyId=');
    await userEvent.click(await screen.findByRole('button', { name: 'Next page' }));
    expect(await screen.findByText('E-101')).toBeTruthy();
    expect(lastReport().url).toContain('offset=100');
    expect(screen.getByRole('button', { name: 'Previous page' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Next page' })).toBeNull();
  });

  it('FR-HRM-RPT-05 the export button asks the export centre for the report\'s own type with exactly the filter shown', async () => {
    reports();
    show(<LeaveReportsScreen />, shell(PERMS));
    await userEvent.type(await screen.findByLabelText('Employee number or name'), 'amna');
    await vi.waitFor(() => expect(lastReport().url).toContain('search=amna'));
    await userEvent.click(await screen.findByRole('button', { name: /Export/ }));
    await vi.waitFor(() => expect(sent.some((c) => c.url.includes('/api/itg/exports/check'))).toBe(true));
    const check = sent.filter((c) => c.url.includes('/api/itg/exports/check')).at(-1)!.body as { source: string; query: Record<string, string>; companyId: string; columns: { field: string }[] };
    expect(check.source).toBe('hrm.rpt_leave_balance_summary');
    expect(check.query).toMatchObject({ search: 'amna' });
    expect(check.companyId).toBeTruthy();
    expect(check.columns.map((c) => c.field)).not.toContain('employmentId');
  });

  it('FR-HRM-RPT-05 there is no export button without the export permission', async () => {
    reports();
    show(<LeaveReportsScreen />, shell(['hrm.leave_reports.read']));
    await screen.findByRole('table', { name: 'People' });
    expect(screen.queryByRole('button', { name: /Export/ })).toBeNull();
  });

  it('FR-HRM-GHR-06 no column of any leave report is about pay or salary, even when an answer carries one', async () => {
    const sneaky = { ...CATALOGUE, reports: CATALOGUE.reports.map((r) => ({ ...r, columns: [...r.columns, 'salaryAmount', 'netPay'] })) };
    serve((_m, url) => {
      const org = orgAnswer(url);
      if (org) return org;
      if (url.endsWith('/api/hrm/leave/reports')) return json(sneaky);
      if (url.includes('/api/hrm/leave/reports/')) return json(url.includes('leave_taken') ? TAKEN : url.includes('pending') ? PENDING : BALANCE());
      return json({ items: [], total: 0 });
    });
    show(<LeaveReportsScreen />, shell(PERMS));
    for (const [name, report] of [['balance_summary', null], ['leave_taken', 'Leave taken by type and month'], ['pending_requests', 'Leave waiting for a decision']] as const) {
      if (report) await pick(/Report/, report);
      await vi.waitFor(() => expect(lastReport().url).toContain(`/${name}?`));
      const table = await screen.findByRole('table', { name: 'People' });
      await vi.waitFor(() => expect(lastReport().url).toContain(`/${name}?`));
      const keys = [...document.querySelectorAll('[data-column]')].map((e) => e.getAttribute('data-column') ?? '');
      expect(keys.length).toBeGreaterThan(5);
      for (const key of keys) expect(key).not.toMatch(FORBIDDEN);
      for (const th of within(table).getAllByRole('columnheader')) expect(th.textContent).not.toMatch(FORBIDDEN);
    }
  });

  it('FR-HRM-LVE-10 a refused date range is explained in words with no retry', async () => {
    reports((url) => (url.includes('search=') ? json({ title: 'errors.validation', status: 422, errors: { to: ['errors.hrm.leave.report.range_order'] } }, 422) : json(PENDING)));
    show(<LeaveReportsScreen />, shell(PERMS));
    await screen.findByRole('table', { name: 'People' });
    await pick(/Report/, 'Leave waiting for a decision');
    await userEvent.type(await screen.findByLabelText('Employee number or name'), 'x');
    expect(await screen.findByText('The end date is before the start date.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Try again' })).toBeNull();
  });

  it('FR-HRM-LVE-10 shows loading, then an empty state, an error with retry, and denies without the permission', async () => {
    reports(() => json(BALANCE({ items: [], total: 0, summary: { groups: [] } })));
    show(<LeaveReportsScreen />, shell(PERMS));
    expect(screen.getByLabelText('Loading content')).toBeTruthy();
    expect(await screen.findByText('No rows')).toBeTruthy();
    cleanup();
    reports(() => json({ title: 'errors.generic', status: 500 }, 500));
    show(<LeaveReportsScreen />, shell(PERMS));
    expect(await screen.findByText('The report could not be loaded.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
    cleanup();
    reports((() => json({ title: 'errors.forbidden', status: 403 }, 403)));
    show(<LeaveReportsScreen />, shell(PERMS));
    expect(await screen.findByText('You may not see leave reports.')).toBeTruthy();
    cleanup();
    reports();
    show(<LeaveReportsScreen />, shell([]));
    expect(screen.getByText('You may not see leave reports.')).toBeTruthy();
    expect(sent.length).toBe(0);
  });

  it('FR-HRM-LVE-10 reads in Urdu with the leave type name in Urdu and both catalogues carry the same keys', async () => {
    expect(Object.keys(ur).sort()).toEqual(Object.keys(en).sort());
    reports();
    show(<LeaveReportsScreen />, shell(PERMS), 'ur');
    expect(await screen.findByRole('heading', { name: ur['lrep.title'] })).toBeTruthy();
    const groups = await screen.findByRole('table', { name: ur['lrep.groups.balance'] });
    await vi.waitFor(() => expect(within(groups).getByText('سالانہ چھٹی')).toBeTruthy());
  });
});
