// The operations screens (slice S1.16c; FR-ADM-OPS-01, FR-KRN-JOB-03, FR-ADM-OPS-04, FR-KRN-NTF-05, FR-ADM-OPS-05, FR-ADM-DAT-08,
// FR-ADM-OPS-07, FR-KRN-ENT-05): jobs with schedule edit, run now, rerun and stop; the message log with resend and withheld texts;
// health with the backup; the licence page with limits and usage; permission-denied and error states; Urdu right-to-left.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import type { Delivery, JobDefinition, JobRun, SystemHealth, Licence } from '../../api/ops';
import JobsScreen from './jobs';
import LicenceScreen from './licence';
import { kindOf, toForm, toSchedule } from './jobs-schedule';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';
const ADMIN = ['job.definitions.read', 'job.definitions.update', 'job.runs.read', 'job.runs.create', 'job.runs.update', 'ntf.outbound_messages.read', 'ntf.outbound_messages.resend', 'cfg.licence.read', 'ntf.subscriptions.read'];

function shell(permissions: string[] = ADMIN, writable = true): ShellValue {
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

const JOB: JobDefinition = {
  id: 'j1', code: 'kernel.purge', module: 'kernel', name: { en: 'Daily clean-up' }, description: { en: 'Removes old data.' }, schedule: { daily: { at: '03:00' } }, isEnabled: true, priority: 5,
  manual: true, scheduleEditable: true, licensed: true, runsIn: ['full'], nextRunAt: '2026-11-25T03:00:00Z', lastRunAt: '2026-11-24T03:00:00Z', lastStatus: 'failed', rowVersion: 4,
};
const LOCKED_JOB: JobDefinition = { ...JOB, id: 'j2', code: 'kernel.audit_verify', name: { en: 'Audit check' }, scheduleEditable: false, schedule: { every_minutes: 15 }, lastStatus: 'succeeded' };
const run = (extra: Partial<JobRun> = {}): JobRun => ({
  id: 'r1', definitionId: 'j1', code: 'kernel.purge', status: 'failed', companyId: null, progressPct: null, attempt: 2, scheduledFor: '2026-11-24T03:00:00Z', startedAt: '2026-11-24T03:00:05Z',
  finishedAt: '2026-11-24T03:01:00Z', nextAttemptAt: null, cancelRequestedAt: null, error: 'The disk is full', requestedBy: null, actorUserId: null, parameters: null, createdAt: '2026-11-24T03:00:00Z', ...extra,
});
const MESSAGE: Delivery = {
  id: 'm1', createdAt: '2026-11-24T08:00:00Z', channel: 'email', status: 'failed', recipient: 'a@mail.test', userId: 'u1', eventCode: 'workflow.task_assigned', companyId: null, subject: 'Approval waiting',
  attempts: 6, nextAttemptAt: null, error: '550 5.1.1 no such user', providerRef: null, updatedAt: '2026-11-24T08:30:00Z', resendable: true,
};
const HEALTH: SystemHealth = {
  jobs: { queued: 3, running: 1, failedLast24Hours: 2, oldestQueuedAt: '2026-11-24T09:00:00Z', lastWorkerActivityAt: '2026-11-24T09:55:00Z', workerSeenRecently: true,
    events: [{ schema: 'hrm', waiting: 4, failing: 1, oldestAt: '2026-11-24T09:30:00Z' }, { schema: 'org', waiting: 0, failing: 0, oldestAt: null }] },
  filesNotScanned: 2, oldestUnscannedAt: '2026-11-24T09:00:00Z', storageUsedBytes: 5 * 1024 * 1024, databaseSizeBytes: 3 * 1024 ** 3, lastBackupAt: '2026-11-24T02:00:00Z', lastRestoreTestAt: null, at: '2026-11-24T10:00:00Z',
};
const LICENCE: Licence = {
  edition: 'Business', version: 3, deployment: 'saas', modules: ['kernel', 'hrm'], features: ['hrm.core'], packs: [], addons: [], supportTier: 'standard',
  limits: [{ key: 'users.named', max: 50, used: 47, percent: 94 }, { key: 'companies', max: 3, used: 3, percent: 100 }, { key: 'storage.gb', max: null, used: 12, percent: null }],
  effectiveFrom: '2026-01-01', expiresAt: '2026-12-31', maintenanceExpiresAt: '2026-12-31', state: 'warning', nextState: 'grace', nextStateOn: '2027-01-01', scheduledFrom: null,
};

interface Sent { method: string; path: string; body: unknown }
const sent: Sent[] = [];
const calls: string[] = [];

function serve(handler: (path: string, method: string, query: URLSearchParams) => Response | undefined) {
  sent.length = 0;
  calls.length = 0;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = '', q = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    calls.push(`${method} ${url}`);
    if (method !== 'GET') sent.push({ method, path, body: typeof init?.body === 'string' ? JSON.parse(init.body) : null });
    const answer = handler(path, method, new URLSearchParams(q));
    if (answer) return answer;
    if (path === '/api/jobs/definitions') return json([JOB, LOCKED_JOB]);
    if (path === '/api/jobs/runs') return json([run(), run({ id: 'r2', status: 'running', progressPct: 40, finishedAt: null, error: null })]);
    if (path === '/api/jobs/runs/r1') return json({ run: run(), logs: [{ at: '2026-11-24T03:00:30Z', level: 'error', message: 'Disk full on /data' }] });
    if (path === '/api/jobs/system-health') return json(HEALTH);
    if (path === '/api/ntf/deliveries') return json([MESSAGE]);
    if (path === '/api/ntf/deliveries/summary') return json({ queued: 1, retrying: 0, sent24h: 40, failed24h: 3, dead24h: 0, oldestQueuedAt: null });
    if (path === '/api/ntf/deliveries/daily') return json([{ day: '2026-11-24', sent: 40, failed: 3, dead: 0, queued: 1 }]);
    if (path === '/api/ntf/deliveries/m1') return json({ message: MESSAGE, body: 'Open the request.', link: '/approvals/x', language: 'en', resendOf: null, bodyWithheld: false });
    if (path === '/api/cfg/licence') return json(LICENCE);
    if (path === '/api/cfg/licence/usage') return json([{ period: '202611', lines: [{ meter: 'users.named', value: 47, limitKey: 'users.named', limit: 50, percent: 94 }], highestPercent: 94 }]);
    return json({ status: 404, title: 'errors.data.not_found' }, 404);
  }));
}

