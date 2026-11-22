// The user record (slice S1.13a Build items 3 and 4; FR-ADM-USR-03, -04, -05, FR-KRN-IAM-04): profile, roles (with company,
// dates and reason, ended ones greyed), groups, data scopes, delegations, sessions and remembered browsers, second
// factors (method and date only, never a secret), sign-ins, recent activity and the change history. Account actions offer
// only the allowed transitions, each with a reason and a statement of its effect; a disabled person's open approvals are
// counted and can be reassigned to someone chosen from the people lookup. The server checks everything again.
import { useMemo, useState, type ReactNode } from 'react';
import { useParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Banner, Button, DateField, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, Tabs, TextField, useToast, type Option } from '@erp/ui';
import { HistoryPanel, useDocumentTitle, type TableMetadata } from '@erp/engine';
import { formatDate, formatDateTime, formatInteger, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import usersMetadata from '@erp/generated/metadata/iam.users.json';
import { ApiProblem } from '../../api/client';
import { transitionsFrom, users, type AccountAction, type RoleLine, type UserAccess, type UserRecord, type UserStatus } from '../../api/users';
import { nameOf } from '../../api/workflow';
import { companyName, useShell } from '../../shell/context';
import { ProfileForm } from './users-form';
import { PersonPicker, ReasonDialog, UserStatusChip, accessKey, listKey, recordKey } from './users-parts';

const metadata = usersMetadata as unknown as TableMetadata;
const USERS_PATH = '/admin/users';

export function UserRecordScreen() {
  const { id = '' } = useParams();
  const { t } = useText();
  const { can, writable } = useShell();
  const record = useQuery({ queryKey: recordKey(id), queryFn: () => users.get(id), refetchOnMount: 'always' });
  useDocumentTitle(record.data?.displayName ?? t('users.title'));
  if (record.isPending) return <div className="erp-page"><Skeleton lines={6} /></div>;
  if (record.isError || !record.data) {
    const problem = record.error instanceof ApiProblem ? record.error : null;
    return (
      <div className="erp-page">
        <ErrorPanel level={1} title={problem?.status === 404 ? t('users.notFound') : undefined} message={problem?.status === 403 ? t('users.denied') : problem?.status === 404 ? t('users.notFound.body') : t('users.loadError')}
          reference={problem?.reference ?? null} action={problem?.status === 404 ? undefined : <Button variant="primary" onPress={() => { void record.refetch(); }}>{t('common.retry')}</Button>} />
      </div>
    );
  }
  return <Record user={record.data} writable={writable} can={can} />;
}

type Can = (code: string, companyId?: string | null) => boolean;

function Record({ user, writable, can }: { user: UserRecord; writable: boolean; can: Can }) {
  const { t } = useText();
  const [tab, setTab] = useState('profile');
  const access = useQuery({ queryKey: accessKey(user.id), queryFn: () => users.access(user.id), refetchOnMount: 'always' });
  const tabs: { id: string; title: string; content: ReactNode }[] = [
    { id: 'profile', title: t('users.tab.profile'), content: tab === 'profile' ? <ProfileForm key={user.rowVersion} record={user} canEdit={writable && can('iam.users.update') && !user.isSystem} /> : null },
    { id: 'roles', title: t('users.tab.roles'), content: tab === 'roles' ? <RolesTab user={user} access={access} canAssign={writable && can('iam.roles.assign')} /> : null },
    { id: 'groups', title: t('users.tab.groups'), content: tab === 'groups' ? <GroupsTab access={access} /> : null },
    { id: 'scopes', title: t('users.tab.scopes'), content: tab === 'scopes' ? <ScopesTab access={access} /> : null },
    { id: 'delegations', title: t('users.tab.delegations'), content: tab === 'delegations' ? <DelegationsTab access={access} userId={user.id} /> : null },
    ...(can('iam.users.read_sessions') ? [{ id: 'sessions', title: t('users.tab.sessions'), content: tab === 'sessions' ? <SessionsTab user={user} access={access} canRevoke={writable && can('iam.users.revoke_sessions')} /> : null }] : []),
    { id: 'factors', title: t('users.tab.factors'), content: tab === 'factors' ? <FactorsTab access={access} /> : null },
    ...(can('aud.security_events.view') ? [{ id: 'signins', title: t('users.tab.signIns'), content: tab === 'signins' ? <SignIns user={user} /> : null }] : []),
    ...(can('aud.changes.view') ? [{ id: 'activity', title: t('users.tab.activity'), content: tab === 'activity' ? <Activity userId={user.id} /> : null }] : []),
    ...(can('aud.history.view') ? [{ id: 'history', title: t('users.tab.history'), content: tab === 'history' ? <HistoryPanel metadata={metadata} id={user.id} /> : null }] : []),
  ];
  return (
    <div className="erp-page">
      <PageHeader title={user.displayName} breadcrumbs={[{ label: t('users.title'), href: USERS_PATH }, { label: user.displayName }]}
        description={<span className="erp-row"><UserStatusChip status={user.status} /><bdi className="erp-code">{user.username}</bdi></span>}
        actions={writable && !user.isSystem ? <AccountActions user={user} can={can} /> : undefined} />
      <Tabs label={t('users.tabs')} selectedKey={tab} onSelectionChange={setTab} tabs={tabs} />
    </div>
  );
}

// ====================================================================== account actions (Build item 4)

type Kind = 'activate' | 'lock' | 'unlock' | 'disable' | 'archive' | 'invite' | AccountAction | 'email';
const STATUS_OF: Partial<Record<Kind, UserStatus>> = { activate: 'active', lock: 'locked', disable: 'disabled', archive: 'archived' };

function AccountActions({ user, can }: { user: UserRecord; can: Can }) {
  const { t } = useText();
  const [kind, setKind] = useState<Kind | null>(null);
  const allowed = new Set(transitionsFrom(user.status));
  const actions: { id: Kind; show: boolean }[] = [
    { id: 'invite', show: user.status === 'invited' && can('iam.users.send_invitation') },
    { id: 'activate', show: user.status !== 'invited' && allowed.has('active') && user.status !== 'locked' && can('iam.users.change_status') },
    { id: 'unlock', show: user.status === 'locked' && can('iam.users.unlock') },
    { id: 'lock', show: allowed.has('locked') && can('iam.users.change_status') },
    { id: 'disable', show: allowed.has('disabled') && can('iam.users.change_status') },
    { id: 'archive', show: allowed.has('archived') && can('iam.users.change_status') },
    { id: 'password-reset-link', show: user.status !== 'archived' && user.status !== 'invited' && can('iam.users.send_password_reset') },
    { id: 'force-password-change', show: (user.status === 'active' || user.status === 'locked') && can('iam.users.force_password_change') },
    { id: 'reset-mfa', show: user.status !== 'archived' && can('iam.users.reset_mfa') },
    { id: 'revoke-sessions', show: user.status !== 'archived' && can('iam.users.revoke_sessions') },
    { id: 'email', show: user.status !== 'archived' && can('iam.users.change_email') },
  ];
  const shown = actions.filter((a) => a.show);
  if (shown.length === 0) return null;
  return (
    <>
      <div className="erp-row erp-wrap" role="group" aria-label={t('users.actions')}>
        {shown.map((a) => <Button key={a.id} variant={a.id === 'disable' || a.id === 'archive' ? 'secondary' : 'quiet'} onPress={() => setKind(a.id)}>{t(`users.action.${a.id}`)}</Button>)}
      </div>
      {kind && <ActionDialog user={user} kind={kind} onClose={() => setKind(null)} />}
    </>
  );
}

function ActionDialog({ user, kind, onClose }: { user: UserRecord; kind: Kind; onClose: () => void }) {
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  const { can } = useShell();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [target, setTarget] = useState<Option | null>(null);
  const [email, setEmail] = useState('');
  const status = STATUS_OF[kind];
  const ends = status === 'disabled' || status === 'archived';
  const tasks = useQuery({ queryKey: ['wf', 'open-tasks', user.id], queryFn: () => users.openTasks(user.id), enabled: ends && can('wf.workflow_tasks.reassign'), refetchOnMount: 'always' });
  const waiting = tasks.data?.length ?? 0;
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: recordKey(user.id) });
    void queryClient.invalidateQueries({ queryKey: accessKey(user.id) });
    void queryClient.invalidateQueries({ queryKey: listKey });
    void queryClient.invalidateQueries({ queryKey: ['iam', 'user', user.id, 'sessions'] });
    void queryClient.invalidateQueries({ queryKey: ['iam', 'user', user.id, 'activity'] });
  };
  const name = user.displayName;

  const confirm = async (reason: string) => {
    if (kind === 'invite') {
      await users.invite(user.id);
      toast.success(t('users.done.invite', { name }));
    } else if (kind === 'unlock') {
      await users.act(user.id, 'unlock', reason);
      toast.success(t('users.done.unlock', { name }));
    } else if (status) {
      const answer = await users.setStatus(user.id, status, reason);
      let left = answer?.openWorkflowTasks ?? 0;
      if (ends && target && waiting > 0) {
        const moved = await users.reassignTasks(user.id, target.id, reason);
        left = moved.refused;
        toast.success(t('users.done.reassigned', { count: formatInteger(moved.done, numbers), person: target.label }));
      }
      toast.success(t(`users.done.${kind}` as MessageKey, { name }));
      if (left > 0) toast.info(t('users.done.tasksLeft', { count: formatInteger(left, numbers) }));
    } else if (kind === 'email') {
      const answer = await users.changeEmail(user.id, email.trim(), reason);
      if (answer?.outcome === 'pending') toast.info(t('users.done.emailPending', { name }));
      else toast.success(t('users.done.email', { name }));
    } else {
      await users.act(user.id, kind as AccountAction, reason);
      toast.success(t(`users.done.${kind}` as MessageKey, { name }));
    }
    refresh();
  };

  return (
    <ReasonDialog isOpen title={t(`users.action.${kind}.title` as MessageKey, { name })} confirmLabel={t(`users.action.${kind}`)} danger={ends}
      effect={t(`users.action.${kind}.effect` as MessageKey, { name })} reasonOptional={kind === 'invite'} onClose={onClose} onConfirm={confirm}
      isDisabled={kind === 'email' && email.trim().length === 0}>
      {ends && waiting > 0 && (
        <div className="erp-stack" role="group" aria-label={t('users.tasks.group')}>
          <Banner tone="warning" title={t('users.tasks.waiting', { count: formatInteger(waiting, numbers) })}>{t('users.tasks.body')}</Banner>
          <PersonPicker label={t('users.tasks.reassignTo')} help={t('users.tasks.reassignHelp')} value={target} onChange={setTarget} exclude={user.id} />
        </div>
      )}
      {kind === 'email' && <TextField label={t('users.email.new')} type="email" isRequired value={email} onChange={setEmail} autoComplete="off" help={t('users.email.pendingHelp')} />}
    </ReasonDialog>
  );
}

