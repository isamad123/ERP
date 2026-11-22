// The two working panels of the data-scope screen (slice S1.13b Build item 8; FR-ADM-ROL-06): attach a scope to a role
// for a resource, and test what a person would see. Both only show what the server answers.
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Banner, Button, EmptyState, ErrorPanel, SelectField, Skeleton, type Option } from '@erp/ui';
import { formatInteger, useLocaleSettings, useText } from '@erp/i18n';
import { access, type RoleSummary } from '../../api/access';
import { nameOf } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { PersonPicker, ReasonDialog, useProblemText } from './users-parts';
import { kindKey, scopesKey, useAnswer, useResources } from './scopes-parts';

const rolesKey = ['iam', 'roles', 'all'] as const;

export function AttachPanel() {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { can, writable } = useShell();
  const queryClient = useQueryClient();
  const answer = useAnswer();
  const roles = useQuery({ queryKey: rolesKey, queryFn: access.roles, staleTime: 30_000 });
  const scopes = useQuery({ queryKey: scopesKey, queryFn: access.scopes });
  const resources = useResources();
  const [roleId, setRoleId] = useState<string | null>(null);
  const [resource, setResource] = useState<string | null>(null);
  const [scopeId, setScopeId] = useState<string | null>(null);
  const [removing, setRemoving] = useState<string | null>(null);
  const [setting, setSetting] = useState(false);
  const summaryKey = ['iam', 'role-summary', roleId] as const;
  const summary = useQuery({ queryKey: summaryKey, queryFn: () => access.summary(roleId ?? ''), enabled: !!roleId });
  const canChange = writable && can('iam.roles.manage');
  const done = (result: Awaited<ReturnType<typeof access.setScopes>>, key: 'scopes.attach.done' | 'scopes.attach.removed') => {
    void queryClient.invalidateQueries({ queryKey: summaryKey });
    answer(result, key);
  };
  const scopeName = (s: { nameI18n: Record<string, string>; code: string }) => nameOf(s.nameI18n, locale) || s.code;
  const rows: RoleSummary['dataScopes'] = summary.data?.dataScopes ?? [];

  return (
    <div className="erp-stack">
      <p>{t('scopes.attach.intro')}</p>
      <SelectField label={t('scopes.attach.role')} value={roleId} onChange={(v) => { setRoleId(v); }}
        options={(roles.data ?? []).filter((r) => r.isActive).map((r): Option => ({ id: r.id, label: nameOf(r.nameI18n, locale) || r.code, detail: r.code }))} />
      {roleId && summary.isPending && <Skeleton lines={3} />}
      {roleId && summary.isError && <ErrorPanel message={t('scopes.loadError')} action={<Button variant="primary" onPress={() => { void summary.refetch(); }}>{t('common.retry')}</Button>} />}
      {roleId && summary.data && (
        <>
          {rows.length === 0 ? <EmptyState title={t('scopes.attach.empty')}>{t('scopes.attach.empty.body')}</EmptyState> : (
            <table className="erp-table">
              <caption>{t('scopes.attach.current', { role: nameOf(summary.data.name, locale) || summary.data.code })}</caption>
              <thead><tr><th scope="col">{t('scopes.resource')}</th><th scope="col">{t('scopes.scope')}</th>{canChange && <th scope="col"><span className="erp-visually-hidden">{t('users.actions')}</span></th>}</tr></thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.resource}>
                    <th scope="row"><bdi className="erp-code">{r.resource}</bdi></th>
                    <td><bdi>{r.code}</bdi> <span className="erp-small erp-muted">({t(kindKey(r.kind))})</span></td>
                    {canChange && <td><Button variant="quiet" onPress={() => setRemoving(r.resource)}>{t('scopes.attach.remove')}</Button></td>}
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {canChange && (
            <div className="erp-stack">
              <h3 className="erp-h3">{t('scopes.attach.set')}</h3>
              <SelectField label={t('scopes.resource')} value={resource} onChange={setResource} options={(resources.data ?? ['*']).map((r) => ({ id: r, label: r === '*' ? t('scopes.allResources') : r }))} />
              <SelectField label={t('scopes.scope')} value={scopeId} onChange={setScopeId}
                options={(scopes.data ?? []).map((s) => ({ id: s.id, label: scopeName(s), detail: `${s.code} · ${t(kindKey(s.kind))}` }))} />
              <div><Button variant="primary" isDisabled={!resource || !scopeId} onPress={() => setSetting(true)}>{t('scopes.attach.button')}</Button></div>
            </div>
          )}
          {setting && resource && scopeId && (
            <ReasonDialog isOpen title={t('scopes.attach.title')} confirmLabel={t('scopes.attach.button')} onClose={() => setSetting(false)}
              effect={t('scopes.attach.effect', { resource: resource === '*' ? t('scopes.allResources') : resource, role: nameOf(summary.data.name, locale) || summary.data.code })}
              onConfirm={async (reason) => { done(await access.setScopes(summary.data.id, [{ resource, dataScopeId: scopeId }], reason), 'scopes.attach.done'); }} />
          )}
          {removing && (
            <ReasonDialog isOpen danger title={t('scopes.remove.title')} confirmLabel={t('scopes.attach.remove')} onClose={() => setRemoving(null)}
              effect={t('scopes.remove.effect', { resource: removing, role: nameOf(summary.data.name, locale) || summary.data.code })}
              onConfirm={async (reason) => { done(await access.setScopes(summary.data.id, [{ resource: removing, dataScopeId: null }], reason), 'scopes.attach.removed'); }} />
          )}
        </>
      )}
    </div>
  );
}

export function TestPanel() {
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  const problemText = useProblemText();
  const resources = useResources();
  const [person, setPerson] = useState<Option | null>(null);
  const [resource, setResource] = useState<string | null>(null);
  const run = useMutation({ mutationFn: () => access.scopePreview(person?.id ?? '', resource ?? '') });
  return (
    <div className="erp-stack">
      <p>{t('scopes.test.intro')}</p>
      <PersonPicker label={t('scopes.test.person')} value={person} onChange={setPerson} isRequired />
      <SelectField label={t('scopes.resource')} isRequired value={resource} onChange={setResource} options={(resources.data ?? []).filter((r) => r !== '*').map((r) => ({ id: r, label: r }))} />
      <div><Button variant="primary" isPending={run.isPending} isDisabled={!person || !resource} onPress={() => run.mutate()}>{t('scopes.test.run')}</Button></div>
      {run.isError && <ErrorPanel message={problemText(run.error)} />}
      {run.data && (
        <div className="erp-stack" aria-live="polite">
          <Banner tone="info" title={t('scopes.test.count', { count: formatInteger(run.data.count, numbers), name: person?.label ?? '' })}>{t('scopes.test.note')}</Banner>
          {run.data.firstPage.length === 0 ? <p className="erp-muted">{t('scopes.test.none')}</p> : (
            <>
              <p>{t('scopes.test.first', { count: formatInteger(run.data.firstPage.length, numbers) })}</p>
              <ul className="erp-stack">{run.data.firstPage.map((i) => <li key={i}><bdi className="erp-code">{i}</bdi></li>)}</ul>
            </>
          )}
        </div>
      )}
    </div>
  );
}