function show(path: string, element: React.ReactNode, routePath: string, value = shell(ADMIN), locale: 'en' | 'ur' = 'en') {
  render(
    <MemoryRouter initialEntries={[path]}>
      <I18nRoot locale={locale}>
        <ToastProvider>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <ShellValueProvider value={value}>
              <Routes><Route path={routePath} element={element} /></Routes>
            </ShellValueProvider>
          </QueryClientProvider>
        </ToastProvider>
      </I18nRoot>
    </MemoryRouter>,
  );
}
const jobs = (path = '/admin/jobs', value = shell(ADMIN)) => show(path, <JobsScreen />, '/admin/jobs/*', value);

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('FR-ADM-OPS-01 job centre', () => {
  it('lists the jobs with schedule, next and last run, and offers edit only where the job allows it', async () => {
    serve(() => undefined);
    jobs();
    expect(await screen.findByText('Daily clean-up')).toBeTruthy();
    expect(screen.getByText('Every day at 03:00')).toBeTruthy();
    expect(screen.getByText('Every 15 minutes')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: 'Change schedule' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Run now' }).length).toBeGreaterThan(0);
  });

  it('runs a job now with the person\'s own request', async () => {
    serve((path, method) => (path === '/api/jobs/definitions/j1/run' && method === 'POST' ? json(run({ id: 'r9', status: 'queued', error: null }), 201) : undefined));
    jobs();
    await userEvent.click((await screen.findAllByRole('button', { name: 'Run now' }))[0]!);
    await waitFor(() => expect(sent.some((s) => s.path === '/api/jobs/definitions/j1/run')).toBe(true));
    expect(await screen.findByText('The job is queued and will start shortly.')).toBeTruthy();
  });

  it('changes a schedule exactly as the server expects it and refuses a bad time before sending', async () => {
    serve((path, method) => (path === '/api/jobs/definitions/j1/schedule' && method === 'PUT' ? json({ ...JOB, schedule: { daily: { at: '04:15' } } }) : undefined));
    jobs();
    await userEvent.click(await screen.findByRole('button', { name: 'Change schedule' }));
    const dialog = await screen.findByRole('dialog');
    const time = within(dialog).getByLabelText('Time of day (24-hour, HH:mm)');
    await userEvent.clear(time);
    await userEvent.type(time, '25:99');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save schedule' }));
    expect(await within(dialog).findByText('Check the schedule: a time like 04:15 and numbers inside their range.')).toBeTruthy();
    expect(sent).toHaveLength(0);
    await userEvent.clear(time);
    await userEvent.type(time, '04:15');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save schedule' }));
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toEqual({ method: 'PUT', path: '/api/jobs/definitions/j1/schedule', body: { schedule: { daily: { at: '04:15' } }, isEnabled: true, rowVersion: 4 } });
  });

  it('filters the runs by status and job, and shows progress of a running one', async () => {
    serve(() => undefined);
    jobs('/admin/jobs/runs');
    expect(await screen.findByText('40%')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: /Status/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'Failed' }));
    await waitFor(() => expect(calls.some((c) => c.startsWith('GET /api/jobs/runs?') && c.includes('status=failed'))).toBe(true));
  });

  it('opens a failed run with its error and log, and runs it again', async () => {
    serve((path, method) => (path === '/api/jobs/runs/r1/rerun' && method === 'POST' ? json(run({ id: 'r1', status: 'queued', error: null }), 201) : undefined));
    jobs('/admin/jobs/runs/r1');
    expect(await screen.findByText('The disk is full')).toBeTruthy();
    expect(screen.getByText('Disk full on /data')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Run again' }));
    await waitFor(() => expect(sent.some((s) => s.path === '/api/jobs/runs/r1/rerun')).toBe(true));
  });

  it('stops a running run and offers no rerun while it runs', async () => {
    serve((path, method) => (path === '/api/jobs/runs/r1' && method === 'GET' ? json({ run: run({ status: 'running', error: null, finishedAt: null }), logs: [] })
      : path === '/api/jobs/runs/r1/cancel' ? json(run({ status: 'running', cancelRequestedAt: '2026-11-24T04:00:00Z' })) : undefined));
    jobs('/admin/jobs/runs/r1');
    await userEvent.click(await screen.findByRole('button', { name: 'Stop' }));
    await waitFor(() => expect(sent.some((s) => s.path === '/api/jobs/runs/r1/cancel')).toBe(true));
    expect(screen.queryByRole('button', { name: 'Run again' })).toBeNull();
    expect(screen.getByText('This run wrote no log lines yet.')).toBeTruthy();
  });

  it('shows no actions to a reader and no screen without the permission', async () => {
    serve(() => undefined);
    jobs('/admin/jobs', shell(['job.definitions.read', 'job.runs.read']));
    expect(await screen.findByText('Daily clean-up')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Change schedule' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Run now' })).toBeNull();
    cleanup();
    serve(() => undefined);
    jobs('/admin/jobs', shell([]));
    expect((await screen.findAllByText('You do not have permission to see this page.')).length).toBeGreaterThan(0);
    expect(calls).toEqual([]);
  });

  it('says a run does not exist and offers the way back', async () => {
    serve(() => undefined);
    jobs('/admin/jobs/runs/zzz');
    expect(await screen.findByText('This run does not exist.')).toBeTruthy();
  });

  it('turns schedule forms into the server\'s shapes', () => {
    expect(kindOf(null)).toBe('none');
    expect(toSchedule({ ...toForm({ schedule: null, isEnabled: true }), kind: 'weekly', days: ['friday', 'monday'], at: '07:30', workingOnly: true }))
      .toEqual({ weekly: { days: ['monday', 'friday'], at: '07:30' }, working_days_only: true });
    expect(toSchedule({ ...toForm({ schedule: null, isEnabled: true }), kind: 'monthly', lastDay: true, at: '01:00' })).toEqual({ monthly: { day: 'last', at: '01:00' } });
    expect(toSchedule({ ...toForm({ schedule: null, isEnabled: true }), kind: 'every_minutes', minutes: '0' })).toBe('invalid');
    expect(toSchedule({ ...toForm({ schedule: null, isEnabled: true }), kind: 'none' })).toBeNull();
  });
});

