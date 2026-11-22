// The grid (slice S1.12c Build item 16; spec 13 FR-UX-GRD-01, -02, -07, -09, -12, -13, -14): keyboard navigation,
// server sort requests, selection with a bulk bar, column order, width and choice, phone cards and the empty state.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { useState } from 'react';
import { I18nRoot } from '@erp/i18n';
import { arrange, DataGrid, VIRTUAL_FROM, type ColumnChoice, type GridColumn, type GridSort } from './Grid';

interface Item { id: string; code: string; name: string }
const rows: Item[] = [{ id: '1', code: 'KHI', name: 'Karachi' }, { id: '2', code: 'LHE', name: 'Lahore' }, { id: '3', code: 'ISB', name: 'Islamabad' }];
const columns: GridColumn<Item>[] = [
  { id: 'code', header: 'Code', sortable: true, cell: (r) => r.code },
  { id: 'name', header: 'Name', cell: (r) => r.name },
];

function Harness({ onOpen, onSort, initial = rows, onChoices }: { onOpen?: (r: Item) => void; onSort?: (s: GridSort | null) => void; initial?: Item[]; onChoices?: (c: ColumnChoice[]) => void }) {
  const [sort, setSort] = useState<GridSort | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [choices, setChoices] = useState<ColumnChoice[] | undefined>(undefined);
  return (
    <I18nRoot locale="en">
      <DataGrid<Item> label="Branches" columns={columns} rows={initial} rowKey={(r) => r.id} rowLabel={(r) => `${r.code} ${r.name}`} status="ready"
        sort={sort} onSortChange={(s) => { setSort(s); onSort?.(s); }} selectionMode="multiple" selected={selected} onSelectedChange={setSelected}
        bulkActions={(chosen) => <button type="button">Approve {chosen.size}</button>} choices={choices}
        onChoicesChange={(c) => { setChoices(c); onChoices?.(c); }} {...(onOpen ? { onOpen } : {})} />
    </I18nRoot>
  );
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-ADM-NAV-02 the grid', () => {
  it('asks the server for a sort: ascending, descending, then none (FR-UX-GRD-07)', () => {
    const onSort = vi.fn();
    render(<Harness onSort={onSort} />);
    const sortButton = screen.getByRole('button', { name: 'Sort by Code' });
    fireEvent.click(sortButton);
    expect(onSort).toHaveBeenLastCalledWith({ column: 'code', descending: false });
    expect(screen.getByRole('columnheader', { name: /Code/ }).getAttribute('aria-sort')).toBe('ascending');
    fireEvent.click(sortButton);
    expect(onSort).toHaveBeenLastCalledWith({ column: 'code', descending: true });
    fireEvent.click(sortButton);
    expect(onSort).toHaveBeenLastCalledWith(null);
  });

  it('moves between cells with the arrow keys, opens with Enter and selects with Space (FR-UX-GRD-12, -09)', () => {
    const onOpen = vi.fn();
    render(<Harness onOpen={onOpen} />);
    const grid = screen.getByRole('grid', { name: 'Branches' });
    const header = within(grid).getAllByRole('columnheader')[0]!;
    header.focus();
    fireEvent.keyDown(header, { key: 'ArrowDown' });
    const firstRowCheck = within(grid).getAllByRole('gridcell')[0]!;
    expect(document.activeElement).toBe(firstRowCheck);
    fireEvent.keyDown(firstRowCheck, { key: 'ArrowRight' });
    const codeCell = within(grid).getAllByRole('gridcell')[1]!;
    expect(document.activeElement).toBe(codeCell);
    expect(codeCell.tabIndex).toBe(0); // one cell in the tab order at a time
    fireEvent.keyDown(codeCell, { key: 'Enter' });
    expect(onOpen).toHaveBeenCalledWith(rows[0]);
    fireEvent.keyDown(codeCell, { key: ' ' });
    expect(screen.getByRole('region', { name: 'Actions for the selected rows' })).toBeTruthy();
    expect(screen.getByText('1 row selected')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Approve 1' })).toBeTruthy();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select all rows shown' }));
    expect(screen.getByText('3 rows selected')).toBeTruthy();
  });

  it('changes a column width by keyboard and keeps the person\'s column order and choice (FR-UX-GRD-02)', () => {
    const onChoices = vi.fn();
    render(<Harness onChoices={onChoices} />);
    const resizer = screen.getByRole('button', { name: /Width of column Code: 180 pixels/ });
    fireEvent.keyDown(resizer, { key: 'ArrowRight' });
    expect(onChoices).toHaveBeenLastCalledWith([{ id: 'code', width: 196 }, { id: 'name', width: 180 }]);
    expect(arrange(columns, [{ id: 'name', width: 300 }, { id: 'code', hidden: true }]).map((c) => [c.id, c.width])).toEqual([['name', 300]]);
    expect(arrange(columns, [{ id: 'name', width: 10 }]).map((c) => [c.id, c.width])).toEqual([['name', 64], ['code', 180]]);
  });

  it('draws only the rows in view from a hundred rows on (FR-UX-GRD-01)', () => {
    const many = Array.from({ length: VIRTUAL_FROM * 5 }, (_, i) => ({ id: String(i), code: `B${i}`, name: `Branch ${i}` }));
    render(<Harness initial={many} />);
    const drawn = within(screen.getByRole('grid')).getAllByRole('row').length;
    expect(drawn).toBeLessThan(many.length);
    expect(screen.getByRole('grid').getAttribute('aria-rowcount')).toBe(String(many.length + 1));
  });

  it('shows cards on a phone and the empty and loading states (FR-UX-GRD-13, -14)', () => {
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: query.includes('max-width'), addEventListener: () => undefined, removeEventListener: () => undefined }));
    const onOpen = vi.fn();
    render(<Harness onOpen={onOpen} />);
    const cards = screen.getByRole('list', { name: 'Branches' });
    expect(within(cards).getAllByRole('listitem')).toHaveLength(3);
    fireEvent.click(screen.getByRole('button', { name: 'Open LHE Lahore' }));
    expect(onOpen).toHaveBeenCalledWith(rows[1]);
    vi.unstubAllGlobals();

    const { rerender } = render(<I18nRoot locale="en"><DataGrid<Item> label="Empty" columns={columns} rows={[]} rowKey={(r) => r.id} rowLabel={(r) => r.code} status="ready" /></I18nRoot>);
    expect(screen.getByText('Nothing to show')).toBeTruthy();
    rerender(<I18nRoot locale="en"><DataGrid<Item> label="Loading" columns={columns} rows={[]} rowKey={(r) => r.id} rowLabel={(r) => r.code} status="loading" /></I18nRoot>);
    expect(document.querySelector('[aria-busy="true"]')).toBeTruthy();
  });
});
