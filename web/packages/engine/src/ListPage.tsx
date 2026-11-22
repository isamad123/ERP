// A list page made from a table's metadata (slice S1.12c Build item 17; FR-ADM-NAV-02, S1.12 rulings Q10 and Q12): the
// title, saved views, search, typed filters, the company in hand or "all my companies", the grid, and a create button
// only for people who may create. Search, filters and sort run on the server (Build item 14); export comes with the
// export centre (S1.16) and has its place in the toolbar.
import { useEffect, useMemo, useState, type FormEvent, type ReactNode } from 'react';
import { keepPreviousData, useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowDown, ArrowUp, Columns3, Plus, Save, Trash2 } from 'lucide-react';
import { Button, CheckboxField, Dialog, ErrorPanel, EmptyState, IconButton, PageHeader, SelectField, SwitchField, TextField, type Option } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import { DataGrid, type ColumnChoice, type GridColumn, type GridSort } from './Grid';
import { isProblem, queryString, tableClient, useEngineApi, type Row } from './data';
import { decimalFields, fieldOf, type TableMetadata } from './metadata';
import { useLabel } from './labels';
import { Value } from './values';
import { CustomValue, CUSTOM_PREFIX, customOf, useCustomFields } from './custom';

export const PAGE_SIZE = 100;

/** What a saved view keeps (cfg.saved_views.definition). */
export interface ViewDefinition {
  search?: string;
  filters?: Record<string, string>;
  sort?: string | null;
  companies?: 'active' | 'all';
  columns?: ColumnChoice[];
}

export interface SavedView {
  id: string;
  name: string;
  definition: ViewDefinition;
  role: { id: string; code: string; name: Record<string, string> } | null;
  mine: boolean;
  ownerName: string;
  rowVersion: number;
}

export interface ListPageProps {
  metadata: TableMetadata;
  /** Columns in their first order (default: the metadata's list fields). */
  columns?: string[];
  /** Company scope (ruling Q10): the company in hand and the person's companies, for company tables. */
  companies?: { activeId: string | null; options: Option[] };
  canCreate: boolean;
  canShare: boolean;
  onCreate?: () => void;
  onOpen: (row: Row) => void;
  rowLabel: (row: Row) => string;
  /** Cells a screen draws itself (field → cell). */
  cells?: Record<string, (row: Row) => ReactNode>;
  /** Shown next to the title (e.g. what the list holds). */
  description?: string;
}

/** The server's sort parameter from the grid's sort, and back. */
export const sortParam = (sort: GridSort | null) => (sort ? (sort.descending ? '-' : '') + sort.column : null);
export const sortOf = (param: string | null | undefined): GridSort | null => (param ? { column: param.replace(/^-/, ''), descending: param.startsWith('-') } : null);

