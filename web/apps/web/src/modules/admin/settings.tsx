// The settings centre (slice S1.15a, Build items 1 and 3; FR-ADM-SET-01 to -04 and -06, FR-KRN-SET-02 and -03, FR-ADM-USR-20,
// -21, -24, FR-ADM-LOC-05): every setting a customer may change, grouped and searchable, each with its effective value and the
// layer it comes from, a typed editor, "override" and "reset to inherited" for the customer and for one company, the history with
// revert, and a clear "waiting for a second administrator" for changes held for approval. The sign-in and security group holds
// the iam.* keys and the number formats. Operator-only settings are never listed (the server leaves them out).
import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { EngineProvider, useDocumentTitle, type EngineApi } from '@erp/engine';
import { Banner, Button, Dialog, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, TextAreaField, TextField, useToast, type Option } from '@erp/ui';
import { useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem, sendText } from '../../api/client';
import { changes } from '../../api/workflow';
import { settingsApi, type SettingHistoryItem, type SettingItem, type Target } from '../../api/settings';
import { users } from '../../api/users';
import { companyName, useShell } from '../../shell/context';
import { ReasonDialog } from './users-parts';
import { ChangedBy, HistoryList, Layer, LayerTable, problemText, unitText, useChangeText, useWords, ValueEditor } from './settings-parts';
import { GROUPS, HIDDEN_KEYS, fromDraft, groupOf, matches, sameValue, shortValue, titleOf, toDraft, type Draft } from './settings-model';
import './config-messages';
import './users.css';
import './settings.css';

const api: EngineApi = { send: sendText };

/** The description of a setting, left out when it only repeats the name. */
const describe = (s: SettingItem, locale: string): string => {
  const text = s.description ? (s.description[locale] ?? s.description.en ?? '') : '';
  return text === titleOf(s, locale) ? '' : text;
};
export const SETTING_CHANGE_KINDS: readonly string[] = ['cfg.setting.change', 'iam.setting.set'];

export default function SettingsScreen() {
  return <EngineProvider api={api}><SettingsCentre /></EngineProvider>;
}

/** The keys of settings with a change waiting for a second administrator (the caller's own and those to decide). */
export function useWaitingKeys(): Set<string> {
  const pending = useQuery({ queryKey: ['cfg', 'pending-changes'], queryFn: changes.pending, retry: false, staleTime: 10_000 });
  return useMemo(() => {
    const keys = new Set<string>();
    for (const request of [...(pending.data?.mine ?? []), ...(pending.data?.toDecide ?? [])]) {
      if (!SETTING_CHANGE_KINDS.includes(request.kind)) continue;
      for (const detail of request.details) if (detail.label === 'changes.detail.settingKey' && detail.text) keys.add(detail.text);
    }
    return keys;
  }, [pending.data]);
}

/** Names of the people who changed things, for the "who" columns (needs the right to read users; otherwise a dash). */
export function usePeopleNames(): Map<string, string> {
  const { can } = useShell();
  const people = useQuery({ queryKey: ['cfg', 'people-names'], queryFn: ({ signal }) => users.list({ limit: 200 }, signal), enabled: can('iam.users.read'), staleTime: 60_000 });
  return useMemo(() => new Map((people.data?.items ?? []).map((p) => [p.id, p.displayName])), [people.data]);
}

