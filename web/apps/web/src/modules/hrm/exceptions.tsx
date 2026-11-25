// The attendance exception queue (slice S1.30a Build item 4; FR-HRM-ATT-11): open exceptions of the signed-in person's scope with filters by kind and age,
// the age of each, and "Ask for a correction" which opens the correction form for that day. Device punches that belong to nobody yet are counted here
// and handled on the device screen (S1.27b).
import { useState } from 'react';
import { Link } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { Banner, Button, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, TextField } from '@erp/ui';
import { formatDate, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { attendanceRequestsApi, type ExceptionRow } from '../../api/attendance-requests';
import { useShell } from '../../shell/context';
import { CorrectionDialog, useRefusal } from './requests-forms';
import './requests-messages';

const KINDS = ['missing_in', 'missing_out', 'late', 'early', 'absent', 'unscheduled', 'odd_count', 'outside_tolerance'];
const STATUSES = ['open', 'regularized', 'accepted', 'rejected', 'closed'];

export default function ExceptionsScreen() {
  const { t } = useText();
  const { can, me, activeCompany, writable } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const refusal = useRefusal();
  useDocumentTitle(t('exc.title'));
  const [company, setCompany] = useState<string>(activeCompany?.id ?? me.companies[0]?.id ?? '');
  const [kind, setKind] = useState('all');
  const [status, setStatus] = useState('open');
  const [age, setAge] = useState('');
  const [asking, setAsking] = useState<ExceptionRow | null>(null);
  const minAge = /^\d{1,3}$/.test(age.trim()) ? age.trim() : '';
  const query = useQuery({
    queryKey: ['hrm', 'attendance-exceptions', company, kind, status, minAge],
    queryFn: ({ signal }) => attendanceRequestsApi.exceptions({ companyId: company, status, ...(kind !== 'all' ? { kind } : {}), ...(minAge ? { minAgeDays: minAge } : {}) }, signal),
    enabled: !!company && can('hrm.attendance_exceptions.read'),
  });
  if (!can('hrm.attendance_exceptions.read')) return <div className="erp-page"><PageHeader title={t('exc.title')} /><EmptyState title={t('exc.denied')} /></div>;
  const options = me.companies.map((c) => ({ id: c.id, label: c.name.en ?? c.code }));
  const data = query.data;
  return (
    <div className="erp-page">
      <PageHeader title={t('exc.title')} description={t('exc.description')} />
      <div className="erp-stack">
        {options.length > 1 && <SelectField label={t('exc.company')} options={options} value={company} onChange={setCompany} />}
        <SelectField label={t('exc.filter.status')} options={STATUSES.map((s) => ({ id: s, label: t(`exc.status.${s}` as MessageKey) }))} value={status} onChange={setStatus} />
        <SelectField label={t('exc.filter.kind')} options={[{ id: 'all', label: t('exc.filter.kind.all') }, ...KINDS.map((k) => ({ id: k, label: t(`exc.kind.${k}` as MessageKey) }))]} value={kind} onChange={setKind} />
        <TextField label={t('exc.filter.age')} value={age} onChange={setAge} inputMode="numeric" isCode maxLength={3} />
        {query.isPending && <Skeleton />}
        {query.isError && <ErrorPanel title={t('exc.loadError')} message={refusal(query.error)} />}
        {data?.unmapped && data.unmapped.count > 0 && (
          <Banner tone="info" title={t('exc.unmapped', { count: data.unmapped.count })}>{can('hrm.attendance_devices.read') ? <Link to="/time/devices">{t('exc.unmapped.open')}</Link> : null}</Banner>
        )}
        {data && (data.items.length === 0 ? <EmptyState title={t('exc.empty')}>{t('exc.empty.body')}</EmptyState> : (
          <table className="erp-pos-table">
            <thead><tr><th scope="col">{t('exc.col.employee')}</th><th scope="col">{t('exc.col.day')}</th><th scope="col">{t('exc.col.kind')}</th><th scope="col">{t('exc.col.age')}</th><th scope="col">{t('exc.col.actions')}</th></tr></thead>
            <tbody>{data.items.map((r) => (
              <tr key={r.id}>
                <td><bdi>{r.employeeNo ?? ''}</bdi></td>
                <td><bdi>{formatDate(`${r.workDate}T12:00:00Z`, lang, timeZone, numbers)}</bdi></td>
                <td>{t(`exc.kind.${r.kind}` as MessageKey)}</td>
                <td>{t('exc.age', { days: r.ageDays })}</td>
                <td>{r.status === 'open' && can('hrm.regularization_requests.create') && <Button variant="quiet" isDisabled={!writable} onPress={() => setAsking(r)}>{t('exc.ask')}</Button>}</td>
              </tr>))}</tbody>
          </table>
        ))}
        {data?.more && <p>{t('exc.more')}</p>}
      </div>
      {asking && <CorrectionDialog company={company} isOpen onOpenChange={(open) => { if (!open) setAsking(null); }} exceptionId={asking.id} workDate={asking.workDate} employmentId={asking.employmentId} />}
    </div>
  );
}
