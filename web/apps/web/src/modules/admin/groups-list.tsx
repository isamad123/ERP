// The group list and the create dialog (slice S1.13b; FR-ADM-USR-06, FR-ADM-NAV-03): code, name and status. Shown with
// `iam.user_groups.read`; creating needs `iam.user_groups.manage`. The server judges every rule.
import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus } from 'lucide-react';
import { DataGrid, useDocumentTitle, type GridColumn } from '@erp/engine';
import { Button, EmptyState, ErrorPanel, PageHeader, StatusChip, TextField, useToast } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import { access, type GroupRecord } from '../../api/access';
import { ApiProblem } from '../../api/client';
import { nameOf } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { FormDialog } from './groups-parts';

export const GROUPS_PATH = '/admin/groups';
export const groupsKey = ['iam', 'groups'] as const;

export function GroupStatus({ active }: { active: boolean }) {
  const { t } = useText();
  return <StatusChip status={active ? 'active' : 'inactive'} label={active ? t('groups.status.active') : t('groups.status.archived')} />;
}

export function GroupList() {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const navigate = useNavigate();
  const { can, writable } = useShell();
  useDocumentTitle(t('groups.title'));
  const [creating, setCreating] = useState(false);
  const list = useQuery({ queryKey: groupsKey, queryFn: access.groups, refetchOnMount: 'always' });
  const canCreate = writable && can('iam.user_groups.manage');
  const columns = useMemo<GridColumn<GroupRecord>[]>(() => [
    { id: 'name', header: t('groups.col.name'), width: 260, cell: (r) => <bdi>{nameOf(r.nameI18n, locale) || r.code}</bdi> },
    { id: 'code', header: t('groups.col.code'), width: 200, cell: (r) => <bdi className="erp-code">{r.code}</bdi> },
    { id: 'status', header: t('groups.col.status'), width: 140, cell: (r) => <GroupStatus active={r.isActive} /> },
  ], [t, locale]);
  const problem = list.error instanceof ApiProblem ? list.error : null;
  return (
    <div className="erp-page">
      <PageHeader title={t('groups.title')} description={t('groups.description')}
        actions={canCreate ? <Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={() => setCreating(true)}>{t('groups.create')}</Button> : undefined} />
      <DataGrid<GroupRecord> label={t('groups.title')} columns={columns} rows={list.data ?? []} rowKey={(r) => r.id} rowLabel={(r) => nameOf(r.nameI18n, locale) || r.code}
        status={list.isPending ? 'loading' : list.isError ? 'error' : 'ready'}
        error={<ErrorPanel message={problem?.status === 403 ? t('groups.denied') : t('groups.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />}
        empty={<EmptyState title={t('groups.empty')} action={canCreate ? <Button variant="primary" onPress={() => setCreating(true)}>{t('groups.create')}</Button> : undefined}>{t('groups.empty.body')}</EmptyState>}
        onOpen={(row) => { void navigate(`${GROUPS_PATH}/${row.id}`); }} />
      {creating && <CreateGroup onClose={() => setCreating(false)} onCreated={(id) => { void navigate(`${GROUPS_PATH}/${id}`); }} />}
    </div>
  );
}

function CreateGroup({ onClose, onCreated }: { onClose: () => void; onCreated: (id: string) => void }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [code, setCode] = useState('');
  const [en, setEn] = useState('');
  const [ur, setUr] = useState('');
  return (
    <FormDialog title={t('groups.create')} confirmLabel={t('groups.create')} effect={t('groups.create.effect')} onClose={onClose}
      isDisabled={code.trim().length === 0 || en.trim().length === 0}
      onConfirm={async () => {
        const name: Record<string, string> = { en: en.trim() };
        if (ur.trim()) name.ur = ur.trim();
        const answer = await access.createGroup(code.trim(), name);
        void queryClient.invalidateQueries({ queryKey: groupsKey });
        toast.success(t('groups.created'));
        if (answer.id) onCreated(answer.id);
      }}>
      <TextField label={t('groups.field.code')} isRequired isCode value={code} onChange={setCode} help={t('groups.field.code.help')} />
      <TextField label={t('groups.field.nameEn')} isRequired value={en} onChange={setEn} />
      <TextField label={t('groups.field.nameUr')} value={ur} onChange={setUr} />
    </FormDialog>
  );
}
