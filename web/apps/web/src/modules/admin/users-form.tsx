// Create and invite (slice S1.13a Build item 2; FR-ADM-USR-02, FR-KRN-IAM-18) and the profile form of the user record
// (Build item 3). The server judges every rule; the limit refusal and the username and e-mail clash are shown in plain
// words. A new person gets an invitation by e-mail (never a password), and the first role is assigned in the same step.
import { useMemo, useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Banner, Button, CheckboxField, DateField, Link, PageHeader, SelectField, TextField, useToast } from '@erp/ui';
import { useDocumentTitle } from '@erp/engine';
import { useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { seatsOf, users, type UserRecord, type UserWrite } from '../../api/users';
import { nameOf } from '../../api/workflow';
import { companyName, useShell } from '../../shell/context';
import { CLASH_RULE, listKey, recordKey, useProblemText } from './users-parts';

const USERS_PATH = '/admin/users';
export const DEFAULT_ZONE = 'Asia/Karachi';

/** The IANA time zones the browser knows; the customer's default first. */
export function timeZoneOptions(current?: string) {
  const supported = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : [];
  const zones = [...new Set([DEFAULT_ZONE, ...(current ? [current] : []), ...supported])];
  return zones.map((z) => ({ id: z, label: z.replace(/_/g, ' ') }));
}

const dateOf = (value: string | null): CalendarDate | null => (value ? parseDate(value) : null);

interface Fields {
  displayName: string;
  username: string;
  email: string;
  language: string;
  timeZone: string;
  companyId: string;
  accessEnd: CalendarDate | null;
}

function useFieldErrors() {
  const { t } = useText();
  const problemText = useProblemText();
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [general, setGeneral] = useState<string | null>(null);
  const fail = (e: unknown) => {
    const next: Record<string, string> = {};
    if (e instanceof ApiProblem) {
      for (const field of Object.keys(e.fieldErrors)) next[field] = t('users.field.invalid');
      if (e.detail === CLASH_RULE) next.username = t('users.error.clash');
      else if (e.detail === 'uq_users_username') next.username = t('users.error.duplicate');
      else if (e.detail === 'uq_users_email') next.email = t('users.error.duplicate');
    }
    setErrors(next);
    setGeneral(Object.keys(next).length === 0 ? problemText(e) : null);
  };
  return { errors, general, fail, clear: () => { setErrors({}); setGeneral(null); } };
}

function CompanyAndLanguage({ f, set, optionalCompany }: { f: Fields; set: (patch: Partial<Fields>) => void; optionalCompany?: boolean }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const { me } = useShell();
  return (
    <>
      <SelectField label={t('users.language')} isRequired value={f.language} onChange={(language) => set({ language })} options={[{ id: 'en', label: t('users.language.en') }, { id: 'ur', label: t('users.language.ur') }]} />
      <SelectField label={t('users.timeZone')} isRequired value={f.timeZone} onChange={(timeZone) => set({ timeZone })} options={timeZoneOptions(f.timeZone)} />
      <SelectField label={t('users.defaultCompany')} value={f.companyId} onChange={(companyId) => set({ companyId })}
        options={[...(optionalCompany ? [{ id: 'none', label: t('users.none') }] : []), ...me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }))]} />
    </>
  );
}

