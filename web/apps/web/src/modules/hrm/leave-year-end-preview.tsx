// The preview table of a year-end run (slice S1.33b; FR-HRM-LVE-11): one line per employee and leave type with the closing balance, what is carried forward, paid out and lapsed, the status in
// words, totals per type, and a search by employee number with filters by type and status. Nothing here changes a balance; the lines are the job's and the server checks them again on confirm.
import { useState } from 'react';
import { EmptyState, SelectField, TextField, ScrollRegion } from '@erp/ui';
import { formatDate, formatDecimal, useLocaleSettings } from '@erp/i18n';
import type { LeaveType } from '@erp/generated/api/hrm';
import type { YearEndSummary } from '../../api/leave-ledger';
import { typeNameOf } from './leave-parts';
import { decimalText, unitsToText } from './leave-ledger-model';
import { NO_LINE_FILTER, carriedOf, filterLines, totalsByType } from './leave-year-end-model';
import { useYearEndText } from './leave-year-end-messages';
import './leave-year-end.css';

const ALL = 'all';
const STATUSES = ['ok', 'no_policy', 'separated'] as const;

export function PreviewTable({ summary, types }: { summary: YearEndSummary; types: LeaveType[] }) {
  const { t } = useYearEndText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const [filter, setFilter] = useState(NO_LINE_FILTER);
  const num = (v: number | string) => formatDecimal(decimalText(v), numbers);
  const unitText = (unit: string) => (unit === 'hour' ? t('lye.unit.hours') : t('lye.unit.days'));
  const typeLabel = (id: string, code: string) => typeNameOf(types.find((x) => x.id === id), locale) || code;
  const lines = filterLines(summary.lines, filter);
  const perType = totalsByType(summary.lines);
  return (
    <div className="erp-stack">
      <h3 id="erp-lye-totals">{t('lye.totals.byType')}</h3>
      {perType.length === 0 ? <p className="erp-muted">{t('lye.totals.none')}</p> : (
        <ScrollRegion className="erp-leave-scroll erp-table-scroll" label={t('lye.totals.caption')}>
          <table className="erp-leave-table">
            <caption className="erp-visually-hidden">{t('lye.totals.caption')}</caption>
            <thead><tr>
              <th scope="col">{t('lye.col.type')}</th><th scope="col">{t('lye.total.lines')}</th><th scope="col">{t('lye.col.closing')}</th><th scope="col">{t('lye.col.carry')}</th><th scope="col">{t('lye.col.encash')}</th><th scope="col">{t('lye.col.lapse')}</th>
            </tr></thead>
            <tbody>
              {perType.map((r) => (
                <tr key={r.leaveTypeId}>
                  <th scope="row"><bdi>{typeLabel(r.leaveTypeId, r.code)}</bdi> <span className="erp-muted">({unitText(r.unit)})</span></th>
                  <td><bdi dir="ltr">{formatDecimal(String(r.lines), numbers)}</bdi></td>
                  <td><bdi dir="ltr">{num(r.closing)}</bdi></td><td><bdi dir="ltr">{num(r.carryForward)}</bdi></td><td><bdi dir="ltr">{num(r.encash)}</bdi></td><td><bdi dir="ltr">{num(r.lapse)}</bdi></td>
                </tr>))}
            </tbody>
          </table>
        </ScrollRegion>)}

      <h3 id="erp-lye-lines">{t('lye.lines')}</h3>
      <form className="erp-row erp-wrap" role="search" aria-label={t('lye.filters')} onSubmit={(e) => e.preventDefault()}>
        <TextField label={t('lye.filter.search')} help={t('lye.filter.search.help')} value={filter.search} onChange={(search) => setFilter({ ...filter, search })} />
        <SelectField label={t('lye.filter.type')} options={[{ id: ALL, label: t('lye.filter.all') }, ...summary.types.map((x) => ({ id: x.leaveTypeId, label: typeLabel(x.leaveTypeId, x.code), detail: x.code }))]}
          value={filter.leaveTypeId || ALL} onChange={(id) => setFilter({ ...filter, leaveTypeId: id === ALL ? '' : id })} />
        <SelectField label={t('lye.filter.status')} options={[{ id: ALL, label: t('lye.filter.all') }, ...STATUSES.map((s) => ({ id: s, label: t(`lye.status.${s}`) }))]}
          value={filter.status || ALL} onChange={(id) => setFilter({ ...filter, status: id === ALL ? '' : id })} />
      </form>
      {lines.length === 0 ? <EmptyState title={t('lye.lines.empty')}>{t(summary.lines.length === 0 ? 'lye.lines.empty.none' : 'lye.lines.empty.filtered')}</EmptyState> : (
        <ScrollRegion className="erp-leave-scroll erp-table-scroll" label={t('lye.lines.caption')}>
          <table className="erp-leave-table">
            <caption className="erp-visually-hidden">{t('lye.lines.caption')}</caption>
            <thead><tr>
              <th scope="col">{t('lye.col.employee')}</th><th scope="col">{t('lye.col.type')}</th><th scope="col">{t('lye.col.closing')}</th><th scope="col">{t('lye.col.carry')}</th><th scope="col">{t('lye.col.encash')}</th><th scope="col">{t('lye.col.lapse')}</th><th scope="col">{t('lye.col.status')}</th>
            </tr></thead>
            <tbody>
              {lines.map((l) => {
                const posted = l.status === 'ok';
                return (
                  <tr key={`${l.employmentId}-${l.leaveTypeId}`}>
                    <th scope="row"><bdi dir="ltr">{l.employeeNo}</bdi></th>
                    <td><bdi>{typeLabel(l.leaveTypeId, l.code)}</bdi> <span className="erp-muted">({unitText(l.unit)})</span></td>
                    <td><bdi dir="ltr">{num(l.closing)}</bdi></td>
                    <td>{posted ? <><bdi dir="ltr">{num(unitsToText(carriedOf(l)))}</bdi>{l.carryExpiresOn && <span className="erp-muted"> {t('lye.carry.expires', { date: formatDate(`${l.carryExpiresOn}T12:00:00Z`, lang, timeZone, numbers) })}</span>}</> : <span className="erp-muted">{t('lye.none')}</span>}</td>
                    <td>{posted ? <bdi dir="ltr">{num(l.encash)}</bdi> : <span className="erp-muted">{t('lye.none')}</span>}</td>
                    <td>{posted ? <bdi dir="ltr">{num(l.lapse)}</bdi> : <span className="erp-muted">{t('lye.none')}</span>}</td>
                    <td>{t(`lye.status.${l.status}`)}</td>
                  </tr>);
              })}
            </tbody>
          </table>
        </ScrollRegion>)}
    </div>
  );
}
