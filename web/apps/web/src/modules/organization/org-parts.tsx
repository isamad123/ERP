// Parts of the organization screens (slice S1.14a): the registrations and bank accounts of a company (values masked, in full
// or hidden as field security decides; a bank account change may wait for a second administrator), the settings a company
// overrides, the company logo, and small pickers. The server checks every request again.
import { useMemo, useState, type ReactNode } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { parseDate } from '@internationalized/date';
import { Pencil, Plus, Trash2 } from 'lucide-react';
import { DataGrid, type FieldEditorProps, type GridColumn } from '@erp/engine';
import { banks as bankApi, calendars as calendarApi } from '@erp/generated/api/ref';
import { Banner, Button, DateField, Dialog, EmptyState, ErrorPanel, Link, SelectField, StatusChip, TextField, useToast, type Option } from '@erp/ui';
import { formatDate, useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { org, REGISTRATION_KINDS, type BankAccount, type BankAccountBody, type Registration, type RegistrationBody, type RegistrationKind } from '../../api/org';
import type { AccessAnswer } from '../../api/access';
import { ReasonDialog, useProblemText } from '../admin/users-parts';

const safeDate = (value: string | null | undefined) => {
  try {
    return value ? parseDate(value.slice(0, 10)) : null;
  } catch {
    return null;
  }
};

export const registrationsKey = (companyId: string) => ['org', 'registrations', companyId] as const;
export const bankAccountsKey = (companyId: string) => ['org', 'bank-accounts', companyId] as const;

/** What a value looks like to this person: in full, masked by field security, or hidden. */
function ValueText({ value, access }: { value: string | null; access: 'full' | 'masked' | 'hidden' }) {
  const { t } = useText();
  if (access === 'hidden' || value === null) return <span className="erp-muted">{t('org.value.hidden')}</span>;
  return <bdi dir="ltr" className="erp-code">{value}</bdi>;
}

/** A date without a time ("2027-01-01") in the person's language: written for noon UTC so no time zone moves it to another day. */
export function useDates() {
  const { locale, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  return (value: string | null) => (value ? formatDate(`${value.slice(0, 10)}T12:00:00Z`, lang, 'UTC', numbers) : '—');
}

// ---------------------------------------------------------------------------------------------------- registrations

export function RegistrationsPanel({ companyId, canCreate, canUpdate }: { companyId: string; canCreate: boolean; canUpdate: boolean }) {
  const { t } = useText();
  const date = useDates();
  const [editing, setEditing] = useState<Registration | 'new' | null>(null);
  const list = useQuery({ queryKey: registrationsKey(companyId), queryFn: ({ signal }) => org.registrations(companyId, signal) });
  const columns = useMemo<GridColumn<Registration>[]>(() => [
    { id: 'kind', header: t('org.reg.kind'), width: 190, cell: (r) => t(`org.reg.kind.${r.kind}`) },
    { id: 'authority', header: t('org.reg.authority'), width: 140, cell: (r) => <bdi dir="ltr">{r.authority}</bdi> },
    { id: 'value', header: t('org.reg.value'), width: 200, cell: (r) => <ValueText value={r.value} access={r.valueAccess} /> },
    { id: 'from', header: t('org.reg.validFrom'), width: 140, cell: (r) => date(r.validFrom) },
    { id: 'to', header: t('org.reg.validTo'), width: 140, cell: (r) => date(r.validTo) },
    { id: 'actions', header: t('org.col.actions'), width: 130, cell: (r) => canUpdate
      ? <Button variant="quiet" icon={<Pencil size={16} aria-hidden="true" />} onPress={() => setEditing(r)}>{t('org.edit')}</Button> : null },
  ], [t, date, canUpdate]);
  return (
    <section className="erp-section" aria-labelledby="org-reg-title">
      <div className="erp-row erp-section-head">
        <h2 id="org-reg-title" className="erp-h2">{t('org.reg.title')}</h2>
        {canCreate && <Button variant="secondary" icon={<Plus size={16} aria-hidden="true" />} onPress={() => setEditing('new')}>{t('org.reg.add')}</Button>}
      </div>
      <p className="erp-help">{t('org.reg.help')}</p>
      <DataGrid label={t('org.reg.title')} columns={columns} rows={list.data ?? []} rowKey={(r) => r.id} rowLabel={(r) => `${t(`org.reg.kind.${r.kind}`)} ${r.authority}`}
        status={list.isPending ? 'loading' : list.isError ? 'error' : 'ready'}
        error={<ErrorPanel message={list.error instanceof ApiProblem && list.error.status === 403 ? t('org.denied') : t('org.loadError')} />}
        empty={<EmptyState title={t('org.reg.empty')}>{t('org.reg.empty.body')}</EmptyState>} />
      {editing !== null && <RegistrationDialog companyId={companyId} registration={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </section>
  );
}

function RegistrationDialog({ companyId, registration, onClose }: { companyId: string; registration: Registration | null; onClose: () => void }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const problemText = useProblemText();
  const creating = registration === null;
  const [kind, setKind] = useState<RegistrationKind>(registration?.kind ?? 'ntn');
  const [authority, setAuthority] = useState(registration?.authority ?? '');
  const [value, setValue] = useState('');
  const [from, setFrom] = useState(registration?.validFrom ?? '');
  const [to, setTo] = useState(registration?.validTo ?? '');
  const [error, setError] = useState<string | null>(null);
  const canChangeValue = creating || registration.valueAccess === 'full';
  const save = useMutation({
    mutationFn: () => {
      const body: RegistrationBody = { validFrom: from || null, validTo: to || null };
      if (creating) Object.assign(body, { kind, authority: authority.trim(), value: value.trim() });
      else {
        body.rowVersion = registration.rowVersion;
        if (value.trim()) body.value = value.trim();
      }
      return creating ? org.addRegistration(companyId, body) : org.changeRegistration(registration.id, body);
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: registrationsKey(companyId) });
      toast.success(t(creating ? 'org.reg.added' : 'org.reg.saved'));
      onClose();
    },
    onError: (e) => { setError(problemText(e)); },
  });
  const options = REGISTRATION_KINDS.map((k): Option => ({ id: k, label: t(`org.reg.kind.${k}`) }));
  return (
    <Dialog title={t(creating ? 'org.reg.addTitle' : 'org.reg.editTitle')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" isPending={save.isPending} isDisabled={creating ? !authority.trim() || !value.trim() : false} onPress={() => { setError(null); save.mutate(); }}>{t('org.save')}</Button>
        </>
      )}>
      <div className="erp-stack">
        <SelectField label={t('org.reg.kind')} options={options} value={kind} onChange={(id) => setKind(id as RegistrationKind)} isDisabled={!creating} />
        <TextField label={t('org.reg.authority')} help={t('org.reg.authority.help')} isCode isRequired isReadOnly={!creating} value={authority} onChange={setAuthority} maxLength={40} />
        <TextField label={t('org.reg.value')} help={canChangeValue ? (creating ? undefined : t('org.reg.value.keep')) : t('org.reg.value.locked')} isCode isRequired={creating} isReadOnly={!canChangeValue}
          value={value} onChange={setValue} maxLength={60} />
        <DateField label={t('org.reg.validFrom')} value={safeDate(from)} onChange={(d) => setFrom(d ? d.toString() : '')} />
        <DateField label={t('org.reg.validTo')} help={t('org.reg.validTo.help')} value={safeDate(to)} onChange={(d) => setTo(d ? d.toString() : '')} />
        {error && <Banner tone="danger" title={error}>{null}</Banner>}
      </div>
    </Dialog>
  );
}

// ---------------------------------------------------------------------------------------------------- bank accounts

export function BankAccountsPanel({ companyId, canCreate, canUpdate, canDelete }: { companyId: string; canCreate: boolean; canUpdate: boolean; canDelete: boolean }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<BankAccount | 'new' | null>(null);
  const [removing, setRemoving] = useState<BankAccount | null>(null);
  const list = useQuery({ queryKey: bankAccountsKey(companyId), queryFn: ({ signal }) => org.bankAccounts(companyId, signal) });
  const columns = useMemo<GridColumn<BankAccount>[]>(() => [
    { id: 'bank', header: t('org.bank.bank'), width: 140, cell: (r) => <bdi dir="ltr">{r.bankCode}</bdi> },
    { id: 'title', header: t('org.bank.title'), width: 220, cell: (r) => <bdi>{r.accountTitle}</bdi> },
    { id: 'number', header: t('org.bank.number'), width: 180, cell: (r) => <ValueText value={r.accountNumber} access={r.accountAccess} /> },
    { id: 'iban', header: t('org.bank.iban'), width: 240, cell: (r) => (r.iban === null && r.accountAccess === 'hidden' ? <span className="erp-muted">{t('org.value.hidden')}</span> : r.iban === null ? '—' : <ValueText value={r.iban} access={r.accountAccess} />) },
    { id: 'currency', header: t('org.bank.currency'), width: 110, cell: (r) => <bdi dir="ltr">{r.currencyCode}</bdi> },
    { id: 'actions', header: t('org.col.actions'), width: 250, cell: (r) => (
      <span className="erp-row">
        {canUpdate && <Button variant="quiet" icon={<Pencil size={16} aria-hidden="true" />} onPress={() => setEditing(r)}>{t('org.edit')}</Button>}
        {canDelete && <Button variant="quiet" icon={<Trash2 size={16} aria-hidden="true" />} onPress={() => setRemoving(r)}>{t('org.bank.remove')}</Button>}
      </span>
    ) },
  ], [t, canUpdate, canDelete]);
  const done = async (answer: AccessAnswer, saved: 'org.bank.added' | 'org.bank.saved' | 'org.bank.removed') => {
    await queryClient.invalidateQueries({ queryKey: bankAccountsKey(companyId) });
    toast.success(t(answer.outcome === 'pending' ? 'org.bank.waiting' : saved));
  };
  return (
    <section className="erp-section" aria-labelledby="org-bank-title">
      <div className="erp-row erp-section-head">
        <h2 id="org-bank-title" className="erp-h2">{t('org.bank.panel')}</h2>
        {canCreate && <Button variant="secondary" icon={<Plus size={16} aria-hidden="true" />} onPress={() => setEditing('new')}>{t('org.bank.add')}</Button>}
      </div>
      <p className="erp-help">{t('org.bank.help')}</p>
      <DataGrid label={t('org.bank.panel')} columns={columns} rows={list.data ?? []} rowKey={(r) => r.id} rowLabel={(r) => `${r.bankCode} ${r.accountTitle}`}
        status={list.isPending ? 'loading' : list.isError ? 'error' : 'ready'}
        error={<ErrorPanel message={list.error instanceof ApiProblem && list.error.status === 403 ? t('org.denied') : t('org.loadError')} />}
        empty={<EmptyState title={t('org.bank.empty')}>{t('org.bank.empty.body')}</EmptyState>} />
      {editing !== null && <BankAccountDialog companyId={companyId} account={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onDone={(a) => done(a, editing === 'new' ? 'org.bank.added' : 'org.bank.saved')} />}
      {removing && (
        <ReasonDialog title={t('org.bank.removeTitle', { account: removing.accountTitle })} effect={t('org.bank.removeEffect')} confirmLabel={t('org.bank.remove')} danger reasonOptional isOpen onClose={() => setRemoving(null)}
          onConfirm={async (reason) => { await done(await org.removeBankAccount(removing.id, { rowVersion: removing.rowVersion, ...(reason ? { reason } : {}) }), 'org.bank.removed'); }} />
      )}
    </section>
  );
}

function BankAccountDialog({ companyId, account, onClose, onDone }: { companyId: string; account: BankAccount | null; onClose: () => void; onDone: (answer: AccessAnswer) => Promise<void> }) {
  const { t } = useText();
  const problemText = useProblemText();
  const creating = account === null;
  const [bank, setBank] = useState(account?.bankCode ?? '');
  const [title, setTitle] = useState(account?.accountTitle ?? '');
  const [number, setNumber] = useState('');
  const [iban, setIban] = useState('');
  const [currency, setCurrency] = useState(account?.currencyCode ?? 'PKR');
  const [format, setFormat] = useState(account?.salaryFileFormat ?? '');
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const banks = useQuery({ queryKey: ['ref', 'banks'], queryFn: () => bankApi.list({ limit: 200, sort: 'code' }), staleTime: 300_000 });
  const options = useMemo(() => (banks.data?.items ?? []).map((b): Option => ({ id: b.code, label: b.code, detail: Object.values(b.nameI18n)[0] ?? '' })), [banks.data]);
  const canChangeNumbers = creating || account.accountAccess === 'full';
  const save = useMutation({
    mutationFn: () => {
      const body: BankAccountBody = { bankCode: bank, accountTitle: title.trim(), currencyCode: currency.trim().toUpperCase(), salaryFileFormat: format.trim() || null, ...(reason.trim() ? { reason: reason.trim() } : {}) };
      if (number.trim()) body.accountNumber = number.trim();
      if (iban.trim()) body.iban = iban.trim();
      if (!creating) body.rowVersion = account.rowVersion;
      return creating ? org.addBankAccount(companyId, body) : org.changeBankAccount(account.id, body);
    },
    onSuccess: async (answer) => { await onDone(answer); onClose(); },
    onError: (e) => { setError(problemText(e)); },
  });
  return (
    <Dialog title={t(creating ? 'org.bank.addTitle' : 'org.bank.editTitle')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" isPending={save.isPending} isDisabled={!bank || !title.trim() || (creating && !number.trim())} onPress={() => { setError(null); save.mutate(); }}>{t('org.save')}</Button>
        </>
      )}>
      <div className="erp-stack">
        <Banner tone="info" title={t('org.bank.approval')}>{null}</Banner>
        <SelectField label={t('org.bank.bank')} isRequired options={options} value={bank || null} onChange={setBank} help={options.length === 0 ? t('org.bank.noBanks') : undefined} />
        <TextField label={t('org.bank.title')} isRequired value={title} onChange={setTitle} maxLength={120} />
        <TextField label={t('org.bank.number')} isCode isRequired={creating} isReadOnly={!canChangeNumbers} value={number} onChange={setNumber} maxLength={50}
          help={canChangeNumbers ? (creating ? undefined : t('org.bank.number.keep')) : t('org.reg.value.locked')} />
        <TextField label={t('org.bank.iban')} isCode isReadOnly={!canChangeNumbers} value={iban} onChange={setIban} maxLength={40} help={t('org.bank.iban.help')} />
        <TextField label={t('org.bank.currency')} isCode isRequired value={currency} onChange={setCurrency} maxLength={3} />
        <TextField label={t('org.bank.format')} isCode value={format} onChange={setFormat} maxLength={40} help={t('org.bank.format.help')} />
        <TextField label={t('users.reason')} value={reason} onChange={setReason} maxLength={500} help={t('org.bank.reason.help')} />
        {error && <Banner tone="danger" title={error}>{null}</Banner>}
      </div>
    </Dialog>
  );
}

// ---------------------------------------------------------------------------------------------------- settings of a company

/** FR-ADM-ORG-07: the settings this company overrides, with a link into the settings centre filtered to the company (S1.15). */
export function SettingOverridesPanel({ companyId }: { companyId: string }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const list = useQuery({ queryKey: ['org', 'overrides', companyId], queryFn: ({ signal }) => org.settingOverrides(companyId, signal), retry: false });
  const lang = locale === 'ur' ? 'ur' : 'en';
  return (
    <section className="erp-section" aria-labelledby="org-set-title">
      <div className="erp-row erp-section-head">
        <h2 id="org-set-title" className="erp-h2">{t('org.settings.title')}</h2>
        <Link href={`/settings?company=${companyId}`}>{t('org.settings.link')}</Link>
      </div>
      {list.isPending ? <p className="erp-muted">{t('common.loading')}</p>
        : list.isError ? <p className="erp-muted">{t('org.settings.denied')}</p>
          : list.data.length === 0 ? <p>{t('org.settings.none')}</p> : (
            <ul className="erp-plain-list">
              {list.data.map((s) => (
                <li key={s.key}><span>{s.name?.[lang] ?? s.name?.en ?? s.key}</span>{' '}<StatusChip status="info" label={String(typeof s.value === 'object' ? JSON.stringify(s.value) : s.value)} /></li>
              ))}
            </ul>
          )}
    </section>
  );
}

export function LetterheadLink({ companyId }: { companyId: string }) {
  const { t } = useText();
  return <p><Link href={`/settings/templates?type=letterhead&company=${companyId}`}>{t('org.letterhead.link')}</Link> <span className="erp-help">{t('org.letterhead.help')}</span></p>;
}

// ---------------------------------------------------------------------------------------------------- pickers and editors

/** Language of a company's documents: English or Urdu (the two languages of Phase 1). */
export const languageEditor = (props: FieldEditorProps): ReactNode => <LanguageSelect {...props} />;

function LanguageSelect({ label, value, onChange, readOnly, error, help }: FieldEditorProps) {
  const { t } = useText();
  const options: Option[] = [{ id: 'en', label: t('org.language.en') }, { id: 'ur', label: t('org.language.ur') }];
  return <SelectField label={label} isRequired isDisabled={readOnly} options={options} value={typeof value === 'string' ? value : null} onChange={onChange} error={error} {...(help ? { help } : {})} />;
}

/** A holiday calendar of the customer, or none (a company's or a site's calendar). */
export const calendarEditor = (props: FieldEditorProps): ReactNode => <CalendarSelect {...props} />;

function CalendarSelect({ label, value, onChange, readOnly, error, help }: FieldEditorProps) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const calendars = useQuery({ queryKey: ['ref', 'calendars', 'all'], queryFn: () => calendarApi.list({ limit: 200, sort: 'code' }), staleTime: 60_000 });
  const none = '__none';
  const options = useMemo<Option[]>(() => [{ id: none, label: t('org.calendar.none') },
    ...(calendars.data?.items ?? []).map((c): Option => ({ id: c.id, label: c.nameI18n[locale] ?? c.nameI18n.en ?? c.code, detail: c.code }))], [calendars.data, locale, t]);
  return <SelectField label={label} isDisabled={readOnly} options={options} value={typeof value === 'string' ? value : none} onChange={(id) => onChange(id === none ? null : id)} error={error} {...(help ? { help } : {})} />;
}
