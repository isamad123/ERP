// The live attendance dashboard (slice S1.30b Build item 5; FR-HRM-ATT-18, FR-HRM-SSP-02): today's counts for the people in the caller's scope (the server limits the
// answer), a table alternative per unit and per location, a drill-down to the people behind a count, and a refresh by polling every `refreshSeconds` of the answer
// (no push). The time shown is the server's "as of", never the browser's clock.
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { RefreshCw } from 'lucide-react';
import { useDocumentTitle } from '@erp/engine';
import { Button, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton } from '@erp/ui';
import { useText } from '@erp/i18n';
import { attendanceReportsApi } from '../../api/attendance-reports';
import { useShell } from '../../shell/context';
import { useRefusal } from './requests-forms';
import { CountCards, CountsTable, PeopleList, isDenied, useCompanyChoice, useNumberText, useOrgNames } from './live-parts';
import './attendance-reports-messages';
import './live.css';

const ALL = 'all';
const DEFAULT_SECONDS = 60;
const MINIMUM_SECONDS = 15;

export default function DashboardScreen() {
  const { t } = useText();
  const { can } = useShell();
  const text = useNumberText();
  const refusal = useRefusal();
  useDocumentTitle(t('alive.title'));
  const allowed = can('hrm.attendance_dashboard.read');
  const { company, setCompany, options } = useCompanyChoice();
  const [unit, setUnit] = useState(ALL);
  const [location, setLocation] = useState(ALL);
  const [status, setStatus] = useState<string | null>(null);
  const names = useOrgNames(company, allowed);
  const filter = { companyId: company || undefined, unitId: unit === ALL ? undefined : unit, locationId: location === ALL ? undefined : location };
  const dashboard = useQuery({
    queryKey: ['hrm', 'attendance-live', 'dashboard', filter],
    queryFn: ({ signal }) => attendanceReportsApi.dashboard(filter, signal),
    enabled: allowed,
    refetchInterval: (query) => Math.max(MINIMUM_SECONDS, query.state.data?.refreshSeconds ?? DEFAULT_SECONDS) * 1000,
  });
  if (!allowed) return <div className="erp-page"><PageHeader title={t('alive.title')} /><EmptyState title={t('alive.denied')} /></div>;
  const data = dashboard.data;
  const seconds = Math.max(MINIMUM_SECONDS, data?.refreshSeconds ?? DEFAULT_SECONDS);
  const peopleFilter = { companyId: filter.companyId, unitId: filter.unitId, locationId: filter.locationId };
  return (
    <div className="erp-page">
      <PageHeader title={t('alive.title')} description={t('alive.description')} />
      <div className="erp-stack">
        <div className="erp-row erp-wrap erp-pos-controls">
          {options.length > 1 && <SelectField label={t('alive.company')} options={options} value={company} onChange={(v) => { setCompany(v); setUnit(ALL); setLocation(ALL); setStatus(null); }} />}
          <SelectField label={t('alive.unit')} options={[{ id: ALL, label: t('alive.all') }, ...names.units]} value={unit} onChange={setUnit} />
          <SelectField label={t('alive.location')} options={[{ id: ALL, label: t('alive.all') }, ...names.locations]} value={location} onChange={setLocation} />
          <Button variant="secondary" icon={<RefreshCw size={16} aria-hidden="true" />} isPending={dashboard.isFetching && !dashboard.isPending} onPress={() => { void dashboard.refetch(); }}>{t('alive.refresh')}</Button>
        </div>
        {data && <p className="erp-muted" role="status">{t('alive.asOf', { time: text.dateTime(data.asOf) })} · {t('alive.refreshEvery', { seconds: text.count(seconds) })}</p>}
        {dashboard.isPending && <Skeleton lines={4} />}
        {dashboard.isError && (
          <ErrorPanel title={t('alive.loadError')} message={isDenied(dashboard.error) ? t('alive.denied') : refusal(dashboard.error)}
            action={isDenied(dashboard.error) ? null : <Button variant="primary" onPress={() => { void dashboard.refetch(); }}>{t('common.retry')}</Button>} />
        )}
        {data && (data.totals.headcount === 0 ? <EmptyState title={t('alive.empty')}>{t('alive.empty.body')}</EmptyState> : (
          <>
            <CountCards counts={data.totals} selected={status} onOpen={(s) => setStatus(status === s ? null : s)} />
            {status && <PeopleList filter={peopleFilter} status={status} refetchMs={seconds * 1000} names={names} onClose={() => setStatus(null)} />}
            {data.byUnit.length > 0 && <CountsTable caption={t('alive.byUnit')} labelHeader={t('alive.col.unit')} rows={data.byUnit.map((u) => ({ key: u.unitId, label: names.unitName(u.unitId), counts: u.counts }))} />}
            {data.byLocation.length > 0 && <CountsTable caption={t('alive.byLocation')} labelHeader={t('alive.col.location')} rows={data.byLocation.map((l) => ({ key: l.locationId, label: names.locationName(l.locationId), counts: l.counts }))} />}
          </>
        ))}
      </div>
    </div>
  );
}
