// The rules of one notification event (slice S1.15d Build item 11; FR-KRN-NTF-03, ruling Q14 of S1.11): who is told besides the people the event names.
// A rule lists roles and named people, the channels and whether it cannot be switched off. A rule's condition (workflow condition format) is kept as stored:
// this screen does not edit it (S1.15d detail in CR-071).
import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus } from 'lucide-react';
import { Banner, Button, CheckboxField, EmptyState, SelectField, Skeleton, StatusChip, SwitchField, TextField, useToast, type Option } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { CHANNELS, notifyApi, type CatalogueEvent, type Channel, type Rule, type RuleWrite } from '../../api/templates';
import { users } from '../../api/users';
import { nameOf } from '../../api/workflow';
import { companyName, useShell } from '../../shell/context';
import { useRefused } from './config-parts';
import { PersonPicker } from './users-parts';
import { ConfirmDialog } from './tpl-parts';

const ALL = 'all';

export function RulesPanel({ event }: { event: CatalogueEvent }) {
  const { t } = useText();
  const { can, writable } = useShell();
  const [editing, setEditing] = useState<Rule | 'new' | null>(null);
  const [deleting, setDeleting] = useState<Rule | null>(null);
  const toast = useToast();
  const queryClient = useQueryClient();
  const rules = useQuery({ queryKey: ['ntf', 'rules'], queryFn: notifyApi.rules, refetchOnMount: 'always' });
  const roles = useQuery({ queryKey: ['ntf', 'roles'], queryFn: users.roles, retry: false });
  const canWrite = writable;
  if (rules.isPending) return <Skeleton lines={3} />;
  if (rules.error instanceof ApiProblem) return <Banner tone="danger">{rules.error.status === 403 ? t('ntfx.denied') : t('ntfx.loadError')}</Banner>;
  const mine = (rules.data ?? []).filter((r) => r.eventCode === event.code);
  const roleName = (code: string) => { const r = roles.data?.find((x) => x.code === code); return r ? nameOf(r.nameI18n, 'en') : code; };
  return (
    <div className="erp-stack">
      <p className="erp-muted">{t('ntfx.rules.intro')}</p>
      {mine.length === 0 && !editing && <EmptyState title={t('ntfx.rules.none')}>{t('ntfx.rules.none.body')}</EmptyState>}
      <ul className="erp-config-list" aria-label={t('ntfx.rules.list')}>
        {mine.map((r) => (
          <li key={r.id}>
            <div className="erp-config-card">
              <span className="erp-settings-name"><bdi>{r.name ?? t('ntfx.rule.unnamed')}</bdi></span>
              <span className="erp-small">{t('ntfx.rule.who', { roles: (r.audience.roles ?? []).map(roleName).join(', ') || '—', people: r.audience.users?.length ?? 0 })}</span>
              <span className="erp-row erp-wrap">
                {r.channels.map((c) => <StatusChip key={c} status="active" label={t(`ntfx.channel.${c}` as MessageKey)} />)}
                {r.mandatory && <StatusChip status="blocked" label={t('ntfx.mandatory')} />}
                {r.condition != null && <span className="erp-small erp-muted">{t('ntfx.rule.condition')}</span>}
              </span>
              {canWrite && can('ntf.subscriptions.update') && <Button variant="secondary" aria-label={t('ntfx.rule.edit.aria', { name: r.name ?? t('ntfx.rule.unnamed') })} onPress={() => setEditing(r)}>{t('ntfx.rule.edit')}</Button>}
              {canWrite && can('ntf.subscriptions.delete') && <Button variant="quiet" aria-label={t('ntfx.rule.delete.aria', { name: r.name ?? t('ntfx.rule.unnamed') })} onPress={() => setDeleting(r)}>{t('ntfx.rule.delete')}</Button>}
            </div>
          </li>
        ))}
      </ul>
      {canWrite && can('ntf.subscriptions.create') && !editing && <div><Button variant="secondary" icon={<Plus size={16} aria-hidden="true" />} onPress={() => setEditing('new')}>{t('ntfx.rule.add')}</Button></div>}
      {editing && (
        <RuleForm key={editing === 'new' ? 'new' : editing.id} event={event} rule={editing === 'new' ? null : editing} roleOptions={(roles.data ?? []).map((r) => ({ id: r.code, label: nameOf(r.nameI18n, 'en'), detail: r.code }))}
          onClose={() => setEditing(null)} onSaved={async () => { setEditing(null); await queryClient.invalidateQueries({ queryKey: ['ntf', 'rules'] }); toast.success(t('ntfx.rule.saved')); }} />
      )}
      {deleting && (
        <ConfirmDialog title={t('ntfx.rule.delete.title')} effect={t('ntfx.rule.delete.effect')} confirmLabel={t('ntfx.rule.delete.confirm')} danger onClose={() => setDeleting(null)}
          onConfirm={async () => { await notifyApi.deleteRule(deleting.id, deleting.rowVersion); await queryClient.invalidateQueries({ queryKey: ['ntf', 'rules'] }); toast.success(t('ntfx.rule.deleted')); }} />
      )}
    </div>
  );
}

