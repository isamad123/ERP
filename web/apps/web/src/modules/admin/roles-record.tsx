// The role record (slice S1.13b Build item 7; FR-ADM-ROL-02, -03, FR-ADM-NAV-03): status, kind and last change at the top;
// tabs for permissions, data scopes and field rules (read-only here; they are edited on their own screens) and history.
// Rename, copy and archive are offered only to those who may manage roles. The server decides.
import { useState, type ReactNode } from 'react';
import { useParams } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { Banner, Button, EmptyState, ErrorPanel, Link, PageHeader, Skeleton, Tabs } from '@erp/ui';
import { HistoryPanel, useDocumentTitle, type TableMetadata } from '@erp/engine';
import { formatInteger, useLocaleSettings, useText } from '@erp/i18n';
import rolesMetadata from '@erp/generated/metadata/iam.roles.json';
import { ApiProblem } from '../../api/client';
import { access, type RoleRecord, type RoleSummary } from '../../api/access';
import { nameOf } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { ReasonDialog } from './users-parts';
import { CreateRole } from './roles-list';
import { PermissionPicker } from './roles-picker';
import { ActiveChip, NameDialog, ROLES_PATH, SourceChip, isProtectedRole, rolesKey, summaryKey, useRoleAnswer } from './roles-parts';

const metadata = rolesMetadata as unknown as TableMetadata;

export function RoleRecordScreen() {
  const { id = '' } = useParams();
  const { t } = useText();
  const summary = useQuery({ queryKey: summaryKey(id), queryFn: () => access.summary(id), refetchOnMount: 'always' });
  const roles = useQuery({ queryKey: rolesKey, queryFn: access.roles, staleTime: 15_000 });
  useDocumentTitle(summary.data ? nameOf(summary.data.name, 'en') || summary.data.code : t('roles.title'));
  if (summary.isPending) return <div className="erp-page"><Skeleton lines={6} /></div>;
  if (summary.isError || !summary.data) {
    const problem = summary.error instanceof ApiProblem ? summary.error : null;
    return (
      <div className="erp-page">
        <ErrorPanel level={1} title={problem?.status === 404 ? t('roles.notFound') : undefined} message={problem?.status === 403 ? t('roles.denied') : problem?.status === 404 ? t('roles.notFound.body') : t('roles.loadError')}
          reference={problem?.reference ?? null} action={problem?.status === 404 ? undefined : <Button variant="primary" onPress={() => { void summary.refetch(); }}>{t('common.retry')}</Button>} />
      </div>
    );
  }
  const record = roles.data?.find((r) => r.id === id);
  return <Record summary={summary.data} record={record} />;
}

