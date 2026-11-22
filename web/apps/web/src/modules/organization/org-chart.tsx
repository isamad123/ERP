// The org chart (slice S1.14b Build item 10; FR-ADM-ORG-03, Q10): a hierarchy as it stood on a chosen date, as an expandable tree
// (keyboard navigable) or a top-down chart. A unit is moved from a date by dragging it onto its new parent or with "Move to…"
// (same dialog, keyboard way); "Compare two dates" lists the units added, removed and moved. Units not in the hierarchy on that
// date are listed so they can be placed. The server checks every request again (permissions, companies, cycles, dates).
import './organization.css';
import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { parseDate } from '@internationalized/date';
import { GitCompare, ListTree, Network, MoveRight } from 'lucide-react';
import { orgHierarchies as hierarchyApi } from '@erp/generated/api/org';
import { Button, DateField, EmptyState, ErrorPanel, Link, PageHeader, SelectField, Skeleton, type Option } from '@erp/ui';
import { useDocumentTitle } from '@erp/engine';
import { useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { org, type OrgTreeNode } from '../../api/org';
import { systemClock } from '../../session/clock';
import { useShell } from '../../shell/context';
import { ancestorIds, buildModel } from './org-chart-model';
import { CompareDialog, LIMIT, MoveDialog, UnitChart, UnitTree, treeKey, useUnitName } from './org-chart-parts';
import { UNITS_PATH } from './units';

const todayIso = () => new Date(systemClock.now()).toISOString().slice(0, 10);
const yearLater = (iso: string) => `${Number(iso.slice(0, 4)) + 1}${iso.slice(4)}`;

export default function OrgChartScreen() {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { can, writable } = useShell();
  const unitName = useUnitName();
  useDocumentTitle(t('chart.title'));
  const [params, setParams] = useSearchParams();
  const [asOf, setAsOf] = useState(() => params.get('asOf') ?? todayIso());
  const [view, setView] = useState<'tree' | 'chart'>(() => (params.get('view') === 'chart' ? 'chart' : 'tree'));
  const [open, setOpen] = useState<Set<string>>(new Set());
  const [limit, setLimit] = useState(LIMIT);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [moving, setMoving] = useState<{ unit: OrgTreeNode; target: OrgTreeNode | null } | null>(null);
  const [comparing, setComparing] = useState(false);

  const hierarchies = useQuery({ queryKey: ['org', 'hierarchies', 'active'], queryFn: () => hierarchyApi.list({ isActive: true, limit: 100, sort: 'code' }), staleTime: 60_000 });
  const options = useMemo<Option[]>(() => (hierarchies.data?.items ?? []).map((h) => ({ id: h.id, label: h.nameI18n[locale] ?? h.nameI18n.en ?? h.code, detail: h.code })), [hierarchies.data, locale]);
  const hierarchyId = params.get('hierarchy') ?? options.find((o) => o.detail === 'reporting')?.id ?? options[0]?.id ?? null;
  const asOfDate = (() => { try { return parseDate(asOf); } catch { return null; } })();
  const validAsOf = asOfDate ? asOf : null;

  const tree = useQuery({
    queryKey: treeKey(hierarchyId ?? '', validAsOf ?? ''), queryFn: ({ signal }) => org.tree(hierarchyId as string, validAsOf as string, signal), enabled: hierarchyId !== null && validAsOf !== null,
  });
  const model = useMemo(() => buildModel(tree.data?.nodes ?? []), [tree.data]);
  // The top of every company opens at first sight; the rest opens when asked (lazy, ruling Q10).
  useEffect(() => { setOpen(new Set(model.roots.map((r) => r.unitId))); setLimit(LIMIT); }, [model]);

  const selected = selectedId ? model.byId.get(selectedId) ?? tree.data?.unplaced.find((u) => u.unitId === selectedId) ?? null : null;
  const canMove = (node: OrgTreeNode) => writable && can('org.org_unit_links.update', node.companyId);
  const canPlace = (node: OrgTreeNode) => writable && can('org.org_unit_links.create', node.companyId);
  const set = (key: string, value: string) => { const next = new URLSearchParams(params); next.set(key, value); setParams(next, { replace: true }); };
  const handlers = {
    selectedId,
    canDrag: (node: OrgTreeNode) => canMove(node),
    onSelect: (node: OrgTreeNode) => setSelectedId(node.unitId),
    onDropOn: (draggedId: string, target: OrgTreeNode) => { const unit = model.byId.get(draggedId); if (unit && canMove(unit)) setMoving({ unit, target }); },
    onToggle: () => undefined,
  };
  const crumbs = [{ label: t('nav.organizationUnits'), href: UNITS_PATH }, { label: t('chart.title') }];

  let body;
  if (hierarchies.isPending) body = <Skeleton lines={6} />;
  else if (hierarchies.isError) body = <ErrorPanel message={hierarchies.error instanceof ApiProblem && hierarchies.error.status === 403 ? t('org.denied') : t('org.loadError')} />;
  else if (options.length === 0) body = <EmptyState title={t('chart.noHierarchy')}>{t('chart.noHierarchy.body')} <Link href="/organization/org-units/hierarchies">{t('chart.noHierarchy.link')}</Link></EmptyState>;
  else if (!asOfDate) body = <EmptyState title={t('chart.pickDate')}>{null}</EmptyState>;
  else if (tree.isPending) body = <Skeleton lines={6} />;
  else if (tree.isError) body = <ErrorPanel message={tree.error instanceof ApiProblem && tree.error.status === 403 ? t('org.denied') : t('org.loadError')} />;
  else if (tree.data.nodes.length === 0 && tree.data.unplaced.length === 0) body = <EmptyState title={t('chart.empty')}>{t('chart.empty.body')}</EmptyState>;
  else {
    const Drawing = view === 'tree' ? UnitTree : UnitChart;
    body = (
      <div className="erp-chart-layout">
        <div className="erp-chart-main">
          {tree.data.nodes.length === 0 ? <p className="erp-muted">{t('chart.nothingPlaced')}</p>
            : <Drawing model={model} open={open} setOpen={setOpen} limit={limit} onMore={() => setLimit(limit + LIMIT)} {...handlers} />}
          {tree.data.unplaced.length > 0 && (
            <section className="erp-section" aria-labelledby="chart-unplaced">
              <h2 id="chart-unplaced" className="erp-h2">{t('chart.unplaced')} ({tree.data.unplaced.length})</h2>
              <p className="erp-help">{t('chart.unplaced.help')}</p>
              <ul className="erp-plain-list">
                {tree.data.unplaced.map((u) => (
                  <li key={u.unitId} className="erp-row">
                    <bdi className="erp-code">{u.code}</bdi> <bdi>{unitName(u)}</bdi>
                    {canPlace(u) && <Button variant="quiet" onPress={() => setMoving({ unit: u, target: null })}>{t('chart.place.button', { unit: unitName(u) })}</Button>}
                  </li>
                ))}
              </ul>
            </section>
          )}
        </div>
        <aside className="erp-chart-side" aria-label={t('chart.selected')} aria-live="polite">
          {selected ? (
            <div className="erp-stack">
              <h2 className="erp-h2"><bdi>{unitName(selected)}</bdi></h2>
              <p><bdi className="erp-code">{selected.code}</bdi>{!selected.isActive && <span className="erp-muted"> · {t('chart.inactive')}</span>}</p>
              {selected.linkId && <p className="erp-muted">{t('chart.since', { date: selected.validFrom ?? '' })}</p>}
              <div className="erp-row">
                {selected.linkId ? canMove(selected) && <Button variant="primary" icon={<MoveRight size={16} aria-hidden="true" />} onPress={() => setMoving({ unit: selected, target: null })}>{t('chart.moveTo')}</Button>
                  : canPlace(selected) && <Button variant="primary" onPress={() => setMoving({ unit: selected, target: null })}>{t('chart.place.button', { unit: unitName(selected) })}</Button>}
                {can('org.org_units.read') && <Link href={`${UNITS_PATH}/${selected.unitId}`}>{t('chart.openUnit')}</Link>}
              </div>
              {selected.linkId && ancestorIds(model, selected.unitId).length > 0 && (
                <Button variant="quiet" onPress={() => setOpen(new Set([...open, ...ancestorIds(model, selected.unitId)]))}>{t('chart.showInTree')}</Button>
              )}
            </div>
          ) : <p className="erp-muted">{t('chart.select.hint')}</p>}
        </aside>
      </div>
    );
  }

  return (
    <div className="erp-page">
      <PageHeader title={t('chart.title')} breadcrumbs={crumbs} description={t('chart.description')}
        actions={hierarchyId && validAsOf ? <Button variant="secondary" icon={<GitCompare size={16} aria-hidden="true" />} onPress={() => setComparing(true)}>{t('chart.compare')}</Button> : undefined} />
      <div className="erp-row erp-section-head">
        <SelectField label={t('chart.hierarchy')} options={options} value={hierarchyId} onChange={(id) => { setSelectedId(null); set('hierarchy', id); }} />
        <DateField label={t('chart.asOf')} value={asOfDate} onChange={(d) => { if (d) { setAsOf(d.toString()); set('asOf', d.toString()); } }} help={t('chart.asOf.help')} />
        <div className="erp-row" role="group" aria-label={t('chart.view')}>
          <Button variant={view === 'tree' ? 'primary' : 'secondary'} aria-pressed={view === 'tree'} icon={<ListTree size={16} aria-hidden="true" />} onPress={() => { setView('tree'); set('view', 'tree'); }}>{t('chart.view.tree')}</Button>
          <Button variant={view === 'chart' ? 'primary' : 'secondary'} aria-pressed={view === 'chart'} icon={<Network size={16} aria-hidden="true" />} onPress={() => { setView('chart'); set('view', 'chart'); }}>{t('chart.view.chart')}</Button>
        </div>
      </div>
      <p className="erp-help">{t('chart.drag.help')}</p>
      {body}
      {moving && hierarchyId && validAsOf && (
        <MoveDialog hierarchyId={hierarchyId} asOf={validAsOf} model={model} all={tree.data?.nodes ?? []} unit={moving.unit} target={moving.target} onClose={() => setMoving(null)} />
      )}
      {comparing && hierarchyId && validAsOf && <CompareDialog hierarchyId={hierarchyId} initialFrom={validAsOf} initialTo={yearLater(validAsOf)} onClose={() => setComparing(false)} />}
    </div>
  );
}
