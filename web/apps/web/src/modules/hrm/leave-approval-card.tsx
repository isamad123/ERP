// The leave card of the shared approvals inbox (slice S1.33a; FR-HRM-LVE-05, -16, GHR-09, FR-XM-32): under the generic fields of a leave request (or the cancellation of approved
// leave) the manager sees the days counted one by one (sandwich days marked), the balance before and after, the supporting documents, who applied (and for whom) and the
// coverage warning: how many of the team are away on each day against the limit. The warning never blocks; the decision buttons are the inbox's.
import './leave.css';
import { useQuery } from '@tanstack/react-query';
import { TriangleAlert } from 'lucide-react';
import { Banner, Skeleton } from '@erp/ui';
import { formatDate, formatInteger, isolate, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { leaveApi } from '../../api/leave';
import { PROOF_OWNER } from '../../api/attendance-requests';
import { useShell } from '../../shell/context';
import type { ApprovalCardProps } from '../kernel/approval-cards';
import { ProofList } from './proof-files';
import { DayList, useQuantity } from './leave-parts';
import './leave-messages';

export default function LeaveApprovalCard({ documentId }: ApprovalCardProps) {
  const { t } = useText();
  const { me } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const qty = useQuantity();
  const details = useQuery({ queryKey: ['hrm', 'leave', 'details', documentId], queryFn: ({ signal }) => leaveApi.details(documentId, signal), retry: false });
  const coverage = useQuery({ queryKey: ['hrm', 'leave', 'coverage', documentId], queryFn: ({ signal }) => leaveApi.coverage(documentId, signal), retry: false });
  const day = (d: string) => formatDate(`${d}T12:00:00Z`, lang, timeZone, numbers);

  if (details.isPending) return <Skeleton lines={5} />;
  if (details.isError) return <section aria-label={t('leave.card.title')}><p className="erp-muted" role="status">{t('leave.card.unavailable')}</p></section>;
  const d = details.data;
  const mine = d.appliedBy === me.userId;
  return (
    <section aria-labelledby="erp-leave-card" className="erp-stack">
      <h3 id="erp-leave-card">{t('leave.card.title')}</h3>
      <dl className="erp-facts">
        <dt>{t('leave.facts.employee')}</dt><dd><bdi>{d.employeeName ? `${d.employeeName} (${d.employeeNo})` : d.employeeNo}</bdi></dd>
        <dt>{t('leave.facts.dates')}</dt><dd><bdi>{d.startDate === d.endDate ? day(d.startDate) : t('leave.period', { from: day(d.startDate), to: day(d.endDate) })}</bdi></dd>
        {(d.startPart !== 'full' || d.endPart !== 'full') && <><dt>{t('leave.facts.parts')}</dt><dd>{d.startDate === d.endDate ? t(`leave.part.${d.startPart}` as MessageKey) : `${t(`leave.part.${d.startPart}` as MessageKey)} / ${t(`leave.part.${d.endPart}` as MessageKey)}`}</dd></>}
        <dt>{t('leave.facts.counted')}</dt><dd>{d.unit === 'hour' && d.hours !== null ? qty(d.hours, 'hour') : qty(d.days, d.unit)}</dd>
        <dt>{t('leave.facts.appliedBy')}</dt><dd>{d.onBehalf ? (mine ? t('leave.facts.onBehalf.you') : t('leave.facts.onBehalf.other')) : (mine ? t('leave.facts.self.you') : t('leave.facts.self'))}</dd>
        {d.impact?.balance != null && <><dt>{t('leave.preview.before')}</dt><dd>{qty(d.impact.balance, d.unit)}</dd></>}
        {d.impact?.after != null && <><dt>{t('leave.preview.after')}</dt><dd><strong>{qty(d.impact.after, d.unit)}</strong></dd></>}
      </dl>
      {d.dayList.length > 0 && <DayList unit={d.unit} days={d.dayList.map((x) => ({ date: x.date, quantity: x.quantity, kind: x.kind, text: t(`leave.day.kind.${x.kind}` as MessageKey) }))} />}
      <div className="erp-stack">
        <h4>{t('leave.request.files')}</h4>
        <ProofList owner={PROOF_OWNER.leave} ownerId={d.id} canDelete={false} />
      </div>
      <div className="erp-stack" aria-live="polite">
        {coverage.isPending && <Skeleton lines={2} />}
        {coverage.isError && <p className="erp-muted">{t('leave.coverage.unavailable')}</p>}
        {coverage.data && !coverage.data.teamKnown && <Banner tone="info" title={t('leave.coverage.unknown')}>{t('leave.coverage.unknown.body')}</Banner>}
        {coverage.data?.teamKnown && coverage.data.warn && (
          <Banner tone="warning" title={t('leave.coverage.warn.title')}>
            <p className="erp-row"><TriangleAlert size={16} aria-hidden="true" />{t('leave.coverage.warn.body')}</p>
            <ul>
              {coverage.data.days.filter((x) => x.warn).map((x) => (
                <li key={x.date}>
                  {t('leave.coverage.line', { away: formatInteger(x.away, numbers), size: formatInteger(x.teamSize, numbers), date: day(x.date), limit: formatInteger(coverage.data.limitPercent, numbers) })}
                  {x.people.length > 0 && <> {t('leave.coverage.names', { names: x.people.map((p) => isolate([p.firstName, p.lastName].filter(Boolean).join(' ') || p.employeeNo)).join(', ') })}</>}
                </li>))}
            </ul>
          </Banner>)}
        {coverage.data?.teamKnown && !coverage.data.warn && <p className="erp-muted">{t('leave.coverage.ok', { limit: formatInteger(coverage.data.limitPercent, numbers) })}</p>}
      </div>
    </section>
  );
}
