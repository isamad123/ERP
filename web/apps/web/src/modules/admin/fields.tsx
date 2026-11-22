// Field security (slice S1.13b Build item 9; FR-ADM-ROL-07, FR-ADM-NAV-03): a matrix of the fields of one table against roles
// (hidden, masked, read-only, editable, or the default), saved per role with a reason, and a preview of the form as a role
// sees it. The matrix shows the rules the server holds; the server decides and a change may wait for a second administrator.
import { useEffect, useMemo, useState } from 'react';
import { Route, Routes } from 'react-router';
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { EngineProvider, useDocumentTitle, useLabel, type EngineApi } from '@erp/engine';
import { Banner, Button, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, Tabs, useToast } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem, sendText } from '../../api/client';
import { ACCESS_LEVELS, access, type AccessLevel } from '../../api/access';
import { nameOf } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { FormPreview, levelKey } from './fields-preview';
import { ReasonDialog } from './users-parts';
import './users.css';
import './fields.css';

const api: EngineApi = { send: sendText };
const MAX_ROLES = 8;
const TABLES = Object.keys(import.meta.glob('../../../../../generated/metadata/*.json'))
  .map((path) => /([^/]+)\.json$/.exec(path)?.[1] ?? '')
  .filter(Boolean)
  .sort();
type Cell = AccessLevel | 'default';
const rolesKey = ['iam', 'roles', 'all'] as const;

