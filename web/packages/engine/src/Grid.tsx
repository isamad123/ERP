// The product's grid (slice S1.12c Build item 16; spec 13 section 8: FR-UX-GRD-01 server keyset paging with "load more"
// and row virtualisation, -02 column choice, resize and order (no grouping), -07 sort, -09 single and multiple selection
// with a bulk bar, -12 keyboard navigation, -13 a card layout on phones, -14 loading, empty and error states). Sorting and
// filtering happen on the server; the grid only shows what it is given and says what the person asked for.
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent, type ReactNode } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { ArrowDown, ArrowUp, ArrowUpDown } from 'lucide-react';
import { Button, EmptyState, Skeleton } from '@erp/ui';
import { formatInteger, useLocaleSettings, useText } from '@erp/i18n';

export interface GridColumn<Row> {
  id: string;
  header: string;
  cell: (row: Row) => ReactNode;
  /** The server can sort by this column. */
  sortable?: boolean;
  /** Numbers and amounts line up at the end. */
  align?: 'start' | 'end';
  /** Width in pixels when the person has not chosen one. */
  width?: number;
}

/** What the person chose for one column: shown or not, its width; the array order is the column order. */
export interface ColumnChoice {
  id: string;
  hidden?: boolean;
  width?: number;
}

export interface GridSort {
  column: string;
  descending: boolean;
}

export type SelectionMode = 'none' | 'single' | 'multiple';

export interface DataGridProps<Row> {
  /** Accessible name of the grid (usually the list title). */
  label: string;
  columns: GridColumn<Row>[];
  rows: Row[];
  rowKey: (row: Row) => string;
  /** Short name of a row for screen readers and the selection boxes (e.g. code and name). */
  rowLabel: (row: Row) => string;
  status: 'loading' | 'error' | 'ready';
  error?: ReactNode;
  empty?: ReactNode;
  choices?: ColumnChoice[];
  onChoicesChange?: (choices: ColumnChoice[]) => void;
  sort?: GridSort | null;
  onSortChange?: (sort: GridSort | null) => void;
  hasMore?: boolean;
  loadingMore?: boolean;
  onLoadMore?: () => void;
  selectionMode?: SelectionMode;
  selected?: ReadonlySet<string>;
  onSelectedChange?: (selected: Set<string>) => void;
  /** Actions for the selected rows (shown in the bulk bar while something is selected). */
  bulkActions?: (selected: ReadonlySet<string>) => ReactNode;
  onOpen?: (row: Row) => void;
}

export const MIN_COLUMN_WIDTH = 64;
export const DEFAULT_COLUMN_WIDTH = 180;
const RESIZE_STEP = 16;
/** From this many rows the grid draws only the rows in view (FR-UX-GRD-01). */
export const VIRTUAL_FROM = 100;
const ROW_ESTIMATE = 44;

/** The columns in the person's order, with hidden ones left out and widths applied. */
export function arrange<Row>(columns: GridColumn<Row>[], choices: ColumnChoice[] | undefined): (GridColumn<Row> & { width: number })[] {
  const byId = new Map(columns.map((c) => [c.id, c]));
  const ordered = [
    ...(choices ?? []).flatMap((c) => { const column = byId.get(c.id); return column ? [{ column, choice: c as ColumnChoice | undefined }] : []; }),
    ...columns.filter((c) => !(choices ?? []).some((x) => x.id === c.id)).map((column) => ({ column, choice: undefined })),
  ];
  return ordered.filter((x) => !x.choice?.hidden).map(({ column, choice }) => ({ ...column, width: Math.max(MIN_COLUMN_WIDTH, choice?.width ?? column.width ?? DEFAULT_COLUMN_WIDTH) }));
}