function Record({ summary, record }: { summary: RoleSummary; record: RoleRecord | undefined }) {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  const { can, writable } = useShell();
  const [tab, setTab] = useState('permissions');
  const [dialog, setDialog] = useState<'rename' | 'copy' | 'archive' | null>(null);
  const answer = useRoleAnswer();
  const manage = writable && can('iam.roles.manage');
  const protectedRole = isProtectedRole(summary);
  const title = nameOf(summary.name, locale) || summary.code;
  const tabs: { id: string; title: string; content: ReactNode }[] = [
    { id: 'permissions', title: t('roles.tab.permissions'), content: tab === 'permissions' ? <PermissionPicker key={`${summary.id}-${summary.rowVersion}-${summary.permissions.length}`} role={summary} canEdit={manage} protectedRole={protectedRole} /> : null },
    { id: 'scopes', title: t('roles.tab.scopes'), content: tab === 'scopes' ? <ScopesView summary={summary} /> : null },
    { id: 'fields', title: t('roles.tab.fields'), content: tab === 'fields' ? <FieldsView summary={summary} /> : null },
    ...(can('aud.history.view') ? [{ id: 'history', title: t('roles.tab.history'), content: tab === 'history' ? <HistoryPanel metadata={metadata} id={summary.id} /> : null }] : []),
  ];
  return (
    <div className="erp-page">
      <PageHeader title={title} breadcrumbs={[{ label: t('roles.title'), href: ROLES_PATH }, { label: title }]}
        description={(
          <span className="erp-row erp-wrap">
            <SourceChip role={summary} /><ActiveChip isActive={summary.isActive} /><bdi className="erp-code">{summary.code}</bdi>
            <span>{t('roles.users', { count: formatInteger(summary.userCount, numbers) })}</span>
            {summary.isPrivileged && <span>{t('roles.privileged')}</span>}
            {protectedRole && <span>{t('roles.protected')}</span>}
          </span>
        )}
        actions={manage ? (
          <span className="erp-row erp-wrap">
            <Button variant="secondary" onPress={() => setDialog('rename')} isDisabled={!record}>{t('roles.rename')}</Button>
            <Button variant="secondary" onPress={() => setDialog('copy')} isDisabled={!record}>{t('roles.copy')}</Button>
            {!protectedRole && summary.isActive && <Button variant="danger" onPress={() => setDialog('archive')}>{t('roles.archive')}</Button>}
          </span>
        ) : undefined} />
      {!summary.isActive && <Banner tone="warning" title={t('roles.archived.title')}>{t('roles.archived.body')}</Banner>}
      <Tabs label={t('roles.tabs')} selectedKey={tab} onSelectionChange={setTab} tabs={tabs} />
      {dialog === 'rename' && record && (
        <NameDialog title={t('roles.rename.title')} initial={record.nameI18n} confirmLabel={t('roles.rename')} onClose={() => setDialog(null)}
          onSave={async (name) => { answer(await access.renameRole(summary.id, name, record.descriptionI18n ?? undefined, summary.rowVersion), 'roles.renamed'); }} />
      )}
      {dialog === 'copy' && record && <CreateRole from={record} onClose={() => setDialog(null)} />}
      {dialog === 'archive' && (
        <ReasonDialog isOpen danger title={t('roles.archive.title', { role: title })} confirmLabel={t('roles.archive')} onClose={() => setDialog(null)}
          effect={t('roles.archive.effect', { role: title, count: formatInteger(summary.userCount, numbers) })}
          onConfirm={async (reason) => { answer(await access.archiveRole(summary.id, reason), 'roles.archived.done'); }} />
      )}
    </div>
  );
}

function ScopesView({ summary }: { summary: RoleSummary }) {
  const { t } = useText();
  const { can } = useShell();
  return (
    <div className="erp-stack">
      {can('iam.data_scopes.read') && <p><Link href="/admin/data-scopes">{t('roles.scopes.edit')}</Link></p>}
      {summary.dataScopes.length === 0 ? <EmptyState title={t('roles.scopes.empty')}>{t('roles.scopes.empty.body')}</EmptyState> : (
        <table className="erp-table">
          <caption className="erp-visually-hidden">{t('roles.tab.scopes')}</caption>
          <thead><tr><th scope="col">{t('roles.scopes.resource')}</th><th scope="col">{t('roles.scopes.scope')}</th><th scope="col">{t('roles.scopes.kind')}</th></tr></thead>
          <tbody>
            {summary.dataScopes.map((s) => (
              <tr key={`${s.resource}/${s.dataScopeId}`}>
                <th scope="row"><bdi className="erp-code">{s.resource}</bdi></th>
                <td><bdi className="erp-code">{s.code}</bdi></td>
                <td>{s.kind}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

function FieldsView({ summary }: { summary: RoleSummary }) {
  const { t } = useText();
  const { can } = useShell();
  return (
    <div className="erp-stack">
      {can('iam.field_policies.read') && <p><Link href="/admin/field-security">{t('roles.fields.edit')}</Link></p>}
      {summary.fieldPolicies.length === 0 ? <EmptyState title={t('roles.fields.empty')}>{t('roles.fields.empty.body')}</EmptyState> : (
        <table className="erp-table">
          <caption className="erp-visually-hidden">{t('roles.tab.fields')}</caption>
          <thead><tr><th scope="col">{t('roles.fields.resource')}</th><th scope="col">{t('roles.fields.field')}</th><th scope="col">{t('roles.fields.access')}</th></tr></thead>
          <tbody>
            {summary.fieldPolicies.map((f) => (
              <tr key={`${f.resource}/${f.field}`}>
                <th scope="row"><bdi className="erp-code">{f.resource}</bdi></th>
                <td><bdi className="erp-code">{f.field}</bdi></td>
                <td>{t(`roles.access.${f.access}` as 'roles.access.hidden')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