function SettingsCentre() {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  const { me, can } = useShell();
  const words = useWords();
  useDocumentTitle(t('settings.title'));
  const [params, setParams] = useSearchParams();
  const company = params.get('company') ?? '';
  const group = params.get('group') ?? 'all';
  const openKey = params.get('key');
  const [search, setSearch] = useState(params.get('q') ?? '');
  const names = usePeopleNames();
  const waiting = useWaitingKeys();
  const list = useQuery({ queryKey: ['cfg', 'settings', company], queryFn: () => settingsApi.list({ companyId: company || undefined }), refetchOnMount: 'always' });
  const patch = (changes: Record<string, string | null>) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [k, v] of Object.entries(changes)) { if (v) next.set(k, v); else next.delete(k); }
    return next;
  }, { replace: true });

  const visible = useMemo(() => (list.data ?? []).filter((s) => !HIDDEN_KEYS.includes(s.key)), [list.data]);
  const present = useMemo(() => GROUPS.filter((g) => visible.some((s) => groupOf(s) === g)), [visible]);
  const sections = useMemo(() => {
    const found = visible.filter((s) => (group === 'all' || groupOf(s) === group) && matches(s, search));
    return present.map((g) => ({ group: g, items: found.filter((s) => groupOf(s) === g).sort((a, b) => titleOf(a, locale).localeCompare(titleOf(b, locale), locale)) })).filter((x) => x.items.length > 0);
  }, [visible, group, search, present, locale]);
  const count = sections.reduce((n, s) => n + s.items.length, 0);
  const groupOptions: Option[] = [{ id: 'all', label: t('settings.group.all') }, ...present.map((g) => ({ id: g, label: t(`settings.group.${g}` as MessageKey) }))];
  const companyOptions: Option[] = [{ id: 'all', label: t('settings.company.all') }, ...me.companies.map((c) => ({ id: c.id, label: companyName(c, locale) }))];
  const problem = list.error instanceof ApiProblem ? list.error : null;
  const selected = visible.find((s) => s.key === openKey) ?? null;

  return (
    <div className="erp-page">
      <PageHeader title={t('settings.title')} description={t('settings.description')} />
      <div className="erp-settings-tools" role="search" aria-label={t('settings.search.label')}>
        <TextField label={t('settings.search.label')} value={search} onChange={(v) => { setSearch(v); patch({ q: v.trim() || null }); }} help={t('settings.search.help')} />
        <SelectField label={t('settings.group.label')} options={groupOptions} value={groupOptions.some((o) => o.id === group) ? group : 'all'} onChange={(g) => patch({ group: g === 'all' ? null : g })} />
        {me.companies.length > 1 && (
          <SelectField label={t('settings.company.label')} options={companyOptions} value={company || 'all'} onChange={(c) => patch({ company: c === 'all' ? null : c })} help={t('settings.company.help')} />
        )}
      </div>
      {list.isPending && <Skeleton lines={6} />}
      {list.isError && (
        <ErrorPanel message={problem?.status === 403 ? t('settings.denied') : t('settings.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {list.isSuccess && count === 0 && (
        <EmptyState title={search.trim() ? t('settings.noMatch') : t('settings.empty')}>{search.trim() ? t('settings.noMatch.body') : t('settings.empty.body')}</EmptyState>
      )}
      {list.isSuccess && count > 0 && <p className="erp-small erp-muted" role="status">{t('settings.count', { count })}</p>}
      {sections.map(({ group: g, items }) => (
        <section key={g} className="erp-settings-group" aria-labelledby={`settings-group-${g}`}>
          <h2 id={`settings-group-${g}`}>{t(`settings.group.${g}` as MessageKey)}</h2>
          <ul className="erp-settings-list">
            {items.map((s) => (
              <li key={s.key}>
                <div className="erp-settings-row">
                  <span className="erp-stack">
                    <span className="erp-settings-name">{titleOf(s, locale)}</span>
                    {describe(s, locale) && <span className="erp-small erp-muted">{describe(s, locale)}</span>}
                  </span>
                  <span className="erp-stack">
                    <span className="erp-settings-value"><bdi>{shortValue(s, s.value, numbers, words)}</bdi>{s.unit && s.type === 'int' ? <> <span className="erp-small erp-muted">{unitText(t, s.unit)}</span></> : null}</span>
                    <span className="erp-row erp-wrap"><Layer layer={s.source} /><span className="erp-small"><ChangedBy info={s.lastChange} names={names} /></span></span>
                    {waiting.has(s.key) && <StatusChip status="pending" label={t('settings.waiting')} />}
                  </span>
                  <Button variant="secondary" aria-label={t(can('cfg.setting_values.update') ? 'settings.open.change' : 'settings.open.view', { name: titleOf(s, locale) })} onPress={() => patch({ key: s.key })}>{can('cfg.setting_values.update') ? t('settings.change') : t('settings.view')}</Button>
                </div>
              </li>
            ))}
          </ul>
        </section>
      ))}
      {selected && <SettingDrawer setting={selected} companyId={company || null} names={names} waiting={waiting.has(selected.key)} onClose={() => patch({ key: null })} />}
    </div>
  );
}

export function SettingDrawer({ setting, companyId, names, waiting, onClose }: { setting: SettingItem; companyId: string | null; names: Map<string, string>; waiting: boolean; onClose: () => void }) {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  const { me, can, writable } = useShell();
  const toast = useToast();
  const queryClient = useQueryClient();
  const changeText = useChangeText();
  const words = useWords();
  const canWrite = writable && can('cfg.setting_values.update');
  const allowsCompany = setting.layers.includes('company');
  const allowsTenant = setting.layers.includes('tenant');
  const [target, setTarget] = useState(allowsCompany && companyId ? `company:${companyId}` : allowsTenant ? 'tenant' : `company:${me.companies[0]?.id ?? ''}`);
  const layer = target.startsWith('company:') ? 'company' : 'tenant';
  const scopeId = layer === 'company' ? target.slice('company:'.length) : null;
  const effectiveCompany = layer === 'company' ? scopeId : companyId;
  const detail = useQuery({ queryKey: ['cfg', 'setting', setting.key, effectiveCompany], queryFn: () => settingsApi.detail(setting.key, effectiveCompany), refetchOnMount: 'always' });
  const history = useQuery({ queryKey: ['cfg', 'history', setting.key], queryFn: () => settingsApi.history(setting.key), retry: false });
  const own = detail.data?.layers.find((l) => l.layer === layer && l.scopeId === scopeId && !l.ignored) ?? null;
  const start = own?.value ?? (detail.data?.setting.value ?? setting.value);
  return (
    <Dialog drawer isOpen title={titleOf(setting, locale)} onOpenChange={(open) => { if (!open) onClose(); }}>
      {() => (
        <>
          <p>{setting.help ? (setting.help[locale] ?? setting.help.en ?? '') : (setting.description[locale] ?? setting.description.en ?? '')}</p>
          <p className="erp-small erp-muted"><span className="erp-code">{setting.key}</span> · {t('settings.default', { value: shortValue(setting, setting.default, numbers, words) })}</p>
          {waiting && <Banner tone="info" title={t('settings.waiting')}>{t('settings.waiting.body')}</Banner>}
          {setting.requiresApproval && <Banner tone="info">{t('settings.approval.note')}</Banner>}
          {detail.isPending && <Skeleton lines={3} />}
          {detail.isError && <ErrorPanel message={t('settings.loadError')} action={<Button variant="primary" onPress={() => { void detail.refetch(); }}>{t('common.retry')}</Button>} />}
          {detail.data && (
            <>
              <h3 className="erp-h3">{t('settings.layers.title')}</h3>
              <LayerTable setting={setting} layers={detail.data.layers} names={names} />
              <Editor key={`${setting.key}|${target}|${JSON.stringify(start)}`} setting={setting} layer={layer} scopeId={scopeId} hasOwn={own !== null} start={start} canWrite={canWrite}
                targets={[...(allowsTenant ? [{ id: 'tenant', label: t('settings.company.all') }] : []), ...(allowsCompany ? me.companies.map((c) => ({ id: `company:${c.id}`, label: companyName(c, locale) })) : [])]}
                target={target} onTarget={setTarget}
                onDone={(answer) => {
                  void queryClient.invalidateQueries({ queryKey: ['cfg'] });
                  toast.success(answer === 'pending' ? t('settings.saved.pending') : t('settings.saved'));
                }} changeText={changeText} />
            </>
          )}
          <h3 className="erp-h3">{t('settings.history.title')}</h3>
          {history.isPending && <Skeleton lines={2} />}
          {history.isError && <p className="erp-muted">{history.error instanceof ApiProblem && history.error.status === 403 ? t('settings.history.denied') : t('settings.history.error')}</p>}
          {history.data && <RevertableHistory setting={setting} items={history.data} names={names} canWrite={canWrite} />}
          <p className="erp-small erp-muted">{t('settings.note.scopes')}</p>
        </>
      )}
    </Dialog>
  );
}

function Editor({ setting, layer, scopeId, hasOwn, start, canWrite, targets, target, onTarget, onDone, changeText }: {
  setting: SettingItem; layer: 'tenant' | 'company'; scopeId: string | null; hasOwn: boolean; start: unknown; canWrite: boolean;
  targets: Option[]; target: string; onTarget: (id: string) => void; onDone: (outcome: 'done' | 'pending') => void; changeText: (e: unknown) => string;
}) {
  const { t } = useText();
  const [draft, setDraft] = useState<Draft>(() => toDraft(setting, start));
  const [reason, setReason] = useState('');
  const [problem, setProblem] = useState<string | null>(null);
  const [busy, setBusy] = useState<'save' | 'reset' | null>(null);
  const dest: Target = { layer, scopeId };
  const parsed = fromDraft(setting, draft);
  const unchanged = parsed.ok && sameValue(parsed.value, start) && hasOwn;
  const run = async (kind: 'save' | 'reset') => {
    setProblem(null);
    if (reason.trim() === '') { setProblem(t('settings.problem.reason')); return; }
    if (kind === 'save' && !parsed.ok) { setProblem(problemText(t, parsed)); return; }
    setBusy(kind);
    try {
      const answer = kind === 'save' && parsed.ok ? await settingsApi.set(setting.key, dest, parsed.value, reason.trim()) : await settingsApi.reset(setting.key, dest, reason.trim());
      onDone(answer?.outcome === 'pending' ? 'pending' : 'done');
      setReason('');
    } catch (error) {
      setProblem(changeText(error));
    } finally {
      setBusy(null);
    }
  };
  const fieldError = !parsed.ok && draftTouched(draft, start, setting) ? problemText(t, parsed) : null;
  return (
    <form className="erp-stack" onSubmit={(e) => { e.preventDefault(); void run('save'); }} aria-label={t('settings.editor.title')}>
      <h3 className="erp-h3">{t('settings.editor.title')}</h3>
      {targets.length > 1 && <SelectField label={t('settings.editor.target')} options={targets} value={target} onChange={onTarget} isDisabled={!canWrite} />}
      <ValueEditor setting={setting} draft={draft} onChange={setDraft} error={fieldError} isDisabled={!canWrite} />
      {canWrite && (
        <>
          <TextAreaField label={t('settings.editor.reason')} help={t('settings.editor.reason.help')} value={reason} onChange={setReason} isRequired />
          {problem && <p className="erp-field-error" role="alert">{problem}</p>}
          <div className="erp-row erp-wrap">
            <Button type="submit" variant="primary" isPending={busy === 'save'} isDisabled={busy !== null || unchanged}>{layer === 'company' ? t('settings.editor.saveCompany') : t('settings.editor.save')}</Button>
            {hasOwn && <Button variant="secondary" isPending={busy === 'reset'} isDisabled={busy !== null} onPress={() => { void run('reset'); }}>{t('settings.editor.reset')}</Button>}
          </div>
          {hasOwn && <p className="erp-small erp-muted">{t('settings.editor.reset.help')}</p>}
        </>
      )}
      {!canWrite && <p className="erp-small erp-muted">{t('settings.readOnly')}</p>}
    </form>
  );
}

const draftTouched = (draft: Draft, start: unknown, setting: SettingItem): boolean => !sameValue(draft, toDraft(setting, start));

function RevertableHistory({ setting, items, names, canWrite }: { setting: SettingItem; items: SettingHistoryItem[]; names: Map<string, string>; canWrite: boolean }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [reverting, setReverting] = useState<SettingHistoryItem | null>(null);
  return (
    <>
      <HistoryList setting={setting} items={items} names={names} onRevert={canWrite ? setReverting : null} />
      {reverting && (
        <ReasonDialog isOpen danger title={t('settings.revert.title')} confirmLabel={t('settings.history.revert')} effect={t('settings.revert.effect')} onClose={() => setReverting(null)}
          onConfirm={async (reason) => {
            const answer = await settingsApi.revert(reverting.id, reason);
            void queryClient.invalidateQueries({ queryKey: ['cfg'] });
            toast.success(answer?.outcome === 'pending' ? t('settings.saved.pending') : t('settings.reverted'));
          }} />
      )}
    </>
  );
}
