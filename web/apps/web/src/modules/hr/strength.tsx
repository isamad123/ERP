// The strength report (slice S1.22b Build item 7; FR-HRM-POS-03, GHR-03): sanctioned, filled, vacant and over strength per unit, grade and
// location as of a date, with the planned and frozen figures, a drill into the positions or people behind a row, and a CSV export.
// What a person sees is what the server returned for their data scope; the figures are the server's, never worked out here (STA-06).
import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { parseDate } from '@internationalized/date';
import { Banner, Button, CheckboxField, DateField, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, useToast } from '@erp/ui';
import { useDocumentTitle } from '@erp/engine';
import { formatDecimal, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { strengthApi, type StrengthFilter, type StrengthRow } from '../../api/positions';
import { useProblemText } from '../admin/users-parts';
import { todayIso, useCompanyChoice } from './positions-parts';

const DIMENSIONS = ['unit', 'grade', 'location'] as const;
type Dimension = (typeof DIMENSIONS)[number];

export default function StrengthScreen() {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  const toast = useToast();
  const problemText = useProblemText();
  useDocumentTitle(t('pos.strength.title'));
  const { companyId, setCompanyId, options: companies } = useCompanyChoice();
  const [asOf, setAsOf] = useState(todayIso());
  const [group, setGroup] = useState<Dimension[]>(['unit', 'grade']);
  const [row, setRow] = useState<StrengthRow | null>(null);
  const [exporting, setExporting] = useState(false);
  const asOfDate = (() => { try { return parseDate(asOf); } catch { return null; } })();
  const filter: StrengthFilter = { companyId: companyId ?? '', asOf, groupBy: group };
  const report = useQuery({ queryKey: ['hrm', 'strength', companyId, asOf, group.join(',')], enabled: companyId !== null && asOfDate !== null && group.length > 0, queryFn: ({ signal }) => strengthApi.report(filter, signal) });
  const problem = report.error instanceof ApiProblem ? report.error : null;
  const num = (value: number) => formatDecimal(String(Math.round(value * 10_000) / 10_000), numbers);
  const name = (names: Record<string, string> | null, code: string | null) => names?.[locale] ?? names?.en ?? code ?? '';
  const headcount = report.data?.model === 'headcount';
  const label = (r: StrengthRow) => [r.unitCode ? name(r.unitName, r.unitCode) : null, r.gradeCode, r.locationCode ? name(r.locationName, r.locationCode) : null].filter(Boolean).join(' · ') || t('pos.all');
  const toggle = (d: Dimension, on: boolean) => { setRow(null); setGroup(DIMENSIONS.filter((x) => (x === d ? on : group.includes(x)))); };
  const exportCsv = async () => {
    setExporting(true);
    try { await strengthApi.exportCsv(filter); toast.success(t('pos.exported')); } catch (e) { toast.error(e instanceof ApiProblem ? problemText(e) : t('pos.exportFailed')); } finally { setExporting(false); }
  };

  let body;
  if (companyId === null) body = <EmptyState title={t('pos.noCompany')}>{null}</EmptyState>;
  else if (report.isPending) body = <Skeleton lines={6} />;
  else if (report.isError) body = <ErrorPanel message={problem?.status === 403 ? t('pos.denied') : t('pos.loadError')} reference={problem?.reference ?? null} action={<Button variant="primary" onPress={() => { void report.refetch(); }}>{t('common.retry')}</Button>} />;
  else if (report.data.rows.length === 0) body = <EmptyState title={t('pos.strength.empty')}>{null}</EmptyState>;
  else {
    const data = report.data;
    const columns = ['sanctioned', 'filled', 'vacant', 'over', 'planned', 'frozen'] as const;
    body = (
      <div className="erp-pos-scroll">
        <table className="erp-pos-table" aria-label={t('pos.strength.title')}>
          <thead><tr>
            <th scope="col">{t('pos.unit')}</th><th scope="col">{t('pos.grade')}</th>{!headcount && <th scope="col">{t('pos.location')}</th>}
            {columns.map((c) => <th key={c} scope="col" className="erp-num">{t(`pos.strength.col.${c}` as MessageKey)}</th>)}
            <th scope="col"><span className="erp-visually-hidden">{t('pos.strength.drill.positions')}</span></th>
          </tr></thead>
          <tbody>
            {data.rows.map((r, i) => (
              <tr key={`${r.unitId}-${r.gradeId}-${r.locationId}-${i}`} aria-selected={row === r}>
                <td>{r.unitId ? <bdi>{name(r.unitName, r.unitCode)}</bdi> : t('pos.all')}</td>
                <td>{r.gradeId ? <bdi className="erp-code">{r.gradeCode}</bdi> : headcount || group.includes('grade') ? t('pos.strength.anyGrade') : t('pos.all')}</td>
                {!headcount && <td>{r.locationId ? <bdi>{name(r.locationName, r.locationCode)}</bdi> : t('pos.all')}</td>}
                {columns.map((c) => <td key={c} className={`erp-num${c === 'over' && r.over > 0 ? ' erp-pos-over' : ''}`}>{num(r[c])}</td>)}
                <td><Button variant="quiet" aria-label={t('pos.strength.open', { row: label(r) })} onPress={() => setRow(row === r ? null : r)}>{t('pos.strength.drill.holders')}</Button></td>
              </tr>
            ))}
          </tbody>
          <tfoot><tr>
            <th scope="row" colSpan={headcount ? 2 : 3}>{t('pos.strength.total')}</th>
            {columns.map((c) => <td key={c} className="erp-num"><strong>{num(data.totals[c])}</strong></td>)}
            <td />
          </tr></tfoot>
        </table>
      </div>
    );
  }

  return (
    <div className="erp-page">
      <PageHeader title={t('pos.strength.title')} description={t('pos.strength.description')}
        actions={<Button variant="secondary" isPending={exporting} isDisabled={!report.isSuccess || report.data.rows.length === 0} onPress={() => { void exportCsv(); }}>{t('pos.export.csv')}</Button>} />
      <div className="erp-row erp-pos-controls">
        {companies.length > 1 && <SelectField label={t('pos.company')} options={companies} value={companyId} onChange={(id) => { setCompanyId(id); setRow(null); }} />}
        <DateField label={t('pos.asOf')} help={t('pos.asOf.help')} value={asOfDate} onChange={(d) => { if (d) { setAsOf(d.toString()); setRow(null); } }} />
        {!headcount && (
          <fieldset className="erp-pos-fieldset">
            <legend>{t('pos.strength.groupBy')}</legend>
            <div className="erp-row">
              {DIMENSIONS.map((d) => <CheckboxField key={d} label={t(`pos.strength.group.${d}` as MessageKey)} isSelected={group.includes(d)} onChange={(on) => toggle(d, on)} />)}
            </div>
          </fieldset>
        )}
      </div>
      {headcount && <Banner tone="info">{t('pos.strength.headcountNote')}</Banner>}
      {body}
      {row && companyId && <Drill filter={{ ...filter, unitId: row.unitId, gradeId: row.gradeId, locationId: row.locationId }} title={label(row)} />}
    </div>
  );
}

/** The positions (position companies) or the people (headcount companies) behind one row. */
function Drill({ filter, title }: { filter: StrengthFilter; title: string }) {
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  const drill = useQuery({ queryKey: ['hrm', 'strength', 'drill', filter.companyId, filter.asOf, filter.unitId, filter.gradeId, filter.locationId], queryFn: ({ signal }) => strengthApi.drill(filter, signal) });
  const num = (value: number) => formatDecimal(String(Math.round(value * 10_000) / 10_000), numbers);
  const rows = useMemo(() => drill.data, [drill.data]);
  return (
    <section className="erp-section" aria-labelledby="strength-drill" aria-live="polite">
      <h2 id="strength-drill" className="erp-h2">{t('pos.strength.drill', { row: title })}</h2>
      {drill.isPending && <Skeleton lines={3} />}
      {drill.isError && <ErrorPanel message={t('pos.loadError')} action={<Button variant="primary" onPress={() => { void drill.refetch(); }}>{t('common.retry')}</Button>} />}
      {rows && rows.positions.length === 0 && rows.people.length === 0 && <p className="erp-muted">{t('pos.strength.drill.none')}</p>}
      {rows && rows.positions.length > 0 && (
        <div className="erp-pos-scroll">
          <table className="erp-pos-table" aria-label={t('pos.strength.drill.positions')}>
            <thead><tr>
              <th scope="col">{t('pos.positions.col.code')}</th><th scope="col">{t('pos.positions.col.title')}</th><th scope="col">{t('pos.positions.col.status')}</th>
              <th scope="col" className="erp-num">{t('pos.strength.col.sanctioned')}</th><th scope="col" className="erp-num">{t('pos.strength.col.filled')}</th>
              <th scope="col">{t('pos.strength.drill.holders')}</th>
            </tr></thead>
            <tbody>
              {rows.positions.map((p) => (
                <tr key={p.id}>
                  <td><bdi className="erp-code">{p.code}</bdi></td><td><bdi>{p.title}</bdi></td>
                  <td><StatusChip status={STATUS_CHIP[p.status] ?? 'draft'} label={t(`pos.status.${p.status}` as MessageKey)} /></td>
                  <td className="erp-num">{num(p.budgetedFte)}</td><td className="erp-num">{num(p.filledFte)}</td>
                  <td>{p.holders.length === 0 ? <span className="erp-muted">{t('pos.strength.drill.vacantFte', { fte: num(p.vacantFte) })}</span> : p.holders.map((h) => <div key={h.employmentId}><bdi>{h.name ?? h.employeeNo}</bdi></div>)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {rows && rows.people.length > 0 && (
        <ul className="erp-plain-list" aria-label={t('pos.strength.drill.people')}>
          {rows.people.map((p) => <li key={p.employmentId}><bdi className="erp-code">{p.employeeNo}</bdi> <bdi>{p.name ?? ''}</bdi> <span className="erp-muted"><bdi>{p.title}</bdi></span></li>)}
        </ul>
      )}
    </section>
  );
}

const STATUS_CHIP: Record<string, 'draft' | 'info' | 'approved' | 'on_hold' | 'blocked'> = { planned: 'draft', open: 'info', filled: 'approved', frozen: 'on_hold', closed: 'blocked' };
