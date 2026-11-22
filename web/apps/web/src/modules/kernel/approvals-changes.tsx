// "Approvals of changes" (slice S1.12d Build item 22, S1.12 ruling Q15; FR-KRN-IAM-14, FR-ADM-SET-04, FR-XM-32): pending
// maker-checker requests (settings, access changes, route activation, e-mail changes) in plain words, who asked and
// when, approve or reject with a reason on the S1.06 endpoints. Approve is offered only where the server says approving
// would pass now; the person's own requests are listed without decision buttons. The server decides and checks again.
import { useState, type ReactNode } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Dialog, EmptyState, ErrorPanel, Skeleton, StatusChip, TextAreaField, useToast } from '@erp/ui';
import { formatDate, formatDateTime, isolate, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { changes, nameOf, type ChangeDetail, type ChangeRequest } from '../../api/workflow';
import { useShell } from '../../shell/context';

/** Kinds of held changes with a plain-words title (others: a general one). */
const KINDS = ['iam.role.set_permissions', 'iam.role.set_data_scopes', 'iam.role.set_field_policies', 'iam.role.assign', 'iam.role.end_assignment',
  'iam.group.assign', 'iam.group.end_role', 'iam.group.add_member', 'iam.group.remove_member', 'iam.setting.set', 'cfg.setting.change',
  'wf.version.activate', 'iam.user.change_email', 'org.bank_account.save', 'org.bank_account.delete', 'hrm.person.merge'] as const;

const LABELS = ['changes.detail.role', 'changes.detail.company', 'changes.detail.from', 'changes.detail.to', 'changes.detail.endDate', 'changes.detail.member',
  'changes.detail.permissionsAdded', 'changes.detail.permissionsRemoved', 'changes.detail.dataScopes', 'changes.detail.fieldRules', 'changes.detail.settingKey',
  'changes.detail.layer', 'changes.detail.newValue', 'changes.detail.version', 'changes.detail.previousEmail', 'changes.detail.newEmail',
  'changes.detail.operation', 'changes.detail.bankAccount', 'changes.detail.bankCode', 'changes.detail.accountTitle', 'changes.detail.accountNumber', 'changes.detail.iban', 'changes.detail.currency',
  'changes.detail.mergeInto'] as const;

const KEYS = ['changes.detail.allCompanies', 'changes.detail.reset', 'changes.layer.tenant', 'changes.layer.company', 'changes.layer.role', 'changes.layer.user', 'changes.layer.pack',
  'changes.operation.create', 'changes.operation.update', 'changes.operation.delete', 'changes.detail.removed', 'changes.detail.changed'] as const;

export function ChangeApprovals() {
  const { t } = useText();
  const pending = useQuery({ queryKey: ['wf', 'changes'], queryFn: changes.pending, refetchOnMount: 'always' });
  if (pending.isPending) return <Skeleton lines={6} />;
  if (pending.isError) {
    return <ErrorPanel message={t('approvals.changes.loadError')} reference={pending.error instanceof ApiProblem ? pending.error.reference : null}
      action={<Button variant="primary" onPress={() => { void pending.refetch(); }}>{t('common.retry')}</Button>} />;
  }

  const { toDecide, mine } = pending.data;
  return (
    <div className="erp-stack erp-changes">
      <section aria-labelledby="erp-changes-decide" className="erp-stack">
        <h2 id="erp-changes-decide">{t('approvals.changes.toDecide')}</h2>
        {toDecide.length === 0
          ? <EmptyState title={t('approvals.changes.none')}>{t('approvals.changes.none.body')}</EmptyState>
          : <ul className="erp-change-list">{toDecide.map((c) => <ChangeCard key={c.id} change={c} decidable />)}</ul>}
      </section>
      {mine.length > 0 && (
        <section aria-labelledby="erp-changes-mine" className="erp-stack">
          <h2 id="erp-changes-mine">{t('approvals.changes.mine')}</h2>
          <p className="erp-muted">{t('approvals.changes.mine.body')}</p>
          <ul className="erp-change-list">{mine.map((c) => <ChangeCard key={c.id} change={c} decidable={false} />)}</ul>
        </section>
      )}
    </div>
  );
}

function kindTitle(kind: string): MessageKey {
  return (KINDS as readonly string[]).includes(kind) ? (`approvals.changes.kind.${kind}` as MessageKey) : 'approvals.changes.kind.other';
}

function ChangeCard({ change, decidable }: { change: ChangeRequest; decidable: boolean }) {
  const { t, server } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const writable = useShell().writable;
  const [decision, setDecision] = useState<'approve' | 'reject' | null>(null);
  const subject = nameOf(change.subject, locale);
  const heading = t(kindTitle(change.kind), { subject: subject ? isolate(subject) : t('approvals.changes.unnamed') });
  return (
    <li className="erp-change-card erp-stack" aria-label={heading}>
      <h3>{heading}</h3>
      <p className="erp-muted">
        {t('approvals.changes.asked', {
          who: change.requestedByName ? isolate(change.requestedByName) : t('approvals.unknownPerson'),
          when: formatDateTime(change.requestedAt, lang, timeZone, numbers),
        })}
        {' · '}
        {t('approvals.changes.expires', { when: formatDateTime(change.expiresAt, lang, timeZone, numbers) })}
      </p>
      {change.reason && <p>{t('approvals.changes.reason', { reason: change.reason })}</p>}
      <dl className="erp-facts">
        {change.details.map((d, i) => <DetailRow key={`${d.label}-${i}`} detail={d} />)}
      </dl>
      {!decidable && <StatusChip status="pending" label={t('approvals.changes.waiting')} />}
      {decidable && writable && (
        <div className="erp-row erp-decisions" role="group" aria-label={t('approvals.detail.decide')}>
          {change.canApprove && <Button variant="primary" onPress={() => setDecision('approve')}>{t('approvals.action.approve')}</Button>}
          <Button variant="danger" onPress={() => setDecision('reject')}>{t('approvals.action.reject')}</Button>
        </div>
      )}
      {decidable && !change.canApprove && change.approveProblem && (
        <p className="erp-muted">{t('approvals.changes.cannotApprove', { why: server(change.approveProblem) ?? t('approvals.actionError') })}</p>
      )}
      {decision && <ChangeDecision change={change} decision={decision} heading={heading} onClose={() => setDecision(null)} />}
    </li>
  );
}

function DetailRow({ detail }: { detail: ChangeDetail }) {
  const { t, server } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const label = (LABELS as readonly string[]).includes(detail.label) ? t(`approvals.${detail.label}` as MessageKey) : detail.label;
  let value: ReactNode;
  switch (detail.type) {
    case 'date':
      value = detail.date ? formatDate(detail.date, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) : '';
      break;
    case 'names':
      value = isolate(nameOf(detail.names, locale)) || t('approvals.field.empty');
      break;
    case 'key':
      value = detail.text && (KEYS as readonly string[]).includes(detail.text) ? t(`approvals.${detail.text}` as MessageKey) : (detail.text ?? '');
      break;
    case 'items':
      value = (
        <ul className="erp-change-items">
          {(detail.items ?? []).map((item) => <li key={item}>{server(`permissions.${item}`) ?? <span className="erp-code">{item}</span>}</li>)}
        </ul>
      );
      break;
    default:
      value = <span className="erp-code">{detail.text ?? ''}</span>;
  }
  return (
    <>
      <dt>{label}</dt>
      <dd>{value}</dd>
    </>
  );
}

function ChangeDecision({ change, decision, heading, onClose }: { change: ChangeRequest; decision: 'approve' | 'reject'; heading: string; onClose: () => void }) {
  const { t, server } = useText();
  const toast = useToast();
  const client = useQueryClient();
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const required = decision === 'reject';
  const submit = async () => {
    const text = reason.trim();
    if (required && !text) {
      setError(t('approvals.dialog.reasonNeeded'));
      return;
    }
    setPending(true);
    setError(null);
    try {
      await (decision === 'approve' ? changes.approve(change.id, text || null) : changes.reject(change.id, text));
      toast.success(t(decision === 'approve' ? 'approvals.changes.approved' : 'approvals.changes.rejected', { change: heading }));
      await client.invalidateQueries({ queryKey: ['wf'] });
      onClose();
    } catch (e) {
      setError(e instanceof ApiProblem ? server(e.key) ?? t('approvals.actionError') : t('approvals.actionError'));
      setPending(false);
      await client.invalidateQueries({ queryKey: ['wf', 'changes'] });
    }
  };
  return (
    <Dialog title={t(decision === 'approve' ? 'approvals.changes.dialog.approve' : 'approvals.changes.dialog.reject', { change: heading })} isOpen
      onOpenChange={(open) => { if (!open && !pending) onClose(); }}
      actions={() => (
        <>
          <Button variant="quiet" isDisabled={pending} onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant={decision === 'reject' ? 'danger' : 'primary'} isPending={pending} onPress={() => { void submit(); }}>
            {t(decision === 'approve' ? 'approvals.action.approve' : 'approvals.action.reject')}
          </Button>
        </>
      )}>
      <TextAreaField label={required ? t('approvals.dialog.reason') : t('approvals.dialog.comment')} value={reason} onChange={setReason} isRequired={required} error={error} maxLength={2000} />
    </Dialog>
  );
}
