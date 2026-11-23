// Feature flags (slice S1.15a, Build item 2; FR-KRN-SET-05, FR-ADM-SET-01): each flag with its description, whether the licence
// allows it, its state for the chosen company and where that state comes from, and overrides for the customer or one company.
// A flag the licence does not cover is shown off and cannot be switched on (the server refuses it too).
import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { EngineProvider, useDocumentTitle, type EngineApi } from '@erp/engine';
import { Banner, Button, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, StatusChip, SwitchField, useToast, type Option } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem, sendText } from '../../api/client';
import { nameOf } from '../../api/workflow';
import { settingsApi, type FlagItem } from '../../api/settings';
import { companyName, useShell } from '../../shell/context';
import { ReasonDialog } from './users-parts';
import { Layer } from './settings-parts';
import { usePeopleNames } from './settings';
import './config-messages';
import './users.css';
import './settings.css';

const api: EngineApi = { send: sendText };

export default function FlagsScreen() {
  return <EngineProvider api={api}><Flags /></EngineProvider>;
}

function Flags() {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { me, can, writable } = useShell();
  useDocumentTitle(t('flags.title'));
  const [company, setCompany] = useState('all');
  const [editing, setEditing] = useState<FlagItem | null>(null);
  const [clearing, setClearing] = useState<{ flag: FlagItem; layer: string; scopeId: string | null } | null>(null);
  const toast = useToast();
  const queryClient = useQueryClient();
  const names = usePeopleNames();
  const list = useQuery({ queryKey: ['cfg', 'flags', company], queryFn: () => settingsApi.flags(company === 'all' ? null : company), refetchOnMount: 'always' });
  const canChange = writable && can('cfg.feature_flag_overrides.update');
  const companyOptions: Option[] = [{ id: 'all', label: t('settings.company.all') }, ...me.companies.map((c) => ({ id: c.id, label: companyName(c, locale) }))];
  const problem = list.error instanceof ApiProblem ? list.error : null;
  const scopeLabel = (layer: string, scopeId: string | null) => (layer === 'company' ? companyName(me.companies.find((c) => c.id === scopeId) ?? null, locale) : layer === 'tenant' ? t('settings.company.all') : t('flags.layer.group'));
  return (
    <div className="erp-page">
      <PageHeader title={t('flags.title')} description={t('flags.description')} />
      {me.companies.length > 1 && (
        <div className="erp-settings-tools">
          <SelectField label={t('settings.company.label')} options={companyOptions} value={company} onChange={setCompany} help={t('flags.company.help')} />
        </div>
      )}
      {list.isPending && <Skeleton lines={4} />}
      {list.isError && (
        <ErrorPanel message={problem?.status === 403 ? t('flags.denied') : t('flags.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void list.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {list.isSuccess && list.data.length === 0 && <EmptyState title={t('flags.empty')}>{t('flags.empty.body')}</EmptyState>}
      {list.isSuccess && list.data.length > 0 && (
        <ul className="erp-settings-list" aria-label={t('flags.title')}>
          {list.data.map((f) => (
            <li key={f.key}>
              <div className="erp-settings-row">
                <span className="erp-stack">
                  <span className="erp-settings-name">{nameOf(f.name ?? f.description, locale)}</span>
                  <span className="erp-small erp-muted">{nameOf(f.help ?? f.description, locale)}</span>
                  <span className="erp-small"><span className="erp-code">{f.key}</span></span>
                </span>
                <span className="erp-stack">
                  <span className="erp-row erp-wrap">
                    <StatusChip status={f.enabled ? 'active' : 'inactive'} label={f.enabled ? t('flags.on') : t('flags.off')} />
                    <Layer layer={f.source === 'default' ? 'product' : f.source} />
                    {!f.licensed && <StatusChip status="blocked" label={t('flags.notLicensed')} />}
                  </span>
                  {f.overrides.length > 0 && (
                    <ul className="erp-settings-list" aria-label={t('flags.overrides')}>
                      {f.overrides.map((o) => (
                        <li key={`${o.layer}-${o.scopeId ?? ''}`} className="erp-row erp-wrap">
                          <span>{scopeLabel(o.layer, o.scopeId)}: {o.enabled ? t('flags.on') : t('flags.off')}</span>
                          <span className="erp-small erp-muted">{names.get(o.changed.by) ?? ''}</span>
                          {canChange && <Button variant="quiet" aria-label={t('flags.clear.aria', { name: nameOf(f.name ?? f.description, locale), scope: scopeLabel(o.layer, o.scopeId) })} onPress={() => setClearing({ flag: f, layer: o.layer, scopeId: o.scopeId })}>{t('flags.clear')}</Button>}
                        </li>
                      ))}
                    </ul>
                  )}
                </span>
                {canChange && f.licensed ? <Button variant="secondary" aria-label={t('flags.change.aria', { name: nameOf(f.name ?? f.description, locale) })} onPress={() => setEditing(f)}>{t('flags.change')}</Button> : <span />}
              </div>
            </li>
          ))}
        </ul>
      )}
      {editing && <OverrideDialog flag={editing} onClose={() => setEditing(null)} onSaved={(pending) => { void queryClient.invalidateQueries({ queryKey: ['cfg'] }); toast.success(pending ? t('settings.saved.pending') : t('flags.saved')); }} />}
      {clearing && (
        <ReasonDialog isOpen danger title={t('flags.clear.title')} confirmLabel={t('flags.clear')} effect={t('flags.clear.effect', { scope: scopeLabel(clearing.layer, clearing.scopeId) })} onClose={() => setClearing(null)}
          onConfirm={async (reason) => {
            await settingsApi.resetFlag(clearing.flag.key, { layer: clearing.layer, scopeId: clearing.scopeId }, reason);
            void queryClient.invalidateQueries({ queryKey: ['cfg'] });
            toast.success(t('flags.cleared'));
          }} />
      )}
    </div>
  );
}

function OverrideDialog({ flag, onClose, onSaved }: { flag: FlagItem; onClose: () => void; onSaved: (pending: boolean) => void }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { me } = useShell();
  const [target, setTarget] = useState('tenant');
  const [enabled, setEnabled] = useState(flag.enabled);
  const options: Option[] = [{ id: 'tenant', label: t('settings.company.all') }, ...me.companies.map((c) => ({ id: `company:${c.id}`, label: companyName(c, locale) }))];
  return (
    <ReasonDialog isOpen title={t('flags.override.title', { name: nameOf(flag.name ?? flag.description, locale) })} confirmLabel={t('flags.override.save')} effect={t('flags.override.effect')} onClose={onClose}
      onConfirm={async (reason) => {
        const isCompany = target.startsWith('company:');
        const answer = await settingsApi.setFlag(flag.key, { layer: isCompany ? 'company' : 'tenant', scopeId: isCompany ? target.slice('company:'.length) : null }, enabled, reason);
        onSaved(answer?.outcome === 'pending');
      }}>
      <SelectField label={t('settings.editor.target')} options={options} value={target} onChange={setTarget} />
      <SwitchField label={t('flags.override.enabled')} isSelected={enabled} onChange={setEnabled} />
      {!flag.licensed && <Banner tone="warning">{t('flags.notLicensed')}</Banner>}
    </ReasonDialog>
  );
}
