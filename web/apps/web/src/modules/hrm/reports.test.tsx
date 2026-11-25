// The attendance reports (slice S1.30b; FR-HRM-ATT-19, FR-HRM-RPT-05): the report picker with the seven reports, filters that apply to the chosen report, the table of the
// answer's columns with unit and location names, the summary strip, paging by the server's offset, the export of exactly the filter shown, and the loading, empty,
// error and denied states, in English and Urdu. No pay field is ever shown.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import ur from '@erp/i18n/messages/attendance-reports.ur.json';
import ReportsScreen from './reports';
import { UNIT, LOCATION, json, orgAnswer, sent, serve, shell, show } from './live-test-support';

const PERMS = ['hrm.attendance_reports.read', 'itg.exports.run'];
const CATALOGUE = {
  reports: [
    { code: 'muster_daily', exportType: 'hrm.rpt_att_muster_daily', columns: ['employmentId', 'employeeNo', 'firstName', 'lastName', 'orgUnitId', 'locationId', 'workDate', 'result', 'firstIn', 'lateMinutes'] },
    { code: 'absenteeism', exportType: 'hrm.rpt_att_absenteeism', columns: ['employeeNo', 'month', 'scheduledDays', 'absentDays', 'absencePercent'] },
    { code: 'punch_log', exportType: 'hrm.rpt_att_punch_log', columns: ['employeeNo', 'eventAt', 'direction', 'source', 'isDuplicate'] },
    { code: 'exception_ageing', exportType: 'hrm.rpt_att_exception_ageing', columns: ['employeeNo', 'workDate', 'kind', 'status', 'ageDays'] },
    { code: 'device_sync', exportType: 'hrm.rpt_att_device_sync', columns: ['serialNo', 'locationId', 'status', 'lastSyncAt'] },
  ],
};
const ROW = { employmentId: 'e1', employeeNo: 'E-1', firstName: 'Amna', lastName: 'Khan', orgUnitId: UNIT, locationId: LOCATION, workDate: '2026-11-24', result: 'present', firstIn: '2026-11-24T04:05:00Z', lateMinutes: 12 };
const PAGE = (overrides: Record<string, unknown> = {}) => ({ code: 'muster_daily', from: '2026-10-26', to: '2026-11-25', items: [ROW], more: false, next: null, total: 1, summary: { 'result.present': 20, 'result.absent': 2 }, ...overrides });

