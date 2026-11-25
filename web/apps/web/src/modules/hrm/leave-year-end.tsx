/* eslint-disable jsx-a11y/no-noninteractive-tabindex -- a scrolling table region must be reachable by keyboard (WCAG 2.1.1, R-318) */
// Leave year-end for HR (slice S1.33b Build item 8; FR-HRM-LVE-11, FR-XM-05, -06, R-306): the runs of a company, a new preview prepared through the job centre (`hrm.leave_year_end`, which
// changes no balance), and the run itself: the preview table, confirm (posted at once or held for a second person, as the server says) and roll back until it is confirmed. The screen decides no
// leave rule. Deep link: `?run=<id>`.
import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, TextField } from '@erp/ui';
import { digitsOf, formatDateTime, formatDecimal, useLocaleSettings } from '@erp/i18n';
import { leaveLedgerApi, summaryOf } from '../../api/leave-ledger';
import { opsApi } from '../../api/ops';
import { useShell } from '../../shell/context';
import { isDenied, useCompanyChoice } from './live-parts';
import { useLeaveRefusal } from './leave-parts';
import { RunView, STATUS_CHIP } from './leave-year-end-run';
import { useYearEndText } from './leave-year-end-messages';

const JOB_CODE = 'hrm.leave_year_end';
const POLL_MS = 3000;
const YEAR = /^\d{4}$/;
const isOpen = (status: string) => status === 'queued' || status === 'running';

