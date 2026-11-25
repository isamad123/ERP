// The team leave calendar screen (slice S1.33a Build item 4; FR-HRM-LVE-15, LVE-05): leave cells, half days, waiting against approved, coverage warning, filters, paging, keyboard and the
// loading, empty, error and denied states.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import ur from '@erp/i18n/messages/leave-calendar.ur.json';
import LeaveTeamScreen from './leave-team';
import { COMPANY, UNIT, json, orgAnswer, sent, serve, shell, show } from './live-test-support';

const PERMS = ['hrm.leave_requests.read'];
const T1 = '00000000-0000-7000-8000-0000000000c1';
const T2 = '00000000-0000-7000-8000-0000000000c2';
const CAL = '00000000-0000-7000-8000-0000000000d1';
const person = (n: number, extra: Record<string, unknown> = {}) => ({
  employmentId: `e${n}`, companyId: COMPANY, employeeNo: `E-${n}`, firstName: n === 1 ? 'Sara' : `Person${n}`, lastName: n === 1 ? 'Khan' : 'Test', unitId: UNIT, calendarId: CAL, leave: [], offDays: [], ...extra,
});
const ANSWER = {
  from: '2026-03-01', to: '2026-03-31', companies: [{ companyId: COMPANY, today: '2026-03-12', limitPercent: 30 }],
  types: [{ id: T1, code: 'AL', nameI18n: { en: 'Annual leave', ur: 'سالانہ رخصت' }, colorToken: 'success', unit: 'day' }, { id: T2, code: 'CL', nameI18n: { en: 'Casual leave' }, colorToken: null, unit: 'day' }],
  total: 2, more: false,
  people: [
    person(1, {
      leave: [
        { date: '2026-03-14', leaveTypeId: T1, requestId: 'r1', quantity: 1, status: 'approved', kind: 'leave' },
        { date: '2026-03-15', leaveTypeId: T2, requestId: 'r2', quantity: 0.5, status: 'submitted', kind: 'leave' },
      ],
      offDays: [{ date: '2026-03-21', kind: 'weekly_off' }],
    }),
    person(2),
  ],
  holidays: [{ calendarId: CAL, items: [{ date: '2026-03-23', name: { en: 'Pakistan Day' }, kind: 'public' }] }],
  coverage: [
    { companyId: COMPANY, date: '2026-03-14', teamSize: 8, away: 3, percent: 37.5, warn: true },
    { companyId: COMPANY, date: '2026-03-15', teamSize: 8, away: 1, percent: 12.5, warn: false },
  ],
};

function calendar(answer: Response | ((url: string) => Response) = json(ANSWER)) {
  serve((_m, url) => {
    const org = orgAnswer(url);
    if (org) return org;
    if (url.includes('/api/hrm/leave-types')) return json({ items: [{ id: T1, code: 'AL', nameI18n: { en: 'Annual leave' }, colorToken: 'success' }, { id: T2, code: 'CL', nameI18n: { en: 'Casual leave' } }] });
    if (url.includes('/api/hrm/leave-calendar')) return typeof answer === 'function' ? answer(url) : answer.clone();
    return json({});
  });
}
const calls = () => sent.filter((c) => c.url.includes('/api/hrm/leave-calendar'));

beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-03-12T08:00:00Z') }); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); });

