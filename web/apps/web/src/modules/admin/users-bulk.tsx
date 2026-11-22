// Bulk actions over a selection of users (slice S1.13a Build item 5; FR-ADM-USR-09): assign a role, end a role, change the
// default company, disable, extend the access end date. The count comes first; then one request per person, one result
// per person, failures listed with the server's reason. A person is never changed twice and a failure stops nobody else.
// Export of the selection arrives with S1.16.
import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { Button, DateField, Dialog, SelectField, Spinner, TextAreaField } from '@erp/ui';
import { formatInteger, useLocaleSettings, useText } from '@erp/i18n';
import { users, type UserRecord, type UserRow, type UserWrite } from '../../api/users';
import { nameOf } from '../../api/workflow';
import { companyName, useShell } from '../../shell/context';
import { useProblemText } from './users-parts';

type Kind = 'assignRole' | 'endRole' | 'company' | 'disable' | 'extend';
const KINDS: Kind[] = ['assignRole', 'endRole', 'company', 'disable', 'extend'];

interface Outcome { id: string; name: string; ok: boolean; text: string }

const toWrite = (u: UserRecord, over: Partial<UserWrite>): UserWrite => ({
  username: u.username, displayName: u.displayName, defaultCompanyId: u.defaultCompanyId, language: u.language, timeZone: u.timeZone, accessValidTo: u.accessValidTo, rowVersion: u.rowVersion, ...over,
});

