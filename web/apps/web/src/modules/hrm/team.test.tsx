// The manager's team view (slice S1.30b; FR-HRM-SSP-02, FR-HRM-ATT-18): today's counts and people of the team, this month's days and late minutes per person from the
// monthly muster (the month is the company's, taken from the server's date), and the loading, empty, error and denied states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import ur from '@erp/i18n/messages/attendance-reports.ur.json';
import TeamScreen from './team';
import { COUNTS, DASHBOARD, PERSON, json, orgAnswer, sent, serve, shell, show } from './live-test-support';

const PERMS = ['hrm.attendance_dashboard.read', 'hrm.attendance_reports.read'];
const MONTH_ROW = { employmentId: 'e1', employeeNo: 'E-1', firstName: 'Amna', lastName: 'Khan', month: '2026-11-01', presentDays: 17, absentDays: 1, lateDays: 3, leaveDays: 2, lateMinutes: 95 };

function team(options: { dashboard?: Response; month?: Response } = {}) {
  serve((_m, url) => {
    const org = orgAnswer(url);
    if (org) return org;
    if (url.includes('/dashboard/people')) return json({ status: 'present', total: 1, more: false, items: [{ ...PERSON, status: 'present' }] });
    if (url.includes('/attendance/dashboard')) return options.dashboard ?? json(DASHBOARD);
    if (url.includes('/reports/muster_monthly')) return options.month ?? json({ code: 'muster_monthly', from: '2026-11-01', to: '2026-11-30', items: [MONTH_ROW], more: false, next: null, total: 1, summary: {} });
    return json({});
  });
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('team attendance view', () => {
  it('FR-HRM-SSP-02 shows today\'s counts and people, and this month\'s days and late minutes per person for the company\'s month', async () => {
    team();
    show(<TeamScreen />, shell(PERMS));
    expect(await screen.findByRole('button', { name: /^[\d,]+\s*Present$/ })).toBeTruthy();
    expect(await screen.findByText('Amna Khan', { selector: 'td bdi' })).toBeTruthy();
    const month = await screen.findByRole('table', { name: 'This month' });
    expect(within(month).getByText('95')).toBeTruthy();
    expect(within(month).getByText('17')).toBeTruthy();
    const call = sent.find((c) => c.url.includes('/reports/muster_monthly'))!;
    expect(call.url).toContain('from=2026-11-01');
    expect(call.url).toContain('to=2026-11-30');
  });

  it('FR-HRM-SSP-02 choosing another count lists those people', async () => {
    team();
    show(<TeamScreen />, shell(PERMS));
    await userEvent.click(await screen.findByRole('button', { name: /^[\d,]+\s*Absent$/ }));
    await vi.waitFor(() => expect(sent.some((c) => c.url.includes('/dashboard/people') && c.url.includes('status=absent'))).toBe(true));
  });

  it('FR-HRM-SSP-02 the monthly summary is left out without the reports permission', async () => {
    team();
    show(<TeamScreen />, shell(['hrm.attendance_dashboard.read']));
    expect(await screen.findByRole('button', { name: /^[\d,]+\s*Present$/ })).toBeTruthy();
    expect(screen.queryByText('This month')).toBeNull();
    expect(sent.some((c) => c.url.includes('/reports/'))).toBe(false);
  });

  it('FR-HRM-SSP-02 says so when the team is empty, shows an error with a retry, and denies without the permission', async () => {
    team({ dashboard: json({ ...DASHBOARD, totals: { ...COUNTS, headcount: 0 } }), month: json({ code: 'muster_monthly', from: null, to: null, items: [], more: false, next: null, total: 0, summary: {} }) });
    show(<TeamScreen />, shell(PERMS));
    expect(await screen.findByText('Nobody to show')).toBeTruthy();
    expect(await screen.findByText('No attendance days yet this month.')).toBeTruthy();
    cleanup();
    team({ dashboard: json({ title: 'errors.generic', status: 500 }, 500) });
    show(<TeamScreen />, shell(PERMS));
    expect(await screen.findByText('The live attendance could not be loaded.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
    cleanup();
    show(<TeamScreen />, shell([]));
    expect(screen.getByText('You may not see your team\'s attendance.')).toBeTruthy();
  });

  it('FR-HRM-SSP-02 reads in Urdu', async () => {
    team();
    show(<TeamScreen />, shell(PERMS), 'ur');
    expect(await screen.findByRole('heading', { name: ur['team.title'] })).toBeTruthy();
  });
});
