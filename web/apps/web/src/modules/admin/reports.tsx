// The access reports (slice S1.13b Build item 11; FR-ADM-ROL-11, ruling Q8): who changed which role or assignment and when
// (from the audit log; names of fields only, never values), and the role usage report (permissions no active person holds,
// people who have not signed in for N days; N is the setting `iam.report.inactive_days`). Read-only lists; export arrives with S1.16.
import { useMemo, useState } from 'react';
import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Button, DateField, EmptyState, ErrorPanel, Link, PageHeader, Skeleton, StatusChip, Tabs } from '@erp/ui';
import { useDocumentTitle } from '@erp/engine';
import { formatDateTime, formatInteger, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { access, type PermissionChange } from '../../api/access';
import { useShell } from '../../shell/context';
import './reports-messages';
import './users.css';

export const REPORTS_PATH = '/admin/access-reports';
const PAGE = 50;

const ENTITY_KEY: Record<string, MessageKey> = {
  'iam.roles': 'rep.entity.roles',
  'iam.role_permissions': 'rep.entity.rolePermissions',
  'iam.role_data_scopes': 'rep.entity.roleDataScopes',
  'iam.field_policies': 'rep.entity.fieldPolicies',
  'iam.user_roles': 'rep.entity.userRoles',
  'iam.user_group_roles': 'rep.entity.groupRoles',
  'iam.user_group_members': 'rep.entity.groupMembers',
};
const ACTION_KEY: Record<string, MessageKey> = { create: 'rep.action.create', update: 'rep.action.update', delete: 'rep.action.delete', status: 'rep.action.status' };

export default function ReportsScreen() {
  const { t } = useText();
  const { can } = useShell();
  const [tab, setTab] = useState(can('aud.changes.view') ? 'changes' : 'usage');
  useDocumentTitle(t('rep.title'));
  const tabs = [
    ...(can('aud.changes.view') ? [{ id: 'changes', title: t('rep.tab.changes'), content: tab === 'changes' ? <PermissionChanges /> : null }] : []),
    ...(can('iam.roles.read') ? [{ id: 'usage', title: t('rep.tab.usage'), content: tab === 'usage' ? <Usage /> : null }] : []),
  ];
  return (
    <div className="erp-page">
      <PageHeader title={t('rep.title')} description={t('rep.description')} />
      {tabs.length === 0 ? <ErrorPanel message={t('rep.denied')} /> : <Tabs label={t('rep.tabs')} selectedKey={tab} onSelectionChange={setTab} tabs={tabs} />}
    </div>
  );
}

// ---------------------------------------------------------------------- permission changes

function PermissionChanges() {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [to, setTo] = useState<CalendarDate | null>(null);
  const params = { from: from ? `${from.toString()}T00:00:00Z` : undefined, to: to ? `${to.add({ days: 1 }).toString()}T00:00:00Z` : undefined };
  const list = useInfiniteQuery({
    queryKey: ['iam', 'reports', 'permission-changes', params],
    queryFn: ({ pageParam, signal }) => access.permissionChanges({ ...params, cursor: pageParam, limit: PAGE }, signal),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.next ?? null,
    placeholderData: keepPreviousData,
    refetchOnMount: 'always',
  });
  const rows = useMemo<PermissionChange[]>(() => list.data?.pages.flatMap((p) => p.items) ?? [], [list.data]);
  const problem = list.error instanceof ApiProblem ? list.error : null;
  const when = (value: string) => formatDateTime(value, locale === 'ur' ? 'ur' : 'en', timeZone, numbers);
  return (
    <div className="erp-stack">
      <p className="erp-muted">{t('rep.changes.help')}</p>
      <div className="erp-filters" role="search" aria-label={t('rep.filters')}>
        <DateField label={t('rep.from')} value={from} onChange={(v) => setFrom(v ? parseDate(v.toString()) : null)} />
        <DateField label={t('rep.to')} value={to} onChange={(v) => setTo(v ? parseDate(v.toString()) : null)} />
        {(from || to) && <Button variant="quiet" onPress={() => { setFrom(null); setTo(null); }}>{t('rep.clear')}</Button>}
      </div>
      {list.isPending ? <Skeleton lines={5} /> : list.isError && rows.length === 0 ? (
        <ErrorPanel message={problem?.status === 403 ? t('rep.denied') : t('rep.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
      ) : rows.length === 0 ? <EmptyState title={t('rep.changes.empty')}>{t('rep.changes.empty.body')}</EmptyState> : (
        <>
          <table className="erp-table">
            <caption className="erp-visually-hidden">{t('rep.tab.changes')}</caption>
            <thead><tr>
              <th scope="col">{t('rep.col.when')}</th><th scope="col">{t('rep.col.who')}</th><th scope="col">{t('rep.col.what')}</th>
              <th scope="col">{t('rep.col.action')}</th><th scope="col">{t('rep.col.role')}</th><th scope="col">{t('rep.col.fields')}</th>
            </tr></thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id}>
                  <th scope="row">{when(r.occurredAt)}</th>
                  <td><bdi>{r.actorName ?? t('rep.system')}</bdi></td>
                  <td>{ENTITY_KEY[r.entityType] ? t(ENTITY_KEY[r.entityType] as MessageKey) : <bdi className="erp-code">{r.entityType}</bdi>}</td>
                  <td>{ACTION_KEY[r.action] ? t(ACTION_KEY[r.action] as MessageKey) : r.action}</td>
                  <td>{r.roleCode ? <bdi className="erp-code">{r.roleCode}</bdi> : <span className="erp-muted">{t('rep.none')}</span>}</td>
                  <td>{r.fields.length === 0 ? <span className="erp-muted">{t('rep.none')}</span> : <bdi className="erp-small">{r.fields.join(', ')}</bdi>}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="erp-small erp-muted">{t('rep.shown', { count: formatInteger(rows.length, numbers) })}</p>
          {list.hasNextPage && <div><Button variant="secondary" isPending={list.isFetchingNextPage} onPress={() => { void list.fetchNextPage(); }}>{t('rep.more')}</Button></div>}
        </>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------- role usage

function Usage() {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const { can } = useShell();
  const usage = useQuery({ queryKey: ['iam', 'reports', 'role-usage'], queryFn: access.roleUsage, refetchOnMount: 'always' });
  if (usage.isPending) return <Skeleton lines={5} />;
  if (usage.isError || !usage.data) {
    const problem = usage.error instanceof ApiProblem ? usage.error : null;
    return (
      <ErrorPanel message={problem?.status === 403 ? t('rep.denied') : t('rep.loadError')} reference={problem?.reference ?? null}
        action={<Button variant="primary" onPress={() => { void usage.refetch(); }}>{t('common.retry')}</Button>} />
    );
  }
  const { unusedPermissions, inactiveUsers, inactiveDays } = usage.data;
  const days = formatInteger(inactiveDays, numbers);
  return (
    <div className="erp-stack">
      <section className="erp-stack" aria-labelledby="rep-unused">
        <h2 id="rep-unused" className="erp-h3">{t('rep.unused.title')}</h2>
        <p className="erp-muted">{t('rep.unused.help')}</p>
        {unusedPermissions.length === 0 ? <EmptyState title={t('rep.unused.empty')} /> : (
          <table className="erp-table">
            <caption className="erp-visually-hidden">{t('rep.unused.title')}</caption>
            <thead><tr><th scope="col">{t('rep.col.permission')}</th><th scope="col">{t('rep.col.module')}</th><th scope="col">{t('rep.col.sensitive')}</th></tr></thead>
            <tbody>
              {unusedPermissions.map((p) => (
                <tr key={p.code}>
                  <th scope="row"><bdi className="erp-code">{p.code}</bdi></th>
                  <td><bdi>{p.module}</bdi></td>
                  <td>{p.isSensitive ? <StatusChip status="pending" label={t('rep.sensitive')} /> : <span className="erp-muted">{t('rep.none')}</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
      <section className="erp-stack" aria-labelledby="rep-inactive">
        <h2 id="rep-inactive" className="erp-h3">{t('rep.inactive.title', { days })}</h2>
        <p className="erp-muted">
          {t('rep.inactive.help', { days })}{' '}
          {can('cfg.setting_values.read') && <Link href="/settings">{t('rep.inactive.setting')}</Link>}
        </p>
        {inactiveUsers.length === 0 ? <EmptyState title={t('rep.inactive.empty', { days })} /> : (
          <table className="erp-table">
            <caption className="erp-visually-hidden">{t('rep.inactive.title', { days })}</caption>
            <thead><tr><th scope="col">{t('users.col.name')}</th><th scope="col">{t('users.col.username')}</th><th scope="col">{t('users.col.lastSignIn')}</th></tr></thead>
            <tbody>
              {inactiveUsers.map((u) => (
                <tr key={u.id}>
                  <th scope="row">{can('iam.users.read') ? <Link href={`/admin/users/${u.id}`}><bdi>{u.displayName}</bdi></Link> : <bdi>{u.displayName}</bdi>}</th>
                  <td><bdi className="erp-code">{u.username}</bdi></td>
                  <td>{u.lastLoginAt ? formatDateTime(u.lastLoginAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) : t('users.never')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}
