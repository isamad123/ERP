// The permission catalogue (slice S1.13b Build item 6; FR-ADM-ROL-01): permissions by module and resource, search, a
// sensitivity mark (icon and words, never colour alone) and the roles that hold each. Also the grouping the role's
// permission picker uses.
import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ShieldAlert } from 'lucide-react';
import { useDocumentTitle } from '@erp/engine';
import { Button, EmptyState, ErrorPanel, PageHeader, Skeleton, TextField } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { access, type CatalogEntry } from '../../api/access';
import { nameOf } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { ROLES_PATH, catalogKey, rolesKey } from './roles-parts';

export interface ResourceGroup { resource: string; entries: CatalogEntry[] }
export interface ModuleGroup { module: string; resources: ResourceGroup[] }

/** Entries matching the text (code, resource, module or action), grouped by module then resource, in code order. */
export function groupCatalogue(entries: CatalogEntry[], search: string): ModuleGroup[] {
  const needle = search.trim().toLowerCase();
  const matching = entries.filter((e) => !needle || e.code.toLowerCase().includes(needle) || e.module.toLowerCase().includes(needle));
  const modules = new Map<string, Map<string, CatalogEntry[]>>();
  for (const entry of [...matching].sort((a, b) => a.code.localeCompare(b.code))) {
    const resources = modules.get(entry.module) ?? new Map<string, CatalogEntry[]>();
    resources.set(entry.resource, [...(resources.get(entry.resource) ?? []), entry]);
    modules.set(entry.module, resources);
  }
  return [...modules].map(([module, resources]) => ({ module, resources: [...resources].map(([resource, list]) => ({ resource, entries: list })) }));
}

export function SensitiveMark() {
  const { t } = useText();
  return <span className="erp-perm-sensitive"><ShieldAlert size={14} aria-hidden="true" />{t('perms.sensitive')}</span>;
}

export function Catalogue() {
  const { t, server } = useText();
  const { locale } = useLocaleSettings();
  const { can } = useShell();
  useDocumentTitle(t('perms.title'));
  const [search, setSearch] = useState('');
  const catalog = useQuery({ queryKey: catalogKey, queryFn: access.catalog, refetchOnMount: 'always' });
  const roles = useQuery({ queryKey: rolesKey, queryFn: access.roles, enabled: can('iam.roles.read'), staleTime: 60_000 });
  const roleNames = useMemo(() => new Map((roles.data ?? []).map((r) => [r.code, nameOf(r.nameI18n, locale) || r.code])), [roles.data, locale]);
  const groups = useMemo(() => groupCatalogue(catalog.data ?? [], search), [catalog.data, search]);
  const problem = catalog.error instanceof ApiProblem ? catalog.error : null;
  return (
    <div className="erp-page">
      <PageHeader title={t('perms.title')} description={t('perms.description')} breadcrumbs={[{ label: t('roles.title'), href: ROLES_PATH }, { label: t('perms.title') }]} />
      <TextField type="search" label={t('perms.search')} help={t('perms.search.help')} value={search} onChange={setSearch} />
      {catalog.isPending ? <Skeleton lines={8} />
        : catalog.isError ? (
          <ErrorPanel message={problem?.status === 403 ? t('perms.denied') : t('perms.loadError')} reference={problem?.reference ?? null}
            action={<Button variant="primary" onPress={() => { void catalog.refetch(); }}>{t('common.retry')}</Button>} />
        ) : groups.length === 0 ? <EmptyState title={t('perms.noMatches')}>{t('perms.noMatches.body')}</EmptyState>
        : groups.map((m) => (
          <section key={m.module} className="erp-perm-module" aria-label={m.module}>
            <h2 className="erp-h3">{m.module}</h2>
            {m.resources.map((r) => (
              <table key={r.resource} className="erp-table">
                <caption className="erp-visually-hidden">{r.resource}</caption>
                <thead><tr>
                  <th scope="col">{r.resource}</th><th scope="col">{t('perms.col.description')}</th><th scope="col">{t('perms.col.sensitivity')}</th><th scope="col">{t('perms.col.heldBy')}</th>
                </tr></thead>
                <tbody>
                  {r.entries.map((e) => (
                    <tr key={e.code}>
                      <th scope="row"><bdi className="erp-code">{e.code}</bdi></th>
                      <td>{server(e.descriptionKey) ?? e.action}</td>
                      <td>{e.isSensitive ? <SensitiveMark /> : <span className="erp-muted">{t('perms.normal')}</span>}</td>
                      <td>{e.roles.length === 0 ? <span className="erp-muted">{t('perms.heldBy.none')}</span> : <bdi>{e.roles.map((c) => roleNames.get(c) ?? c).join(', ')}</bdi>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            ))}
          </section>
        ))}
    </div>
  );
}