export function BulkUsers({ people, onClose, onDone }: { people: UserRow[]; onClose: () => void; onDone: () => void }) {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  const { me, can } = useShell();
  const problemText = useProblemText();
  const [kind, setKind] = useState<Kind | null>(null);
  const [roleId, setRoleId] = useState<string | null>(null);
  const [company, setCompany] = useState<string>('all');
  const [until, setUntil] = useState<CalendarDate | null>(null);
  const [reason, setReason] = useState('');
  const [step, setStep] = useState<'choose' | 'preview' | 'running' | 'done'>('choose');
  const [results, setResults] = useState<Outcome[]>([]);
  const roles = useQuery({ queryKey: ['iam', 'roles', 'options'], queryFn: users.roles, enabled: can('iam.roles.read'), staleTime: 60_000 });

  const available = KINDS.filter((k) => (k === 'assignRole' || k === 'endRole' ? can('iam.roles.assign') : k === 'disable' ? can('iam.users.change_status') : can('iam.users.update')));
  const needsRole = kind === 'assignRole' || kind === 'endRole';
  const ready = kind !== null && reason.trim().length > 0 && (!needsRole || roleId !== null) && (kind !== 'extend' || until !== null);
  const roleOptions = useMemo(() => (roles.data ?? []).map((r) => ({ id: r.id, label: nameOf(r.nameI18n, locale) || r.code, detail: r.code })), [roles.data, locale]);

  const run = async () => {
    if (!kind) return;
    setStep('running');
    const out: Outcome[] = [];
    for (const person of people) {
      try {
        let text = t('users.bulk.result.done');
        if (kind === 'assignRole' && roleId) {
          const answer = await users.assignRole({ userId: person.id, roleId, companyId: company === 'all' ? null : company, validFrom: null, validTo: null, reason: reason.trim() });
          if (answer.outcome === 'pending') text = t('users.bulk.result.pending');
        } else if (kind === 'endRole' && roleId) {
          const access = await users.access(person.id);
          const held = access.roles.filter((r) => r.roleId === roleId && r.inForce);
          if (held.length === 0) throw new Error('none');
          let waiting = false;
          for (const line of held) waiting = (await users.endRole(line.assignmentId, null, reason.trim())).outcome === 'pending' || waiting;
          if (waiting) text = t('users.bulk.result.pending');
        } else if (kind === 'company') {
          const record = await users.get(person.id);
          await users.update(person.id, toWrite(record, { defaultCompanyId: company === 'all' ? null : company }));
        } else if (kind === 'extend' && until) {
          const record = await users.get(person.id);
          await users.update(person.id, toWrite(record, { accessValidTo: until.toString() }));
        } else if (kind === 'disable') {
          const answer = await users.setStatus(person.id, 'disabled', reason.trim());
          if ((answer?.openWorkflowTasks ?? 0) > 0) text = t('users.bulk.result.tasks', { count: formatInteger(answer?.openWorkflowTasks ?? 0, numbers) });
        }
        out.push({ id: person.id, name: person.displayName, ok: true, text });
      } catch (e) {
        out.push({ id: person.id, name: person.displayName, ok: false, text: e instanceof Error && e.message === 'none' ? t('users.bulk.result.noRole') : problemText(e) });
      }
    }
    setResults(out);
    setStep('done');
    onDone();
  };

  const count = formatInteger(people.length, numbers);
  const failed = results.filter((r) => !r.ok);
  return (
    <Dialog title={t('users.bulk.title')} isOpen onOpenChange={(open) => { if (!open) onClose(); }} role={step === 'running' ? 'alertdialog' : 'dialog'}
      actions={() => step === 'done' ? <Button variant="primary" onPress={onClose}>{t('common.close')}</Button> : step === 'running' ? null : (
        <>
          <Button variant="secondary" onPress={step === 'preview' ? () => setStep('choose') : onClose}>{step === 'preview' ? t('common.back') : t('common.cancel')}</Button>
          {step === 'choose'
            ? <Button variant="primary" isDisabled={!ready} onPress={() => setStep('preview')}>{t('common.continue')}</Button>
            : <Button variant={kind === 'disable' ? 'danger' : 'primary'} onPress={() => { void run(); }}>{t('users.bulk.confirm', { count })}</Button>}
        </>
      )}>
      {step === 'choose' && (
        <div className="erp-stack">
          <p>{t('users.bulk.selected', { count })}</p>
          <SelectField label={t('users.bulk.action')} isRequired value={kind} onChange={(v) => setKind(v as Kind)} options={available.map((k) => ({ id: k, label: t(`users.bulk.kind.${k}`) }))} />
          {needsRole && <SelectField label={t('users.role')} isRequired value={roleId} onChange={setRoleId} options={roleOptions} />}
          {(kind === 'assignRole' || kind === 'company') && (
            <SelectField label={t('users.company')} value={company} onChange={setCompany}
              options={[{ id: 'all', label: t('users.allCompanies') }, ...me.companies.map((c) => ({ id: c.id, label: companyName(c, locale), detail: c.code }))]} />
          )}
          {kind === 'extend' && <DateField label={t('users.accessEnd')} isRequired value={until} onChange={(v) => setUntil(v ? parseDate(v.toString()) : null)} />}
          <TextAreaField label={t('users.reason')} help={t('users.reason.help')} isRequired value={reason} onChange={setReason} maxLength={500} />
        </div>
      )}
      {step === 'preview' && kind && (
        <div className="erp-stack">
          <p role="status"><strong>{t(`users.bulk.preview.${kind}`, { count })}</strong></p>
          <ul className="erp-bulk-results">{people.map((p) => <li key={p.id}><bdi>{p.displayName}</bdi> <span className="erp-muted erp-small"><bdi>{p.username}</bdi></span></li>)}</ul>
        </div>
      )}
      {step === 'running' && <Spinner label={t('users.bulk.running')} />}
      {step === 'done' && (
        <div className="erp-stack">
          <p role="status"><strong>{t('users.bulk.summary', { done: formatInteger(results.length - failed.length, numbers), failed: formatInteger(failed.length, numbers) })}</strong></p>
          <ul className="erp-bulk-results" aria-label={t('users.bulk.results')}>
            {results.map((r) => <li key={r.id}><bdi>{r.name}</bdi>: {r.ok ? r.text : <strong>{t('users.bulk.result.failed', { reason: r.text })}</strong>}</li>)}
          </ul>
        </div>
      )}
    </Dialog>
  );
}
