// The job centre (slice S1.16c Build item 9; FR-KRN-JOB-03, FR-ADM-OPS-01): the jobs with their schedule and next run, schedule editing
// for the jobs that allow it, run now, the runs with filters and progress, one run with its log, rerun and stop. Routes: `/admin/jobs`
// (jobs), `/admin/jobs/runs` (runs, the target of the failed-job notification) and `/admin/jobs/runs/:id`. The server decides every right.
import { useState } from 'react';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, Play, RotateCcw, Square } from 'lucide-react';
import { Banner, Button, DateField, EmptyState, Link, PageHeader, SelectField, StatusChip, Tabs, useToast, type Status } from '@erp/ui';
import { useDocumentTitle } from '@erp/engine';
import { useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { opsApi, PAGE_SIZE, RUN_STATUSES, type JobDefinition, type JobRun, type RunStatus } from '../../api/ops';
import { useShell } from '../../shell/context';
import { dayAfter, dayStart, Loaded, useFormats, useProblemText } from './ops-parts';
import { HealthBody } from './health';
import { NotificationLogBody } from './notification-log';
import { ScheduleDialog, useScheduleText } from './jobs-schedule';

const JOBS = '/admin/jobs';
const POLL_MS = 5000;
const CHIP: Record<RunStatus, Status> = { queued: 'pending', running: 'info', succeeded: 'approved', failed: 'blocked', cancelled: 'inactive' };
const isOpen = (status: string) => status === 'queued' || status === 'running';

export function RunChip({ status }: { status: string }) {
  const { t } = useText();
  return <StatusChip status={CHIP[status as RunStatus] ?? 'draft'} label={t(`jobs.status.${status}` as MessageKey)} />;
}

export default function JobsScreen() {
  const { t } = useText();
  const { can } = useShell();
  if (!can('job.runs.read') && !can('job.definitions.read') && !can('ntf.outbound_messages.read')) return <div className="erp-page"><PageHeader title={t('jobs.title')} /><Banner tone="danger">{t('ops.denied')}</Banner></div>;
  return (
    <Routes>
      <Route index element={<JobCentre tab="jobs" />} />
      <Route path="runs" element={<JobCentre tab="runs" />} />
      <Route path="runs/:id" element={<RunPage />} />
      <Route path="messages" element={<JobCentre tab="messages" />} />
      <Route path="health" element={<JobCentre tab="health" />} />
    </Routes>
  );
}

type Tab = 'jobs' | 'runs' | 'messages' | 'health';
const TAB_PATH: Record<Tab, string> = { jobs: JOBS, runs: `${JOBS}/runs`, messages: `${JOBS}/messages`, health: `${JOBS}/health` };

function JobCentre({ tab }: { tab: Tab }) {
  const { t } = useText();
  const { can } = useShell();
  const navigate = useNavigate();
  useDocumentTitle(t('jobs.title'));
  const tabs = [
    ...(can('job.definitions.read') ? [{ id: 'jobs', title: t('jobs.tab.jobs'), content: <Definitions /> }] : []),
    ...(can('job.runs.read') ? [{ id: 'runs', title: t('jobs.tab.runs'), content: <Runs /> }] : []),
    ...(can('ntf.outbound_messages.read') ? [{ id: 'messages', title: t('ntl.title'), content: <NotificationLogBody /> }] : []),
    ...(can('job.runs.read') ? [{ id: 'health', title: t('hlt.title'), content: <HealthBody /> }] : []),
  ];
  const selected = tabs.some((x) => x.id === tab) ? tab : tabs[0]?.id;
  return (
    <div className="erp-page">
      <PageHeader title={t('jobs.title')} description={t('jobs.description')} />
      <Tabs label={t('jobs.tabs')} tabs={tabs} {...(selected ? { selectedKey: selected } : {})} onSelectionChange={(id) => { void navigate(TAB_PATH[id as Tab] ?? JOBS); }} />
    </div>
  );
}

function Definitions() {
  const { t } = useText();
  const { can, writable } = useShell();
  const f = useFormats();
  const scheduleText = useScheduleText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const problemText = useProblemText();
  const [editing, setEditing] = useState<JobDefinition | null>(null);
  const query = useQuery({ queryKey: ['jobs', 'definitions'], queryFn: opsApi.definitions, refetchOnMount: 'always' });
  const runNow = useMutation({
    mutationFn: (job: JobDefinition) => opsApi.runNow(job.id),
    onSuccess: () => { toast.success(t('jobs.run.queued')); void queryClient.invalidateQueries({ queryKey: ['jobs'] }); },
    onError: (e) => toast.error(problemText(e)),
  });
  return (
    <Loaded query={query}>
      {(jobs) => jobs.length === 0 ? <EmptyState title={t('jobs.empty')}>{t('jobs.empty.body')}</EmptyState> : (
        <>
          <table className="erp-table">
            <caption className="erp-visually-hidden">{t('jobs.tab.jobs')}</caption>
            <thead><tr>
              <th scope="col">{t('jobs.col.job')}</th><th scope="col">{t('jobs.col.schedule')}</th><th scope="col">{t('jobs.col.next')}</th>
              <th scope="col">{t('jobs.col.last')}</th><th scope="col">{t('jobs.col.actions')}</th>
            </tr></thead>
            <tbody>
              {jobs.map((j) => (
                <tr key={j.id}>
                  <th scope="row">
                    <bdi>{f.name(j.name, j.code)}</bdi>
                    <div className="erp-small erp-muted"><bdi>{f.name(j.description)}</bdi></div>
                    {!j.licensed && <div className="erp-small erp-muted">{t('jobs.notLicensed')}</div>}
                  </th>
                  <td>{j.isEnabled ? scheduleText(j.schedule) : t('jobs.switchedOff')}</td>
                  <td>{j.isEnabled && j.nextRunAt ? f.when(j.nextRunAt) : '–'}</td>
                  <td>{j.lastRunAt ? <>{f.when(j.lastRunAt)} {j.lastStatus && <RunChip status={j.lastStatus} />}</> : '–'}</td>
                  <td>
                    <div className="erp-row erp-wrap">
                      {j.scheduleEditable && can('job.definitions.update') && writable && <Button variant="secondary" onPress={() => setEditing(j)}>{t('jobs.edit')}</Button>}
                      {j.manual && j.licensed && can('job.runs.create') && writable && (
                        <Button variant="secondary" icon={<Play size={16} aria-hidden="true" />} isPending={runNow.isPending && runNow.variables?.id === j.id} onPress={() => runNow.mutate(j)}>{t('jobs.run.now')}</Button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {editing && <ScheduleDialog job={editing} onClose={() => setEditing(null)} />}
        </>
      )}
    </Loaded>
  );
}

function Runs() {
  const { t } = useText();
  const f = useFormats();
  const [status, setStatus] = useState('any');
  const [code, setCode] = useState('any');
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [to, setTo] = useState<CalendarDate | null>(null);
  const [limit, setLimit] = useState(PAGE_SIZE);
  const jobs = useQuery({ queryKey: ['jobs', 'definitions'], queryFn: opsApi.definitions, refetchOnMount: 'always' });
  const params = { status: status === 'any' ? undefined : status, code: code === 'any' ? undefined : code, from: dayStart(from), to: dayAfter(to), limit };
  const runs = useQuery({
    queryKey: ['jobs', 'runs', params],
    queryFn: ({ signal }) => opsApi.runs(params, signal),
    refetchOnMount: 'always',
    refetchInterval: (query) => ((query.state.data ?? []).some((r) => isOpen(r.status)) ? POLL_MS : false),
  });
  const names = new Map((jobs.data ?? []).map((j) => [j.code, f.name(j.name, j.code)]));
  return (
    <section className="erp-stack">
      <div className="erp-filters" role="search" aria-label={t('jobs.runs.filters')}>
        <SelectField label={t('jobs.filter.job')} value={code} onChange={setCode}
          options={[{ id: 'any', label: t('ops.any') }, ...(jobs.data ?? []).map((j) => ({ id: j.code, label: f.name(j.name, j.code) }))]} />
        <SelectField label={t('jobs.filter.status')} value={status} onChange={setStatus}
          options={[{ id: 'any', label: t('ops.any') }, ...RUN_STATUSES.map((s) => ({ id: s, label: t(`jobs.status.${s}` as MessageKey) }))]} />
        <DateField label={t('jobs.filter.from')} value={from} onChange={(v) => setFrom(v ? parseDate(v.toString()) : null)} />
        <DateField label={t('jobs.filter.to')} value={to} onChange={(v) => setTo(v ? parseDate(v.toString()) : null)} />
      </div>
      <Loaded query={runs}>
        {(rows) => rows.length === 0 ? <EmptyState title={t('jobs.runs.empty')}>{t('jobs.runs.empty.body')}</EmptyState> : (
          <>
            <table className="erp-table">
              <caption className="erp-visually-hidden">{t('jobs.tab.runs')}</caption>
              <thead><tr>
                <th scope="col">{t('jobs.col.job')}</th><th scope="col">{t('jobs.col.status')}</th><th scope="col">{t('jobs.col.progress')}</th>
                <th scope="col">{t('jobs.col.started')}</th><th scope="col">{t('jobs.col.finished')}</th>
              </tr></thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.id}>
                    <th scope="row"><Link href={`${JOBS}/runs/${r.id}`}><bdi>{names.get(r.code) ?? r.code}</bdi></Link></th>
                    <td><RunChip status={r.status} /></td>
                    <td>{r.progressPct === null ? '–' : `${f.int(Math.round(r.progressPct))}%`}</td>
                    <td>{f.when(r.startedAt ?? r.scheduledFor ?? r.createdAt)}</td>
                    <td>{f.when(r.finishedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {rows.length >= limit && <div><Button variant="secondary" onPress={() => setLimit(limit + PAGE_SIZE)}>{t('jobs.runs.more')}</Button></div>}
          </>
        )}
      </Loaded>
    </section>
  );
}

function RunPage() {
  const { t } = useText();
  const { id = '' } = useParams();
  const { can, writable } = useShell();
  const f = useFormats();
  const toast = useToast();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const problemText = useProblemText();
  const query = useQuery({
    queryKey: ['jobs', 'run', id],
    queryFn: () => opsApi.run(id),
    refetchOnMount: 'always',
    refetchInterval: (q) => (q.state.data && isOpen(q.state.data.run.status) ? POLL_MS : false),
  });
  const jobs = useQuery({ queryKey: ['jobs', 'definitions'], queryFn: opsApi.definitions });
  const done = () => { void queryClient.invalidateQueries({ queryKey: ['jobs'] }); };
  const rerun = useMutation({
    mutationFn: () => opsApi.rerun(id),
    onSuccess: (run) => { toast.success(t('jobs.rerun.done')); done(); void navigate(`${JOBS}/runs/${run.id}`); },
    onError: (e) => toast.error(problemText(e)),
  });
  const cancel = useMutation({ mutationFn: () => opsApi.cancel(id), onSuccess: () => { toast.success(t('jobs.cancel.done')); done(); }, onError: (e) => toast.error(problemText(e)) });
  const missing = query.error instanceof ApiProblem && query.error.status === 404;
  const name = (run: JobRun) => f.name(jobs.data?.find((j) => j.code === run.code)?.name, run.code);
  useDocumentTitle(t('jobs.title'));
  return (
    <div className="erp-page">
      {missing ? (
        <>
          <PageHeader title={t('jobs.title')} />
          <EmptyState title={t('jobs.run.notFound')} action={<Link href={`${JOBS}/runs`}>{t('jobs.run.back')}</Link>} />
        </>
      ) : (
        <Loaded query={query}>
          {({ run, logs }) => (
            <>
              <PageHeader title={t('jobs.run.title', { name: name(run) })} actions={<Link href={`${JOBS}/runs`}><ArrowLeft size={16} aria-hidden="true" /> {t('jobs.run.back')}</Link>} />
              <div className="erp-row erp-wrap">
                <RunChip status={run.status} />
                {run.progressPct !== null && <span>{f.int(Math.round(run.progressPct))}%</span>}
                <span className="erp-muted">{t('jobs.col.attempt')}: {f.int(run.attempt)}</span>
                <span className="erp-muted">{run.requestedBy ? t('jobs.run.requested') : t('jobs.run.scheduled')}</span>
              </div>
              <dl className="erp-stack">
                <div><dt className="erp-small erp-muted">{t('jobs.col.started')}</dt><dd>{f.when(run.startedAt)}</dd></div>
                <div><dt className="erp-small erp-muted">{t('jobs.col.finished')}</dt><dd>{f.when(run.finishedAt)}</dd></div>
              </dl>
              {run.cancelRequestedAt && isOpen(run.status) && <Banner tone="info">{t('jobs.run.cancelRequested')}</Banner>}
              {run.nextAttemptAt && run.status === 'queued' && <Banner tone="info" title={t('jobs.col.next')}>{t('jobs.run.nextAttempt', { when: f.when(run.nextAttemptAt) })}</Banner>}
              {run.error && <Banner tone="danger" title={t('jobs.run.error')}><bdi>{run.error}</bdi></Banner>}
              {writable && (
                <div className="erp-row erp-wrap">
                  {!isOpen(run.status) && can('job.runs.create') && <Button variant="secondary" icon={<RotateCcw size={16} aria-hidden="true" />} isPending={rerun.isPending} onPress={() => rerun.mutate()}>{t('jobs.rerun')}</Button>}
                  {isOpen(run.status) && !run.cancelRequestedAt && can('job.runs.update') && <Button variant="danger" icon={<Square size={16} aria-hidden="true" />} isPending={cancel.isPending} onPress={() => cancel.mutate()}>{t('jobs.cancel')}</Button>}
                </div>
              )}
              <section className="erp-stack" aria-labelledby="run-log">
                <h2 id="run-log" className="erp-h3">{t('jobs.run.logs')}</h2>
                {logs.length === 0 ? <p className="erp-muted">{t('jobs.run.logs.empty')}</p> : (
                  <table className="erp-table">
                    <caption className="erp-visually-hidden">{t('jobs.run.logs')}</caption>
                    <tbody>
                      {logs.map((l, i) => (
                        <tr key={`${l.at}-${i}`}><td>{f.when(l.at)}</td><td>{l.level}</td><td><bdi>{l.message}</bdi></td></tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </section>
            </>
          )}
        </Loaded>
      )}
    </div>
  );
}