function reports(answer: (url: string) => Response = () => json(PAGE())) {
  serve((_m, url) => {
    const org = orgAnswer(url);
    if (org) return org;
    if (url.endsWith('/attendance/reports')) return json(CATALOGUE);
    if (url.includes('/attendance/reports/')) return answer(url);
    if (url.includes('/api/itg/exports/check')) return json({ sensitiveClasses: [], reasonRequired: false, needsApproval: false, approvalOn: false, rows: 1, rowsCapped: false, approvalRows: 100, hidden: [] });
    return json({});
  });
}
const lastReport = () => [...sent].reverse().find((c) => c.url.includes('/attendance/reports/'))!;

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('attendance reports screen', () => {
  it('FR-HRM-ATT-19 shows the report with the columns of the answer, names for unit and location, the summary and no id columns', async () => {
    reports();
    show(<ReportsScreen />, shell(PERMS));
    const table = await screen.findByRole('table', { name: 'Daily muster' });
    await vi.waitFor(() => expect(within(table).getByText('Operations')).toBeTruthy());
    expect(within(table).getByText('Head office')).toBeTruthy();
    expect(within(table).getByText('E-1')).toBeTruthy();
    expect(within(table).getByText('Late minutes')).toBeTruthy();
    expect(within(table).queryByText('employmentId')).toBeNull();
    expect(screen.getByLabelText('Summary').textContent).toContain('result.present');
    expect(lastReport().url).toContain('/attendance/reports/muster_daily?');
    expect(lastReport().url).toContain('limit=100');
    expect(screen.queryByText(/salary|pay/i)).toBeNull();
  });

  it('FR-HRM-ATT-19 changing the report asks for that report and shows only the filters that apply to it', async () => {
    reports((url) => json(url.includes('exception_ageing') ? PAGE({ code: 'exception_ageing', items: [{ employeeNo: 'E-2', workDate: '2026-11-20', kind: 'missing_out', status: 'open', ageDays: 5 }] }) : PAGE()));
    show(<ReportsScreen />, shell(PERMS));
    await screen.findByRole('table', { name: 'Daily muster' });
    expect(screen.queryByLabelText('At least this many days old')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: /Report/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'Exception ageing' }));
    expect(await screen.findByRole('table', { name: 'Exception ageing' })).toBeTruthy();
    expect(screen.getByLabelText('At least this many days old')).toBeTruthy();
    expect(lastReport().url).toContain('/exception_ageing?');
    expect(lastReport().url).toContain('status=open');
    await userEvent.type(screen.getByLabelText('At least this many days old'), '3');
    await vi.waitFor(() => expect(lastReport().url).toContain('minAgeDays=3'));
  });

  it('FR-HRM-ATT-19 the search and status filters go to the server and paging uses the offset the server gave', async () => {
    reports((url) => json(PAGE(url.includes('offset=100') ? { items: [{ ...ROW, employeeNo: 'E-101' }], next: null } : { more: true, next: 100, total: 101 })));
    show(<ReportsScreen />, shell(PERMS));
    await userEvent.type(await screen.findByLabelText('Employee number or name'), 'amna');
    await vi.waitFor(() => expect(lastReport().url).toContain('search=amna'));
    await userEvent.click(await screen.findByRole('button', { name: 'Next page' }));
    expect(await screen.findByText('E-101')).toBeTruthy();
    expect(lastReport().url).toContain('offset=100');
    expect(screen.getByRole('button', { name: 'Previous page' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Next page' })).toBeNull();
  });

  it('FR-HRM-RPT-05 the export button asks the export centre for the report\'s own type with the filter shown', async () => {
    reports();
    show(<ReportsScreen />, shell(PERMS));
    await userEvent.type(await screen.findByLabelText('Employee number or name'), 'amna');
    await vi.waitFor(() => expect(lastReport().url).toContain('search=amna'));
    await userEvent.click(await screen.findByRole('button', { name: /Export/ }));
    await vi.waitFor(() => expect(sent.some((c) => c.url.includes('/api/itg/exports/check'))).toBe(true));
    const check = sent.filter((c) => c.url.includes('/api/itg/exports/check')).at(-1)!.body as { source: string; query: Record<string, string>; companyId: string; columns: { field: string }[] };
    expect(check.source).toBe('hrm.rpt_att_muster_daily');
    expect(check.query).toMatchObject({ search: 'amna' });
    expect(check.companyId).toBeTruthy();
    expect(check.columns.map((c) => c.field)).not.toContain('employmentId');
  });

  it('FR-HRM-RPT-05 there is no export button without the export permission', async () => {
    reports();
    show(<ReportsScreen />, shell(['hrm.attendance_reports.read']));
    await screen.findByRole('table', { name: 'Daily muster' });
    expect(screen.queryByRole('button', { name: /Export/ })).toBeNull();
  });

  it('FR-HRM-ATT-19 shows loading, then an empty state, an error with retry, and denies without the permission', async () => {
    reports(() => json(PAGE({ items: [], total: 0, summary: {} })));
    show(<ReportsScreen />, shell(PERMS));
    expect(screen.getByLabelText('Loading content')).toBeTruthy();
    expect(await screen.findByText('No rows')).toBeTruthy();
    cleanup();
    reports(() => json({ title: 'errors.generic', status: 500 }, 500));
    show(<ReportsScreen />, shell(PERMS));
    expect(await screen.findByText('The report could not be loaded.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
    cleanup();
    reports();
    show(<ReportsScreen />, shell([]));
    expect(screen.getByText('You may not see attendance reports.')).toBeTruthy();
    expect(sent.length).toBe(0);
  });

  it('FR-HRM-ATT-19 reads in Urdu', async () => {
    reports();
    show(<ReportsScreen />, shell(PERMS), 'ur');
    expect(await screen.findByRole('heading', { name: ur['arep.title'] })).toBeTruthy();
    expect(await screen.findByRole('table', { name: ur['arep.report.muster_daily'] })).toBeTruthy();
  });
});
