// The leave year-end screen (slice S1.33b; FR-HRM-LVE-11, FR-XM-05, FR-XM-06, R-306): prepare a preview through the job centre, read the preview table and totals, confirm (posted at once, or held for
// a second person), roll back a preview, an applied run is read-only and says the year is closed, and every refusal of the server is shown in words.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import LeaveYearEndScreen from './leave-year-end';
import { COMPANY, json, posts, problem, sent, serve, shell, show, type Body } from './leave-test-kit';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const READ = ['hrm.leave_year_end_runs.read'];
const ALL = [...READ, 'hrm.leave_year_end_runs.confirm'];
const line = (employmentId: string, employeeNo: string, leaveTypeId: string, code: string, extra: Record<string, unknown> = {}) => ({
  employmentId, employeeNo, leaveTypeId, code, unit: 'day', yearStart: '2026-01-01', yearEnd: '2026-12-31', closing: 10, carryForward: 5, encash: 2, lapse: 3, carryExpiresOn: '2027-03-31', status: 'ok', lots: null, ...extra,
});
const LINES = [
  line('e1', 'E-1', 't-al', 'AL'),
  line('e2', 'E-2', 't-al', 'AL', { closing: 4, carryForward: 4, encash: 0, lapse: 0, carryExpiresOn: null }),
  line('e3', 'E-30', 't-sl', 'SL', { status: 'no_policy', closing: 6, carryForward: 0, encash: 0, lapse: 0 }),
  line('e4', 'E-4', 't-sl', 'SL', { status: 'separated', closing: 1, carryForward: 0, encash: 0, lapse: 0 }),
];
const SUMMARY = {
  leaveYear: 2026, preparedAt: '2026-11-24T08:00:00Z', types: [{ leaveTypeId: 't-al', code: 'AL', yearStart: '2026-01-01', yearEnd: '2026-12-31' }, { leaveTypeId: 't-sl', code: 'SL', yearStart: '2026-01-01', yearEnd: '2026-12-31' }],
  totals: { lines: 4, carryForward: 9, encash: 2, lapse: 3, noPolicy: 1, separated: 1 }, lines: LINES,
};
const run = (id: string, status: string, extra: Record<string, unknown> = {}, summary: Record<string, unknown> = {}) => ({
  id, companyId: COMPANY, leaveYear: 2026, jobRunId: 'jr0', summary: { ...SUMMARY, ...summary }, status, createdAt: '2026-11-24T08:00:00Z', createdBy: 'u1', updatedAt: '2026-11-24T08:00:00Z', updatedBy: 'u1', rowVersion: 3, ...extra,
});
const TYPES = ['t-al', 't-sl'].map((id) => ({ id, companyId: COMPANY, code: id === 't-al' ? 'AL' : 'SL', nameI18n: { en: id === 't-al' ? 'Annual leave' : 'Sick leave' }, unit: 'day', colorToken: 'success', isActive: true, isDeleted: false, rowVersion: 1 }));
const JOB_RUN = (status: string, extra: Record<string, unknown> = {}) => ({ id: 'jr1', definitionId: 'd1', code: 'hrm.leave_year_end', status, companyId: COMPANY, progressPct: 40, error: null, ...extra });

function rig(options: { runs?: unknown[]; one?: unknown; extra?: (method: string, url: string, body: Body) => Response | null } = {}) {
  serve((method, url, body) => {
    const custom = options.extra?.(method, url, body);
    if (custom) return custom;
    if (url.startsWith('/api/hrm/leave-year-end-runs?')) return json({ items: options.runs ?? [run('r1', 'preview')], next: null });
    if (url.startsWith('/api/hrm/leave-year-end-runs/') && method === 'GET') return json(options.one ?? run('r1', 'preview'));
    if (url.includes('/api/hrm/leave-types')) return json({ items: TYPES, next: null });
    if (url === '/api/jobs/definitions') return json([{ id: 'd1', code: 'hrm.leave_year_end' }]);
    return json({});
  });
}
const open = (permissions = ALL, path = '/?run=r1', writable = true) => show(<LeaveYearEndScreen />, shell(permissions, writable), path);
const buttons = () => ({ confirm: screen.queryByRole('button', { name: 'Confirm the year-end' }), rollback: screen.queryByRole('button', { name: 'Roll back the preview' }) });