function useNarrow(): boolean {
  const query = '(max-width: 599px)';
  const [narrow, setNarrow] = useState(() => typeof window !== 'undefined' && typeof window.matchMedia === 'function' && window.matchMedia(query).matches);
  useEffect(() => {
    if (typeof window.matchMedia !== 'function') return;
    const list = window.matchMedia(query);
    const update = () => setNarrow(list.matches);
    list.addEventListener('change', update);
    return () => list.removeEventListener('change', update);
  }, []);
  return narrow;
}

export function DataGrid<Row>(props: DataGridProps<Row>) {
  const { label, columns, rows, rowKey, rowLabel, status, error, empty, choices, onChoicesChange, sort, onSortChange, hasMore, loadingMore, onLoadMore,
    selectionMode = 'none', selected = EMPTY, onSelectedChange, bulkActions, onOpen } = props;
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  const narrow = useNarrow();
  const shown = useMemo(() => arrange(columns, choices), [columns, choices]);

  const toggle = useCallback((key: string) => {
    if (!onSelectedChange) return;
    if (selectionMode === 'single') {
      onSelectedChange(selected.has(key) ? new Set() : new Set([key]));
      return;
    }
    const next = new Set(selected);
    if (next.has(key)) next.delete(key); else next.add(key);
    onSelectedChange(next);
  }, [onSelectedChange, selected, selectionMode]);

  if (status === 'loading') return <div className="erp-grid-state" aria-busy="true"><Skeleton lines={6} /></div>;
  if (status === 'error') return <div className="erp-grid-state">{error}</div>;
  if (rows.length === 0) return <div className="erp-grid-state">{empty ?? <EmptyState title={t('engine.grid.empty')} />}</div>;

  const bulk = selectionMode !== 'none' && selected.size > 0 && bulkActions ? (
    <div className="erp-grid-bulk" role="region" aria-label={t('engine.grid.bulk')}>
      <span aria-live="polite">{t('engine.grid.selected', { count: selected.size, shown: formatInteger(selected.size, numbers) })}</span>
      {bulkActions(selected)}
      <Button variant="quiet" onPress={() => onSelectedChange?.(new Set())}>{t('engine.grid.clearSelection')}</Button>
    </div>
  ) : null;

  const more = hasMore ? (
    <div className="erp-grid-more">
      <Button variant="secondary" isPending={loadingMore ?? false} onPress={() => onLoadMore?.()}>{t('engine.grid.loadMore')}</Button>
    </div>
  ) : null;

  const count = (
    <p className="erp-small erp-muted" aria-live="polite">
      {hasMore ? t('engine.grid.countMore', { count: formatInteger(rows.length, numbers) }) : t('engine.grid.count', { count: rows.length, shown: formatInteger(rows.length, numbers) })}
    </p>
  );

  if (narrow) {
    // Phones (FR-UX-GRD-13): each row is a card with the shown columns as label and value.
    return (
      <div className="erp-grid-wrap">
        {bulk}
        <ul className="erp-grid-cards" aria-label={label}>
          {rows.map((row) => {
            const key = rowKey(row);
            return (
              <li key={key} className="erp-grid-card" data-selected={selected.has(key) || undefined}>
                {selectionMode !== 'none' && (
                  <label className="erp-grid-check">
                    <input type="checkbox" checked={selected.has(key)} onChange={() => toggle(key)} aria-label={t('engine.grid.selectRow', { row: rowLabel(row) })} />
                  </label>
                )}
                <dl className="erp-grid-card-fields">
                  {shown.map((column) => (
                    <div key={column.id} className="erp-grid-card-field">
                      <dt className="erp-small erp-muted">{column.header}</dt>
                      <dd>{column.cell(row)}</dd>
                    </div>
                  ))}
                </dl>
                {onOpen && <Button variant="secondary" onPress={() => onOpen(row)}>{t('engine.grid.open', { row: rowLabel(row) })}</Button>}
              </li>
            );
          })}
        </ul>
        {count}
        {more}
      </div>
    );
  }

  return (
    <div className="erp-grid-wrap">
      {bulk}
      <GridTable {...props} shown={shown} toggle={toggle} selected={selected} selectionMode={selectionMode} sort={sort ?? null}
        onSortChange={onSortChange} choices={choices} onChoicesChange={onChoicesChange} label={label} />
      {count}
      {more}
    </div>
  );
}

