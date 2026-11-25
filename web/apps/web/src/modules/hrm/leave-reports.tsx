// The leave reports (slice S1.33b Build item 9; FR-HRM-LVE-10, FR-HRM-RPT-05, FR-HRM-GHR-06): balance summary by unit, leave taken by type and month, and leave waiting for a decision.
// Filters that apply to the chosen report, a summary table built from the server's groups (over every row of the filter, not only the page), the rows, paging and an export of
// exactly the filter shown. Quantities are exact decimals shown with their unit; balances of types counted in different units are never added. Nothing here shows pay.
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { EngineProvider, ExportButton, useDocumentTitle, type EngineApi } from '@erp/engine';
import { Button, DateField, EmptyState, ErrorPanel, Link, PageHeader, SelectField, Skeleton, TextField, ScrollRegion } from '@erp/ui';
import { formatDecimal, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { leaveReportsApi, LEAVE_REPORT_CODES, type LeaveReportFilter, type LeaveReportGroup, type LeaveReportRow } from '../../api/leave-reports';
import { ApiProblem, sendText } from '../../api/client';
import { useShell } from '../../shell/context';
import { useRefusal } from './requests-forms';
import { isDenied, useCompanyChoice, useNumberText, useOrgNames } from './live-parts';
import { typeNameOf, useLeaveTypes } from './leave-parts';
import './leave-reports-messages';
import './live.css';

const exportApi: EngineApi = { send: sendText };
const PAGE = 100;
const ALL = 'all';
const STATUSES = ['submitted', 'cancel_requested'];

type Applies = 'dates' | 'unit' | 'type' | 'year' | 'search' | 'status';
const APPLIES: Record<string, Applies[]> = {
  balance_summary: ['unit', 'type', 'year', 'search'],
  leave_taken: ['dates', 'unit', 'type', 'search'],
  pending_requests: ['dates', 'unit', 'type', 'status', 'search'],
};
const DEFAULT_COLUMNS: Record<string, string[]> = {
  balance_summary: ['employeeNo', 'firstName', 'lastName', 'orgUnitId', 'leaveTypeCode', 'unit', 'leaveYear', 'added', 'used', 'adjusted', 'removed', 'balance'],
  leave_taken: ['employeeNo', 'firstName', 'lastName', 'orgUnitId', 'leaveTypeCode', 'unit', 'month', 'requests', 'taken', 'unpaid'],
  pending_requests: ['requestNo', 'employeeNo', 'firstName', 'lastName', 'orgUnitId', 'leaveTypeCode', 'unit', 'startDate', 'endDate', 'days', 'status', 'requestedAt', 'ageDays'],
};
/** Columns that only carry an id the person cannot use (the name beside it is shown instead). */
const HIDDEN = new Set(['companyId', 'employmentId', 'requestId', 'leaveTypeId', 'branchId']);
/** No leave report ever shows pay (FR-HRM-GHR-06): a column of that kind is dropped even if an answer carried it. */
const FORBIDDEN = /pay(?!ing)|salary|wage|amount|gross|net|money|currency|rate|tax/i;
const QUANTITY = new Set(['added', 'used', 'adjusted', 'removed', 'balance', 'taken', 'unpaid', 'days']);
const COUNTS = new Set(['requests', 'people', 'ageDays', 'oldestAgeDays']);
const ISO_DAY = /^\d{4}-\d{2}-\d{2}$/;

export default function LeaveReportsScreen() {
  const { t, server } = useText();
  const { can } = useShell();
  const text = useNumberText();
  const { numbers, locale } = useLocaleSettings();
  const refusal = useRefusal();
  useDocumentTitle(t('lrep.title'));
  const allowed = can('hrm.leave_reports.read');
  const { company, setCompany, options } = useCompanyChoice();
  const names = useOrgNames(company, allowed);
  const types = useLeaveTypes(allowed ? company : '');
  const [code, setCode] = useState<string>('balance_summary');
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [to, setTo] = useState<CalendarDate | null>(null);
  const [unit, setUnit] = useState(ALL);
  const [leaveType, setLeaveType] = useState(ALL);
  const [year, setYear] = useState('');
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState(ALL);
  const [offset, setOffset] = useState(0);
  const catalogue = useQuery({ queryKey: ['hrm', 'leave-reports', 'catalogue'], queryFn: ({ signal }) => leaveReportsApi.reports(signal), enabled: allowed, staleTime: 300_000 });
  const info = catalogue.data?.reports.find((r) => r.code === code);
  const applies = APPLIES[code] ?? [];
  const filter: LeaveReportFilter = {
    companyId: company || undefined,
    ...(applies.includes('dates') ? { from: from?.toString(), to: to?.toString() } : {}),
    ...(applies.includes('unit') && unit !== ALL ? { unitId: unit } : {}),
    ...(applies.includes('type') && leaveType !== ALL ? { leaveTypeId: leaveType } : {}),
    ...(applies.includes('year') && /^\d{4}$/.test(year.trim()) ? { leaveYear: year.trim() } : {}),
    ...(applies.includes('search') && search.trim() ? { search: search.trim() } : {}),
    ...(applies.includes('status') && status !== ALL ? { status } : {}),
  };
  const report = useQuery({
    queryKey: ['hrm', 'leave-reports', code, filter, offset],
    queryFn: ({ signal }) => leaveReportsApi.report(code, filter, { limit: PAGE, offset }, signal),
    enabled: allowed,
  });
  if (!allowed) return <div className="erp-page"><PageHeader title={t('lrep.title')} /><EmptyState title={t('lrep.denied')} /></div>;
  const reset = () => setOffset(0);
  const change = <T,>(set: (v: T) => void) => (v: T) => { set(v); reset(); };
  const reportOptions = (catalogue.data?.reports.map((r) => r.code) ?? [...LEAVE_REPORT_CODES]).filter((c) => c in APPLIES).map((c) => ({ id: c, label: t(`lrep.report.${c}` as MessageKey) }));
  const typeByCode = new Map((types.data ?? []).map((ty) => [ty.code, typeNameOf(ty, locale)]));
  const typeName = (c: unknown) => (typeof c === 'string' ? typeByCode.get(c) ?? c : '');
  const unitName = (u: unknown) => (u === 'day' || u === 'half_day' || u === 'hour' ? t(`lrep.unit.${u}` as MessageKey) : String(u ?? ''));
  const columns = (info?.columns ?? DEFAULT_COLUMNS[code] ?? []).filter((c) => !HIDDEN.has(c) && !FORBIDDEN.test(c));
  const label = (c: string) => t(`lrep.col.${c}` as MessageKey);
  const decimal = (v: string | number) => formatDecimal(String(v), numbers);
  const monthText = (m: string) => text.date(`${m.slice(0, 7)}-01`);
  const cell = (c: string, row: LeaveReportRow) => {
    const v = row[c];
    if (v === null || v === undefined || v === '') return '';
    if (c === 'requestNo' && row.requestId) return <Link href={`/time/leave/requests/${String(row.requestId)}`}><bdi>{String(v)}</bdi></Link>;
    if (c === 'orgUnitId') return names.unitName(String(v));
    if (c === 'locationId') return names.locationName(String(v));
    if (c === 'leaveTypeCode') return typeName(v);
    if (c === 'unit') return unitName(v);
    if (c === 'status') return STATUSES.includes(String(v)) ? t(`lrep.status.${String(v)}` as MessageKey) : String(v);
    if (c === 'leaveYear') return String(v);
    if (QUANTITY.has(c) && (typeof v === 'number' || typeof v === 'string')) return decimal(v);
    if (COUNTS.has(c) && typeof v === 'number') return text.count(v);
    if (c === 'month' && typeof v === 'string') return monthText(v);
    if (typeof v === 'string' && ISO_DAY.test(v)) return text.date(v);
    if (typeof v === 'string' && /^\d{4}-\d{2}-\d{2}T/.test(v)) return text.dateTime(v);
    return String(v);
  };
  const data = report.data;
  const groups = data?.summary.groups ?? [];
  const exportColumns = columns.map((c) => ({ field: c, label: label(c) }));
  const exportQuery = Object.fromEntries(Object.entries(filter).filter(([, v]) => v !== undefined)) as Record<string, string>;
  const shown = data ? { from: offset + 1, to: offset + data.items.length } : null;
  const problem = report.error instanceof ApiProblem && report.error.status === 422 ? report.error : null;
  const fieldText = (key: string) => server(key) ?? (key.endsWith('range_order') ? t('lrep.error.range_order') : key.endsWith('range_too_long') ? t('lrep.error.range_too_long') : t('lrep.error.generic'));
  const errorMessage = problem ? [...new Set(Object.values(problem.fieldErrors).flat().map(fieldText))].join(' ') || t('lrep.error.generic') : refusal(report.error);
  const title = t(`lrep.report.${code}` as MessageKey);
  const g = (grp: LeaveReportGroup, k: string) => grp[k] ?? null;
  const qty = (grp: LeaveReportGroup, k: string) => { const v = g(grp, k); return v === null ? '' : decimal(v); };
  const num = (grp: LeaveReportGroup, k: string) => { const v = g(grp, k); return typeof v === 'number' ? text.count(v) : ''; };
  const months = [...new Set(groups.map((x) => String(x.month ?? '')).filter(Boolean))].sort();
  const takenRows = [...new Map(groups.map((x) => [`${String(x.leaveTypeCode)}|${String(x.unit)}`, x])).values()];
  const takenCell = (type: string, u: string, m: string) => {
    const hit = groups.find((x) => x.leaveTypeCode === type && x.unit === u && x.month === m);
    if (!hit) return '';
    const unpaid = Number(hit.unpaid ?? 0);
    return unpaid > 0 ? `${qty(hit, 'taken')} · ${t('lrep.unpaidNote', { value: qty(hit, 'unpaid') })}` : qty(hit, 'taken');
  };
  const region = (aria: string, head: React.ReactNode, body: React.ReactNode, hidden = false) => (
    <ScrollRegion className="erp-live-scroll" label={aria}>
      <table className="erp-pos-table">
        <caption className={hidden ? 'erp-visually-hidden' : undefined}>{aria}</caption>
        <thead>{head}</thead>
        <tbody>{body}</tbody>
      </table>
    </ScrollRegion>
  );
  const th = (k: string) => <th key={k} scope="col" data-column={k}>{label(k)}</th>;
  return (
    <div className="erp-page">
      <PageHeader title={t('lrep.title')} description={t('lrep.description')} />
      <div className="erp-stack">
        <form className="erp-row erp-wrap erp-pos-controls" role="search" aria-label={t('lrep.filters')} onSubmit={(e) => e.preventDefault()}>
          <SelectField label={t('lrep.report')} options={reportOptions} value={code} onChange={(v) => { setCode(v); setStatus(ALL); setOffset(0); }} />
          {options.length > 1 && <SelectField label={t('lrep.company')} options={options} value={company} onChange={(v) => { setCompany(v); setUnit(ALL); setLeaveType(ALL); setOffset(0); }} />}
          {applies.includes('year') && <TextField label={t('lrep.leaveYear')} value={year} onChange={change(setYear)} inputMode="numeric" isCode maxLength={4} />}
          {applies.includes('dates') && <DateField label={t('lrep.from')} value={from} onChange={(v) => change(setFrom)(v ? parseDate(v.toString()) : null)} help={code === 'leave_taken' ? t('lrep.months.help') : undefined} />}
          {applies.includes('dates') && <DateField label={t('lrep.to')} value={to} onChange={(v) => change(setTo)(v ? parseDate(v.toString()) : null)} />}
          {applies.includes('unit') && <SelectField label={t('lrep.unit')} options={[{ id: ALL, label: t('lrep.all') }, ...names.units]} value={unit} onChange={change(setUnit)} />}
          {applies.includes('type') && <SelectField label={t('lrep.leaveType')} options={[{ id: ALL, label: t('lrep.all') }, ...(types.data ?? []).map((ty) => ({ id: ty.id, label: typeNameOf(ty, locale) }))]} value={leaveType} onChange={change(setLeaveType)} />}
          {applies.includes('status') && <SelectField label={t('lrep.status')} options={[{ id: ALL, label: t('lrep.all') }, ...STATUSES.map((s) => ({ id: s, label: t(`lrep.status.${s}` as MessageKey) }))]} value={status} onChange={change(setStatus)} />}
          {applies.includes('search') && <TextField label={t('lrep.search')} value={search} onChange={change(setSearch)} />}
          {can('itg.exports.run') && info && data && (
            <EngineProvider api={exportApi}>
              <ExportButton source={info.exportType} title={title} companyId={company || null} columns={exportColumns} query={exportQuery} search={filter.search ?? null} />
            </EngineProvider>
          )}
        </form>
        <p className="erp-muted">{t('lrep.scope')}</p>
        {report.isPending && <Skeleton lines={5} />}
        {report.isError && (
          <ErrorPanel title={t('lrep.loadError')} message={isDenied(report.error) ? t('lrep.denied') : errorMessage}
            action={isDenied(report.error) || problem ? null : <Button variant="primary" onPress={() => { void report.refetch(); }}>{t('common.retry')}</Button>} />
        )}
        {data && data.items.length === 0 && <EmptyState title={t('lrep.empty')}>{t('lrep.empty.body')}</EmptyState>}
        {data && data.items.length > 0 && groups.length > 0 && code === 'balance_summary' && region(t('lrep.groups.balance'),
          <tr>{['orgUnitId', 'leaveTypeCode', 'unit', 'people', 'added', 'used', 'adjusted', 'removed', 'balance'].map(th)}</tr>,
          groups.map((x, i) => (
            <tr key={`${String(x.orgUnitId)}|${String(x.leaveTypeCode)}|${String(x.unit)}|${i}`}>
              <td><bdi>{names.unitName(x.orgUnitId === null || x.orgUnitId === undefined ? null : String(x.orgUnitId))}</bdi></td>
              <td><bdi>{typeName(x.leaveTypeCode)}</bdi></td><td>{unitName(x.unit)}</td><td>{num(x, 'people')}</td>
              <td>{qty(x, 'added')}</td><td>{qty(x, 'used')}</td><td>{qty(x, 'adjusted')}</td><td>{qty(x, 'removed')}</td><td>{qty(x, 'balance')}</td>
            </tr>)))}
        {data && data.items.length > 0 && groups.length > 0 && code === 'leave_taken' && region(t('lrep.groups.taken'),
          <tr><th scope="col" data-column="leaveTypeCode">{label('leaveTypeCode')}</th><th scope="col" data-column="unit">{label('unit')}</th>{months.map((m) => <th key={m} scope="col">{monthText(m)}</th>)}</tr>,
          takenRows.map((x) => (
            <tr key={`${String(x.leaveTypeCode)}|${String(x.unit)}`}>
              <th scope="row"><bdi>{typeName(x.leaveTypeCode)}</bdi></th><td>{unitName(x.unit)}</td>
              {months.map((m) => <td key={m}>{takenCell(String(x.leaveTypeCode), String(x.unit), m)}</td>)}
            </tr>)))}
        {data && data.items.length > 0 && groups.length > 0 && code === 'pending_requests' && region(t('lrep.groups.pending'),
          <tr>{['leaveTypeCode', 'status', 'requests', 'oldestAgeDays'].map(th)}</tr>,
          groups.map((x, i) => (
            <tr key={`${String(x.leaveTypeCode)}|${String(x.status)}|${i}`}>
              <td><bdi>{typeName(x.leaveTypeCode)}</bdi></td><td>{STATUSES.includes(String(x.status)) ? t(`lrep.status.${String(x.status)}` as MessageKey) : String(x.status ?? '')}</td>
              <td>{num(x, 'requests')}</td><td>{num(x, 'oldestAgeDays')}</td>
            </tr>)))}
        {data && data.items.length > 0 && (
          <>
            {region(t('lrep.people'), <tr>{columns.map(th)}</tr>,
              data.items.map((row, i) => <tr key={`${String(row.requestId ?? row.employmentId ?? '')}|${String(row.leaveTypeId ?? '')}|${String(row.month ?? '')}|${i}`}>{columns.map((c) => <td key={c}><bdi>{cell(c, row)}</bdi></td>)}</tr>))}
            <div className="erp-row erp-wrap">
              {shown && <span className="erp-muted">{t('lrep.page', { from: text.count(shown.from), to: text.count(shown.to) })} · {t('lrep.total', { count: text.count(data.total) })}</span>}
              {offset > 0 && <Button variant="secondary" onPress={() => setOffset(Math.max(0, offset - PAGE))}>{t('lrep.previous')}</Button>}
              {data.next !== null && <Button variant="secondary" onPress={() => setOffset(data.next as number)}>{t('lrep.next')}</Button>}
            </div>
          </>
        )}
      </div>
    </div>
  );
}