describe('prepare a preview', () => {
  it('FR-HRM-LVE-11 starts the year-end job for the company and leave year through the job centre, and shows its progress', async () => {
    rig({ extra: (method, url) => {
      if (method === 'POST' && url === '/api/jobs/definitions/d1/run') return json(JOB_RUN('queued'), 202);
      if (url === '/api/jobs/runs/jr1') return json({ run: JOB_RUN('running'), logs: [] });
      return null;
    } });
    open(ALL, '/');
    await userEvent.type(await screen.findByRole('textbox', { name: /Leave year/ }), '2026');
    await userEvent.click(screen.getByRole('button', { name: 'Prepare the preview' }));
    expect(await screen.findByText(/The preview is being prepared \(40 percent\)/)).toBeTruthy();
    expect(posts()).toHaveLength(1);
    expect(posts()[0]).toMatchObject({ url: '/api/jobs/definitions/d1/run', body: { companyId: COMPANY, parameters: { leave_year: 2026 } } });
  });

  it('FR-HRM-LVE-11 when the job has succeeded the list of runs is read again and the screen says the preview is ready', async () => {
    rig({ extra: (method, url) => {
      if (method === 'POST' && url === '/api/jobs/definitions/d1/run') return json(JOB_RUN('queued'), 202);
      if (url === '/api/jobs/runs/jr1') return json({ run: JOB_RUN('succeeded'), logs: [] });
      return null;
    } });
    open(ALL, '/');
    await screen.findByText('Year-end runs');
    const before = sent.filter((c) => c.url.startsWith('/api/hrm/leave-year-end-runs?')).length;
    await userEvent.type(await screen.findByRole('textbox', { name: /Leave year/ }), '2026');
    await userEvent.click(screen.getByRole('button', { name: 'Prepare the preview' }));
    expect(await screen.findByText(/The preview is ready/)).toBeTruthy();
    await waitFor(() => expect(sent.filter((c) => c.url.startsWith('/api/hrm/leave-year-end-runs?')).length).toBeGreaterThan(before));
  });

  it('FR-HRM-LVE-11 shows why a failed job stopped, in words', async () => {
    rig({ extra: (method, url) => {
      if (method === 'POST' && url === '/api/jobs/definitions/d1/run') return json(JOB_RUN('queued'), 202);
      if (url === '/api/jobs/runs/jr1') return json({ run: JOB_RUN('failed', { error: 'errors.hrm.leave.year_end.already_applied' }), logs: [] });
      return null;
    } });
    open(ALL, '/');
    await userEvent.type(await screen.findByRole('textbox', { name: /Leave year/ }), '2026');
    await userEvent.click(screen.getByRole('button', { name: 'Prepare the preview' }));
    expect(await screen.findByText('The year-end of this leave year has already been confirmed.')).toBeTruthy();
  });

  it('FR-HRM-LVE-11 refuses a year that is not four digits before asking the server', async () => {
    rig();
    open(ALL, '/');
    await userEvent.type(await screen.findByRole('textbox', { name: /Leave year/ }), '26');
    expect((screen.getByRole('button', { name: 'Prepare the preview' }) as HTMLButtonElement).disabled).toBe(true);
    expect(posts()).toHaveLength(0);
  });

  it('without the confirm permission there is no prepare form', async () => {
    rig();
    open(READ, '/');
    await screen.findByText('Year-end runs');
    expect(screen.queryByText('Prepare a preview')).toBeNull();
  });
});

describe('the preview', () => {
  it('FR-HRM-LVE-11 shows the lines by employee and type, the status in words, the totals per type, and filters by employee number', async () => {
    rig();
    open();
    const lines = await screen.findByRole('table', { name: 'Year-end preview by employee and leave type' });
    expect(within(lines).getByText('No leave policy: nothing happens to this balance')).toBeTruthy();
    expect(within(lines).getByText('Left before the end of the year: left to final settlement')).toBeTruthy();
    const totals = screen.getByRole('table', { name: 'Year-end totals by leave type' });
    const al = within(totals).getByRole('row', { name: /Annual leave/ });
    expect(within(al).getAllByRole('cell').map((c) => c.textContent)).toEqual(['2', '14', '9', '2', '3']);
    await userEvent.type(screen.getByRole('textbox', { name: /Employee number/ }), 'E-3');
    await waitFor(() => expect(within(screen.getByRole('table', { name: 'Year-end preview by employee and leave type' })).queryByText('E-1')).toBeNull());
    expect(within(screen.getByRole('table', { name: 'Year-end preview by employee and leave type' })).getByText('E-30')).toBeTruthy();
  });

  it('FR-HRM-LVE-11 opens a run from the list', async () => {
    rig();
    open(ALL, '/');
    await userEvent.click(await screen.findByRole('button', { name: 'Open the year-end of 2026' }));
    expect(await screen.findByRole('table', { name: 'Year-end preview by employee and leave type' })).toBeTruthy();
  });
});

