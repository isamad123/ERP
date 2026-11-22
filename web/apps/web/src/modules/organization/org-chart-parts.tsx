// Parts of the org chart (slice S1.14b Build item 10; FR-ADM-ORG-03): the keyboard-driven tree, the top-down chart, the "Move to…"
// dialog and the comparison of two dates. Drawn with our own components (ruling Q10, D-09): at most LIMIT boxes at once with
// "show more", opened level by level. Dragging a unit onto another opens the same move dialog as the button, which is the keyboard
// way to do it. The server decides every move (cycles, companies, dates); the screen shows what it answered.
import { useMemo, useRef, useState, type DragEvent, type KeyboardEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { parseDate } from '@internationalized/date';
import { ChevronDown, ChevronRight } from 'lucide-react';
import { Banner, Button, DateField, Dialog, EmptyState, ErrorPanel, SelectField, Skeleton, TextField, useToast, type Option } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { org, type OrgCompareChange, type OrgTreeNode } from '../../api/org';
import { useProblemText } from '../admin/users-parts';
import { useUnitTypes } from './units';
import { parentChoices, visibleRows, type ChartModel } from './org-chart-model';
import { useDates } from './org-parts';

/** Boxes shown at once (ruling Q10); "show more" adds as many again. */
export const LIMIT = 200;

export const treeKey = (hierarchyId: string, asOf: string) => ['org', 'tree', hierarchyId, asOf] as const;

export function useUnitName() {
  const { locale } = useLocaleSettings();
  return (node: { code: string; name: Record<string, string> | null }) => node.name?.[locale] ?? node.name?.en ?? node.code;
}

interface BoxHandlers {
  selectedId: string | null;
  canDrag: (node: OrgTreeNode) => boolean;
  onSelect: (node: OrgTreeNode) => void;
  onDropOn: (dragged: string, target: OrgTreeNode) => void;
  onToggle: (node: OrgTreeNode) => void;
}

function useDrag(model: ChartModel, handlers: BoxHandlers) {
  const dragging = useRef<string | null>(null);
  return {
    props: (node: OrgTreeNode) => ({
      draggable: handlers.canDrag(node),
      onDragStart: (e: DragEvent) => { dragging.current = node.unitId; e.dataTransfer.setData('text/plain', node.unitId); e.dataTransfer.effectAllowed = 'move'; },
      onDragOver: (e: DragEvent) => {
        const from = dragging.current ? model.byId.get(dragging.current) : null;
        if (from && from.companyId === node.companyId && from.unitId !== node.unitId) e.preventDefault();
      },
      onDrop: (e: DragEvent) => {
        e.preventDefault();
        const id = dragging.current ?? e.dataTransfer.getData('text/plain');
        dragging.current = null;
        if (id && id !== node.unitId) handlers.onDropOn(id, node);
      },
      onDragEnd: () => { dragging.current = null; },
    }),
  };
}

// ---------------------------------------------------------------------------------------------------------------- tree

export function UnitTree({ model, open, setOpen, limit, onMore, ...handlers }: { model: ChartModel; open: Set<string>; setOpen: (next: Set<string>) => void; limit: number; onMore: () => void } & BoxHandlers) {
  const { t } = useText();
  const unitName = useUnitName();
  const types = useUnitTypes();
  const { rows, total } = useMemo(() => visibleRows(model, open, limit), [model, open, limit]);
  const [focusId, setFocusId] = useState<string | null>(null);
  const refs = useRef(new Map<string, HTMLDivElement>());
  const drag = useDrag(model, handlers);
  const current = rows.find((r) => r.node.unitId === focusId)?.node.unitId ?? rows[0]?.node.unitId ?? null;
  const toggle = (id: string, on: boolean) => { const next = new Set(open); if (on) next.add(id); else next.delete(id); setOpen(next); };
  const focus = (id: string | undefined) => { if (id) { setFocusId(id); refs.current.get(id)?.focus(); } };
  const onKey = (e: KeyboardEvent, index: number) => {
    const row = rows[index];
    if (!row) return;
    switch (e.key) {
      case 'ArrowDown': focus(rows[index + 1]?.node.unitId); break;
      case 'ArrowUp': focus(rows[index - 1]?.node.unitId); break;
      case 'Home': focus(rows[0]?.node.unitId); break;
      case 'End': focus(rows[rows.length - 1]?.node.unitId); break;
      case 'ArrowRight':
        if (row.hasChildren && !row.expanded) toggle(row.node.unitId, true);
        else if (row.expanded) focus(rows[index + 1]?.node.unitId);
        break;
      case 'ArrowLeft':
        if (row.expanded) toggle(row.node.unitId, false);
        else focus(rows.slice(0, index).reverse().find((r) => r.level < row.level)?.node.unitId);
        break;
      case 'Enter': case ' ': handlers.onSelect(row.node); break;
      default: return;
    }
    e.preventDefault();
  };
  return (
    <div className="erp-org-tree-wrap">
      <div role="tree" aria-label={t('chart.tree.label')} className="erp-org-tree">
        {rows.map((row, index) => (
          <div key={row.node.unitId} role="treeitem" aria-level={row.level} aria-posinset={row.posInSet} aria-setsize={row.setSize}
            {...(row.hasChildren ? { 'aria-expanded': row.expanded } : {})} aria-selected={handlers.selectedId === row.node.unitId}
            tabIndex={current === row.node.unitId ? 0 : -1} ref={(el) => { if (el) refs.current.set(row.node.unitId, el); else refs.current.delete(row.node.unitId); }}
            className={`erp-org-item${handlers.selectedId === row.node.unitId ? ' erp-org-item-selected' : ''}`} style={{ paddingInlineStart: `calc(${row.level - 1} * var(--space-4))` }}
            onKeyDown={(e) => { if (e.target === e.currentTarget) onKey(e, index); }} onFocus={(e) => { if (e.target === e.currentTarget) setFocusId(row.node.unitId); }}
            onClick={() => handlers.onSelect(row.node)} {...drag.props(row.node)} data-unit={row.node.code}>
            <span className="erp-org-twisty" aria-hidden="true" onClick={(e) => { e.stopPropagation(); if (row.hasChildren) toggle(row.node.unitId, !row.expanded); }}>
              {row.hasChildren ? (row.expanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />) : null}
            </span>
            <bdi className="erp-code">{row.node.code}</bdi> <bdi>{unitName(row.node)}</bdi>
            <span className="erp-muted"> · <bdi>{types.name.get(row.node.unitTypeId) ?? ''}</bdi></span>
            {!row.node.isActive && <span className="erp-muted"> ({t('chart.inactive')})</span>}
          </div>
        ))}
      </div>
      {total > rows.length && <Button variant="secondary" onPress={onMore}>{t('chart.more', { shown: rows.length, total })}</Button>}
    </div>
  );
}

// -------------------------------------------------------------------------------------------------------------- chart

export function UnitChart({ model, open, setOpen, limit, onMore, ...handlers }: { model: ChartModel; open: Set<string>; setOpen: (next: Set<string>) => void; limit: number; onMore: () => void } & BoxHandlers) {
  const { t } = useText();
  const unitName = useUnitName();
  const drag = useDrag(model, handlers);
  let drawn = 0;
  let total = 0;
  const countAll = (list: OrgTreeNode[]) => { for (const n of list) { total += 1; if (open.has(n.unitId)) countAll(model.children.get(n.unitId) ?? []); } };
  countAll(model.roots);
  const toggle = (id: string) => { const next = new Set(open); if (next.has(id)) next.delete(id); else next.add(id); setOpen(next); };
  const draw = (node: OrgTreeNode) => {
    if (drawn >= limit) return null;
    drawn += 1;
    const kids = model.children.get(node.unitId) ?? [];
    const expanded = kids.length > 0 && open.has(node.unitId);
    return (
      <li key={node.unitId}>
        <div className="erp-chart-node">
          <button type="button" className={`erp-chart-box${handlers.selectedId === node.unitId ? ' erp-chart-box-selected' : ''}`} aria-pressed={handlers.selectedId === node.unitId}
            onClick={() => handlers.onSelect(node)} {...drag.props(node)} data-unit={node.code}>
            <bdi className="erp-code">{node.code}</bdi>
            <bdi>{unitName(node)}</bdi>
            {!node.isActive && <span className="erp-muted">{t('chart.inactive')}</span>}
          </button>
          {kids.length > 0 && (
            <button type="button" className="erp-chart-toggle" aria-expanded={expanded} aria-label={t(expanded ? 'chart.collapse' : 'chart.expand', { unit: unitName(node), count: kids.length })}
              onClick={() => toggle(node.unitId)}>{expanded ? <ChevronDown size={14} aria-hidden="true" /> : <ChevronRight size={14} aria-hidden="true" />}<span aria-hidden="true">{kids.length}</span></button>
          )}
        </div>
        {expanded && <ul>{kids.map(draw)}</ul>}
      </li>
    );
  };
  const body = model.roots.map(draw);
  return (
    <div className="erp-org-tree-wrap">
      <div className="erp-chart" role="group" aria-label={t('chart.chart.label')}><ul>{body}</ul></div>
      {total > drawn && <Button variant="secondary" onPress={onMore}>{t('chart.more', { shown: drawn, total })}</Button>}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------- move

/** Moves a unit (or places one that is in no place yet) from a date. `target` is the parent already chosen by a drop, if any. */
export function MoveDialog({ hierarchyId, asOf, model, all, unit, target, onClose }: {
  hierarchyId: string; asOf: string; model: ChartModel; all: OrgTreeNode[]; unit: OrgTreeNode; target: OrgTreeNode | null; onClose: () => void;
}) {
  const { t, server } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const problemText = useProblemText();
  const unitName = useUnitName();
  const date = useDates();
  const top = '__top';
  const [parent, setParent] = useState<string>(target && target.companyId === unit.companyId ? target.unitId : unit.linkId === null ? '' : unit.parentUnitId ?? top);
  const [effective, setEffective] = useState(asOf);
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const placing = unit.linkId === null;
  const options = useMemo<Option[]>(() => [
    { id: top, label: t('chart.move.top') },
    ...parentChoices(model, unit, all).map((n): Option => ({ id: n.unitId, label: unitName(n), detail: n.code })),
  ], [model, unit, all, t, unitName]);
  // A unit dropped on a unit it cannot sit under (one of its own sub-units, or one of another company) is said so before any request.
  const refused = target && !options.some((o) => o.id === target.unitId)
    ? server(target.companyId === unit.companyId ? 'errors.org.move_cycle' : 'errors.org.move_other_company') ?? t('chart.move.failed') : null;
  const safe = (() => { try { return parseDate(effective); } catch { return null; } })();
  const mutation = useMutation({
    mutationFn: () => org.moveUnit(unit.unitId, { hierarchyId, newParentId: parent === top ? null : parent, effectiveFrom: effective, ...(reason.trim() ? { reason: reason.trim() } : {}) }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['org', 'tree', hierarchyId] });
      toast.success(t('chart.move.done', { unit: unitName(unit), date: date(effective) }));
      onClose();
    },
    onError: (e) => { setError(e instanceof ApiProblem ? problemText(e) : t('chart.move.failed')); },
  });
  return (
    <Dialog title={t(placing ? 'chart.place.title' : 'chart.move.title', { unit: unitName(unit) })} isOpen onOpenChange={(isOpen) => { if (!isOpen) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" isPending={mutation.isPending} isDisabled={!safe || parent === '' || refused !== null} onPress={() => { setError(null); mutation.mutate(); }}>{t(placing ? 'chart.place.confirm' : 'chart.move.confirm')}</Button>
        </>
      )}>
      <div className="erp-stack">
        <p>{t(placing ? 'chart.place.effect' : 'chart.move.effect', { unit: unitName(unit) })}</p>
        <SelectField label={t('chart.move.parent')} isRequired options={options} value={parent === '' ? null : parent} onChange={setParent} help={t('chart.move.parent.help')} />
        <DateField label={t('chart.move.date')} isRequired value={safe} onChange={(d) => setEffective(d ? d.toString() : '')} help={t('chart.move.date.help')} />
        <TextField label={t('chart.move.reason')} value={reason} onChange={setReason} maxLength={500} />
        {refused && <Banner tone="danger" title={refused}>{null}</Banner>}
        {error && <Banner tone="danger" title={error}>{null}</Banner>}
      </div>
    </Dialog>
  );
}

// ------------------------------------------------------------------------------------------------------------ compare

export function CompareDialog({ hierarchyId, initialFrom, initialTo, onClose }: { hierarchyId: string; initialFrom: string; initialTo: string; onClose: () => void }) {
  const { t } = useText();
  const unitName = useUnitName();
  const [from, setFrom] = useState(initialFrom);
  const [to, setTo] = useState(initialTo);
  const parse = (value: string) => { try { return parseDate(value); } catch { return null; } };
  const ready = parse(from) !== null && parse(to) !== null;
  const result = useQuery({ queryKey: ['org', 'compare', hierarchyId, from, to], queryFn: ({ signal }) => org.compare(hierarchyId, from, to, signal), enabled: ready });
  const group = (id: string, title: string, items: OrgCompareChange[], line: (c: OrgCompareChange) => string) => (
    <section aria-labelledby={`cmp-${id}`}>
      <h3 id={`cmp-${id}`} className="erp-h3">{title} ({items.length})</h3>
      {items.length === 0 ? <p className="erp-muted">{t('chart.compare.none')}</p> : (
        <ul className="erp-plain-list">{items.map((c) => <li key={c.unitId}><bdi className="erp-code">{c.code}</bdi> <bdi>{unitName(c)}</bdi> <span className="erp-muted">{line(c)}</span></li>)}</ul>
      )}
    </section>
  );
  const parentText = (code: string | null) => code ?? t('chart.compare.top');
  return (
    <Dialog title={t('chart.compare.title')} isOpen onOpenChange={(isOpen) => { if (!isOpen) onClose(); }} actions={() => <Button variant="primary" onPress={onClose}>{t('common.close')}</Button>}>
      <div className="erp-stack">
        <div className="erp-i18n-pair">
          <DateField label={t('chart.compare.from')} isRequired value={parse(from)} onChange={(d) => setFrom(d ? d.toString() : '')} />
          <DateField label={t('chart.compare.to')} isRequired value={parse(to)} onChange={(d) => setTo(d ? d.toString() : '')} />
        </div>
        {!ready ? <EmptyState title={t('chart.compare.pick')}>{null}</EmptyState>
          : result.isPending ? <Skeleton lines={4} />
          : result.isError ? <ErrorPanel message={result.error instanceof ApiProblem && result.error.status === 403 ? t('org.denied') : t('org.loadError')} />
          : (
            <div className="erp-stack" aria-live="polite">
              {group('added', t('chart.compare.added'), result.data.added, (c) => t('chart.compare.under', { parent: parentText(c.toParentCode) }))}
              {group('removed', t('chart.compare.removed'), result.data.removed, (c) => t('chart.compare.wasUnder', { parent: parentText(c.fromParentCode) }))}
              {group('moved', t('chart.compare.moved'), result.data.moved, (c) => t('chart.compare.fromTo', { from: parentText(c.fromParentCode), to: parentText(c.toParentCode) }))}
            </div>
          )}
      </div>
    </Dialog>
  );
}