// ====================================================================== tabs

type AccessQuery = ReturnType<typeof useQuery<UserAccess>>;

function AccessState({ access, children }: { access: AccessQuery; children: (data: UserAccess) => ReactNode }) {
  const { t } = useText();
  if (access.isPending) return <Skeleton lines={4} />;
  if (access.isError || !access.data) {
    const problem = access.error instanceof ApiProblem ? access.error : null;
    return <ErrorPanel message={problem?.status === 403 ? t('users.denied') : t('users.loadError')} reference={problem?.reference ?? null} action={<Button variant="primary" onPress={() => { void access.refetch(); }}>{t('common.retry')}</Button>} />;
  }
  return <>{children(access.data)}</>;
}

function useCompany() {
  const { me } = useShell();
  const { locale } = useLocaleSettings();
  const { t } = useText();
  const names = useMemo(() => new Map(me.companies.map((c) => [c.id, companyName(c, locale)])), [me.companies, locale]);
  return (id: string | null) => (id === null ? t('users.allCompanies') : names.get(id) ?? id);
}

function RolesTab({ user, access, canAssign }: { user: UserRecord; access: AccessQuery; canAssign: boolean }) {
  const { t } = useText();
  const { locale, timeZone } = useLocaleSettings();
  const company = useCompany();
  const [give, setGive] = useState(false);
  const [ending, setEnding] = useState<RoleLine | null>(null);
  return (
    <AccessState access={access}>
      {(data) => (
        <div className="erp-stack">
          {canAssign && <div><Button variant="primary" onPress={() => setGive(true)}>{t('users.roles.give')}</Button></div>}
          {data.roles.length === 0 ? <EmptyState title={t('users.roles.empty')}>{t('users.roles.empty.body')}</EmptyState> : (
            <table className="erp-table">
              <caption className="erp-visually-hidden">{t('users.tab.roles')}</caption>
              <thead><tr>
                <th scope="col">{t('users.role')}</th><th scope="col">{t('users.company')}</th><th scope="col">{t('users.roles.from')}</th>
                <th scope="col">{t('users.roles.to')}</th><th scope="col">{t('users.reason')}</th><th scope="col">{t('users.roles.state')}</th>{canAssign && <th scope="col"><span className="erp-visually-hidden">{t('users.actions')}</span></th>}
              </tr></thead>
              <tbody>
                {data.roles.map((r) => (
                  <tr key={r.assignmentId} className={r.inForce ? undefined : 'erp-ended'}>
                    <th scope="row"><bdi>{nameOf(r.roleName, locale) || r.roleCode}</bdi> <span className="erp-small erp-muted erp-code">{r.roleCode}</span></th>
                    <td>{company(r.companyId)}</td>
                    <td>{formatDate(r.validFrom, locale === 'ur' ? 'ur' : 'en', timeZone)}</td>
                    <td>{r.validTo ? formatDate(r.validTo, locale === 'ur' ? 'ur' : 'en', timeZone) : t('users.roles.open')}</td>
                    <td>{r.reason ?? ''}</td>
                    <td>{r.inForce ? t('users.roles.inForce') : t('users.roles.ended')}</td>
                    {canAssign && <td>{r.inForce && <Button variant="quiet" onPress={() => setEnding(r)}>{t('users.roles.end')}</Button>}</td>}
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {give && <GiveRole user={user} onClose={() => setGive(false)} />}
          {ending && <EndRole user={user} line={ending} onClose={() => setEnding(null)} />}
        </div>
      )}
    </AccessState>
  );
}

function useRoleAnswer(userId: string) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  return (outcome: string, done: MessageKey) => {
    void queryClient.invalidateQueries({ queryKey: accessKey(userId) });
    void queryClient.invalidateQueries({ queryKey: listKey });
    if (outcome === 'pending') toast.info(t('users.roles.pending'));
    else toast.success(t(done));
  };
}

function GiveRole({ user, onClose }: { user: UserRecord; onClose: () => void }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { me } = useShell();
  const answer = useRoleAnswer(user.id);
  const roles = useQuery({ queryKey: ['iam', 'roles', 'options'], queryFn: users.roles, staleTime: 60_000 });
  const [roleId, setRoleId] = useState<string | null>(null);
  const [companyId, setCompanyId] = useState('all');
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [to, setTo] = useState<CalendarDate | null>(null);
  return (
    <ReasonDialog isOpen title={t('users.roles.give.title', { name: user.displayName })} confirmLabel={t('users.roles.give')} effect={t('users.roles.give.effect', { name: user.displayName })}
      onClose={onClose} isDisabled={!roleId}
      onConfirm={async (reason) => {
        if (!roleId) return;
        const result = await users.assignRole({ userId: user.id, roleId, companyId: companyId === 'all' ? null : companyId, validFrom: from?.toString() ?? null, validTo: to?.toString() ?? null, reason });
        answer(result.outcome, 'users.roles.given');
      }}>
      <SelectField label={t('users.role')} isRequired value={roleId} onChange={setRoleId} options={(roles.data ?? []).map((r) => ({ id: r.id, label: nameOf(r.nameI18n, locale) || r.code, detail: r.code }))} />
      <SelectField label={t('users.company')} value={companyId} onChange={setCompanyId} options={[{ id: 'all', label: t('users.allCompanies') }, ...me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }))]} />
      <DateField label={t('users.roles.from')} help={t('users.roles.from.help')} value={from} onChange={(v) => setFrom(v ? parseDate(v.toString()) : null)} />
      <DateField label={t('users.roles.to')} help={t('users.roles.to.help')} value={to} onChange={(v) => setTo(v ? parseDate(v.toString()) : null)} />
    </ReasonDialog>
  );
}

function EndRole({ user, line, onClose }: { user: UserRecord; line: RoleLine; onClose: () => void }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const answer = useRoleAnswer(user.id);
  const [on, setOn] = useState<CalendarDate | null>(null);
  const role = nameOf(line.roleName, locale) || line.roleCode;
  return (
    <ReasonDialog isOpen title={t('users.roles.end.title', { role })} confirmLabel={t('users.roles.end')} effect={t('users.roles.end.effect', { role, name: user.displayName })} danger onClose={onClose}
      onConfirm={async (reason) => { answer((await users.endRole(line.assignmentId, on?.toString() ?? null, reason)).outcome, 'users.roles.ended.done'); }}>
      <DateField label={t('users.roles.endDate')} help={t('users.roles.endDate.help')} value={on} onChange={(v) => setOn(v ? parseDate(v.toString()) : null)} />
    </ReasonDialog>
  );
}

function GroupsTab({ access }: { access: AccessQuery }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  return (
    <AccessState access={access}>
      {(data) => data.groups.length === 0 ? <EmptyState title={t('users.groups.empty')} /> : (
        <ul className="erp-stack">
          {data.groups.map((g) => (
            <li key={g.groupId}>
              <strong><bdi>{nameOf(g.name, locale) || g.code}</bdi></strong> <span className="erp-small erp-muted erp-code">{g.code}</span>
              <div className="erp-small erp-muted">{g.roles.length === 0 ? t('users.groups.noRoles') : t('users.groups.roles', { roles: g.roles.filter((r) => r.inForce).map((r) => nameOf(r.roleName, locale) || r.roleCode).join(', ') })}</div>
            </li>
          ))}
        </ul>
      )}
    </AccessState>
  );
}

function ScopesTab({ access }: { access: AccessQuery }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  return (
    <AccessState access={access}>
      {(data) => data.scopes.length === 0 ? <EmptyState title={t('users.scopes.empty')}>{t('users.scopes.empty.body')}</EmptyState> : (
        <table className="erp-table">
          <caption className="erp-visually-hidden">{t('users.tab.scopes')}</caption>
          <thead><tr><th scope="col">{t('users.scopes.resource')}</th><th scope="col">{t('users.scopes.scope')}</th><th scope="col">{t('users.scopes.from')}</th></tr></thead>
          <tbody>
            {data.scopes.map((s) => (
              <tr key={`${s.resource}/${s.roleCode}/${s.source}`}>
                <th scope="row"><bdi className="erp-code">{s.resource}</bdi></th>
                <td><bdi>{nameOf(s.scopeName, locale) || s.scopeCode}</bdi> <span className="erp-small erp-muted">({t(`users.scopes.kind.${s.scopeKind}` as MessageKey)})</span></td>
                <td><bdi>{s.roleCode}</bdi>{s.source === 'role' ? '' : ` · ${s.source.replace('group:', '')}`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </AccessState>
  );
}

function DelegationsTab({ access, userId }: { access: AccessQuery; userId: string }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  return (
    <AccessState access={access}>
      {(data) => data.delegations.length === 0 ? <EmptyState title={t('users.delegations.empty')} /> : (
        <ul className="erp-stack">
          {data.delegations.map((d) => (
            <li key={d.id}>
              {t(d.fromUserId === userId ? 'users.delegations.gives' : 'users.delegations.receives', { person: d.fromUserId === userId ? d.toName : d.fromName })}{' '}
              <span className="erp-muted">{t('users.delegations.period', { from: formatDateTime(d.validFrom, locale === 'ur' ? 'ur' : 'en', timeZone, numbers), to: formatDateTime(d.validTo, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) })} · {t(`users.delegations.status.${d.status === 'active' ? 'active' : 'revoked'}`)}</span>
            </li>
          ))}
        </ul>
      )}
    </AccessState>
  );
}

function SessionsTab({ user, access, canRevoke }: { user: UserRecord; access: AccessQuery; canRevoke: boolean }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const toast = useToast();
  const queryClient = useQueryClient();
  const key = ['iam', 'user', user.id, 'sessions'] as const;
  const sessions = useQuery({ queryKey: key, queryFn: () => users.sessions(user.id), refetchOnMount: 'always' });
  const [target, setTarget] = useState<{ type: 'session' | 'device' | 'all'; id?: string } | null>(null);
  const when = (iso: string) => formatDateTime(iso, locale === 'ur' ? 'ur' : 'en', timeZone, numbers);
  const refresh = () => { void queryClient.invalidateQueries({ queryKey: key }); void queryClient.invalidateQueries({ queryKey: accessKey(user.id) }); };
  const revoke = useMutation({
    mutationFn: async ({ reason }: { reason: string }) => {
      if (target?.type === 'all') await users.act(user.id, 'revoke-sessions', reason);
      else if (target?.type === 'session' && target.id) await users.revokeSession(user.id, target.id, reason);
      else if (target?.type === 'device' && target.id) await users.removeDevice(user.id, target.id, reason);
    },
  });
  return (
    <div className="erp-stack">
      <section aria-label={t('users.sessions.title')} className="erp-stack">
        <h2 className="erp-h3">{t('users.sessions.title')}</h2>
        {sessions.isPending ? <Skeleton lines={3} /> : sessions.isError ? (
          <ErrorPanel message={t('users.loadError')} action={<Button variant="primary" onPress={() => { void sessions.refetch(); }}>{t('common.retry')}</Button>} />
        ) : sessions.data.length === 0 ? <EmptyState title={t('users.sessions.empty')} /> : (
          <ul className="erp-stack">
            {sessions.data.map((s) => (
              <li key={s.id} className="erp-row erp-wrap">
                <span>{t('users.sessions.line', { started: when(s.issuedAt), seen: when(s.lastSeenAt) })} <span className="erp-muted"><bdi>{s.userAgent ?? ''} {s.ipAddress ?? ''}</bdi></span></span>
                {canRevoke && <Button variant="quiet" onPress={() => setTarget({ type: 'session', id: s.id })}>{t('users.sessions.revoke')}</Button>}
              </li>
            ))}
          </ul>
        )}
        {canRevoke && (sessions.data?.length ?? 0) > 0 && <div><Button variant="secondary" onPress={() => setTarget({ type: 'all' })}>{t('users.sessions.revokeAll')}</Button></div>}
      </section>
      <section aria-label={t('users.devices.title')} className="erp-stack">
        <h2 className="erp-h3">{t('users.devices.title')}</h2>
        <AccessState access={access}>
          {(data) => data.trustedDevices.length === 0 ? <EmptyState title={t('users.devices.empty')} /> : (
            <ul className="erp-stack">
              {data.trustedDevices.map((d) => (
                <li key={d.id} className="erp-row erp-wrap">
                  <span><bdi>{d.label ?? t('users.devices.unnamed')}</bdi> <span className="erp-muted">{t('users.devices.until', { date: when(d.expiresAt) })}</span></span>
                  {canRevoke && <Button variant="quiet" onPress={() => setTarget({ type: 'device', id: d.id })}>{t('users.devices.remove')}</Button>}
                </li>
              ))}
            </ul>
          )}
        </AccessState>
      </section>
      {target && (
        <ReasonDialog isOpen danger title={t(`users.sessions.${target.type}.title` as MessageKey)} confirmLabel={t(`users.sessions.${target.type}.confirm` as MessageKey)}
          effect={t(`users.sessions.${target.type}.effect` as MessageKey, { name: user.displayName })} onClose={() => setTarget(null)}
          onConfirm={async (reason) => { await revoke.mutateAsync({ reason }); toast.success(t(`users.sessions.${target.type}.done` as MessageKey)); refresh(); }} />
      )}
    </div>
  );
}

function FactorsTab({ access }: { access: AccessQuery }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  return (
    <AccessState access={access}>
      {(data) => data.mfaDevices.length === 0 ? <EmptyState title={t('users.factors.empty')}>{t('users.factors.empty.body')}</EmptyState> : (
        <table className="erp-table">
          <caption className="erp-visually-hidden">{t('users.tab.factors')}</caption>
          <thead><tr><th scope="col">{t('users.factors.method')}</th><th scope="col">{t('users.factors.added')}</th><th scope="col">{t('users.factors.lastUsed')}</th><th scope="col">{t('users.roles.state')}</th></tr></thead>
          <tbody>
            {data.mfaDevices.map((m) => (
              <tr key={m.id}>
                <th scope="row">{t(`users.factors.kind.${m.kind}` as MessageKey)}{m.label ? <> <bdi className="erp-muted">{m.label}</bdi></> : null}</th>
                <td>{formatDateTime(m.createdAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers)}</td>
                <td>{m.lastUsedAt ? formatDateTime(m.lastUsedAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) : t('users.never')}</td>
                <td>{t(`users.factors.status.${m.status}` as MessageKey)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </AccessState>
  );
}

function SignIns({ user }: { user: UserRecord }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  // Sign-ins are recorded under the login name that was typed (username or e-mail, lower case); changes made to the account
  // under the person's id. Both are the user's security events.
  const events = useQuery({
    queryKey: ['iam', 'user', user.id, 'signins'],
    queryFn: async () => {
      const [byName, byMail, byId] = await Promise.all([users.signIns(user.username.toLowerCase()), users.signIns(user.email.toLowerCase()), users.signIns(user.id)]);
      const all = [...byName.items, ...byMail.items, ...byId.items];
      const seen = new Set<string>();
      return all.filter((e) => !seen.has(e.id) && seen.add(e.id)).sort((a, b) => b.occurredAt.localeCompare(a.occurredAt)).slice(0, 30);
    },
    refetchOnMount: 'always',
  });
  if (events.isPending) return <Skeleton lines={4} />;
  if (events.isError) return <ErrorPanel message={t('users.loadError')} action={<Button variant="primary" onPress={() => { void events.refetch(); }}>{t('common.retry')}</Button>} />;
  if (events.data.length === 0) return <EmptyState title={t('users.signIns.empty')} />;
  return (
    <ol className="erp-history" aria-label={t('users.tab.signIns')}>
      {events.data.map((e) => (
        <li key={e.id} className="erp-history-entry">
          <p className="erp-history-head"><strong><bdi>{e.eventType}</bdi></strong><span className="erp-muted">{formatDateTime(e.occurredAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers)}</span></p>
        </li>
      ))}
    </ol>
  );
}

function Activity({ userId }: { userId: string }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const changes = useQuery({ queryKey: ['iam', 'user', userId, 'activity'], queryFn: () => users.activity(userId), refetchOnMount: 'always' });
  if (changes.isPending) return <Skeleton lines={4} />;
  if (changes.isError) return <ErrorPanel message={t('users.loadError')} action={<Button variant="primary" onPress={() => { void changes.refetch(); }}>{t('common.retry')}</Button>} />;
  if (changes.data.items.length === 0) return <EmptyState title={t('users.activity.empty')} />;
  return (
    <ol className="erp-history" aria-label={t('users.tab.activity')}>
      {changes.data.items.map((c) => (
        <li key={c.id} className="erp-history-entry">
          <p className="erp-history-head">
            <strong>{t(`users.activity.${c.action === 'insert' || c.action === 'create' ? 'create' : c.action === 'delete' ? 'delete' : 'update'}` as MessageKey)}</strong>
            <span className="erp-muted">{t('users.activity.by', { who: c.actorName ?? t('users.activity.system'), when: formatDateTime(c.occurredAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) })}</span>
          </p>
        </li>
      ))}
    </ol>
  );
}
