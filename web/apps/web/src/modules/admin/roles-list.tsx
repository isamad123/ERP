// The role list (slice S1.13b Build item 7; FR-ADM-ROL-02, -03, FR-ADM-NAV-03): name, code, shipped or custom, status, user
// count and permission count; create (empty, copied from a role, copied from a shipped role), compare, open.
import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { useQueries, useQuery } from '@tanstack/react-query';
import { GitCompare, Plus } from 'lucide-react';
import { DataGrid, useDocumentTitle, type GridColumn } from '@erp/engine';
import { Banner, Button, Dialog, EmptyState, ErrorPanel, PageHeader, SelectField, TextField } from '@erp/ui';
import { formatInteger, useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { access, type RoleRecord, type RoleSummary } from '../../api/access';
import { nameOf } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { useProblemText } from './users-parts';
import { ActiveChip, ROLES_PATH, SourceChip, isProtectedRole, rolesKey, summaryKey, useRoleAnswer } from './roles-parts';

interface Row { role: RoleRecord; summary: RoleSummary | undefined }

export function RoleList() {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  const navigate = useNavigate();
  const { can, writable } = useShell();
  useDocumentTitle(t('roles.title'));
  const [search, setSearch] = useState('');
  const [creating, setCreating] = useState(false);
  const roles = useQuery({ queryKey: rolesKey, queryFn: access.roles, refetchOnMount: 'always' });
  const summaries = useQueries({ queries: (roles.data ?? []).map((r) => ({ queryKey: summaryKey(r.id), queryFn: () => access.summary(r.id), staleTime: 15_000 })) });
  const rows = useMemo<Row[]>(() => {
    const needle = search.trim().toLowerCase();
    return (roles.data ?? [])
      .map((role, i) => ({ role, summary: summaries[i]?.data }))
      .filter(({ role }) => !needle || role.code.toLowerCase().includes(needle) || (nameOf(role.nameI18n, locale) || '').toLowerCase().includes(needle));
  }, [roles.data, summaries, search, locale]);

  const canManage = writable && can('iam.roles.manage');
  const columns = useMemo<GridColumn<Row>[]>(() => [
    { id: 'name', header: t('roles.col.name'), width: 220, cell: (r) => <bdi>{nameOf(r.role.nameI18n, locale) || r.role.code}</bdi> },
    { id: 'code', header: t('roles.col.code'), width: 180, cell: (r) => <bdi className="erp-code">{r.role.code}</bdi> },
    { id: 'kind', header: t('roles.col.kind'), width: 130, cell: (r) => <span className="erp-row"><SourceChip role={r.role} />{isProtectedRole(r.role) && <span className="erp-small erp-muted">{t('roles.protected')}</span>}</span> },
    { id: 'status', header: t('roles.col.status'), width: 130, cell: (r) => <ActiveChip isActive={r.role.isActive} /> },
    { id: 'users', header: t('roles.col.users'), width: 110, cell: (r) => r.summary ? formatInteger(r.summary.userCount, numbers) : '…' },
    { id: 'permissions', header: t('roles.col.permissions'), width: 130, cell: (r) => r.summary ? formatInteger(r.summary.permissions.length, numbers) : '…' },
  ], [t, locale, numbers]);

  const problem = roles.error instanceof ApiProblem ? roles.error : null;
  return (
    <div className="erp-page">
      <PageHeader title={t('roles.title')} description={t('roles.description')}
        actions={(
          <span className="erp-row erp-wrap">
            {can('iam.permissions.read') && <Button variant="secondary" onPress={() => { void navigate(`${ROLES_PATH}/catalogue`); }}>{t('roles.catalogue.open')}</Button>}
            <Button variant="secondary" icon={<GitCompare size={18} aria-hidden="true" />} onPress={() => { void navigate(`${ROLES_PATH}/compare`); }}>{t('roles.compare')}</Button>
            {canManage && <Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={() => setCreating(true)}>{t('roles.create')}</Button>}
          </span>
        )} />
      <TextField type="search" label={t('roles.search')} value={search} onChange={setSearch} />
      <DataGrid<Row> label={t('roles.title')} columns={columns} rows={rows} rowKey={(r) => r.role.id} rowLabel={(r) => nameOf(r.role.nameI18n, locale) || r.role.code}
        status={roles.isPending ? 'loading' : roles.isError ? 'error' : 'ready'}
        error={<ErrorPanel message={problem?.status === 403 ? t('roles.denied') : t('roles.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void roles.refetch(); }}>{t('common.retry')}</Button>} />}
        empty={<EmptyState title={search ? t('roles.noMatches') : t('roles.empty')}>{search ? t('roles.noMatches.body') : t('roles.empty.body')}</EmptyState>}
        onOpen={(row) => { void navigate(`${ROLES_PATH}/${row.role.id}`); }} />
      {creating && <CreateRole onClose={() => setCreating(false)} />}
    </div>
  );
}

type Mode = 'empty' | 'role' | 'shipped';

/** New role: empty, copied from any role, or copied from a shipped (baseline) role by its code (FR-ADM-ROL-02, -03). */
export function CreateRole({ onClose, from }: { onClose: () => void; from?: RoleRecord | undefined }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const navigate = useNavigate();
  const problemText = useProblemText();
  const answer = useRoleAnswer();
  const roles = useQuery({ queryKey: rolesKey, queryFn: access.roles, staleTime: 60_000 });
  const [mode, setMode] = useState<Mode>(from ? 'role' : 'empty');
  const [source, setSource] = useState<string | null>(from?.id ?? null);
  const [code, setCode] = useState('');
  const [en, setEn] = useState('');
  const [ur, setUr] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const all = roles.data ?? [];
  const shipped = all.filter((r) => r.baselineCode);
  const options = (mode === 'shipped' ? shipped : all).map((r) => ({ id: mode === 'shipped' ? (r.baselineCode ?? r.code) : r.id, label: nameOf(r.nameI18n, locale) || r.code, detail: r.code }));
  const ready = code.trim().length > 0 && en.trim().length > 0 && (mode === 'empty' || !!source);

  const create = async () => {
    setBusy(true);
    setError(null);
    try {
      const name = { en: en.trim(), ...(ur.trim() ? { ur: ur.trim() } : {}) };
      const result = await access.createRole({ code: code.trim(), name, ...(mode === 'role' && source ? { copyFromRoleId: source } : {}), ...(mode === 'shipped' && source ? { copyFromBaseline: source } : {}) });
      answer(result, 'roles.created');
      onClose();
      if (result.id) void navigate(`${ROLES_PATH}/${result.id}`);
    } catch (e) {
      setError(problemText(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog title={t('roles.create.title')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" isPending={busy} isDisabled={!ready} onPress={() => { void create(); }}>{t('roles.create')}</Button>
        </>
      )}>
      {error && <Banner tone="danger" title={t('roles.create.failed')}>{error}</Banner>}
      <TextField label={t('roles.col.code')} help={t('roles.code.help')} isRequired isCode value={code} onChange={setCode} maxLength={64} />
      <TextField label={t('roles.name.en')} isRequired value={en} onChange={setEn} maxLength={200} />
      <TextField label={t('roles.name.ur')} value={ur} onChange={setUr} maxLength={200} />
      <SelectField label={t('roles.create.start')} value={mode} onChange={(v) => { setMode(v as Mode); setSource(null); }}
        options={[{ id: 'empty', label: t('roles.create.empty') }, { id: 'role', label: t('roles.create.copyRole') }, { id: 'shipped', label: t('roles.create.copyShipped') }]} />
      {mode !== 'empty' && <SelectField label={t('roles.create.source')} isRequired value={source} onChange={setSource} options={options} />}
    </Dialog>
  );
}