export default function LeaveYearEndScreen() {
  const { t, server } = useYearEndText();
  const { can, writable } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const refusal = useLeaveRefusal();
  const queryClient = useQueryClient();
  useDocumentTitle(t('lye.title'));
  const [params, setParams] = useSearchParams();
  const selected = params.get('run') ?? '';
  const { company, setCompany, options } = useCompanyChoice();
  const [year, setYear] = useState('');
  const [starting, setStarting] = useState(false);
  const [startError, setStartError] = useState<string | null>(null);
  const [jobRunId, setJobRunId] = useState('');
  const allowed = can('hrm.leave_year_end_runs.read');
  const runs = useQuery({ queryKey: ['hrm', 'leave', 'year-end-runs', company], queryFn: () => leaveLedgerApi.yearEndRuns(company), enabled: allowed && !!company });
  const job = useQuery({
    queryKey: ['hrm', 'leave', 'year-end-job', jobRunId],
    queryFn: async () => {
      const detail = await opsApi.run(jobRunId);
      if (!isOpen(detail.run.status)) await queryClient.invalidateQueries({ queryKey: ['hrm', 'leave', 'year-end-runs'] });
      return detail.run;
    },
    enabled: !!jobRunId,
    refetchInterval: (query) => (!query.state.data || isOpen(query.state.data.status) ? POLL_MS : false),
  });

  const open = (id: string) => { const next = new URLSearchParams(params); if (id) next.set('run', id); else next.delete('run'); setParams(next, { replace: true }); };
  const prepare = async () => {
    setStarting(true);
    setStartError(null);
    try {
      const definition = (await opsApi.definitions()).find((d) => d.code === JOB_CODE);
      if (!definition) { setStartError(t('lye.prepare.noJob')); return; }
      const started = await leaveLedgerApi.startPreview(definition.id, company, Number(year));
      setJobRunId(started.id);
    } catch (error) {
      setStartError(refusal(error));
    } finally {
      setStarting(false);
    }
  };

  if (!allowed) return <div className="erp-page"><PageHeader title={t('lye.title')} /><ErrorPanel title={t('lye.denied')} message={t('lye.denied.body')} /></div>;
  const yearOk = YEAR.test(year) && Number(year) >= 2000 && Number(year) <= 2200;
  const finished = job.data && !isOpen(job.data.status);
  return (
    <div className="erp-page">
      <PageHeader title={t('lye.title')} description={t('lye.description')} />
      <div className="erp-stack">
        {options.length > 1 && <SelectField label={t('lye.company')} options={options} value={company} onChange={(id) => { setCompany(id); open(''); }} />}
        {can('hrm.leave_year_end_runs.confirm') && (
          <section aria-labelledby="erp-lye-prepare" className="erp-stack">
            <h2 id="erp-lye-prepare">{t('lye.prepare.title')}</h2>
            <p className="erp-muted">{t('lye.prepare.body')}</p>
            <form className="erp-row erp-wrap" onSubmit={(e) => { e.preventDefault(); if (yearOk && writable && !starting) void prepare(); }}>
              <TextField label={t('lye.prepare.year')} help={t('lye.prepare.year.help')} isCode inputMode="numeric" maxLength={4} value={year} onChange={setYear}
                error={year !== '' && !yearOk ? t('lye.prepare.year.invalid') : null} />
              <Button variant="primary" type="submit" isPending={starting} isDisabled={!yearOk || !writable || (!!jobRunId && !finished)}>{t('lye.prepare.start')}</Button>
            </form>
            {startError && <Banner tone="danger" title={t('lye.refused')}>{startError}</Banner>}
            {job.data && isOpen(job.data.status) && <p role="status" className="erp-muted">{t('lye.prepare.running', { percent: formatDecimal(String(Math.round(job.data.progressPct ?? 0)), numbers) })}</p>}
            {job.data?.status === 'succeeded' && <Banner tone="success">{t('lye.prepare.done')}</Banner>}
            {job.data && (job.data.status === 'failed' || job.data.status === 'cancelled') && (
              <Banner tone="danger" title={t('lye.prepare.failed')}>{(job.data.error ? server(job.data.error) : null) ?? t('lye.prepare.failed.generic')}</Banner>)}
            {job.isError && <Banner tone="danger">{refusal(job.error)}</Banner>}
          </section>)}

        <section aria-labelledby="erp-lye-runs" className="erp-stack">
          <h2 id="erp-lye-runs">{t('lye.runs')}</h2>
          {runs.isPending && <Skeleton lines={3} />}
          {runs.isError && <ErrorPanel title={t('lye.loadError')} message={isDenied(runs.error) ? t('lye.denied.body') : refusal(runs.error)} action={isDenied(runs.error) ? null : <Button variant="primary" onPress={() => { void runs.refetch(); }}>{t('lye.retry')}</Button>} />}
          {runs.data && (runs.data.length === 0 ? <EmptyState title={t('lye.runs.empty')}>{t('lye.runs.empty.body')}</EmptyState> : (
            <div className="erp-leave-scroll erp-table-scroll" role="region" tabIndex={0} aria-label={t('lye.runs.caption')}>
              <table className="erp-leave-table">
                <caption className="erp-visually-hidden">{t('lye.runs.caption')}</caption>
                <thead><tr><th scope="col">{t('lye.col.year')}</th><th scope="col">{t('lye.col.status')}</th><th scope="col">{t('lye.col.prepared')}</th><th scope="col">{t('lye.total.lines')}</th><th scope="col">{t('lye.col.actions')}</th></tr></thead>
                <tbody>
                  {runs.data.map((r) => {
                    const summary = summaryOf(r);
                    return (
                      <tr key={r.id} aria-selected={selected === r.id}>
                        <th scope="row"><bdi dir="ltr">{digitsOf(String(r.leaveYear), numbers.digits)}</bdi></th>
                        <td><StatusChip status={STATUS_CHIP[r.status]} label={t(`lye.run.status.${r.status}`)} /></td>
                        <td>{summary.preparedAt ? <bdi>{formatDateTime(summary.preparedAt, lang, timeZone, numbers)}</bdi> : null}</td>
                        <td><bdi dir="ltr">{formatDecimal(String(summary.totals.lines), numbers)}</bdi></td>
                        <td><Button variant="quiet" onPress={() => open(r.id)} aria-label={t('lye.open.aria', { year: digitsOf(String(r.leaveYear), numbers.digits) })}>{t('lye.open')}</Button></td>
                      </tr>);
                  })}
                </tbody>
              </table>
            </div>))}
        </section>

        {selected && <RunView key={selected} runId={selected} companyId={company} onClosed={() => open('')} />}
      </div>
    </div>
  );
}
