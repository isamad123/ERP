// The org chart by position (slice S1.22b Build item 8; FR-HRM-POS-04, GHR-03): the reporting lines of positions (of managers in a
// headcount company) as of a date, also a future one, as an expandable tree that is walked with the keyboard. Holders are shown with their
// names, vacant boxes are marked; filter by unit and location, search a person or position, zoom, print (the browser's print with an A4
// landscape page rule, ruling Q7) and export (PDF up to 500 boxes through the document service, else CSV of the reporting lines). Photos
// are shown only where the person may read them: this screen shows initials until the profile photo service (S1.21) serves the file.
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { useSearchParams } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { parseDate } from '@internationalized/date';
import { ChevronDown, ChevronRight, Printer, ZoomIn, ZoomOut } from 'lucide-react';
import { Banner, Button, DateField, EmptyState, ErrorPanel, IconButton, PageHeader, SelectField, Skeleton, TextField, useToast } from '@erp/ui';
import { useDocumentTitle } from '@erp/engine';
import { formatDecimal, formatInteger, useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { chartApi, type ChartNode } from '../../api/positions';
import { useProblemText } from '../admin/users-parts';
import { allParents, ancestorIds, buildTree, initialsOf, nextZoom, searchMatches, visibleRows } from './position-chart-model';
import { ANY, todayIso, useCompanyChoice, useLocationOptions, useUnitOptions } from './positions-parts';

/** Boxes drawn at once (like the unit chart, ruling Q10); "show more" adds as many again. */
const LIMIT = 200;
const PDF_LIMIT = 500;

export default function PositionChartScreen() {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  const toast = useToast();
  const problemText = useProblemText();
  useDocumentTitle(t('pos.chart.title'));
  const { companyId, setCompanyId, options: companies } = useCompanyChoice();
  const [params, setParams] = useSearchParams();
  const asOf = params.get('asOf') ?? todayIso();
  const unitId = params.get('unit');
  const locationId = params.get('location');
  const setParam = (key: string, value: string | null) => { const next = new URLSearchParams(params); if (value) next.set(key, value); else next.delete(key); setParams(next, { replace: true }); };
  const asOfDate = (() => { try { return parseDate(asOf); } catch { return null; } })();
  const units = useUnitOptions(companyId);
  const locations = useLocationOptions(companyId);
  const [search, setSearch] = useState('');
  const [zoom, setZoom] = useState(100);
  const [open, setOpen] = useState<Set<string>>(new Set());
  const [limit, setLimit] = useState(LIMIT);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [exporting, setExporting] = useState<'pdf' | 'csv' | null>(null);

  const filter = { companyId: companyId ?? '', asOf, unitId, locationId };
  const chart = useQuery({ queryKey: ['hrm', 'position-chart', companyId, asOf, unitId, locationId], enabled: companyId !== null && asOfDate !== null, queryFn: ({ signal }) => chartApi.chart(filter, signal) });
  const nodes = useMemo(() => chart.data?.nodes ?? [], [chart.data]);
  const tree = useMemo(() => buildTree(nodes), [nodes]);
  const matches = useMemo(() => searchMatches(nodes, search), [nodes, search]);
  // The top boxes open at first sight; a search opens the way down to every box it finds.
  useEffect(() => { setOpen(new Set(tree.roots.map((r) => r.id))); setLimit(LIMIT); setSelectedId(null); }, [tree]);
  useEffect(() => {
    if (matches.size === 0) return;
    setOpen((current) => new Set([...current, ...[...matches].flatMap((id) => ancestorIds(tree, id))]));
  }, [matches, tree]);

  const { rows, total } = useMemo(() => visibleRows(tree, open, limit), [tree, open, limit]);
  const problem = chart.error instanceof ApiProblem ? chart.error : null;
  const date = asOfDate ? asOfDate.toString() : asOf;
  const exportFile = async (format: 'pdf' | 'csv') => {
    setExporting(format);
    try {
      await chartApi.exportFile(filter, format, locale === 'ur' ? 'ur' : 'en', { title: t('pos.chart.pdf.title'), asOf: t('pos.chart.pdf.asOf'), vacant: t('pos.chart.pdf.vacant') });
      toast.success(t('pos.exported'));
    } catch (e) { toast.error(e instanceof ApiProblem ? problemText(e) : t('pos.exportFailed')); } finally { setExporting(null); }
  };
  const tooLargeForPdf = (chart.data?.total ?? 0) > PDF_LIMIT;

  let body;
  if (companyId === null) body = <EmptyState title={t('pos.noCompany')}>{null}</EmptyState>;
  else if (chart.isPending) body = <Skeleton lines={6} />;
  else if (chart.isError) body = <ErrorPanel message={problem?.status === 403 ? t('pos.denied') : t('pos.loadError')} reference={problem?.reference ?? null} action={<Button variant="primary" onPress={() => { void chart.refetch(); }}>{t('common.retry')}</Button>} />;
  else if (nodes.length === 0) body = <EmptyState title={t('pos.chart.empty')}>{null}</EmptyState>;
  else {
    body = (
      <>
        {chart.data.truncated && <Banner tone="warning">{t('pos.chart.truncated', { shown: formatInteger(nodes.length, numbers), total: formatInteger(chart.data.total, numbers) })}</Banner>}
        {tooLargeForPdf && <Banner tone="info">{t('pos.chart.pdfTooLarge', { limit: formatInteger(PDF_LIMIT, numbers) })}</Banner>}
        <ChartTree rows={rows} total={total} matches={matches} selectedId={selectedId} zoom={zoom} open={open} setOpen={setOpen} onSelect={setSelectedId} onMore={() => setLimit(limit + LIMIT)} />
      </>
    );
  }

  return (
    <div className="erp-page">
      <PageHeader title={t('pos.chart.title')} description={t('pos.chart.description')}
        actions={(
          <div className="erp-row erp-wrap erp-pos-noprint">
            <Button variant="secondary" icon={<Printer size={16} aria-hidden="true" />} onPress={() => window.print()}>{t('pos.print')}</Button>
            <Button variant="secondary" isPending={exporting === 'pdf'} isDisabled={tooLargeForPdf || nodes.length === 0} onPress={() => { void exportFile('pdf'); }}>{t('pos.export.pdf')}</Button>
            <Button variant="secondary" isPending={exporting === 'csv'} isDisabled={nodes.length === 0} onPress={() => { void exportFile('csv'); }}>{t('pos.export.csv')}</Button>
          </div>
        )} />
      <h2 className="erp-pos-print-heading">{t('pos.chart.print.heading', { title: t('pos.chart.title'), date })}</h2>
      <div className="erp-row erp-pos-controls">
        {companies.length > 1 && <SelectField label={t('pos.company')} options={companies} value={companyId} onChange={(id) => { setCompanyId(id); setParam('unit', null); setParam('location', null); }} />}
        <DateField label={t('pos.asOf')} help={t('pos.asOf.help')} value={asOfDate} onChange={(d) => { if (d) setParam('asOf', d.toString()); }} />
        <SelectField label={t('pos.unit')} options={[{ id: ANY, label: t('pos.all') }, ...units.options]} value={unitId ?? ANY} onChange={(id) => setParam('unit', id === ANY ? null : id)} />
        <SelectField label={t('pos.location')} options={[{ id: ANY, label: t('pos.all') }, ...locations.options]} value={locationId ?? ANY} onChange={(id) => setParam('location', id === ANY ? null : id)} />
        <TextField label={t('pos.chart.search')} value={search} onChange={setSearch} />
        <div className="erp-row" role="group" aria-label={t('pos.chart.zoom.level', { percent: formatInteger(zoom, numbers) })}>
          <IconButton variant="secondary" label={t('pos.chart.zoom.out')} icon={<ZoomOut size={16} aria-hidden="true" />} isDisabled={zoom <= 60} onPress={() => setZoom(nextZoom(zoom, -1))} />
          <Button variant="quiet" onPress={() => setZoom(100)}>{formatInteger(zoom, numbers)}%</Button>
          <IconButton variant="secondary" label={t('pos.chart.zoom.in')} icon={<ZoomIn size={16} aria-hidden="true" />} isDisabled={zoom >= 200} onPress={() => setZoom(nextZoom(zoom, 1))} />
        </div>
        <Button variant="quiet" onPress={() => setOpen(allParents(tree))}>{t('pos.chart.expandAll')}</Button>
        <Button variant="quiet" onPress={() => setOpen(new Set())}>{t('pos.chart.collapseAll')}</Button>
      </div>
      {search.trim() !== '' && chart.isSuccess && <p className="erp-help" role="status">{matches.size === 0 ? t('pos.chart.search.none') : t('pos.chart.search.count', { count: matches.size })}</p>}
      {body}
    </div>
  );
}

function ChartTree({ rows, total, matches, selectedId, zoom, open, setOpen, onSelect, onMore }: {
  rows: ReturnType<typeof visibleRows>['rows']; total: number; matches: Set<string>; selectedId: string | null; zoom: number; open: Set<string>;
  setOpen: (next: Set<string>) => void; onSelect: (id: string) => void; onMore: () => void;
}) {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  const [focusId, setFocusId] = useState<string | null>(null);
  const refs = useRef(new Map<string, HTMLDivElement>());
  const current = rows.find((r) => r.node.id === focusId)?.node.id ?? rows[0]?.node.id ?? null;
  const toggle = (id: string, on: boolean) => { const next = new Set(open); if (on) next.add(id); else next.delete(id); setOpen(next); };
  const focus = (id: string | undefined) => { if (id) { setFocusId(id); refs.current.get(id)?.focus(); } };
  const onKey = (e: KeyboardEvent, index: number) => {
    const row = rows[index];
    if (!row) return;
    switch (e.key) {
      case 'ArrowDown': focus(rows[index + 1]?.node.id); break;
      case 'ArrowUp': focus(rows[index - 1]?.node.id); break;
      case 'Home': focus(rows[0]?.node.id); break;
      case 'End': focus(rows[rows.length - 1]?.node.id); break;
      case 'ArrowRight':
        if (row.hasChildren && !row.expanded) toggle(row.node.id, true);
        else if (row.expanded) focus(rows[index + 1]?.node.id);
        break;
      case 'ArrowLeft':
        if (row.expanded) toggle(row.node.id, false);
        else focus(rows.slice(0, index).reverse().find((r) => r.level < row.level)?.node.id);
        break;
      case 'Enter': case ' ': onSelect(row.node.id); break;
      default: return;
    }
    e.preventDefault();
  };
  return (
    <div className="erp-org-tree-wrap">
      <div role="tree" aria-label={t('pos.chart.tree')} className="erp-pos-chart" style={{ fontSize: `${zoom}%` }}>
        {rows.map((row, index) => {
          const n = row.node;
          return (
            <div key={n.id} role="treeitem" aria-level={row.level} aria-posinset={row.posInSet} aria-setsize={row.setSize} {...(row.hasChildren ? { 'aria-expanded': row.expanded } : {})}
              aria-selected={selectedId === n.id} aria-label={t('pos.chart.box', { title: n.title, code: n.code })} tabIndex={current === n.id ? 0 : -1}
              ref={(el) => { if (el) refs.current.set(n.id, el); else refs.current.delete(n.id); }}
              className={`erp-pos-box${n.vacant ? ' erp-pos-box-vacant' : ''}${matches.has(n.id) ? ' erp-pos-box-match' : ''}`} style={{ marginInlineStart: `calc(${row.level - 1} * var(--space-4))` }}
              onKeyDown={(e) => { if (e.target === e.currentTarget) onKey(e, index); }} onFocus={(e) => { if (e.target === e.currentTarget) setFocusId(n.id); }} onClick={() => onSelect(n.id)} data-code={n.code}>
              <span className="erp-org-twisty" aria-hidden="true" onClick={(e) => { e.stopPropagation(); if (row.hasChildren) toggle(n.id, !row.expanded); }}>
                {row.hasChildren ? (row.expanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />) : null}
              </span>
              <Holders node={n} locale={locale} />
              <span className="erp-pos-lines">
                <strong><bdi>{n.title}</bdi></strong>
                <span className="erp-muted"><bdi className="erp-code">{n.code}</bdi>{n.unitCode ? <> · <bdi>{n.unitCode}</bdi></> : null}</span>
                {n.holders.map((h) => (
                  <span key={h.employmentId}><bdi>{(locale === 'ur' && h.nameLocal) || h.name || h.employeeNo}</bdi>{h.isActing ? <span className="erp-muted"> ({t('pos.chart.acting')})</span> : null}</span>
                ))}
                {n.vacant && <span>{n.holders.length === 0 ? t('pos.chart.vacant') : t('pos.chart.vacantFte', { fte: formatDecimal(String(Math.round((n.budgetedFte - n.filledFte) * 10_000) / 10_000), numbers) })}</span>}
              </span>
            </div>
          );
        })}
      </div>
      {total > rows.length && <Button variant="secondary" onPress={onMore}>{t('pos.chart.more', { shown: formatInteger(rows.length, numbers), total: formatInteger(total, numbers) })}</Button>}
    </div>
  );
}

/** The initials of the holders (photos come with the profile photo service; a person who may not read the photo sees initials too, ruling Q8). */
function Holders({ node, locale }: { node: ChartNode; locale: string }) {
  const first = node.holders[0];
  const name = first ? ((locale === 'ur' && first.nameLocal) || first.name) : null;
  return <span className="erp-pos-initials" aria-hidden="true">{initialsOf(name ?? null, node.code)}</span>;
}
