// The group record (slice S1.13b Build item 10; FR-ADM-USR-06, FR-ADM-ROL-04, FR-ADM-NAV-03): members (add, remove), roles given to
// every member (with company, dates and reason; ended ones greyed; a change that needs a second administrator says so) and the
// change history. Rename and archive sit in the header. The server checks everything again.
import { useMemo, useState } from 'react';
import { useParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Button, DateField, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, Tabs, TextField, useToast, type Option } from '@erp/ui';
import { HistoryPanel, useDocumentTitle, type TableMetadata } from '@erp/engine';
import { formatDate, formatInteger, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import groupsMetadata from '@erp/generated/metadata/iam.user_groups.json';
import { access, isPending, type GroupDetail } from '../../api/access';
import { ApiProblem } from '../../api/client';
import { users, type RoleLine } from '../../api/users';
import { nameOf } from '../../api/workflow';
import { companyName, useShell } from '../../shell/context';
import { FormDialog } from './groups-parts';
import { GROUPS_PATH, GroupStatus, groupsKey } from './groups-list';
import { PersonPicker, ReasonDialog } from './users-parts';

const metadata = groupsMetadata as unknown as TableMetadata;
const groupKey = (id: string) => ['iam', 'group', id] as const;

export function GroupRecordScreen() {
  const { id = '' } = useParams();
  const { t } = useText();
  const { can, writable } = useShell();
  const record = useQuery({ queryKey: groupKey(id), queryFn: () => access.group(id), refetchOnMount: 'always' });
  useDocumentTitle(record.data ? nameOf(record.data.name, 'en') || record.data.code : t('groups.title'));
  if (record.isPending) return <div className="erp-page"><Skeleton lines={6} /></div>;
  if (record.isError || !record.data) {
    const problem = record.error instanceof ApiProblem ? record.error : null;
    return (
      <div className="erp-page">
        <ErrorPanel level={1} title={problem?.status === 404 ? t('groups.notFound') : undefined}
          message={problem?.status === 403 ? t('groups.denied') : problem?.status === 404 ? t('groups.notFound.body') : t('groups.loadError')}
          reference={problem?.reference ?? null} action={problem?.status === 404 ? undefined : <Button variant="primary" onPress={() => { void record.refetch(); }}>{t('common.retry')}</Button>} />
      </div>
    );
  }
  return <Record group={record.data} canManage={writable && can('iam.user_groups.manage')} canHistory={can('aud.history.view')} />;
}

function useRefresh(groupId: string) {
  const queryClient = useQueryClient();
  return () => {
    void queryClient.invalidateQueries({ queryKey: groupKey(groupId) });
    void queryClient.invalidateQueries({ queryKey: groupsKey });
  };
}

function Record({ group, canManage, canHistory }: { group: GroupDetail; canManage: boolean; canHistory: boolean }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const [tab, setTab] = useState('members');
  const [dialog, setDialog] = useState<'rename' | 'archive' | null>(null);
  const title = nameOf(group.name, locale) || group.code;
  const tabs = [
    { id: 'members', title: t('groups.tab.members'), content: tab === 'members' ? <MembersTab group={group} canManage={canManage} /> : null },
    { id: 'roles', title: t('groups.tab.roles'), content: tab === 'roles' ? <RolesTab group={group} canManage={canManage} /> : null },
    ...(canHistory ? [{ id: 'history', title: t('groups.tab.history'), content: tab === 'history' ? <HistoryPanel metadata={metadata} id={group.id} /> : null }] : []),
  ];
  return (
    <div className="erp-page">
      <PageHeader title={title} breadcrumbs={[{ label: t('groups.title'), href: GROUPS_PATH }, { label: title }]}
        description={<span className="erp-row"><GroupStatus active={group.isActive} /><bdi className="erp-code">{group.code}</bdi></span>}
        actions={canManage && group.isActive ? (
          <div className="erp-row erp-wrap" role="group" aria-label={t('groups.actions')}>
            <Button variant="quiet" onPress={() => setDialog('rename')}>{t('groups.rename')}</Button>
            <Button variant="secondary" onPress={() => setDialog('archive')}>{t('groups.archive')}</Button>
          </div>
        ) : undefined} />
      <Tabs label={t('groups.tabs')} selectedKey={tab} onSelectionChange={setTab} tabs={tabs} />
      {dialog === 'rename' && <RenameGroup group={group} onClose={() => setDialog(null)} />}
      {dialog === 'archive' && <ArchiveGroup group={group} onClose={() => setDialog(null)} />}
    </div>
  );
}

function RenameGroup({ group, onClose }: { group: GroupDetail; onClose: () => void }) {
  const { t } = useText();
  const toast = useToast();
  const refresh = useRefresh(group.id);
  const [en, setEn] = useState(group.name.en ?? '');
  const [ur, setUr] = useState(group.name.ur ?? '');
  return (
    <FormDialog title={t('groups.rename.title')} confirmLabel={t('groups.rename')} effect={t('groups.rename.effect')} onClose={onClose} isDisabled={en.trim().length === 0}
      onConfirm={async () => {
        const name: Record<string, string> = { en: en.trim() };
        if (ur.trim()) name.ur = ur.trim();
        await access.renameGroup(group.id, name, group.rowVersion);
        refresh();
        toast.success(t('groups.renamed'));
      }}>
      <TextField label={t('groups.field.nameEn')} isRequired value={en} onChange={setEn} />
      <TextField label={t('groups.field.nameUr')} value={ur} onChange={setUr} />
    </FormDialog>
  );
}

function ArchiveGroup({ group, onClose }: { group: GroupDetail; onClose: () => void }) {
  const { t } = useText();
  const toast = useToast();
  const refresh = useRefresh(group.id);
  return (
    <ReasonDialog isOpen danger title={t('groups.archive.title')} confirmLabel={t('groups.archive')} effect={t('groups.archive.effect', { count: String(group.members.length) })} onClose={onClose}
      onConfirm={async (reason) => {
        await access.archiveGroup(group.id, reason);
        refresh();
        toast.success(t('groups.archived'));
      }} />
  );
}

function MembersTab({ group, canManage }: { group: GroupDetail; canManage: boolean }) {
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  const toast = useToast();
  const refresh = useRefresh(group.id);
  const [person, setPerson] = useState<Option | null>(null);
  const [removing, setRemoving] = useState<GroupDetail['members'][number] | null>(null);
  const add = useMutation({
    mutationFn: (userId: string) => access.addMember(group.id, userId),
    onSuccess: () => { setPerson(null); refresh(); toast.success(t('groups.members.added')); },
    onError: () => toast.error(t('groups.members.addFailed')),
  });
  const editable = canManage && group.isActive;
  return (
    <div className="erp-stack">
      <p className="erp-muted">{t('groups.members.count', { count: formatInteger(group.members.length, numbers) })}</p>
      {editable && (
        <div className="erp-row erp-wrap">
          <PersonPicker label={t('groups.members.add')} value={person} onChange={setPerson} />
          <Button variant="primary" isDisabled={!person} isPending={add.isPending} onPress={() => { if (person) add.mutate(person.id); }}>{t('groups.members.addButton')}</Button>
        </div>
      )}
      {group.members.length === 0 ? <EmptyState title={t('groups.members.empty')}>{t('groups.members.empty.body')}</EmptyState> : (
        <table className="erp-table">
          <caption className="erp-visually-hidden">{t('groups.tab.members')}</caption>
          <thead><tr>
            <th scope="col">{t('groups.members.name')}</th><th scope="col">{t('groups.members.username')}</th>
            {editable && <th scope="col"><span className="erp-visually-hidden">{t('users.actions')}</span></th>}
          </tr></thead>
          <tbody>
            {group.members.map((m) => (
              <tr key={m.id}>
                <th scope="row"><bdi>{m.displayName}</bdi></th>
                <td><bdi className="erp-code">{m.username}</bdi></td>
                {editable && <td><Button variant="quiet" onPress={() => setRemoving(m)}>{t('groups.members.remove')}</Button></td>}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {removing && (
        <FormDialog danger title={t('groups.members.remove.title', { name: removing.displayName })} confirmLabel={t('groups.members.remove')}
          effect={t('groups.members.remove.effect', { name: removing.displayName, group: nameOf(group.name, 'en') || group.code })} onClose={() => setRemoving(null)}
          onConfirm={async () => { await access.removeMember(group.id, removing.id); refresh(); toast.success(t('groups.members.removed')); }} />
      )}
    </div>
  );
}

function RolesTab({ group, canManage }: { group: GroupDetail; canManage: boolean }) {
  const { t } = useText();
  const { locale, timeZone } = useLocaleSettings();
  const { me } = useShell();
  const [give, setGive] = useState(false);
  const [ending, setEnding] = useState<RoleLine | null>(null);
  const companies = useMemo(() => new Map(me.companies.map((c) => [c.id, companyName(c, locale)])), [me.companies, locale]);
  const company = (id: string | null) => (id === null ? t('users.allCompanies') : companies.get(id) ?? id);
  const editable = canManage && group.isActive;
  const lang = locale === 'ur' ? 'ur' : 'en';
  return (
    <div className="erp-stack">
      {editable && <div><Button variant="primary" onPress={() => setGive(true)}>{t('groups.roles.give')}</Button></div>}
      {group.roles.length === 0 ? <EmptyState title={t('groups.roles.empty')}>{t('groups.roles.empty.body')}</EmptyState> : (
        <table className="erp-table">
          <caption className="erp-visually-hidden">{t('groups.tab.roles')}</caption>
          <thead><tr>
            <th scope="col">{t('users.role')}</th><th scope="col">{t('users.company')}</th><th scope="col">{t('users.roles.from')}</th>
            <th scope="col">{t('users.roles.to')}</th><th scope="col">{t('users.reason')}</th><th scope="col">{t('users.roles.state')}</th>
            {editable && <th scope="col"><span className="erp-visually-hidden">{t('users.actions')}</span></th>}
          </tr></thead>
          <tbody>
            {group.roles.map((r) => (
              <tr key={r.assignmentId} className={r.inForce ? undefined : 'erp-ended'}>
                <th scope="row"><bdi>{nameOf(r.roleName, locale) || r.roleCode}</bdi> <span className="erp-small erp-muted erp-code">{r.roleCode}</span></th>
                <td>{company(r.companyId)}</td>
                <td>{formatDate(r.validFrom, lang, timeZone)}</td>
                <td>{r.validTo ? formatDate(r.validTo, lang, timeZone) : t('users.roles.open')}</td>
                <td>{r.reason ?? ''}</td>
                <td>{r.inForce ? t('users.roles.inForce') : t('users.roles.ended')}</td>
                {editable && <td>{r.inForce && <Button variant="quiet" onPress={() => setEnding(r)}>{t('users.roles.end')}</Button>}</td>}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {give && <GiveRole group={group} onClose={() => setGive(false)} />}
      {ending && <EndRole group={group} line={ending} onClose={() => setEnding(null)} />}
    </div>
  );
}

function useAnswer(groupId: string) {
  const { t } = useText();
  const toast = useToast();
  const refresh = useRefresh(groupId);
  return (outcome: string | undefined, done: MessageKey) => {
    refresh();
    if (isPending({ outcome })) toast.info(t('users.roles.pending'));
    else toast.success(t(done));
  };
}

function GiveRole({ group, onClose }: { group: GroupDetail; onClose: () => void }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { me } = useShell();
  const answer = useAnswer(group.id);
  const roles = useQuery({ queryKey: ['iam', 'roles', 'options'], queryFn: users.roles, staleTime: 60_000 });
  const [roleId, setRoleId] = useState<string | null>(null);
  const [companyId, setCompanyId] = useState('all');
  const [from, setFrom] = useState<CalendarDate | null>(null);
  const [to, setTo] = useState<CalendarDate | null>(null);
  const name = nameOf(group.name, locale) || group.code;
  return (
    <ReasonDialog isOpen title={t('groups.roles.give.title', { name })} confirmLabel={t('groups.roles.give')} effect={t('groups.roles.give.effect', { name })} onClose={onClose} isDisabled={!roleId}
      onConfirm={async (reason) => {
        if (!roleId) return;
        const result = await access.assignGroupRole(group.id, { roleId, companyId: companyId === 'all' ? null : companyId, validFrom: from?.toString() ?? null, validTo: to?.toString() ?? null, reason });
        answer(result.outcome, 'groups.roles.given');
      }}>
      <SelectField label={t('users.role')} isRequired value={roleId} onChange={setRoleId} options={(roles.data ?? []).map((r) => ({ id: r.id, label: nameOf(r.nameI18n, locale) || r.code, detail: r.code }))} />
      <SelectField label={t('users.company')} value={companyId} onChange={setCompanyId} options={[{ id: 'all', label: t('users.allCompanies') }, ...me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }))]} />
      <DateField label={t('users.roles.from')} help={t('users.roles.from.help')} value={from} onChange={(v) => setFrom(v ? parseDate(v.toString()) : null)} />
      <DateField label={t('users.roles.to')} help={t('users.roles.to.help')} value={to} onChange={(v) => setTo(v ? parseDate(v.toString()) : null)} />
    </ReasonDialog>
  );
}

function EndRole({ group, line, onClose }: { group: GroupDetail; line: RoleLine; onClose: () => void }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const answer = useAnswer(group.id);
  const [on, setOn] = useState<CalendarDate | null>(null);
  const role = nameOf(line.roleName, locale) || line.roleCode;
  const name = nameOf(group.name, locale) || group.code;
  return (
    <ReasonDialog isOpen danger title={t('users.roles.end.title', { role })} confirmLabel={t('users.roles.end')} effect={t('groups.roles.end.effect', { role, name })} onClose={onClose}
      onConfirm={async (reason) => { answer((await access.endGroupRole(line.assignmentId, on?.toString() ?? null, reason)).outcome, 'groups.roles.ended.done'); }}>
      <DateField label={t('users.roles.endDate')} help={t('users.roles.endDate.help')} value={on} onChange={(v) => setOn(v ? parseDate(v.toString()) : null)} />
    </ReasonDialog>
  );
}
