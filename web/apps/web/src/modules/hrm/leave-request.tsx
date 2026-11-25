// The leave request page (slice S1.33a; FR-HRM-LVE-04 to -06, -16, GHR-09): the link target of the approvals inbox and of "My leave". Days one by one, the balance impact of a
// waiting request, the ledger entries the request made, the files, who applied (and for whom). Decisions stay in the shared inbox (FR-XM-32); this page only shows.
import { Fragment } from 'react';
import { useParams, useNavigate } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { Button, EmptyState, ErrorPanel, Link, PageHeader, Skeleton } from '@erp/ui';
import { formatDate, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { leaveApi } from '../../api/leave';
import { PROOF_OWNER } from '../../api/attendance-requests';
import { ApiProblem } from '../../api/client';
import { useShell } from '../../shell/context';
import { ProofList } from './proof-files';
import { DayList, EntryList, LeaveStatus, TypeLabel, typeNameOf, useLeaveRefusal, useLeaveTypes, useQuantity } from './leave-parts';
import { RequestActions } from './leave-my';
import './leave.css';

export default function LeaveRequestPage() {
  const { id = '' } = useParams();
  const { t } = useText();
  const { me } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const navigate = useNavigate();
  const refusal = useLeaveRefusal();
  const qty = useQuantity();
  const query = useQuery({ queryKey: ['hrm', 'leave', 'details', id], queryFn: ({ signal }) => leaveApi.details(id, signal), enabled: !!id });
  const types = useLeaveTypes(query.data?.companyId ?? '');
  useDocumentTitle(query.data ? t('leave.request.title', { no: query.data.requestNo }) : t('leave.request.titleLoading'));
  const back = <Button variant="quiet" onPress={() => { void navigate('/time/leave'); }}>{t('leave.request.back')}</Button>;

  if (query.isPending) return <div className="erp-page"><PageHeader title={t('leave.request.titleLoading')} /><Skeleton lines={8} /></div>;
  if (query.isError) {
    const gone = query.error instanceof ApiProblem && (query.error.status === 404 || query.error.status === 403);
    return (
      <div className="erp-page"><PageHeader title={t('leave.request.titleLoading')} />
        {gone ? <EmptyState title={t('leave.request.gone')} action={back}>{t('leave.request.gone.body')}</EmptyState>
          : <ErrorPanel title={t('leave.loadError')} message={refusal(query.error)} action={<Button onPress={() => { void query.refetch(); }}>{t('leave.retry')}</Button>} />}
      </div>);
  }

  const d = query.data;
  const type = types.data?.find((x) => x.id === d.leaveTypeId);
  const day = (v: string) => formatDate(`${v}T12:00:00Z`, lang, timeZone, numbers);
  const mine = d.appliedBy === me.userId;
  return (
    <div className="erp-page">
      <PageHeader title={t('leave.request.title', { no: d.requestNo })} description={t('leave.request.description')} actions={back} />
      <div className="erp-stack">
        <div className="erp-row erp-wrap"><LeaveStatus status={d.status} /><RequestActions request={d} /></div>
        <dl className="erp-facts">
          <dt>{t('leave.facts.employee')}</dt><dd><bdi>{d.employeeName ? `${d.employeeName} (${d.employeeNo})` : d.employeeNo}</bdi></dd>
          <dt>{t('leave.facts.type')}</dt><dd><TypeLabel name={typeNameOf(type, locale) || d.leaveTypeCode} token={type?.colorToken} /></dd>
          <dt>{t('leave.facts.dates')}</dt><dd><bdi>{d.startDate === d.endDate ? day(d.startDate) : t('leave.period', { from: day(d.startDate), to: day(d.endDate) })}</bdi></dd>
          {(d.startPart !== 'full' || d.endPart !== 'full') && <><dt>{t('leave.facts.parts')}</dt><dd>{d.startDate === d.endDate ? t(`leave.part.${d.startPart}` as MessageKey) : `${t(`leave.part.${d.startPart}` as MessageKey)} / ${t(`leave.part.${d.endPart}` as MessageKey)}`}</dd></>}
          <dt>{t('leave.facts.counted')}</dt><dd>{d.unit === 'hour' && d.hours !== null ? qty(d.hours, 'hour') : qty(d.days, d.unit)}</dd>
          <dt>{t('leave.facts.reason')}</dt><dd>{d.reason ? <bdi>{d.reason}</bdi> : t('leave.facts.none')}</dd>
          <dt>{t('leave.facts.appliedBy')}</dt><dd>{d.onBehalf ? (mine ? t('leave.facts.onBehalf.you') : t('leave.facts.onBehalf.other')) : (mine ? t('leave.facts.self.you') : t('leave.facts.self'))}</dd>
          {d.modifiesRequestId && <><dt>{t('leave.facts.changes')}</dt><dd><Link href={`/time/leave/requests/${d.modifiesRequestId}`}>{t('leave.facts.changes.link')}</Link></dd></>}
          {d.changes.map((c) => <Fragment key={c.id}><dt>{t('leave.facts.changedBy')}</dt><dd><Link href={`/time/leave/requests/${c.id}`}><bdi>{c.requestNo}</bdi></Link> {t(`leave.status.${c.status}` as MessageKey)}</dd></Fragment>)}
        </dl>

        {d.impact && (
          <section aria-labelledby="erp-leave-impact" className="erp-stack">
            <h2 id="erp-leave-impact">{t('leave.request.impact')}</h2>
            <dl className="erp-leave-sum">
              {d.impact.balance !== null && <><dt>{t('leave.preview.before')}</dt><dd>{qty(d.impact.balance, d.unit)}</dd></>}
              {d.impact.pending !== null && <><dt>{t('leave.preview.pending')}</dt><dd>{qty(d.impact.pending, d.unit)}</dd></>}
              {d.impact.after !== null && <><dt>{t('leave.preview.after')}</dt><dd data-strong>{qty(d.impact.after, d.unit)}</dd></>}
            </dl>
          </section>)}

        <section aria-labelledby="erp-leave-days" className="erp-stack">
          <h2 id="erp-leave-days">{t('leave.request.days')}</h2>
          {d.dayList.length === 0 ? <p className="erp-muted">{t('leave.request.days.empty')}</p>
            : <DayList unit={d.unit} days={d.dayList.map((x) => ({ date: x.date, quantity: x.quantity, kind: x.kind, text: t(`leave.day.kind.${x.kind}` as MessageKey) }))} />}
        </section>

        <section aria-labelledby="erp-leave-entries" className="erp-stack">
          <h2 id="erp-leave-entries">{t('leave.request.entries')}</h2>
          <EntryList entries={d.entries} unit={d.unit} />
        </section>

        <section aria-labelledby="erp-leave-files" className="erp-stack">
          <h2 id="erp-leave-files">{t('leave.request.files')}</h2>
          {d.documentRequired && d.currentFileIds.length === 0 && d.status === 'draft' && <p className="erp-muted">{t('leave.request.files.needed')}</p>}
          <ProofList owner={PROOF_OWNER.leave} ownerId={d.id} canDelete={d.status === 'draft'} />
        </section>

        {d.workflowInstanceId && <p><Link href="/approvals">{t('leave.request.history')}</Link></p>}
      </div>
    </div>
  );
}
