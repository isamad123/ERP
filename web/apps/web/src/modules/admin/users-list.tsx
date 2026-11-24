// The user list (slice S1.13a Build items 1 and 5; FR-ADM-USR-01, -08, -09, FR-ADM-NAV-02): name, username, e-mail, status,
// roles, companies, last sign-in and second-factor state, with filters on each, saved views, the seat bar of the licence
// (shown only to licence readers) and bulk actions over a selection with a count first and one result per user.
import { useMemo, useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router';
import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { Plus, ShieldCheck } from 'lucide-react';
import { ColumnsChooser, DataGrid, LazyExportButton, orderedChoices, SavedViews, useDocumentTitle, type GridColumn, type ViewDefinition } from '@erp/engine';
import { Banner, Button, EmptyState, ErrorPanel, Link, PageHeader, SelectField, TextField } from '@erp/ui';
import { formatDateTime, formatInteger, useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { seatsOf, USER_STATUSES, users, type UserRow } from '../../api/users';
import { nameOf } from '../../api/workflow';
import { companyName, useShell } from '../../shell/context';
import { UserStatusChip, listKey } from './users-parts';
import { BulkUsers } from './users-bulk';

export const USERS_PATH = '/admin/users';
const PAGE = 100;
const VIEW_KEY = 'iam.users.overview';

export function UserList() {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const navigate = useNavigate();
  const { me, can, writable, activeCompany } = useShell();
  useDocumentTitle(t('users.title'));
  const [definition, setDefinition] = useState<ViewDefinition>({});
  const [viewId, setViewId] = useState('standard');
  const [draft, setDraft] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [bulk, setBulk] = useState(false);
  const filters = definition.filters ?? {};

  const params = { search: definition.search, status: filters.status, roleId: filters.roleId, companyId: filters.companyId, mfa: filters.mfa === undefined ? undefined : filters.mfa === 'yes' };
  const list = useInfiniteQuery({
    queryKey: [...listKey, params],
    queryFn: ({ pageParam, signal }) => users.list({ ...params, cursor: pageParam, limit: PAGE }, signal),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
    refetchOnMount: 'always',
  });
  const rows = useMemo(() => list.data?.pages.flatMap((p) => p.items) ?? [], [list.data]);
  const roles = useQuery({ queryKey: ['iam', 'roles', 'options'], queryFn: users.roles, enabled: can('iam.roles.read'), staleTime: 60_000 });
  const companyNames = useMemo(() => new Map(me.companies.map((c) => [c.id, companyName(c, locale)])), [me.companies, locale]);
  const licence = useQuery({ queryKey: ['cfg', 'licence', 'limits'], queryFn: users.licence, enabled: can('cfg.licence.read'), refetchOnMount: 'always' });
  const seats = seatsOf(licence.data);

  const apply = (next: ViewDefinition, id = 'custom') => { setDefinition(next); setViewId(id); setSelected(new Set()); };
  const setFilter = (name: string, value: string) => {
    const rest = Object.fromEntries(Object.entries(filters).filter(([k]) => k !== name));
    apply({ ...definition, filters: value === 'any' ? rest : { ...rest, [name]: value } });
  };
  const submit = (event: FormEvent) => { event.preventDefault(); apply({ ...definition, search: draft.trim() || undefined }); };

  const columns = useMemo<GridColumn<UserRow>[]>(() => [
    { id: 'displayName', header: t('users.col.name'), width: 200, cell: (r) => <bdi>{r.displayName}</bdi> },
    { id: 'username', header: t('users.col.username'), width: 160, cell: (r) => <bdi className="erp-code">{r.username}</bdi> },
    { id: 'email', header: t('users.col.email'), width: 240, cell: (r) => <bdi>{r.email}</bdi> },
    { id: 'status', header: t('users.col.status'), width: 130, cell: (r) => <UserStatusChip status={r.status} /> },
    { id: 'roles', header: t('users.col.roles'), width: 240, cell: (r) => r.roles.length === 0 ? <span className="erp-muted">{t('users.none')}</span> : <bdi>{r.roles.map((x) => nameOf(x.name, locale) || x.code).join(', ')}</bdi> },
    { id: 'companies', header: t('users.col.companies'), width: 200, cell: (r) => r.allCompanies ? t('users.allCompanies') : r.companyIds.length === 0 ? <span className="erp-muted">{t('users.none')}</span> : <bdi>{r.companyIds.map((c) => companyNames.get(c) ?? c).join(', ')}</bdi> },
    { id: 'lastLoginAt', header: t('users.col.lastSignIn'), width: 180, cell: (r) => r.lastLoginAt ? formatDateTime(r.lastLoginAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) : <span className="erp-muted">{t('users.never')}</span> },
    { id: 'hasMfa', header: t('users.col.mfa'), width: 140, cell: (r) => r.hasMfa ? <span><ShieldCheck size={14} aria-hidden="true" /> {t('users.mfa.on')}</span> : <span className="erp-muted">{t('users.mfa.off')}</span> },
  ], [t, locale, timeZone, numbers, companyNames]);

  // The bulk export of the list as it is now (S1.16b, FR-ADM-USR-09): the shown columns in order and the same search and filters.
  const exportColumns = useMemo(() => orderedChoices(columns, definition.columns).filter((c) => !c.hidden)
    .map((c) => ({ field: c.id === 'companies' ? 'companyIds' : c.id, label: columns.find((x) => x.id === c.id)?.header ?? c.id })), [columns, definition.columns]);
  const exportQuery = useMemo(() => ({ status: filters.status, roleId: filters.roleId, companyId: filters.companyId, mfa: filters.mfa === undefined ? undefined : filters.mfa === 'yes' }), [filters.status, filters.roleId, filters.companyId, filters.mfa]);
  const problem = list.error instanceof ApiProblem ? list.error : null;
  const filtered = !!definition.search || Object.keys(filters).length > 0;
  const canCreate = writable && can('iam.users.create');
  const create = () => { void navigate(`${USERS_PATH}/new`); };
  const anySelected = rows.filter((r) => selected.has(r.id));

  return (
    <div className="erp-page">
      <PageHeader title={t('users.title')} description={t('users.description')}
        actions={(
          <>
            {can('itg.exports.run') && <LazyExportButton source="iam.user_overview" title={t('users.title')} columns={exportColumns} query={exportQuery} search={definition.search ?? null} companyId={activeCompany?.id ?? null} />}
            {canCreate && <Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={create}>{t('users.create')}</Button>}
          </>
        )} />
      {seats && (
        <div className="erp-seats" role="group" aria-label={t('users.seats.label')}>
          <p><strong>{t('users.seats', { used: formatInteger(seats.used, numbers), max: formatInteger(seats.max, numbers) })}</strong></p>
          <meter className="erp-meter" min={0} max={seats.max} value={Math.min(seats.used, seats.max)} aria-label={t('users.seats.label')} />
          {seats.percent >= 90 && (
            <Banner tone={seats.used >= seats.max ? 'danger' : 'warning'} title={seats.used >= seats.max ? t('users.seats.full') : t('users.seats.almost', { percent: formatInteger(seats.percent, numbers) })}
              action={<Link href="/settings/licence">{t('users.seats.licence')}</Link>}>
              {t('users.seats.body')}
            </Banner>
          )}
        </div>
      )}
      <div className="erp-list-toolbar">
        <SavedViews listKey={VIEW_KEY} current={viewId} definition={definition} canShare={can('cfg.saved_views.share')}
          onApply={(view) => { setDraft(view?.definition.search ?? ''); apply(view?.definition ?? {}, view?.id ?? 'standard'); }} />
        <ColumnsChooser columns={columns} choices={definition.columns} onChange={(choice) => apply({ ...definition, columns: choice }, viewId)} />
      </div>
      <form className="erp-filters" role="search" aria-label={t('users.filters')} onSubmit={submit}>
        <TextField type="search" label={t('users.search')} help={t('users.search.help')} value={draft} onChange={setDraft} />
        <Button type="submit" variant="secondary">{t('users.search.button')}</Button>
        <SelectField label={t('users.col.status')} value={filters.status ?? 'any'} onChange={(v) => setFilter('status', v)}
          options={[{ id: 'any', label: t('users.any') }, ...USER_STATUSES.map((s) => ({ id: s, label: t(`users.status.${s}`) }))]} />
        {(roles.data?.length ?? 0) > 0 && (
          <SelectField label={t('users.col.roles')} value={filters.roleId ?? 'any'} onChange={(v) => setFilter('roleId', v)}
            options={[{ id: 'any', label: t('users.any') }, ...(roles.data ?? []).map((r) => ({ id: r.id, label: nameOf(r.nameI18n, locale) || r.code, detail: r.code }))]} />
        )}
        {me.companies.length > 1 && (
          <SelectField label={t('users.col.companies')} value={filters.companyId ?? 'any'} onChange={(v) => setFilter('companyId', v)}
            options={[{ id: 'any', label: t('users.any') }, ...me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }))]} />
        )}
        <SelectField label={t('users.col.mfa')} value={filters.mfa ?? 'any'} onChange={(v) => setFilter('mfa', v)}
          options={[{ id: 'any', label: t('users.any') }, { id: 'yes', label: t('users.mfa.on') }, { id: 'no', label: t('users.mfa.off') }]} />
      </form>
      <DataGrid<UserRow> label={t('users.title')} columns={columns} rows={rows} rowKey={(r) => r.id} rowLabel={(r) => r.displayName}
        status={list.isPending ? 'loading' : list.isError && rows.length === 0 ? 'error' : 'ready'}
        error={<ErrorPanel message={problem?.status === 403 ? t('users.denied') : t('users.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />}
        empty={<EmptyState title={filtered ? t('users.noMatches') : t('users.empty')} action={!filtered && canCreate ? <Button variant="primary" onPress={create}>{t('users.create')}</Button> : undefined}>
          {filtered ? t('users.noMatches.body') : t('users.empty.body')}
        </EmptyState>}
        choices={definition.columns} onChoicesChange={(choice) => apply({ ...definition, columns: choice }, viewId)}
        hasMore={list.hasNextPage} loadingMore={list.isFetchingNextPage} onLoadMore={() => { void list.fetchNextPage(); }}
        selectionMode={writable && can('iam.users.update') ? 'multiple' : 'none'} selected={selected} onSelectedChange={setSelected}
        bulkActions={() => <Button variant="secondary" onPress={() => setBulk(true)}>{t('users.bulk.open', { count: formatInteger(selected.size, numbers) })}</Button>}
        onOpen={(row) => { void navigate(`${USERS_PATH}/${row.id}`); }} />
      {bulk && <BulkUsers people={anySelected} onClose={() => setBulk(false)} onDone={() => { setSelected(new Set()); void list.refetch(); }} />}
    </div>
  );
}