export function UserCreate() {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const navigate = useNavigate();
  const toast = useToast();
  const queryClient = useQueryClient();
  const { me, can, writable, activeCompany } = useShell();
  useDocumentTitle(t('users.create'));
  const [f, setF] = useState<Fields>({ displayName: '', username: '', email: '', language: 'en', timeZone: DEFAULT_ZONE, companyId: activeCompany?.id ?? me.companies[0]?.id ?? 'none', accessEnd: null });
  const [roleId, setRoleId] = useState<string | null>(null);
  const [roleCompany, setRoleCompany] = useState('all');
  const [invite, setInvite] = useState(true);
  const [busy, setBusy] = useState(false);
  const { errors, general, fail, clear } = useFieldErrors();
  const roles = useQuery({ queryKey: ['iam', 'roles', 'options'], queryFn: users.roles, enabled: can('iam.roles.read') && can('iam.roles.assign'), staleTime: 60_000 });
  const licence = useQuery({ queryKey: ['cfg', 'licence', 'limits'], queryFn: users.licence, enabled: can('cfg.licence.read'), refetchOnMount: 'always' });
  const seats = seatsOf(licence.data);
  const atLimit = seats !== null && seats.used >= seats.max;
  const set = (patch: Partial<Fields>) => setF((old) => ({ ...old, ...patch }));
  const roleOptions = useMemo(() => (roles.data ?? []).map((r) => ({ id: r.id, label: nameOf(r.nameI18n, locale) || r.code, detail: r.code })), [roles.data, locale]);
  const complete = f.displayName.trim() && f.username.trim() && f.email.trim();

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    clear();
    setBusy(true);
    let created: UserRecord | null = null;
    try {
      created = await users.create({
        username: f.username.trim(), email: f.email.trim(), displayName: f.displayName.trim(), language: f.language, timeZone: f.timeZone,
        defaultCompanyId: f.companyId === 'none' ? null : f.companyId, accessValidTo: f.accessEnd?.toString() ?? null,
      });
      let pending = false;
      if (roleId) {
        const answer = await users.assignRole({ userId: created.id, roleId, companyId: roleCompany === 'all' ? null : roleCompany, validFrom: null, validTo: null, reason: t('users.firstRole.reason') });
        pending = answer.outcome === 'pending';
      }

      if (invite && can('iam.users.send_invitation')) await users.invite(created.id);
      void queryClient.invalidateQueries({ queryKey: listKey });
      toast.success(pending ? t('users.created.pending', { name: created.displayName }) : invite ? t('users.created.invited', { name: created.displayName }) : t('users.created', { name: created.displayName }));
      void navigate(`${USERS_PATH}/${created.id}`, { replace: true });
    } catch (e) {
      if (created) {
        // The person exists; only the role or the invitation failed. Say so, and let the administrator finish on the record.
        void queryClient.invalidateQueries({ queryKey: listKey });
        toast.error(t('users.created.partial', { name: created.displayName }));
        void navigate(`${USERS_PATH}/${created.id}`, { replace: true });
      } else {
        fail(e);
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="erp-page">
      <PageHeader title={t('users.create')} description={t('users.create.description')}
        breadcrumbs={[{ label: t('users.title'), href: USERS_PATH }, { label: t('users.create') }]} />
      {atLimit && (
        <Banner tone="danger" title={t('users.error.limit.title')} action={<Link href="/settings/licence">{t('users.seats.licence')}</Link>}>
          {t('users.error.limit')}
        </Banner>
      )}
      <form className="erp-form" onSubmit={(e) => { void submit(e); }} aria-label={t('users.create')}>
        {general && <Banner tone="danger" title={t('users.error.title')}>{general}</Banner>}
        <TextField label={t('users.col.name')} isRequired value={f.displayName} onChange={(displayName) => set({ displayName })} error={errors.displayName} maxLength={200} />
        <TextField label={t('users.col.username')} isRequired value={f.username} onChange={(username) => set({ username })} error={errors.username} help={t('users.username.help')} autoComplete="off" maxLength={100} />
        <TextField label={t('users.col.email')} type="email" isRequired value={f.email} onChange={(email) => set({ email })} error={errors.email} help={t('users.email.help')} autoComplete="off" maxLength={254} />
        <CompanyAndLanguage f={f} set={set} optionalCompany />
        <DateField label={t('users.accessEnd')} help={t('users.accessEnd.help')} value={f.accessEnd} onChange={(accessEnd) => set({ accessEnd: accessEnd ? parseDate(accessEnd.toString()) : null })} />
        {roleOptions.length > 0 && (
          <fieldset className="erp-fieldset">
            <legend>{t('users.firstRole')}</legend>
            <SelectField label={t('users.role')} value={roleId} onChange={setRoleId} options={roleOptions} placeholder={t('users.firstRole.none')} />
            {roleId && (
              <SelectField label={t('users.company')} value={roleCompany} onChange={setRoleCompany}
                options={[{ id: 'all', label: t('users.allCompanies') }, ...me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }))]} />
            )}
          </fieldset>
        )}
        {can('iam.users.send_invitation') && <CheckboxField label={t('users.invite.now')} help={t('users.invite.help')} isSelected={invite} onChange={setInvite} />}
        <div className="erp-form-actions">
          <Button type="submit" variant="primary" isPending={busy} isDisabled={!writable || !complete || atLimit}>{invite ? t('users.create.invite') : t('users.create.save')}</Button>
          <Button variant="secondary" onPress={() => { void navigate(USERS_PATH); }}>{t('common.cancel')}</Button>
        </div>
      </form>
    </div>
  );
}