describe('FR-ADM-OPS-04 and FR-KRN-NTF-05 notification log', () => {
  const log = (value = shell(ADMIN), locale: 'en' | 'ur' = 'en') => show('/admin/jobs/messages', <JobsScreen />, '/admin/jobs/*', value, locale);

  it('shows the summary, the days and the messages with their reason', async () => {
    serve(() => undefined);
    log();
    expect(await screen.findByText('a@mail.test')).toBeTruthy();
    expect(screen.getByText('550 5.1.1 no such user')).toBeTruthy();
    expect(screen.getByText('Sent in 24 hours')).toBeTruthy();
    expect(screen.getAllByText('Per day').length).toBeGreaterThan(0);
  });

  it('opens a message and sends it again', async () => {
    serve((path, method) => (path === '/api/ntf/deliveries/m1/resend' && method === 'POST' ? json({ ...MESSAGE, id: 'm2', status: 'queued' }, 201) : undefined));
    log();
    await userEvent.click(await screen.findByRole('button', { name: 'Details' }));
    expect(await screen.findByText('Open the request.')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Send again' }));
    await waitFor(() => expect(sent.some((s) => s.path === '/api/ntf/deliveries/m1/resend')).toBe(true));
  });

  it('GHR-06 never shows the text of a message the server withholds, and offers no resend', async () => {
    const identity = { ...MESSAGE, eventCode: 'identity.password_reset', resendable: false };
    serve((path) => (path === '/api/ntf/deliveries' ? json([identity]) : path === '/api/ntf/deliveries/m1' ? json({ message: identity, body: '', link: null, language: 'en', resendOf: null, bodyWithheld: true }) : undefined));
    log();
    await userEvent.click(await screen.findByRole('button', { name: 'Details' }));
    expect(await screen.findByText('The text of this message is not shown here because it carries a sign-in link, a code or pay details.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Send again' })).toBeNull();
    expect(screen.queryByText('Open the request.')).toBeNull();
  });

  it('labels a bounce, and a company-scoped reader (summary refused) still gets the list', async () => {
    serve((path) => (path === '/api/ntf/deliveries' ? json([{ ...MESSAGE, error: 'bounced: 5.1.1 mailbox unknown' }])
      : path === '/api/ntf/deliveries/summary' || path === '/api/ntf/deliveries/daily' ? json({ status: 403, title: 'errors.access.forbidden' }, 403) : undefined));
    log();
    expect(await screen.findByText('Bounced back')).toBeTruthy();
    expect(screen.queryByText('Sent in 24 hours')).toBeNull();
  });

  it('shows a permission-denied page without the permission, an empty state, and works in Urdu right to left', async () => {
    serve(() => undefined);
    log(shell([]));
    expect((await screen.findAllByText('You do not have permission to see this page.')).length).toBeGreaterThan(0);
    expect(calls).toEqual([]);
    cleanup();
    serve((path) => (path === '/api/ntf/deliveries' ? json([]) : undefined));
    log(shell(ADMIN), 'ur');
    expect(await screen.findByText('کوئی پیغام نہیں')).toBeTruthy();
  });
});

