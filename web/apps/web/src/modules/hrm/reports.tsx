// The attendance reports (slice S1.30b Build item 6; FR-HRM-ATT-19, FR-HRM-RPT-05): seven reports of the caller's scope with filters, a table of the answer's columns, the
// summary strip, paging and an export of exactly the filter shown (the export centre makes the file as the person, with the same scope and field security).
// Nothing here shows pay. Dates left empty mean the server's default (the last 30 days, or this month for the monthly reports).
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { EngineProvider, ExportButton, useDocumentTitle, type EngineApi } from '@erp/engine';
import { Button, DateField, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, TextField } from '@erp/ui';
import { formatDecimal, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { attendanceReportsApi, REPORT_CODES, type ReportFilter, type ReportRow } from '../../api/attendance-reports';
import { sendText } from '../../api/client';
import { useShell } from '../../shell/context';
import { useRefusal } from './requests-forms';
import { isDenied, useCompanyChoice, useNumberText, useOrgNames } from './live-parts';
import './attendance-reports-messages';
import './live.css';

const exportApi: EngineApi = { send: sendText };
const PAGE = 100;
const ALL = 'all';

type Applies = 'dates' | 'unit' | 'location' | 'search' | 'status' | 'kind' | 'minAge';
const APPLIES: Record<string, Applies[]> = {
  muster_daily: ['dates', 'unit', 'location', 'search', 'status'],
  muster_monthly: ['dates', 'unit', 'location', 'search'],
  absenteeism: ['dates', 'unit', 'location', 'search'],
  late_early: ['dates', 'unit', 'location', 'search'],
  punch_log: ['dates', 'unit', 'location', 'search', 'kind'],
  device_sync: ['location', 'status'],
  exception_ageing: ['dates', 'unit', 'location', 'search', 'status', 'kind', 'minAge'],
};
const EXCEPTION_STATUSES = ['open', 'regularized', 'accepted', 'rejected', 'closed'];
const EXCEPTION_KINDS = ['missing_in', 'missing_out', 'late', 'early', 'absent', 'unscheduled', 'odd_count', 'outside_tolerance'];
/** Columns that only carry an id the person cannot use (the name beside it is shown instead). */
const HIDDEN = new Set(['employmentId', 'shiftId', 'deviceId', 'branchId']);
const UNIT_COLUMNS = new Set(['orgUnitId', 'unitId']);
const DAY_COLUMNS = new Set(['workDate', 'eventDate']);
const KNOWN = new Set(['employeeNo', 'firstName', 'lastName', 'orgUnitId', 'unitId', 'locationId', 'workDate', 'dayType', 'result', 'firstIn', 'lastOut', 'workedMinutes', 'lateMinutes', 'earlyMinutes', 'overtimeMinutes',
  'month', 'presentDays', 'absentDays', 'lateDays', 'halfDays', 'leaveDays', 'holidayDays', 'weeklyOffDays', 'dutyDays', 'incompleteDays', 'scheduledDays', 'absencePercent', 'eventDate', 'eventAt', 'direction',
  'source', 'deviceSerialNo', 'receivedAt', 'isDuplicate', 'serialNo', 'model', 'status', 'lastSeenAt', 'lastSyncAt', 'lastError', 'syncCursorAt', 'openUnmappedPunches', 'kind', 'ageDays', 'createdAt']);
const ISO_DAY = /^\d{4}-\d{2}-\d{2}$/;
const ISO_MONTH = /^\d{4}-\d{2}$/;

export default function ReportsScreen() {
  const { t } = useText();
  const { can } = useShell();
  const text = useNumberText();
  const { numbers } = useLocaleSettings();
  const refusal = useRefusal();
  useDocumentTitle(t('arep.title'));
  const allowed = can('hrm.attendance_reports.read');
  const { company, setCompany, options } = useCompanyChoice();
  const names = useOrgNames(company, allowed);
  const [code, setCode] = useState<string>('muster_daily');
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [to, setTo] = useState<CalendarDate | null>(null);
  const [unit, setUnit] = useState(ALL);
  const [location, setLocation] = useState(ALL);
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('');
  const [kind, setKind] = useState('');
  const [minAge, setMinAge] = useState('');
  const [offset, setOffset] = useState(0);
  const catalogue = useQuery({ queryKey: ['hrm', 'attendance-reports', 'catalogue'], queryFn: ({ signal }) => attendanceReportsApi.reports(signal), enabled: allowed, staleTime: 300_000 });
  const info = catalogue.data?.reports.find((r) => r.code === code);
  const applies = APPLIES[code] ?? [];
  const effectiveStatus = code === 'exception_ageing' && status === '' ? 'open' : status;
  const filter: ReportFilter = {
    companyId: company || undefined,
    ...(applies.includes('dates') ? { from: from?.toString(), to: to?.toString() } : {}),
    ...(applies.includes('unit') && unit !== ALL ? { unitId: unit } : {}),
    ...(applies.includes('location') && location !== ALL ? { locationId: location } : {}),
    ...(applies.includes('search') && search.trim() ? { search: search.trim() } : {}),
    ...(applies.includes('status') && effectiveStatus ? { status: effectiveStatus } : {}),
    ...(applies.includes('kind') && kind && kind !== ALL ? { kind } : {}),
    ...(applies.includes('minAge') && /^\d{1,3}$/.test(minAge.trim()) ? { minAgeDays: minAge.trim() } : {}),
  };
  const report = useQuery({
    queryKey: ['hrm', 'attendance-reports', code, filter, offset],
    queryFn: ({ signal }) => attendanceReportsApi.report(code, filter, { limit: PAGE, offset }, signal),
    enabled: allowed,
  });
  if (!allowed) return <div className="erp-page"><PageHeader title={t('arep.title')} /><EmptyState title={t('arep.denied')} /></div>;
  const reset = () => setOffset(0);
  const change = <T,>(set: (v: T) => void) => (v: T) => { set(v); reset(); };
  const reportOptions = (catalogue.data?.reports.map((r) => r.code) ?? [...REPORT_CODES]).map((c) => ({ id: c, label: t(`arep.report.${c}` as MessageKey) }));
  const columns = (info?.columns ?? Object.keys(report.data?.items[0] ?? {})).filter((c) => !HIDDEN.has(c));
  const label = (c: string) => (KNOWN.has(c) ? t(`arep.col.${c}` as MessageKey) : c);
  const cell = (c: string, row: ReportRow): string => {
    const v = row[c];
    if (v === null || v === undefined || v === '') return '';
    if (UNIT_COLUMNS.has(c)) return names.unitName(String(v));
    if (c === 'locationId') return names.locationName(String(v));
    if (typeof v === 'boolean') return v ? t('arep.yes') : t('arep.no');
    if (typeof v === 'number') return c === 'absencePercent' ? formatDecimal(String(v), numbers) : text.count(v);
    if (DAY_COLUMNS.has(c) && ISO_DAY.test(v)) return text.date(v);
    if (c === 'month' && (ISO_MONTH.test(v) || ISO_DAY.test(v))) return text.date(`${v.slice(0, 7)}-01`);
    if (/^\d{4}-\d{2}-\d{2}T/.test(v)) return text.dateTime(v);
    return v;
  };
  const data = report.data;
  const exportColumns = columns.map((c) => ({ field: c, label: label(c) }));
  const exportQuery = Object.fromEntries(Object.entries(filter).filter(([k, v]) => v !== undefined)) as Record<string, string>;
  const shown = data ? { from: offset + 1, to: offset + data.items.length } : null;
  const dateRange = applies.includes('dates');
  return (
    <div className="erp-page">
      <PageHeader title={t('arep.title')} description={t('arep.description')} />
      <div className="erp-stack">
        <form className="erp-row erp-wrap erp-pos-controls" role="search" aria-label={t('arep.filters')} onSubmit={(e) => e.preventDefault()}>
          <SelectField label={t('arep.report')} options={reportOptions} value={code} onChange={(v) => { setCode(v); setStatus(''); setKind(''); setOffset(0); }} />
          {options.length > 1 && <SelectField label={t('arep.company')} options={options} value={company} onChange={(v) => { setCompany(v); setUnit(ALL); setLocation(ALL); setOffset(0); }} />}
          {dateRange && <DateField label={t('arep.from')} value={from} onChange={(v) => change(setFrom)(v ? parseDate(v.toString()) : null)} help={code === 'muster_monthly' || code === 'absenteeism' ? t('arep.months.help') : undefined} />}
          {dateRange && <DateField label={t('arep.to')} value={to} onChange={(v) => change(setTo)(v ? parseDate(v.toString()) : null)} />}
          {applies.includes('unit') && <SelectField label={t('arep.unit')} options={[{ id: ALL, label: t('arep.all') }, ...names.units]} value={unit} onChange={change(setUnit)} />}
          {applies.includes('location') && <SelectField label={t('arep.location')} options={[{ id: ALL, label: t('arep.all') }, ...names.locations]} value={location} onChange={change(setLocation)} />}
          {applies.includes('search') && <TextField label={t('arep.search')} value={search} onChange={change(setSearch)} />}
          {applies.includes('status') && (code === 'exception_ageing'
            ? <SelectField label={t('arep.status')} options={EXCEPTION_STATUSES.map((s) => ({ id: s, label: t(`exc.status.${s}` as MessageKey) }))} value={effectiveStatus} onChange={change(setStatus)} />
            : <TextField label={t('arep.status')} value={status} onChange={change(setStatus)} isCode />)}
          {applies.includes('kind') && (code === 'exception_ageing'
            ? <SelectField label={t('arep.kind')} options={[{ id: ALL, label: t('arep.exc.kind.all') }, ...EXCEPTION_KINDS.map((k) => ({ id: k, label: t(`exc.kind.${k}` as MessageKey) }))]} value={kind || ALL} onChange={change(setKind)} />
            : <TextField label={t('arep.kind.source')} value={kind} onChange={change(setKind)} isCode />)}
          {applies.includes('minAge') && <TextField label={t('arep.minAge')} value={minAge} onChange={change(setMinAge)} inputMode="numeric" isCode maxLength={3} />}
          {can('itg.exports.run') && info && data && (
            <EngineProvider api={exportApi}>
              <ExportButton source={info.exportType} title={t(`arep.report.${code}` as MessageKey)} companyId={company || null} columns={exportColumns} query={exportQuery} search={filter.search ?? null} />
            </EngineProvider>
          )}
        </form>
        {report.isPending && <Skeleton lines={5} />}
        {report.isError && (
          <ErrorPanel title={t('arep.loadError')} message={isDenied(report.error) ? t('arep.denied') : refusal(report.error)}
            action={isDenied(report.error) ? null : <Button variant="primary" onPress={() => { void report.refetch(); }}>{t('common.retry')}</Button>} />
        )}
        {data && Object.keys(data.summary).length > 0 && (
          <dl className="erp-row erp-wrap" aria-label={t('arep.summary')}>
            {Object.entries(data.summary).map(([key, value]) => <div key={key} className="erp-live-card" data-static="true"><dt className="erp-live-card-label"><bdi>{key}</bdi></dt><dd className="erp-live-card-count">{text.count(value)}</dd></div>)}
          </dl>
        )}
        {data && (data.items.length === 0 ? <EmptyState title={t('arep.empty')}>{t('arep.empty.body')}</EmptyState> : (
          <>
            <div className="erp-live-scroll">
              <table className="erp-pos-table">
                <caption className="erp-visually-hidden">{t(`arep.report.${code}` as MessageKey)}</caption>
                <thead><tr>{columns.map((c) => <th key={c} scope="col">{label(c)}</th>)}</tr></thead>
                <tbody>{data.items.map((row, i) => <tr key={String(row.employmentId ?? row.deviceId ?? i) + String(row.workDate ?? row.eventAt ?? row.month ?? i) + i}>{columns.map((c) => <td key={c}><bdi>{cell(c, row)}</bdi></td>)}</tr>)}</tbody>
              </table>
            </div>
            <div className="erp-row erp-wrap">
              {shown && <span className="erp-muted">{t('arep.page', { from: text.count(shown.from), to: text.count(shown.to) })}{data.total !== null ? ` · ${t('arep.summary.total', { count: text.count(data.total) })}` : ''}</span>}
              {offset > 0 && <Button variant="secondary" onPress={() => setOffset(Math.max(0, offset - PAGE))}>{t('arep.previous')}</Button>}
              {data.next !== null && <Button variant="secondary" onPress={() => setOffset(data.next as number)}>{t('arep.next')}</Button>}
            </div>
          </>
        ))}
      </div>
    </div>
  );
}
