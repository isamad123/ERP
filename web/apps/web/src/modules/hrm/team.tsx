// The manager's team view (slice S1.30b Build item 7; FR-HRM-SSP-02, FR-HRM-ATT-18): today's counts and people of the caller's team (the server limits every answer to the
// caller's data scope) and this month's days and late minutes per person from the monthly muster. The month comes from the company's date in the dashboard answer,
// never from the browser's clock.
import { useState } from 'react';
import { ScrollRegion } from './scroll-region';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { Button, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton } from '@erp/ui';
import { useText } from '@erp/i18n';
import { attendanceReportsApi } from '../../api/attendance-reports';
import { useShell } from '../../shell/context';
import { useRefusal } from './requests-forms';
import { CountCards, PeopleList, isDenied, useCompanyChoice, useNumberText, useOrgNames } from './live-parts';
import './attendance-reports-messages';
import './live.css';

const SECONDS = 60;

const monthRange = (date: string): { from: string; to: string } => {
  const [y, m] = date.split('-').map(Number) as [number, number];
  const last = new Date(Date.UTC(y, m, 0)).getUTCDate();
  const mm = String(m).padStart(2, '0');
  return { from: `${y}-${mm}-01`, to: `${y}-${mm}-${String(last).padStart(2, '0')}` };
};

export default function TeamScreen() {
  const { t } = useText();
  const { can } = useShell();
  const text = useNumberText();
  const refusal = useRefusal();
  useDocumentTitle(t('team.title'));
  const allowed = can('hrm.attendance_dashboard.read');
  const { company, setCompany, options } = useCompanyChoice();
  const [status, setStatus] = useState('present');
  const names = useOrgNames(company, allowed);
  const filter = { companyId: company || undefined };
  const dashboard = useQuery({
    queryKey: ['hrm', 'attendance-live', 'team', filter],
    queryFn: ({ signal }) => attendanceReportsApi.dashboard(filter, signal),
    enabled: allowed,
    refetchInterval: (query) => Math.max(15, query.state.data?.refreshSeconds ?? SECONDS) * 1000,
  });
  const today = dashboard.data?.companies.find((c) => c.companyId === company)?.date ?? dashboard.data?.companies[0]?.date;
  const canMonth = can('hrm.attendance_reports.read');
  const month = useInfiniteQuery({
    queryKey: ['hrm', 'attendance-live', 'team-month', company, today],
    queryFn: ({ pageParam, signal }) => attendanceReportsApi.report('muster_monthly', { companyId: company || undefined, ...monthRange(today as string) }, { limit: 100, offset: pageParam }, signal),
    initialPageParam: 0,
    getNextPageParam: (last) => last.next ?? undefined,
    enabled: allowed && canMonth && !!today,
  });
  if (!allowed) return <div className="erp-page"><PageHeader title={t('team.title')} /><EmptyState title={t('team.denied')} /></div>;
  const data = dashboard.data;
  const rows = month.data?.pages.flatMap((p) => p.items) ?? [];
  const num = (v: unknown) => text.count(Number(v ?? 0));
  return (
    <div className="erp-page">
      <PageHeader title={t('team.title')} description={t('team.description')} />
      <div className="erp-stack">
        {options.length > 1 && <SelectField label={t('alive.company')} options={options} value={company} onChange={setCompany} />}
        <h2>{t('team.today')}</h2>
        {dashboard.isPending && <Skeleton lines={3} />}
        {dashboard.isError && (
          <ErrorPanel title={t('alive.loadError')} message={isDenied(dashboard.error) ? t('team.denied') : refusal(dashboard.error)}
            action={isDenied(dashboard.error) ? null : <Button variant="primary" onPress={() => { void dashboard.refetch(); }}>{t('common.retry')}</Button>} />
        )}
        {data && (data.totals.headcount === 0 ? <EmptyState title={t('alive.empty')}>{t('alive.empty.body')}</EmptyState> : (
          <>
            <CountCards counts={data.totals} selected={status} onOpen={setStatus} />
            <PeopleList filter={filter} status={status} refetchMs={SECONDS * 1000} names={names} />
          </>
        ))}
        {canMonth && (
          <section className="erp-stack" aria-label={t('team.month')}>
            <h2>{t('team.month')}</h2>
            {month.isPending && !!today && <Skeleton lines={3} />}
            {month.isError && <ErrorPanel title={t('team.month.error')} message={isDenied(month.error) ? t('team.denied') : refusal(month.error)} />}
            {month.data && (rows.length === 0 ? <EmptyState title={t('team.month.empty')} /> : (
              <ScrollRegion label={t('team.month')}><table className="erp-pos-table">
                <caption className="erp-visually-hidden">{t('team.month')}</caption>
                <thead><tr>
                  <th scope="col">{t('alive.col.employeeNo')}</th><th scope="col">{t('alive.col.name')}</th><th scope="col">{t('team.col.present')}</th><th scope="col">{t('team.col.late')}</th>
                  <th scope="col">{t('team.col.absent')}</th><th scope="col">{t('team.col.leave')}</th><th scope="col">{t('team.col.lateMinutes')}</th>
                </tr></thead>
                <tbody>{rows.map((r) => (
                  <tr key={String(r.employmentId)}>
                    <td><bdi>{String(r.employeeNo ?? '')}</bdi></td>
                    <td><bdi>{`${r.firstName ?? ''} ${r.lastName ?? ''}`.trim()}</bdi></td>
                    <td>{num(r.presentDays)}</td><td>{num(r.lateDays)}</td><td>{num(r.absentDays)}</td><td>{num(r.leaveDays)}</td><td>{num(r.lateMinutes)}</td>
                  </tr>))}</tbody>
              </table></ScrollRegion>
            ))}
            {month.hasNextPage && <div><Button variant="secondary" isPending={month.isFetchingNextPage} onPress={() => { void month.fetchNextPage(); }}>{t('alive.people.more')}</Button></div>}
          </section>
        )}
      </div>
    </div>
  );
}