describe('confirm and roll back', () => {
  it('FR-HRM-LVE-11 FR-XM-05 the confirmation dialog shows the totals and the warning that the year is closed (R-306) and posts the confirmation with the row version', async () => {
    rig({ extra: (method, url) => (method === 'POST' && url === '/api/hrm/leave-year-end-runs/r1/confirm' ? json({ id: 'r1', status: 'applied', rowVersion: 4 }) : null) });
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'Confirm the year-end' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(/Once confirmed, the leave year is closed: no entry can be added to it any more, also for people who have left during the year\./)).toBeTruthy();
    expect(within(dialog).getByText('Carried forward').nextElementSibling?.textContent).toBe('9');
    expect(posts()).toHaveLength(0);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Confirm' }));
    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]).toMatchObject({ url: '/api/hrm/leave-year-end-runs/r1/confirm', body: { rowVersion: 3 } });
    expect(await screen.findByText('The year-end was confirmed')).toBeTruthy();
  });

  it('FR-XM-32 when the server holds the confirmation for a second person the screen says so, points to Approvals and offers no second confirmation', async () => {
    rig({ extra: (method, url) => (method === 'POST' && url.endsWith('/confirm') ? json({ id: 'r1', status: 'pending', requestId: 'q1' }, 202) : null) });
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'Confirm the year-end' }));
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirm' }));
    expect(await screen.findByText('Waiting for a second person')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Open Approvals' }).getAttribute('href')).toBe('/approvals');
    expect(buttons().confirm).toBeNull();
    expect(buttons().rollback).toBeNull();
  });

  it.each([
    ['errors.hrm.leave.year_end.stale', 'Balances have changed since this preview was made: make a new preview.'],
    ['errors.hrm.leave.year_end.year_not_over', 'The leave year is not over yet.'],
    ['errors.hrm.leave.year_end.requests_waiting', 'Leave of this year is still waiting for a decision: decide or withdraw it before the year-end is confirmed.'],
    ['errors.hrm.leave.year_end.not_preview', 'Only a preview can be confirmed or rolled back.'],
    ['errors.hrm.leave.year_end.pending', 'The confirmation of this year-end is already waiting for approval.'],
  ])('FR-HRM-LVE-11 shows the refusal %s in plain words', async (key, text) => {
    rig({ extra: (method, url) => (method === 'POST' && url.endsWith('/confirm') ? problem(key) : null) });
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'Confirm the year-end' }));
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirm' }));
    expect(await screen.findByText(text)).toBeTruthy();
  });

  it('FR-HRM-LVE-11 rolls a preview back with a reason and the row version', async () => {
    rig({ extra: (method, url) => (method === 'POST' && url === '/api/hrm/leave-year-end-runs/r1/rollback' ? json({ id: 'r1', status: 'rolled_back', rowVersion: 4 }) : null) });
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'Roll back the preview' }));
    const dialog = await screen.findByRole('alertdialog');
    const go = within(dialog).getByRole('button', { name: 'Roll back' }) as HTMLButtonElement;
    expect(go.disabled).toBe(true);
    await userEvent.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'Wrong year');
    await userEvent.click(go);
    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]).toMatchObject({ url: '/api/hrm/leave-year-end-runs/r1/rollback', body: { rowVersion: 3, reason: 'Wrong year' } });
  });

  it('read-only mode disables the decisions', async () => {
    rig();
    open(ALL, '/?run=r1', false);
    await screen.findByRole('table', { name: 'Year-end preview by employee and leave type' });
    expect(buttons().confirm).toBeNull();
  });
});

describe('applied and rolled back runs', () => {
  it('FR-XM-05 FR-XM-06 an applied run is read-only: who confirmed, when, how many entries were posted, and the warning that the year is closed (R-306)', async () => {
    rig({ one: run('r1', 'applied', {}, { confirmedAt: '2026-11-25T09:30:00Z', confirmedBy: 'u-approver', posted: 17 }) });
    open();
    expect(await screen.findByText(/by user u-approver\. 17 ledger entries were posted/)).toBeTruthy();
    expect(screen.getByText(/Once confirmed, the leave year is closed/)).toBeTruthy();
    expect(buttons().confirm).toBeNull();
    expect(buttons().rollback).toBeNull();
  });

  it('FR-HRM-LVE-11 a rolled-back preview is shown but cannot be decided', async () => {
    rig({ one: run('r1', 'rolled_back') });
    open();
    expect(await screen.findByText('This preview is no longer in use')).toBeTruthy();
    expect(buttons().confirm).toBeNull();
  });
});

describe('states', () => {
  it('shows the denied state without asking for runs', async () => {
    rig();
    open([], '/');
    expect(await screen.findByText('You may not see leave year-end')).toBeTruthy();
    expect(sent.some((c) => c.url.startsWith('/api/hrm/leave-year-end-runs'))).toBe(false);
  });
  it('shows the empty state', async () => {
    rig({ runs: [] });
    open(ALL, '/');
    expect(await screen.findByText('No year-end runs yet')).toBeTruthy();
  });
  it('shows an error with a retry', async () => {
    rig({ extra: (_m, url) => (url.startsWith('/api/hrm/leave-year-end-runs?') ? problem('errors.data.forbidden', 500) : null) });
    open(ALL, '/');
    expect(await screen.findByText('The year-end information could not be loaded.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });
});