function Matrix({ resource }: { resource: string }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { can, writable } = useShell();
  const label = useLabel();
  const toast = useToast();
  const queryClient = useQueryClient();
  const roles = useQuery({ queryKey: rolesKey, queryFn: access.roles, staleTime: 30_000 });
  const [chosen, setChosen] = useState<string[] | null>(null);
  const [edits, setEdits] = useState<Record<string, Record<string, Cell>>>({});
  const [saving, setSaving] = useState(false);
  const active = useMemo(() => (roles.data ?? []).filter((r) => r.isActive), [roles.data]);
  useEffect(() => { if (chosen === null && roles.data) setChosen(active.slice(0, MAX_ROLES).map((r) => r.id)); }, [chosen, roles.data, active]);
  useEffect(() => { setEdits({}); }, [resource]);
  const shown = chosen ?? [];
  const summaries = useQueries({ queries: shown.map((id) => ({ queryKey: ['iam', 'role-summary', id], queryFn: () => access.summary(id) })) });
  const preview = useQuery({ queryKey: ['iam', 'field-preview', shown[0], resource], queryFn: () => access.fieldPreview(shown[0] ?? '', resource), enabled: shown.length > 0 });
  const canChange = writable && can('iam.roles.manage');
  const roleName = (id: string) => { const r = roles.data?.find((x) => x.id === id); return r ? nameOf(r.nameI18n, locale) || r.code : id; };
  const fieldLabel = (field: string) => { const text = label(`fields.${resource}.${field}`); return text.startsWith('fields.') ? field : text; };

  const explicit = (index: number, field: string): Cell => summaries[index]?.data?.fieldPolicies.find((p) => p.resource === resource && p.field === field)?.access ?? 'default';
  const value = (index: number, field: string): Cell => edits[shown[index] ?? '']?.[field] ?? explicit(index, field);
  const changes = shown.flatMap((roleId, i) => Object.entries(edits[roleId] ?? {}).filter(([field, v]) => v !== explicit(i, field)).map(([field, v]) => ({ roleId, field, access: v === 'default' ? null : v })));
  const changedRoles = [...new Set(changes.map((c) => c.roleId))];

  const save = async (reason: string) => {
    let pending = false;
    for (const roleId of changedRoles) {
      const answer = await access.setFields(roleId, changes.filter((c) => c.roleId === roleId).map((c) => ({ resource, field: c.field, access: c.access })), reason);
      pending = pending || answer.outcome === 'pending';
      void queryClient.invalidateQueries({ queryKey: ['iam', 'role-summary', roleId] });
    }
    setEdits({});
    if (pending) toast.info(t('fsec.pending')); else toast.success(t('fsec.saved'));
  };

  if (roles.isPending || (shown.length > 0 && preview.isPending)) return <Skeleton lines={5} />;
  if (roles.isError || preview.isError) {
    const problem = (preview.error ?? roles.error) instanceof ApiProblem ? (preview.error ?? roles.error) as ApiProblem : null;
    if (problem?.status === 404) return <EmptyState title={t('fsec.noFields')}>{t('fsec.noFields.body')}</EmptyState>;
    return <ErrorPanel message={problem?.status === 403 ? t('fsec.denied') : t('fsec.loadError')} reference={problem?.reference ?? null}
      action={problem?.status === 403 ? undefined : <Button variant="primary" onPress={() => { void roles.refetch(); void preview.refetch(); }}>{t('common.retry')}</Button>} />;
  }
  const fields = preview.data?.fields ?? [];
  const addable = active.filter((r) => !shown.includes(r.id));
  return (
    <div className="erp-stack">
      <div className="erp-row erp-wrap">
        {shown.map((id) => <Button key={id} variant="quiet" onPress={() => setChosen(shown.filter((x) => x !== id))}>{t('fsec.roles.drop', { role: roleName(id) })}</Button>)}
        {shown.length < MAX_ROLES && addable.length > 0 && (
          <SelectField label={t('fsec.roles.add')} value={null} onChange={(id) => setChosen([...shown, id])}
            options={addable.map((r) => ({ id: r.id, label: nameOf(r.nameI18n, locale) || r.code, detail: r.code }))} />
        )}
      </div>
      {shown.length === 0 || fields.length === 0 ? <EmptyState title={t('fsec.empty')}>{t('fsec.empty.body')}</EmptyState> : (
        <div className="erp-matrix">
          <table className="erp-table">
            <caption className="erp-visually-hidden">{t('fsec.matrix')}</caption>
            <thead><tr>
              <th scope="col">{t('fsec.field')}</th><th scope="col">{t('fsec.class')}</th>
              {shown.map((id) => <th key={id} scope="col"><bdi>{roleName(id)}</bdi></th>)}
            </tr></thead>
            <tbody>
              {fields.map((f) => (
                <tr key={f.field}>
                  <th scope="row"><bdi>{fieldLabel(f.field)}</bdi> <span className="erp-small erp-muted erp-code">{f.field}</span></th>
                  <td>{f.fieldClass ?? <span className="erp-muted">{t('fsec.class.none')}</span>}</td>
                  {shown.map((id, i) => {
                    const v = value(i, f.field);
                    const changed = v !== explicit(i, f.field);
                    return (
                      <td key={id} className={changed ? 'erp-changed' : undefined}>
                        <SelectField label={`${fieldLabel(f.field)} — ${roleName(id)}`} value={v} isDisabled={!canChange || !summaries[i]?.data}
                          onChange={(next) => setEdits((e) => ({ ...e, [id]: { ...(e[id] ?? {}), [f.field]: next as Cell } }))}
                          options={[{ id: 'default', label: t('fsec.level.default') }, ...ACCESS_LEVELS.map((l) => ({ id: l, label: t(levelKey(l)) }))]} />
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {canChange && changes.length > 0 && (
        <div className="erp-row">
          <Button variant="primary" onPress={() => setSaving(true)}>{t('fsec.save', { count: changes.length })}</Button>
          <Button variant="secondary" onPress={() => setEdits({})}>{t('fsec.discard')}</Button>
        </div>
      )}
      {saving && (
        <ReasonDialog isOpen title={t('fsec.save.title')} confirmLabel={t('fsec.save.confirm')} onClose={() => setSaving(false)}
          effect={t('fsec.save.effect', { count: changes.length, roles: changedRoles.map(roleName).join(', ') })} onConfirm={save} />
      )}
    </div>
  );
}

function Preview({ resource }: { resource: string }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const label = useLabel();
  const roles = useQuery({ queryKey: rolesKey, queryFn: access.roles, staleTime: 30_000 });
  const [roleId, setRoleId] = useState<string | null>(null);
  const role = roles.data?.find((r) => r.id === roleId);
  const fieldLabel = (field: string) => { const text = label(`fields.${resource}.${field}`); return text.startsWith('fields.') ? field : text; };
  return (
    <div className="erp-stack">
      <p>{t('fsec.preview.intro')}</p>
      <SelectField label={t('fsec.preview.role')} value={roleId} onChange={setRoleId}
        options={(roles.data ?? []).map((r) => ({ id: r.id, label: nameOf(r.nameI18n, locale) || r.code, detail: r.code }))} />
      {roleId && role && <FormPreview roleId={roleId} resource={resource} roleName={nameOf(role.nameI18n, locale) || role.code} label={fieldLabel} />}
    </div>
  );
}

function FieldsHome() {
  const { t } = useText();
  const { can } = useShell();
  useDocumentTitle(t('fsec.title'));
  const [resource, setResource] = useState<string | null>(null);
  const [tab, setTab] = useState('matrix');
  const tabs = resource ? [
    { id: 'matrix', title: t('fsec.tab.matrix'), content: tab === 'matrix' ? <Matrix resource={resource} /> : null },
    ...(can('iam.access.preview') ? [{ id: 'preview', title: t('fsec.tab.preview'), content: tab === 'preview' ? <Preview resource={resource} /> : null }] : []),
  ] : [];
  return (
    <div className="erp-page">
      <PageHeader title={t('fsec.title')} description={t('fsec.description')} />
      <SelectField label={t('fsec.table')} help={t('fsec.table.help')} value={resource} onChange={(v) => { setResource(v); setTab('matrix'); }}
        options={TABLES.map((x) => ({ id: x, label: x }))} />
      {!resource ? <Banner tone="info" title={t('fsec.pick')}>{t('fsec.pick.body')}</Banner> : <Tabs label={t('fsec.tabs')} selectedKey={tab} onSelectionChange={setTab} tabs={tabs} />}
    </div>
  );
}

export default function FieldsScreen() {
  return (
    <EngineProvider api={api}>
      <Routes><Route index element={<FieldsHome />} /></Routes>
    </EngineProvider>
  );
}