function RuleForm({ event, rule, roleOptions, onClose, onSaved }: { event: CatalogueEvent; rule: Rule | null; roleOptions: Option[]; onClose: () => void; onSaved: () => Promise<void> }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { me } = useShell();
  const refused = useRefused();
  const [name, setName] = useState(rule?.name ?? '');
  const [company, setCompany] = useState(rule?.companyId ?? ALL);
  const [roles, setRoles] = useState<string[]>(rule?.audience.roles ?? []);
  const [people, setPeople] = useState<Record<string, string>>(() => Object.fromEntries((rule?.audience.users ?? []).map((id) => [id, ''])));
  const [channels, setChannels] = useState<Channel[]>(rule?.channels ?? ['in_app']);
  const [mandatory, setMandatory] = useState(rule?.mandatory ?? false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [picked, setPicked] = useState<Option | null>(null);
  useEffect(() => {
    for (const id of Object.keys(people).filter((x) => people[x] === '')) {
      users.get(id).then((u) => setPeople((p) => ({ ...p, [id]: u.displayName }))).catch(() => setPeople((p) => ({ ...p, [id]: t('ntfx.rule.person.unknown') })));
    }
  }, [people, t]);
  const companies: Option[] = [{ id: ALL, label: t('ntfx.rule.company.all') }, ...me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }))];
  const toggle = <T extends string>(list: T[], item: T, on: boolean): T[] => (on ? [...list.filter((x) => x !== item), item] : list.filter((x) => x !== item));
  const save = async () => {
    setBusy(true);
    setError(null);
    const body: RuleWrite = {
      name: name.trim() || null, eventCode: event.code, companyId: company === ALL ? null : company, condition: rule?.condition ?? null,
      audience: { users: Object.keys(people), roles, groups: rule?.audience.groups ?? null, relationships: rule?.audience.relationships ?? null }, channels, mandatory,
      ...(rule ? { rowVersion: rule.rowVersion } : {}),
    };
    try { if (rule) await notifyApi.changeRule(rule.id, body); else await notifyApi.addRule(body); await onSaved(); } catch (e) { setError(refused(e).summary); } finally { setBusy(false); }
  };
  const nobody = roles.length === 0 && Object.keys(people).length === 0 && !(rule?.audience.groups?.length) && !(rule?.audience.relationships?.length);
  return (
    <form className="erp-stack erp-config-card" aria-label={t(rule ? 'ntfx.rule.edit.title' : 'ntfx.rule.add')} onSubmit={(e) => { e.preventDefault(); void save(); }}>
      {error && <Banner tone="danger">{error}</Banner>}
      <TextField label={t('ntfx.rule.name')} value={name} onChange={setName} maxLength={100} />
      {me.companies.length > 1 && <SelectField label={t('ntfx.rule.company')} options={companies} value={company} onChange={setCompany} />}
      <fieldset className="erp-stack">
        <legend>{t('ntfx.rule.roles')}</legend>
        {roleOptions.map((r) => <CheckboxField key={r.id} label={r.label} help={r.detail ?? ''} isSelected={roles.includes(r.id)} onChange={(on) => setRoles(toggle(roles, r.id, on))} />)}
      </fieldset>
      <div className="erp-stack">
        <PersonPicker label={t('ntfx.rule.addPerson')} value={picked} onChange={(o) => { if (o) { setPeople({ ...people, [o.id]: o.label }); setPicked(null); } }} />
        <ul className="erp-row erp-wrap" aria-label={t('ntfx.rule.people')}>
          {Object.entries(people).map(([id, label]) => (
            <li key={id}><bdi>{label}</bdi> <Button variant="quiet" aria-label={t('ntfx.rule.person.remove', { name: label })} onPress={() => setPeople(Object.fromEntries(Object.entries(people).filter(([k]) => k !== id)))}>{t('ntfx.rule.person.removeShort')}</Button></li>
          ))}
        </ul>
      </div>
      <fieldset className="erp-stack">
        <legend>{t('ntfx.rule.channels')}</legend>
        {CHANNELS.map((c) => <CheckboxField key={c} label={t(`ntfx.channel.${c}` as MessageKey)} isSelected={channels.includes(c)} onChange={(on) => setChannels(toggle(channels, c, on))} />)}
      </fieldset>
      <SwitchField label={t('ntfx.rule.mandatory')} isSelected={mandatory} onChange={setMandatory} />
      {nobody && <p className="erp-small erp-muted">{t('ntfx.rule.nobody')}</p>}
      <div className="erp-route-actions">
        <Button variant="primary" type="submit" isPending={busy} isDisabled={nobody || channels.length === 0}>{t('ntfx.rule.save')}</Button>
        <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
      </div>
    </form>
  );
}
