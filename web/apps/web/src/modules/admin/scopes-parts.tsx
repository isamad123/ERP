// Shared parts of the data-scope screens (slice S1.13b, FR-ADM-ROL-06): the scope kinds in plain words, the lists the
// definition pickers use, and the answer of a change that may wait for a second administrator.
import { useQuery } from '@tanstack/react-query';
import { useToast } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';
import { access, type AccessAnswer, type DataScopeRecord, type ScopeKind } from '../../api/access';
import { requestJson } from '../../api/client';
import type { Names } from '../../api/workflow';

export const SCOPES_PATH = '/admin/data-scopes';
export const scopesKey = ['iam', 'data-scopes'] as const;
export const kindKey = (kind: string) => `scopes.kind.${kind}` as MessageKey;
export const kindHelpKey = (kind: string) => `scopes.kind.${kind}.help` as MessageKey;

export interface OrgItem { id: string; code: string; nameI18n: Names }

/** Branches or organization units from the generated lists (read-only pickers of a scope definition). */
export function useOrgItems(kind: 'branches' | 'org-units', enabled: boolean) {
  return useQuery({
    queryKey: ['org', kind, 'options'],
    queryFn: async () => (await requestJson<{ items: OrgItem[] }>('GET', `/api/org/${kind}?limit=200`)).items,
    enabled,
    staleTime: 60_000,
  });
}

/** The resources a scope can be attached to: the distinct resources of the permission catalogue, and `*` for all. */
export function useResources() {
  return useQuery({
    queryKey: ['iam', 'catalog', 'resources'],
    queryFn: async () => ['*', ...[...new Set((await access.catalog()).map((e) => e.resource))].sort()],
    staleTime: 60_000,
  });
}

/** Toasts the answer of a change: done, or waiting for a second administrator. */
export function useAnswer() {
  const { t } = useText();
  const toast = useToast();
  return (answer: AccessAnswer | undefined, done: MessageKey) => {
    if (answer?.outcome === 'pending') toast.info(t('scopes.pending'));
    else toast.success(t(done));
  };
}

/** One line about what a definition holds, e.g. "2 branches". */
export function summaryOf(scope: DataScopeRecord, t: (key: MessageKey, values?: Record<string, string | number>) => string): string {
  const d = scope.definition ?? {};
  const count = (name: string) => (Array.isArray(d[name]) ? (d[name] as unknown[]).length : 0);
  const extra = d.include_children === true ? ` ${t('scopes.withChildren')}` : '';
  if (scope.kind === 'companies') return t('scopes.count.companies', { count: count('company_ids') });
  if (scope.kind === 'branches') return t('scopes.count.branches', { count: count('branch_ids') });
  if (scope.kind === 'org_units') return t('scopes.count.units', { count: count('org_unit_ids') }) + extra;
  return '';
}

export const KINDS: readonly ScopeKind[] = ['own', 'direct_team', 'subtree', 'org_units', 'branches', 'companies', 'all'];
