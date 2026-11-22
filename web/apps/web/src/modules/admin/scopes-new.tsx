// Create a data scope (slice S1.13b Build item 8; FR-ADM-ROL-06). Scopes are never edited: a different rule is a new scope.
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Banner, Button, CheckboxField, ErrorPanel, PageHeader, SelectField, TextField, useToast } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import { access, type ScopeKind } from '../../api/access';
import { nameOf } from '../../api/workflow';
import { companyName, useShell } from '../../shell/context';
import { useProblemText } from './users-parts';
import { KINDS, SCOPES_PATH, kindHelpKey, kindKey, scopesKey, useOrgItems } from './scopes-parts';

function Picks({ label, items, chosen, onChange }: { label: string; items: { id: string; label: string }[]; chosen: Set<string>; onChange: (next: Set<string>) => void }) {
  const { t } = useText();
  return (
    <fieldset className="erp-stack">
      <legend>{label}</legend>
      {items.length === 0 && <p className="erp-muted">{t('scopes.new.noChoices')}</p>}
      {items.map((i) => (
        <CheckboxField key={i.id} label={i.label} isSelected={chosen.has(i.id)}
          onChange={(on) => { const next = new Set(chosen); if (on) next.add(i.id); else next.delete(i.id); onChange(next); }} />
      ))}
    </fieldset>
  );
}

export function ScopeCreate() {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { me } = useShell();
  const navigate = useNavigate();
  const toast = useToast();
  const queryClient = useQueryClient();
  const problemText = useProblemText();
  const [code, setCode] = useState('');
  const [en, setEn] = useState('');
  const [ur, setUr] = useState('');
  const [kind, setKind] = useState<ScopeKind>('own');
  const [chosen, setChosen] = useState<Set<string>>(new Set());
  const [children, setChildren] = useState(false);
  const branches = useOrgItems('branches', kind === 'branches');
  const units = useOrgItems('org-units', kind === 'org_units');

  const definition = (): Record<string, unknown> => {
    const ids = [...chosen];
    if (kind === 'companies') return { company_ids: ids };
    if (kind === 'branches') return { branch_ids: ids };
    if (kind === 'org_units') return { org_unit_ids: ids, include_children: children };
    return {};
  };
  const needsIds = kind === 'companies' || kind === 'branches' || kind === 'org_units';
  const create = useMutation({
    mutationFn: () => access.createScope({ code: code.trim(), name: { en: en.trim(), ...(ur.trim() ? { ur: ur.trim() } : {}) }, kind, definition: definition() }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: scopesKey });
      toast.success(t('scopes.new.done'));
      void navigate(SCOPES_PATH);
    },
  });
  const ready = code.trim() !== '' && en.trim() !== '' && (!needsIds || chosen.size > 0);
  const options = (items: { id: string; code: string; nameI18n: Record<string, string> }[] | undefined) => (items ?? []).map((i) => ({ id: i.id, label: `${nameOf(i.nameI18n, locale) || i.code} (${i.code})` }));

  return (
    <div className="erp-page">
      <PageHeader title={t('scopes.new.title')} breadcrumbs={[{ label: t('scopes.title'), href: SCOPES_PATH }, { label: t('scopes.new.title') }]} />
      <Banner tone="info" title={t('scopes.new.never')}>{t('scopes.new.never.body')}</Banner>
      <form className="erp-stack" onSubmit={(e) => { e.preventDefault(); if (ready) create.mutate(); }}>
        <TextField label={t('scopes.code')} isRequired isCode value={code} onChange={setCode} maxLength={64} />
        <TextField label={t('scopes.name.en')} isRequired value={en} onChange={setEn} maxLength={200} />
        <TextField label={t('scopes.name.ur')} value={ur} onChange={setUr} maxLength={200} />
        <SelectField label={t('scopes.kind')} isRequired value={kind} help={t(kindHelpKey(kind))}
          onChange={(v) => { setKind(v as ScopeKind); setChosen(new Set()); }} options={KINDS.map((k) => ({ id: k, label: t(kindKey(k)) }))} />
        {kind === 'companies' && <Picks label={t('scopes.pick.companies')} items={me.companies.map((c) => ({ id: c.id, label: `${companyName(c, locale)} (${c.code})` }))} chosen={chosen} onChange={setChosen} />}
        {kind === 'branches' && <Picks label={t('scopes.pick.branches')} items={options(branches.data)} chosen={chosen} onChange={setChosen} />}
        {kind === 'org_units' && (
          <>
            <Picks label={t('scopes.pick.units')} items={options(units.data)} chosen={chosen} onChange={setChosen} />
            <CheckboxField label={t('scopes.includeChildren')} help={t('scopes.includeChildren.help')} isSelected={children} onChange={setChildren} />
          </>
        )}
        {create.isError && <ErrorPanel message={problemText(create.error)} />}
        <div className="erp-row">
          <Button type="submit" variant="primary" isPending={create.isPending} isDisabled={!ready}>{t('scopes.new.create')}</Button>
          <Button variant="secondary" onPress={() => { void navigate(SCOPES_PATH); }}>{t('common.cancel')}</Button>
        </div>
      </form>
    </div>
  );
}