describe('FR-ADM-OPS-05 and FR-ADM-DAT-08 system health', () => {
  const health = (value = shell(ADMIN)) => show('/admin/jobs/health', <JobsScreen />, '/admin/jobs/*', value);

  it('shows the worker, queues, event lag, unscanned files, sizes and the last backup', async () => {
    serve(() => undefined);
    health();
    expect(await screen.findByText('Working')).toBeTruthy();
    expect(screen.getByText('30 min')).toBeTruthy(); // hrm event queue: oldest waiting 30 minutes before the server's own time
    expect(screen.queryByText('org')).toBeNull(); // an empty queue is not listed
    expect(screen.getByText('5.0 MB')).toBeTruthy();
    expect(screen.getByText('3.0 GB')).toBeTruthy();
    expect(screen.getByText('Last successful backup')).toBeTruthy();
    expect(screen.getByText('Never')).toBeTruthy(); // no restore drill yet
    expect(screen.getByText('Open the notification rules')).toBeTruthy();
  });

  it('warns when the worker was not seen and says no backup was recorded', async () => {
    serve((path) => (path === '/api/jobs/system-health' ? json({ ...HEALTH, filesNotScanned: 0, lastBackupAt: null, jobs: { ...HEALTH.jobs, workerSeenRecently: false } }) : undefined));
    health();
    expect(await screen.findByText('No job started in the last minutes. Scheduled work and e-mails wait until the worker runs again.')).toBeTruthy();
    expect(screen.getByText('No backup has been recorded yet. Ask your provider.')).toBeTruthy();
  });

  it('shows an error panel with retry, and nothing without the permission', async () => {
    serve((path) => (path === '/api/jobs/system-health' ? json({ status: 500, title: 'errors.server' }, 500) : undefined));
    health();
    expect(await screen.findByText('This page could not be loaded. Try again.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
    cleanup();
    serve(() => undefined);
    health(shell(['cfg.licence.read']));
    expect((await screen.findAllByText('You do not have permission to see this page.')).length).toBeGreaterThan(0);
    expect(calls).toEqual([]);
  });
});

describe('FR-ADM-OPS-07, FR-KRN-ENT-05 and FR-KRN-ENT-06 licence and usage', () => {
  const licence = (value = shell(ADMIN)) => show('/settings/licence', <LicenceScreen />, '/settings/licence', value);

  it('shows state, next state, dates, limits with warnings and the usage statement', async () => {
    serve(() => undefined);
    licence();
    expect(await screen.findByText('Ending soon')).toBeTruthy();
    expect(screen.getByText(/Next state: Ended, grace period/)).toBeTruthy();
    expect(screen.getByText('Business')).toBeTruthy();
    expect(screen.getByText('47 of 50')).toBeTruthy();
    expect(screen.getByText('Almost full')).toBeTruthy();
    expect(screen.getByText('Full')).toBeTruthy();
    expect(screen.getAllByText('No limit').length).toBeGreaterThan(0);
    expect((await screen.findAllByText('2026-11')).length).toBeGreaterThan(0);
  });

  it('asks for another month range in the server\'s form and refuses a bad month', async () => {
    serve(() => undefined);
    licence();
    await screen.findAllByText('2026-11');
    await userEvent.type(screen.getByLabelText('From month (YYYY-MM)'), '2026-13');
    await userEvent.click(screen.getByRole('button', { name: 'Show' }));
    expect(await screen.findByText('Enter months like 2026-11.')).toBeTruthy();
    await userEvent.clear(screen.getByLabelText('From month (YYYY-MM)'));
    await userEvent.type(screen.getByLabelText('From month (YYYY-MM)'), '2026-09');
    await userEvent.type(screen.getByLabelText('To month (YYYY-MM)'), '2026-11');
    await userEvent.click(screen.getByRole('button', { name: 'Show' }));
    await waitFor(() => expect(calls.some((c) => c.includes('/api/cfg/licence/usage?from=202609&to=202611'))).toBe(true));
  });

  it('shows neither seat nor usage numbers to a person without the licence permission, and never asks the server', async () => {
    serve(() => undefined);
    licence(shell(['job.runs.read']));
    expect((await screen.findAllByText('You do not have permission to see this page.')).length).toBeGreaterThan(0);
    expect(calls).toEqual([]);
    expect(screen.queryByText('47 of 50')).toBeNull();
  });

  it('shows a locked licence with what it means and the way to exports', async () => {
    serve((path) => (path === '/api/cfg/licence' ? json({ ...LICENCE, state: 'locked', nextState: null, nextStateOn: null }) : undefined));
    licence(shell([...ADMIN, 'aud.export']));
    expect(await screen.findByText('The system is locked. Data is kept safe; only this page and exports work until the licence is renewed.')).toBeTruthy();
    expect(screen.getByText('No further change is planned.')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Export data' })).toBeTruthy();
  });
});