const EMPTY: ReadonlySet<string> = new Set();

interface TableProps<Row> extends DataGridProps<Row> {
  shown: (GridColumn<Row> & { width: number })[];
  toggle: (key: string) => void;
  selected: ReadonlySet<string>;
  selectionMode: SelectionMode;
  sort: GridSort | null;
}

/**
 * The table on wider screens: a grid in the ARIA sense, so the arrow keys move between cells (one cell is in the tab
 * order at a time), Home and End go to the row's ends, Enter opens the row and Space selects it (FR-UX-GRD-12).
 */
function GridTable<Row>({ label, rows, rowKey, rowLabel, shown, toggle, selected, selectionMode, sort, onSortChange, choices, onChoicesChange, onOpen, onSelectedChange }: TableProps<Row>) {
  const { t } = useText();
  const { direction } = useLocaleSettings();
  const scroller = useRef<HTMLDivElement>(null);
  const [focus, setFocus] = useState<{ row: number; col: number }>({ row: -1, col: 0 });
  const withCheck = selectionMode !== 'none';
  const columnCount = shown.length + (withCheck ? 1 : 0);
  const virtual = rows.length >= VIRTUAL_FROM;
  const virtualizer = useVirtualizer({ count: rows.length, getScrollElement: () => scroller.current, estimateSize: () => ROW_ESTIMATE, overscan: 10, enabled: virtual });
  const items = virtual ? virtualizer.getVirtualItems() : rows.map((_, index) => ({ index, start: index * ROW_ESTIMATE, end: (index + 1) * ROW_ESTIMATE, key: index }));
  const before = virtual ? items[0]?.start ?? 0 : 0;
  const after = virtual ? Math.max(0, virtualizer.getTotalSize() - (items[items.length - 1]?.end ?? 0)) : 0;

  const cellRef = useRef(new Map<string, HTMLElement>());
  useEffect(() => {
    if (focus.row < 0) return;
    cellRef.current.get(`${focus.row}:${focus.col}`)?.focus();
  }, [focus]);

  const move = (event: KeyboardEvent, row: number, col: number) => {
    const rtl = direction === 'rtl';
    let next: { row: number; col: number } | null = null;
    switch (event.key) {
      case 'ArrowDown': next = { row: Math.min(rows.length - 1, row + 1), col }; break;
      case 'ArrowUp': next = { row: Math.max(-1, row - 1), col }; break;
      case 'ArrowRight': next = { row, col: rtl ? Math.max(0, col - 1) : Math.min(columnCount - 1, col + 1) }; break;
      case 'ArrowLeft': next = { row, col: rtl ? Math.min(columnCount - 1, col + 1) : Math.max(0, col - 1) }; break;
      case 'Home': next = event.ctrlKey ? { row: 0, col: 0 } : { row, col: 0 }; break;
      case 'End': next = event.ctrlKey ? { row: rows.length - 1, col: columnCount - 1 } : { row, col: columnCount - 1 }; break;
      case 'PageDown': next = { row: Math.min(rows.length - 1, row + 10), col }; break;
      case 'PageUp': next = { row: Math.max(0, row - 10), col }; break;
      case 'Enter': {
        const current = rows[row];
        if (current && onOpen) { event.preventDefault(); onOpen(current); }
        return;
      }
      case ' ': {
        const current = rows[row];
        if (current && withCheck) { event.preventDefault(); toggle(rowKey(current)); }
        return;
      }
      default:
        return;
    }
    event.preventDefault();
    if (virtual && next.row >= 0) virtualizer.scrollToIndex(next.row);
    setFocus(next);
  };

  const tabIndexOf = (row: number, col: number) => ((focus.row === row && focus.col === col) || (focus.row === -1 && row === -1 && col === 0) ? 0 : -1);
  const register = (row: number, col: number) => (el: HTMLElement | null) => {
    if (el) cellRef.current.set(`${row}:${col}`, el); else cellRef.current.delete(`${row}:${col}`);
  };
  const allSelected = rows.length > 0 && rows.every((r) => selected.has(rowKey(r)));

  const sortBy = (id: string) => {
    if (!onSortChange) return;
    if (!sort || sort.column !== id) onSortChange({ column: id, descending: false });
    else if (!sort.descending) onSortChange({ column: id, descending: true });
    else onSortChange(null);
  };

  const resize = (id: string, width: number) => {
    if (!onChoicesChange) return;
    const current = shownChoices(shown, choices);
    onChoicesChange(current.map((c) => (c.id === id ? { ...c, width: Math.max(MIN_COLUMN_WIDTH, Math.round(width)) } : c)));
  };

  return (
    <div className="erp-grid-scroll" ref={scroller} data-virtual={virtual || undefined}>
      {/* The ARIA grid pattern on a native table (WAI-ARIA APG "Data Grid" examples use <table role="grid">): the
          arrow keys move between cells, so the table is one interactive widget. */}
      {/* eslint-disable-next-line jsx-a11y/no-noninteractive-element-to-interactive-role -- APG data grid on a table */}
      <table className="erp-grid" role="grid" aria-label={label} aria-rowcount={rows.length + 1} aria-colcount={columnCount} aria-multiselectable={selectionMode === 'multiple' || undefined}>
        <colgroup>
          {withCheck && <col className="erp-grid-check-col" />}
          {shown.map((c) => <col key={c.id} style={{ inlineSize: c.width }} />)}
        </colgroup>
        <thead>
          <tr aria-rowindex={1}>
            {withCheck && (
              <th scope="col" role="columnheader" className="erp-grid-check" ref={register(-1, 0)} tabIndex={tabIndexOf(-1, 0)} onKeyDown={(e) => move(e, -1, 0)}>
                {selectionMode === 'multiple' && (
                  <input type="checkbox" tabIndex={-1} checked={allSelected} aria-label={t('engine.grid.selectAll')}
                    onChange={() => {
                      const keys = rows.map(rowKey);
                      onSelectedChange?.(allSelected ? new Set([...selected].filter((k) => !keys.includes(k))) : new Set([...selected, ...keys]));
                    }} />
                )}
              </th>
            )}
            {shown.map((column, i) => {
              const col = i + (withCheck ? 1 : 0);
              const sorted = sort?.column === column.id ? (sort.descending ? 'descending' : 'ascending') : 'none';
              return (
                <th key={column.id} scope="col" role="columnheader" aria-sort={column.sortable ? sorted : undefined} data-align={column.align}
                  ref={register(-1, col)} tabIndex={tabIndexOf(-1, col)} onKeyDown={(e) => {
                    if ((e.key === 'Enter' || e.key === ' ') && column.sortable && e.target === e.currentTarget) { e.preventDefault(); sortBy(column.id); return; }
                    move(e, -1, col);
                  }}>
                  <span className="erp-grid-head">
                    {column.sortable && onSortChange ? (
                      <button type="button" className="erp-grid-sort" tabIndex={-1} onClick={() => sortBy(column.id)}
                        aria-label={t('engine.grid.sortBy', { column: column.header })}>
                        <span>{column.header}</span>
                        {sorted === 'ascending' ? <ArrowUp size={14} aria-hidden="true" /> : sorted === 'descending' ? <ArrowDown size={14} aria-hidden="true" /> : <ArrowUpDown size={14} aria-hidden="true" className="erp-muted" />}
                      </button>
                    ) : <span>{column.header}</span>}
                    {onChoicesChange && <Resizer column={column.header} width={column.width} onResize={(w) => resize(column.id, w)} />}
                  </span>
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {before > 0 && <tr aria-hidden="true" className="erp-grid-spacer"><td colSpan={columnCount} style={{ blockSize: before }} /></tr>}
          {items.map(({ index }) => {
            const row = rows[index];
            if (!row) return null;
            const key = rowKey(row);
            const isSelected = selected.has(key);
            return (
              <tr key={key} aria-rowindex={index + 2} aria-selected={withCheck ? isSelected : undefined} data-selected={isSelected || undefined}
                data-index={index} ref={virtual ? virtualizer.measureElement : undefined} onDoubleClick={() => onOpen?.(row)}>
                {withCheck && (
                  <td role="gridcell" className="erp-grid-check" ref={register(index, 0)} tabIndex={tabIndexOf(index, 0)} onKeyDown={(e) => move(e, index, 0)}>
                    <input type="checkbox" tabIndex={-1} checked={isSelected} onChange={() => toggle(key)} aria-label={t('engine.grid.selectRow', { row: rowLabel(row) })} />
                  </td>
                )}
                {shown.map((column, i) => {
                  const col = i + (withCheck ? 1 : 0);
                  const first = i === 0 && onOpen;
                  return (
                    <td key={column.id} role="gridcell" data-align={column.align} ref={register(index, col)} tabIndex={tabIndexOf(index, col)} onKeyDown={(e) => move(e, index, col)}
                      onFocus={() => { if (focus.row !== index || focus.col !== col) setFocus({ row: index, col }); }}>
                      {first ? (
                        <button type="button" className="erp-grid-open" tabIndex={-1} onClick={() => onOpen(row)} aria-label={t('engine.grid.open', { row: rowLabel(row) })}>
                          {column.cell(row)}
                        </button>
                      ) : column.cell(row)}
                    </td>
                  );
                })}
              </tr>
            );
          })}
          {after > 0 && <tr aria-hidden="true" className="erp-grid-spacer"><td colSpan={columnCount} style={{ blockSize: after }} /></tr>}
        </tbody>
      </table>
    </div>
  );
}

function shownChoices<Row>(shown: (GridColumn<Row> & { width: number })[], choices: ColumnChoice[] | undefined): ColumnChoice[] {
  const hidden = (choices ?? []).filter((c) => c.hidden);
  return [...shown.map((c) => ({ id: c.id, width: c.width })), ...hidden];
}

/**
 * The handle at a column's end edge (FR-UX-GRD-02): drag it, or focus it and use the arrow keys. It mirrors with the
 * page: in Urdu the end edge is on the left. A button, so keyboard and screen-reader users reach it like any control.
 */
function Resizer({ column, width, onResize }: { column: string; width: number; onResize: (width: number) => void }) {
  const { t } = useText();
  const { direction, numbers } = useLocaleSettings();
  const start = useRef<{ x: number; width: number } | null>(null);
  const sign = direction === 'rtl' ? -1 : 1;
  const onPointerDown = (e: PointerEvent<HTMLButtonElement>) => {
    e.preventDefault();
    e.currentTarget.setPointerCapture(e.pointerId);
    start.current = { x: e.clientX, width };
  };
  const onPointerMove = (e: PointerEvent<HTMLButtonElement>) => {
    if (start.current) onResize(start.current.width + sign * (e.clientX - start.current.x));
  };
  const end = () => { start.current = null; };
  return (
    <button type="button" className="erp-grid-resizer" aria-label={t('engine.grid.resize', { column, width: formatInteger(Math.round(width), numbers) })}
      onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={end} onPointerCancel={end}
      onKeyDown={(e) => {
        const grow = (e.key === 'ArrowRight' && sign > 0) || (e.key === 'ArrowLeft' && sign < 0);
        const shrink = (e.key === 'ArrowLeft' && sign > 0) || (e.key === 'ArrowRight' && sign < 0);
        if (grow || shrink) {
          e.preventDefault();
          e.stopPropagation();
          onResize(width + (grow ? RESIZE_STEP : -RESIZE_STEP));
        }
      }} />
  );
}