describe('team leave calendar', () => {
  it('FR-HRM-LVE-15 asks for the company\'s current month and shows leave, half days, weekly off and holidays with names a screen reader can use', async () => {
    calendar();
    show(<LeaveTeamScreen />, shell(PERMS));
    const grid = await screen.findByRole('grid', { name: /Team leave in March 2026/ });
    expect(calls()[0]!.url).toContain('from=2026-03-01');
    expect(calls()[0]!.url).toContain('to=2026-03-31');
    const approved = within(grid).getByRole('gridcell', { name: 'Sara Khan, 14 March, Annual leave, approved' });
    expect(approved.getAttribute('data-colour')).toBe('success');
    expect(approved.hasAttribute('data-waiting')).toBe(false);
    const waiting = within(grid).getByRole('gridcell', { name: 'Sara Khan, 15 March, Casual leave, half day, waiting for approval' });
    expect(waiting.hasAttribute('data-waiting')).toBe(true);
    expect(waiting.getAttribute('data-colour')).toBe('neutral');
    expect(waiting.textContent).toContain('½');
    expect(within(grid).getByRole('gridcell', { name: 'Sara Khan, 21 March, weekly off' })).toBeTruthy();
    expect(within(grid).getByRole('gridcell', { name: 'Person2 Test, 23 March, holiday' })).toBeTruthy();
    expect(within(grid).getByRole('columnheader', { name: '23 March, Holiday: Pakistan Day' })).toBeTruthy();
    expect(within(grid).getByRole('columnheader', { name: '12 March, Today' })).toBeTruthy();
  });

  it('FR-HRM-LVE-05 the coverage row says 3 of 8 away with the limit setting, marks the warning, and leaves other days alone', async () => {
    calendar();
    show(<LeaveTeamScreen />, shell(PERMS));
    const warn = await screen.findByRole('gridcell', { name: '3 of 8 away on 14 March; limit setting 30 percent' });
    expect(warn.hasAttribute('data-warn')).toBe(true);
    const calm = screen.getByRole('gridcell', { name: '1 of 8 away on 15 March' });
    expect(calm.hasAttribute('data-warn')).toBe(false);
    expect(screen.getByRole('list', { name: 'Marks' })).toBeTruthy();
    expect(screen.getByRole('list', { name: 'Leave types' }).textContent).toContain('Annual leave');
  });

  it('FR-HRM-LVE-15 moving to the next month asks for that month, and a filter asks for that type and restarts at the first page', async () => {
    calendar();
    show(<LeaveTeamScreen />, shell(PERMS));
    await screen.findByRole('grid');
    await userEvent.click(screen.getByRole('button', { name: 'Next month' }));
    await vi.waitFor(() => expect(calls().some((c) => c.url.includes('from=2026-04-01') && c.url.includes('to=2026-04-30'))).toBe(true));
    await screen.findByRole('grid', { name: /April 2026/ });
  });

  it('FR-HRM-LVE-15 the leave type filter asks the server for that type from the first page', async () => {
    calendar();
    show(<LeaveTeamScreen />, shell(PERMS));
    await screen.findByRole('grid');
    await userEvent.click(screen.getByRole('button', { name: /Leave type/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'Casual leave' }));
    await vi.waitFor(() => expect(calls().some((c) => c.url.includes(`leaveTypeId=${T2}`) && c.url.includes("offset=0"))).toBe(true));
  });

  it('FR-HRM-LVE-15 pages the people: shows the range, and the next button asks with the next offset', async () => {
    calendar(json({ ...ANSWER, total: 120, more: true }));
    show(<LeaveTeamScreen />, shell(PERMS));
    expect(await screen.findByText('Showing 1 to 2 of 120')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Previous people' }).hasAttribute('disabled')).toBe(true);
    await userEvent.click(screen.getByRole('button', { name: 'Next people' }));
    await vi.waitFor(() => expect(calls().some((c) => c.url.includes('offset=50') && c.url.includes('limit=50'))).toBe(true));
  });

  it('FR-HRM-LVE-15 arrow keys, Home and End move between cells with one tab stop', async () => {
    calendar();
    show(<LeaveTeamScreen />, shell(PERMS));
    const grid = await screen.findByRole('grid');
    const cells = within(grid).getAllByRole('gridcell');
    expect(cells.filter((c) => c.tabIndex === 0)).toHaveLength(1);
    cells[0]!.focus();
    await userEvent.keyboard('{ArrowRight}');
    expect(document.activeElement?.getAttribute('data-r')).toBe('0');
    expect(document.activeElement?.getAttribute('data-c')).toBe('1');
    await userEvent.keyboard('{ArrowDown}');
    expect(document.activeElement?.getAttribute('data-r')).toBe('1');
    await userEvent.keyboard('{End}');
    expect(document.activeElement?.getAttribute('data-c')).toBe('30');
    await userEvent.keyboard('{Home}');
    expect(document.activeElement?.getAttribute('data-c')).toBe('0');
    expect(within(grid).getAllByRole('gridcell').filter((c) => c.tabIndex === 0)).toHaveLength(1);
  });

  it('FR-HRM-LVE-15 says so when no one is in the team, shows an error with a retry, and denies without the permission', async () => {
    calendar(json({ ...ANSWER, people: [], total: 0, coverage: [] }));
    show(<LeaveTeamScreen />, shell(PERMS));
    expect(await screen.findByText('No one in your team')).toBeTruthy();
    cleanup();
    calendar(json({ title: 'errors.generic', status: 500 }, 500));
    show(<LeaveTeamScreen />, shell(PERMS));
    expect(await screen.findByText('The leave calendar could not be loaded.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
    cleanup();
    calendar();
    show(<LeaveTeamScreen />, shell([]));
    expect(screen.getByText('You may not see the team leave calendar.')).toBeTruthy();
    expect(calls()).toHaveLength(0);
  });

  it('FR-HRM-LVE-15 reads in Urdu with the type name in Urdu', async () => {
    calendar();
    show(<LeaveTeamScreen />, shell(PERMS), 'ur');
    expect(await screen.findByRole('list', { name: ur['lcal.legend.types'] })).toBeTruthy();
    expect(screen.getAllByText('سالانہ رخصت').length).toBeGreaterThan(0);
  });
});