/** The profile tab: the person's own details; the e-mail changes only through its guarded action. */
export function ProfileForm({ record, canEdit }: { record: UserRecord; canEdit: boolean }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [f, setF] = useState<Fields>({
    displayName: record.displayName, username: record.username, email: record.email, language: record.language, timeZone: record.timeZone,
    companyId: record.defaultCompanyId ?? 'none', accessEnd: dateOf(record.accessValidTo),
  });
  const [busy, setBusy] = useState(false);
  const { errors, general, fail, clear } = useFieldErrors();
  const set = (patch: Partial<Fields>) => setF((old) => ({ ...old, ...patch }));
  const save = async (event: FormEvent) => {
    event.preventDefault();
    clear();
    setBusy(true);
    try {
      const body: UserWrite = {
        username: f.username.trim(), displayName: f.displayName.trim(), language: f.language, timeZone: f.timeZone,
        defaultCompanyId: f.companyId === 'none' ? null : f.companyId, accessValidTo: f.accessEnd?.toString() ?? null, rowVersion: record.rowVersion,
      };
      const saved = await users.update(record.id, body);
      queryClient.setQueryData(recordKey(record.id), saved);
      void queryClient.invalidateQueries({ queryKey: listKey });
      toast.success(t('users.saved', { name: saved.displayName }));
    } catch (e) {
      fail(e);
    } finally {
      setBusy(false);
    }
  };
  return (
    <form className="erp-form" onSubmit={(e) => { void save(e); }} aria-label={t('users.tab.profile')}>
      {general && <Banner tone="danger" title={t('users.error.title')}>{general}</Banner>}
      <TextField label={t('users.col.name')} isRequired value={f.displayName} onChange={(displayName) => set({ displayName })} error={errors.displayName} isReadOnly={!canEdit} maxLength={200} />
      <TextField label={t('users.col.username')} isRequired value={f.username} onChange={(username) => set({ username })} error={errors.username} isReadOnly={!canEdit} autoComplete="off" maxLength={100} />
      <TextField label={t('users.col.email')} value={f.email} isReadOnly help={t('users.email.readonly')} />
      {canEdit ? <CompanyAndLanguage f={f} set={set} optionalCompany /> : (
        <>
          <TextField label={t('users.language')} isReadOnly value={t(`users.language.${f.language === 'ur' ? 'ur' : 'en'}`)} />
          <TextField label={t('users.timeZone')} isReadOnly value={f.timeZone} />
        </>
      )}
      <DateField label={t('users.accessEnd')} help={t('users.accessEnd.help')} isReadOnly={!canEdit} value={f.accessEnd} onChange={(accessEnd) => set({ accessEnd: accessEnd ? parseDate(accessEnd.toString()) : null })} />
      {canEdit && <div className="erp-form-actions"><Button type="submit" variant="primary" isPending={busy}>{t('users.save')}</Button></div>}
    </form>
  );
}