export function ListPage({ metadata, columns, companies, canCreate, canShare, onCreate, onOpen, rowLabel, cells, description }: ListPageProps) {
  const { t } = useText();
  const label = useLabel();
  const api = useEngineApi();
  const client = useMemo(() => tableClient(api, metadata, decimalFields(metadata)), [api, metadata]);
  const title = label(metadata.labelKey);
  useDocumentTitle(title);

  const [definition, setDefinition] = useState<ViewDefinition>({ companies: 'active' });
  const [draft, setDraft] = useState('');
  const [viewId, setViewId] = useState<string>('standard');
  const scopeAll = definition.companies === 'all' || !companies?.activeId;
  const filterFields = metadata.fields.filter((f) => f.filter && (f.kind === 'Bool' || f.kind === 'Enum'));

  const params = {
    companyId: metadata.companyScoped && !scopeAll ? companies?.activeId ?? null : null,
    search: definition.search ?? null,
    sort: definition.sort ?? null,
    ...(definition.filters ?? {}),
  };
  const list = useInfiniteQuery({
    queryKey: ['engine', metadata.table, 'list', params],
    queryFn: ({ pageParam, signal }) => client.list({ ...params, after: pageParam ?? null, limit: PAGE_SIZE }, signal),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
  });
  const rows = useMemo(() => list.data?.pages.flatMap((p) => p.items) ?? [], [list.data]);

  // Custom fields can be chosen as columns (S1.15b): display only, no sort, hidden until the person picks them.
  const customFields = useCustomFields(metadata.table, params.companyId, metadata.fields.some((f) => f.field === 'custom'));
  const { locale } = useLocaleSettings();
  const companyNames = useMemo(() => new Map((companies?.options ?? []).map((o) => [o.id, o.label])), [companies]);
  const customColumns = useMemo<GridColumn<Row>[]>(() => (customFields.data ?? []).filter((f) => !f.retired).map((f) => ({
    id: `${CUSTOM_PREFIX}${f.fieldKey}`,
    header: f.label[locale] ?? f.label.en ?? f.fieldKey,
    align: f.dataType === 'number' || f.dataType === 'decimal' ? 'end' as const : 'start' as const,
    width: 180,
    hiddenByDefault: true,
    cell: (row: Row) => <CustomValue field={f} value={customOf(row)[f.fieldKey]} />,
  })), [customFields.data, locale]);
  const gridColumns = useMemo<GridColumn<Row>[]>(() => [...(columns ?? metadata.listFields).flatMap((name) => {
    const field = fieldOf(metadata, name);
    if (!field) return [];
    const own = cells?.[name];
    return [{
      id: name,
      header: label(field.labelKey),
      sortable: !!field.sortable,
      align: ['Int4', 'BigInteger', 'Money', 'Qty', 'Rate', 'Pct', 'Numeric'].includes(field.kind) ? 'end' as const : 'start' as const,
      width: field.kind === 'I18n' ? 260 : field.kind === 'Bool' ? 140 : 180,
      cell: own ?? ((row: Row) => field.references === 'org.companies' && typeof row[name] === 'string' && companyNames.has(row[name] as string)
        ? <bdi>{companyNames.get(row[name] as string)}</bdi>
        : <Value field={field} value={row[name]} label={label} />),
    }];
  }), ...customColumns], [columns, metadata, label, cells, companyNames, customColumns]);

  const apply = (next: ViewDefinition, id = 'custom') => {
    setDefinition(next);
    setViewId(id);
  };
  const submit = (event: FormEvent) => {
    event.preventDefault();
    apply({ ...definition, search: draft.trim() || undefined });
  };

  const problem = list.error && isProblem(list.error) ? list.error : null;
  const filtered = !!definition.search || Object.keys(definition.filters ?? {}).length > 0;
  return (
    <div className="erp-page">
      <PageHeader title={title} {...(description ? { description } : {})}
        actions={canCreate && onCreate ? <Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={onCreate}>{t('engine.list.create', { entity: label(`${metadata.labelKey}.one`) })}</Button> : undefined} />
      <div className="erp-list-toolbar">
        <SavedViews listKey={metadata.table} current={viewId} definition={definition} canShare={canShare}
          onApply={(view) => { setDraft(view?.definition.search ?? ''); apply(view?.definition ?? { companies: 'active' }, view?.id ?? 'standard'); }} />
        <ColumnsChooser columns={gridColumns} choices={definition.columns} onChange={(columnsChoice) => apply({ ...definition, columns: columnsChoice }, viewId)} />
      </div>
      <form className="erp-filters" role="search" aria-label={t('engine.list.filters', { list: title })} onSubmit={submit}>
        {metadata.search && metadata.search.length > 0 && (
          <>
            <TextField type="search" label={t('engine.list.search')} help={t('engine.list.searchHelp')} value={draft} onChange={setDraft} />
            <Button type="submit" variant="secondary">{t('engine.list.searchButton')}</Button>
          </>
        )}
        {metadata.companyScoped && companies && companies.options.length > 1 && (
          <SelectField label={t('engine.list.companies')} value={scopeAll ? 'all' : 'active'}
            onChange={(v) => apply({ ...definition, companies: v === 'all' ? 'all' : 'active' })}
            options={[{ id: 'active', label: companyNames.get(companies.activeId ?? '') ?? t('engine.list.activeCompany') }, { id: 'all', label: t('engine.list.allCompanies') }]} />
        )}
        {filterFields.map((field) => (
          <SelectField key={field.field} label={label(field.labelKey)} value={definition.filters?.[field.field] ?? 'any'}
            onChange={(v) => {
              const rest = Object.fromEntries(Object.entries(definition.filters ?? {}).filter(([k]) => k !== field.field));
              apply({ ...definition, filters: v === 'any' ? rest : { ...rest, [field.field]: v } });
            }}
            options={[{ id: 'any', label: t('engine.filter.any') }, ...(field.kind === 'Bool'
              ? [{ id: 'true', label: field.field === 'isActive' ? t('ui.status.active') : t('engine.value.yes') }, { id: 'false', label: field.field === 'isActive' ? t('ui.status.inactive') : t('engine.value.no') }]
              : (field.options ?? []).map((o) => ({ id: o.value, label: label(o.labelKey) })))]} />
        ))}
      </form>
      <DataGrid<Row> label={title} columns={gridColumns} rows={rows} rowKey={(r) => r.id} rowLabel={rowLabel}
        status={list.isPending ? 'loading' : list.isError && rows.length === 0 ? 'error' : 'ready'}
        error={<ErrorPanel message={problem?.status === 403 ? t('engine.list.denied') : t('engine.list.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />}
        empty={<EmptyState title={filtered ? t('engine.list.noMatches') : t('engine.list.empty', { entity: title })}
          action={!filtered && canCreate && onCreate ? <Button variant="primary" onPress={onCreate}>{t('engine.list.create', { entity: label(`${metadata.labelKey}.one`) })}</Button> : undefined}>
          {filtered ? t('engine.list.noMatches.body') : undefined}
        </EmptyState>}
        choices={definition.columns} onChoicesChange={(choice) => apply({ ...definition, columns: choice }, viewId)}
        sort={sortOf(definition.sort)} onSortChange={(sort) => apply({ ...definition, sort: sortParam(sort) })}
        hasMore={list.hasNextPage} loadingMore={list.isFetchingNextPage} onLoadMore={() => { void list.fetchNextPage(); }}
        onOpen={onOpen} />
    </div>
  );
}

export function useDocumentTitle(title: string) {
  const { t } = useText();
  useEffect(() => { document.title = `${title} – ${t('product.name')}`; }, [title, t]);
}

/** Column choice and order (FR-UX-GRD-02): a checkbox per column and buttons to move it, all by keyboard. */
export function ColumnsChooser<Row>({ columns, choices, onChange }: { columns: GridColumn<Row>[]; choices: ColumnChoice[] | undefined; onChange: (choices: ColumnChoice[]) => void }) {
  const { t } = useText();
  const [open, setOpen] = useState(false);
  const ordered: ColumnChoice[] = [
    ...(choices ?? []).filter((c) => columns.some((x) => x.id === c.id)),
    ...columns.filter((c) => !(choices ?? []).some((x) => x.id === c.id)).map((c) => ({ id: c.id, ...(c.hiddenByDefault ? { hidden: true } : {}) })),
  ];
  const header = (id: string) => columns.find((c) => c.id === id)?.header ?? id;
  const moveBy = (index: number, by: number) => {
    const next = [...ordered];
    const [item] = next.splice(index, 1);
    if (!item) return;
    next.splice(index + by, 0, item);
    onChange(next);
  };
  const visible = ordered.filter((c) => !c.hidden).length;
  return (
    <>
      <Button variant="secondary" icon={<Columns3 size={18} aria-hidden="true" />} onPress={() => setOpen(true)}>{t('engine.columns.button')}</Button>
      <Dialog title={t('engine.columns.title')} isOpen={open} onOpenChange={setOpen} drawer
        actions={(close) => (
          <>
            <Button variant="quiet" onPress={() => onChange(columns.map((c) => ({ id: c.id, ...(c.hiddenByDefault ? { hidden: true } : {}) })))}>{t('engine.columns.reset')}</Button>
            <Button variant="primary" onPress={close}>{t('engine.columns.done')}</Button>
          </>
        )}>
        <p className="erp-muted">{t('engine.columns.help')}</p>
        <ol className="erp-columns-list">
          {ordered.map((choice, index) => (
            <li key={choice.id} className="erp-row">
              <CheckboxField label={header(choice.id)} isSelected={!choice.hidden} isDisabled={!choice.hidden && visible === 1}
                onChange={(on) => onChange(ordered.map((c) => (c.id === choice.id ? { ...c, hidden: !on } : c)))} />
              <span className="erp-row">
                <IconButton label={t('engine.columns.up', { column: header(choice.id) })} icon={<ArrowUp size={16} aria-hidden="true" />} isDisabled={index === 0} onPress={() => moveBy(index, -1)} />
                <IconButton label={t('engine.columns.down', { column: header(choice.id) })} icon={<ArrowDown size={16} aria-hidden="true" />} isDisabled={index === ordered.length - 1} onPress={() => moveBy(index, 1)} />
              </span>
            </li>
          ))}
        </ol>
      </Dialog>
    </>
  );
}

/**
 * Saved views (FR-ADM-NAV-02, ruling Q12): the person's own views and the views shared with a role they hold, kept on
 * the server (cfg.saved_views). Applying a view sets search, filters, sort, companies and columns; the person saves the
 * list as it is now under a name, shares it with a role when allowed, and deletes their own views.
 */
export function SavedViews({ listKey, current, definition, canShare, onApply }: { listKey: string; current: string; definition: ViewDefinition; canShare: boolean; onApply: (view: SavedView | null) => void }) {
  const { t, server } = useText();
  const api = useEngineApi();
  const queryClient = useQueryClient();
  const key = ['engine', 'views', listKey];
  const views = useQuery({ queryKey: key, queryFn: async () => JSON.parse(await api.send('GET', `/api/cfg/saved-views${queryString({ list: listKey })}`)) as SavedView[] });
  const [saving, setSaving] = useState(false);
  const [name, setName] = useState('');
  const [share, setShare] = useState(false);
  const [role, setRole] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const roles = useQuery({
    queryKey: ['engine', 'views', 'roles'],
    queryFn: async () => JSON.parse(await api.send('GET', '/api/cfg/saved-views/roles')) as { id: string; code: string; name: Record<string, string> }[],
    enabled: saving && canShare,
  });
  const { locale } = useLocaleSettings();
  const chosen = (views.data ?? []).find((v) => v.id === current) ?? null;

  const save = async (close: () => void) => {
    setBusy(true);
    setError(null);
    try {
      const body = JSON.stringify({ listKey, name: name.trim(), definition, roleId: share ? role : null });
      const saved = JSON.parse(await api.send('POST', '/api/cfg/saved-views', body)) as SavedView;
      await queryClient.invalidateQueries({ queryKey: key });
      onApply(saved);
      close();
    } catch (e) {
      setError(isProblem(e) ? (server(Object.values(e.fieldErrors)[0]?.[0] ?? e.key) ?? t('engine.views.saveError')) : t('engine.views.saveError'));
    } finally {
      setBusy(false);
    }
  };
  const remove = async (view: SavedView) => {
    await api.send('DELETE', `/api/cfg/saved-views/${encodeURIComponent(view.id)}${queryString({ rowVersion: view.rowVersion })}`);
    await queryClient.invalidateQueries({ queryKey: key });
    onApply(null);
  };

  const options: Option[] = [
    { id: 'standard', label: t('engine.views.standard') },
    ...(current === 'custom' ? [{ id: 'custom', label: t('engine.views.custom') }] : []),
    ...(views.data ?? []).map((v) => ({ id: v.id, label: v.role ? t('engine.views.sharedName', { name: v.name, role: v.role.name[locale] ?? v.role.name.en ?? v.role.code }) : v.name })),
  ];
  return (
    <div className="erp-row erp-views">
      <SelectField label={t('engine.views.label')} value={current} options={options}
        onChange={(id) => { if (id === 'standard') onApply(null); else if (id !== 'custom') { const v = views.data?.find((x) => x.id === id); if (v) onApply(v); } }} />
      <Button variant="secondary" icon={<Save size={18} aria-hidden="true" />} onPress={() => { setName(''); setShare(false); setRole(null); setError(null); setSaving(true); }}>{t('engine.views.save')}</Button>
      {chosen?.mine && (
        <Dialog role="alertdialog" title={t('engine.views.deleteTitle', { name: chosen.name })}
          trigger={<Button variant="quiet" icon={<Trash2 size={18} aria-hidden="true" />}>{t('engine.views.delete')}</Button>}
          actions={(close) => (
            <>
              <Button variant="secondary" onPress={close}>{t('common.cancel')}</Button>
              <Button variant="danger" onPress={() => { void remove(chosen).then(close); }}>{t('engine.views.deleteConfirm')}</Button>
            </>
          )}>
          <p>{chosen.role ? t('engine.views.deleteShared') : t('engine.views.deleteBody')}</p>
        </Dialog>
      )}
      <Dialog title={t('engine.views.saveTitle')} isOpen={saving} onOpenChange={setSaving}
        actions={(close) => (
          <>
            <Button variant="secondary" onPress={close}>{t('common.cancel')}</Button>
            <Button variant="primary" isPending={busy} isDisabled={!name.trim() || (share && !role)} onPress={() => { void save(close); }}>{t('engine.views.saveConfirm')}</Button>
          </>
        )}>
        <TextField label={t('engine.views.name')} isRequired value={name} onChange={setName} maxLength={80} error={error} />
        {canShare && (
          <>
            <SwitchField label={t('engine.views.share')} isSelected={share} onChange={setShare} />
            {share && (
              <SelectField label={t('engine.views.role')} isRequired value={role} onChange={setRole}
                options={(roles.data ?? []).map((r) => ({ id: r.id, label: r.name[locale] ?? r.name.en ?? r.code, detail: r.code }))} />
            )}
          </>
        )}
      </Dialog>
    </div>
  );
}
