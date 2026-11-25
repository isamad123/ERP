// The live attendance dashboard (slice S1.30b; FR-HRM-ATT-18, FR-HRM-SSP-02): counts as cards with the table alternative by unit and location (names, not ids), the drill-down
// to the people with paging, polling at the server's interval (no push), a refresh button, and the loading, empty, error and permission-denied states, in English and Urdu.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import en from '@erp/i18n/messages/attendance-reports.en.json';
import ur from '@erp/i18n/messages/attendance-reports.ur.json';
import DashboardScreen from './dashboard';
import { COUNTS, DASHBOARD, PERSON, json, orgAnswer, sent, serve, shell, show } from './live-test-support';

const PERMS = ['hrm.attendance_dashboard.read'];
const dashboardCalls = () => sent.filter((c) => /\/attendance\/dashboard(\?|$)/.test(c.url)).length;

function live(options: { dashboard?: Response | (() => Response); people?: (url: string) => Response } = {}) {
  serve((_method, url) => {
    const org = orgAnswer(url);
    if (org) return org;
    if (url.includes('/dashboard/people')) return options.people ? options.people(url) : json({ status: 'late', total: 1, more: false, items: [PERSON] });
    if (url.includes('/attendance/dashboard')) return typeof options.dashboard === 'function' ? options.dashboard() : options.dashboard ?? json(DASHBOARD);
    return json({});
  });
}

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('live attendance dashboard', () => {
  it('FR-HRM-ATT-18 shows today\'s counts as cards, the as-of time and the table alternative with unit and location names', async () => {
    live();
    show(<DashboardScreen />, shell(PERMS));
    expect(await screen.findByRole('button', { name: /^[\d,]+\s*Present$/ })).toBeTruthy();
    expect(within(screen.getByRole('button', { name: /^[\d,]+\s*Present$/ })).getByText('25')).toBeTruthy();
    expect(within(screen.getByRole('button', { name: /^[\d,]+\s*of which late$/ })).getByText('4')).toBeTruthy();
    expect(screen.getByRole('status').textContent).toContain('As of');
    const byUnit = await screen.findByRole('table', { name: 'By unit' });
    await vi.waitFor(() => expect(within(byUnit).getByText('Operations')).toBeTruthy());
    const byLocation = screen.getByRole('table', { name: 'By location' });
    await vi.waitFor(() => expect(within(byLocation).getByText('Head office')).toBeTruthy());
    expect(sent.find((c) => c.url.includes('/attendance/dashboard'))?.url).toContain('companyId=');
  });

  it('FR-HRM-SSP-02 a count opens the people behind it with their unit, location and first punch, and the list closes again', async () => {
    live();
    show(<DashboardScreen />, shell(PERMS));
    await userEvent.click(await screen.findByRole('button', { name: /^[\d,]+\s*of which late$/ }));
    expect(await screen.findByText('Amna Khan')).toBeTruthy();
    expect(screen.getByText('E-1')).toBeTruthy();
    const people = sent.find((c) => c.url.includes('/dashboard/people'))!;
    expect(people.url).toContain('status=late');
    expect(people.url).toContain('offset=0');
    await userEvent.click(screen.getByRole('button', { name: 'Close the list' }));
    expect(screen.queryByText('Amna Khan')).toBeNull();
  });

  it('FR-HRM-ATT-18 the people list pages with "Show more" from the offset already shown', async () => {
    const page = (n: number) => Array.from({ length: n }, (_, i) => ({ ...PERSON, employmentId: `e${i}`, employeeNo: `E-${i}` }));
    live({ people: (url) => (url.includes('offset=0') ? json({ status: 'present', total: 52, more: true, items: page(50) }) : json({ status: 'present', total: 52, more: false, items: page(2).map((p, i) => ({ ...p, employmentId: `z${i}`, employeeNo: `Z-${i}` })) }) ) });
    show(<DashboardScreen />, shell(PERMS));
    await userEvent.click(await screen.findByRole('button', { name: /^[\d,]+\s*Present$/ }));
    await userEvent.click(await screen.findByRole('button', { name: 'Show more' }));
    expect(await screen.findByText('Z-1')).toBeTruthy();
    expect(sent.some((c) => c.url.includes('/dashboard/people') && c.url.includes('offset=50'))).toBe(true);
    expect(screen.queryByRole('button', { name: 'Show more' })).toBeNull();
  });

  it('FR-HRM-ATT-18 filters by unit and asks the server with that unit', async () => {
    live();
    show(<DashboardScreen />, shell(PERMS));
    await screen.findByRole('button', { name: /^[\d,]+\s*Present$/ });
    await userEvent.click(screen.getByRole('button', { name: /Unit/ }));
    await userEvent.click(await screen.findByRole('option', { name: /Operations/ }));
    await vi.waitFor(() => expect(sent.some((c) => c.url.includes('/attendance/dashboard') && c.url.includes('unitId='))).toBe(true));
  });

  it('FR-HRM-ATT-18 polls at the interval the server gives (never faster than 15 seconds) and refreshes on demand', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    live({ dashboard: () => json({ ...DASHBOARD, refreshSeconds: 5 }) });
    show(<DashboardScreen />, shell(PERMS));
    await screen.findByRole('button', { name: /^[\d,]+\s*Present$/ });
    expect(dashboardCalls()).toBe(1);
    await vi.advanceTimersByTimeAsync(14_000);
    expect(dashboardCalls()).toBe(1);
    await vi.advanceTimersByTimeAsync(2_000);
    await vi.waitFor(() => expect(dashboardCalls()).toBe(2));
    await userEvent.click(screen.getByRole('button', { name: 'Refresh now' }));
    await vi.waitFor(() => expect(dashboardCalls()).toBe(3));
  });

  it('FR-HRM-ATT-18 polls every 60 seconds by default', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    live();
    show(<DashboardScreen />, shell(PERMS));
    await screen.findByRole('button', { name: /^[\d,]+\s*Present$/ });
    await vi.advanceTimersByTimeAsync(59_000);
    expect(dashboardCalls()).toBe(1);
    await vi.advanceTimersByTimeAsync(2_000);
    await vi.waitFor(() => expect(dashboardCalls()).toBe(2));
  });

  it('FR-HRM-ATT-18 says so when nobody is in scope', async () => {
    live({ dashboard: json({ ...DASHBOARD, totals: { ...COUNTS, headcount: 0 }, byUnit: [], byLocation: [] }) });
    show(<DashboardScreen />, shell(PERMS));
    expect(await screen.findByText('Nobody to show')).toBeTruthy();
  });

  it('FR-HRM-ATT-18 shows a loading state first and an error with a retry that asks again', async () => {
    let fail = true;
    live({ dashboard: () => (fail ? json({ title: 'errors.generic', status: 500 }, 500) : json(DASHBOARD)) });
    show(<DashboardScreen />, shell(PERMS));
    expect(screen.getByLabelText('Loading content')).toBeTruthy();
    expect(await screen.findByText('The live attendance could not be loaded.')).toBeTruthy();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('button', { name: /^[\d,]+\s*Present$/ })).toBeTruthy();
  });

  it('FR-HRM-ATT-18 a person without the permission is told so and nothing is asked; a 403 from the server is shown as denied', async () => {
    live();
    show(<DashboardScreen />, shell([]));
    expect(screen.getByText('You may not see the live attendance.')).toBeTruthy();
    expect(sent.length).toBe(0);
    cleanup();
    live({ dashboard: json({ title: 'errors.forbidden', status: 403 }, 403) });
    show(<DashboardScreen />, shell(PERMS));
    expect(await screen.findByText('You may not see the live attendance.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Try again' })).toBeNull();
  });

  it('FR-HRM-ATT-18 the screen reads in Urdu with its own texts (the layout is right to left through logical properties)', async () => {
    live();
    show(<DashboardScreen />, shell(PERMS), 'ur');
    expect((await screen.findAllByText(ur['alive.card.present'])).length).toBeGreaterThan(0);
    expect(screen.getByRole('heading', { name: ur['alive.title'] })).toBeTruthy();
    expect(screen.queryByText(en['alive.card.present'])).toBeNull();
  });

  it('FR-HRM-ATT-18 the Urdu catalogue has every key of the English one', () => {
    expect(Object.keys(ur).sort()).toEqual(Object.keys(en).sort());
  });
});
