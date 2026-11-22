// Data scopes (slice S1.13b Build item 8; FR-ADM-ROL-06, FR-ADM-NAV-03): which records a role sees. The list of scopes with
// creation, the attachment of a scope to a role for a resource, and the test "what would this person see". The server
// checks every change again; a change that needs a second administrator is shown as waiting.
import { useState } from 'react';
import { Route, Routes, useNavigate } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { Plus } from 'lucide-react';
import { EngineProvider, useDocumentTitle, type EngineApi } from '@erp/engine';
import { Button, EmptyState, ErrorPanel, PageHeader, Skeleton, Tabs } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import { sendText, ApiProblem } from '../../api/client';
import { access } from '../../api/access';
import { nameOf } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { AttachPanel, TestPanel } from './scopes-panels';
import { ScopeCreate } from './scopes-new';
import { SCOPES_PATH, kindKey, scopesKey, summaryOf } from './scopes-parts';
import './users.css';

const api: EngineApi = { send: sendText };

function ScopeList() {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const list = useQuery({ queryKey: scopesKey, queryFn: access.scopes, refetchOnMount: 'always' });
  const problem = list.error instanceof ApiProblem ? list.error : null;
  if (list.isPending) return <Skeleton lines={4} />;
  if (list.isError) {
    return <ErrorPanel message={problem?.status === 403 ? t('scopes.denied') : t('scopes.loadError')} reference={problem?.reference ?? null}
      action={problem?.status === 403 ? undefined : <Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />;
  }
  if (list.data.length === 0) return <EmptyState title={t('scopes.empty')}>{t('scopes.empty.body')}</EmptyState>;
  return (
    <table className="erp-table">
      <caption className="erp-visually-hidden">{t('scopes.title')}</caption>
      <thead><tr><th scope="col">{t('scopes.name')}</th><th scope="col">{t('scopes.code')}</th><th scope="col">{t('scopes.kind')}</th><th scope="col">{t('scopes.definition')}</th></tr></thead>
      <tbody>
        {list.data.map((s) => (
          <tr key={s.id}>
            <th scope="row"><bdi>{nameOf(s.nameI18n, locale) || s.code}</bdi></th>
            <td><bdi className="erp-code">{s.code}</bdi></td>
            <td>{t(kindKey(s.kind))}</td>
            <td>{summaryOf(s, t)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function ScopesHome() {
  const { t } = useText();
  const { can, writable } = useShell();
  const navigate = useNavigate();
  const [tab, setTab] = useState('scopes');
  useDocumentTitle(t('scopes.title'));
  const tabs = [
    { id: 'scopes', title: t('scopes.tab.scopes'), content: tab === 'scopes' ? <ScopeList /> : null },
    ...(can('iam.roles.read') ? [{ id: 'attach', title: t('scopes.tab.attach'), content: tab === 'attach' ? <AttachPanel /> : null }] : []),
    ...(can('iam.access.preview') ? [{ id: 'test', title: t('scopes.tab.test'), content: tab === 'test' ? <TestPanel /> : null }] : []),
  ];
  return (
    <div className="erp-page">
      <PageHeader title={t('scopes.title')} description={t('scopes.description')}
        actions={writable && can('iam.roles.manage') ? <Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={() => { void navigate(`${SCOPES_PATH}/new`); }}>{t('scopes.create')}</Button> : undefined} />
      <Tabs label={t('scopes.tabs')} selectedKey={tab} onSelectionChange={setTab} tabs={tabs} />
    </div>
  );
}

export default function ScopesScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<ScopesHome />} />
        <Route path="new" element={<ScopeCreate />} />
      </Routes>
    </EngineProvider>
  );
}
