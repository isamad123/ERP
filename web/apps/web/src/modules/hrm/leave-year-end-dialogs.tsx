// The two decisions of the year-end screen (slice S1.33b; FR-HRM-LVE-11, R-306): confirm a preview (the leave year is closed for good: the warning is always in the dialog) and roll it
// back (only a preview). The server decides whether the confirmation is posted at once or held for a second person, and refuses a stale preview, a year not over and requests still
// waiting; each refusal is shown in words.
import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Banner, Button, Dialog, Link, TextAreaField, useToast } from '@erp/ui';
import { digitsOf, formatDecimal, useLocaleSettings } from '@erp/i18n';
import type { LeaveYearEndRun } from '@erp/generated/api/hrm';
import { leaveLedgerApi, summaryOf, type YearEndAnswer } from '../../api/leave-ledger';
import { useLeaveRefusal } from './leave-parts';
import { decimalText } from './leave-ledger-model';
import { useYearEndText } from './leave-year-end-messages';
import './leave-year-end.css';

export function ClosedWarning() {
  const { t } = useYearEndText();
  return <Banner tone="warning" title={t('lye.closed.title')}>{t('lye.closed.body')}</Banner>;
}

/** What the confirmation did: posted now, or waiting for a second person (decided in the approvals inbox). */
export function ConfirmOutcome({ answer }: { answer: YearEndAnswer }) {
  const { t } = useYearEndText();
  if (answer.status === 'pending') {
    return <Banner tone="info" title={t('lye.confirm.pending.title')} action={<Link href="/approvals">{t('lye.confirm.pending.link')}</Link>}>{t('lye.confirm.pending.body')}</Banner>;
  }
  return <Banner tone="success" title={t('lye.confirm.applied.title')}>{t('lye.confirm.applied.body')}</Banner>;
}

export function ConfirmDialog({ run, onClose, onDone }: { run: LeaveYearEndRun; onClose: () => void; onDone: (answer: YearEndAnswer) => void }) {
  const { t } = useYearEndText();
  const { numbers } = useLocaleSettings();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refusal = useLeaveRefusal();
  const [reason, setReason] = useState('');
  const totals = summaryOf(run).totals;
  const n = (v: number) => formatDecimal(decimalText(v), numbers);
  const send = useMutation({
    mutationFn: () => leaveLedgerApi.confirm(run.id, run.rowVersion, reason.trim() || undefined),
    onSuccess: async (answer) => {
      toast.success(t(answer.status === 'pending' ? 'lye.confirm.pending.toast' : 'lye.confirm.applied.toast'));
      await queryClient.invalidateQueries({ queryKey: ['hrm', 'leave'] });
      onDone(answer);
      onClose();
    },
  });
  return (
    <Dialog title={t('lye.confirm.title', { year: digitsOf(String(run.leaveYear), numbers.digits) })} isOpen role="alertdialog" onOpenChange={(open) => { if (!open) onClose(); }}
      actions={(close) => <>
        <Button variant="quiet" onPress={close}>{t('lye.cancel')}</Button>
        <Button variant="primary" isPending={send.isPending} onPress={() => send.mutate()}>{t('lye.confirm.button')}</Button>
      </>}>
      <p>{t('lye.confirm.body')}</p>
      <dl className="erp-leave-totals" aria-label={t('lye.totals')}>
        <div><dt>{t('lye.total.lines')}</dt><dd><bdi dir="ltr">{n(totals.lines)}</bdi></dd></div>
        <div><dt>{t('lye.total.carry')}</dt><dd><bdi dir="ltr">{n(totals.carryForward)}</bdi></dd></div>
        <div><dt>{t('lye.total.encash')}</dt><dd><bdi dir="ltr">{n(totals.encash)}</bdi></dd></div>
        <div><dt>{t('lye.total.lapse')}</dt><dd><bdi dir="ltr">{n(totals.lapse)}</bdi></dd></div>
        <div><dt>{t('lye.total.noPolicy')}</dt><dd><bdi dir="ltr">{n(totals.noPolicy)}</bdi></dd></div>
        <div><dt>{t('lye.total.separated')}</dt><dd><bdi dir="ltr">{n(totals.separated)}</bdi></dd></div>
      </dl>
      <ClosedWarning />
      <p className="erp-muted">{t('lye.confirm.second')}</p>
      <TextAreaField label={t('lye.reason.optional')} maxLength={500} value={reason} onChange={setReason} />
      {send.isError && <Banner tone="danger" title={t('lye.refused')}>{refusal(send.error)}</Banner>}
    </Dialog>
  );
}

export function RollbackDialog({ run, onClose, onDone }: { run: LeaveYearEndRun; onClose: () => void; onDone: () => void }) {
  const { t } = useYearEndText();
  const { numbers } = useLocaleSettings();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refusal = useLeaveRefusal();
  const [reason, setReason] = useState('');
  const send = useMutation({
    mutationFn: () => leaveLedgerApi.rollback(run.id, run.rowVersion, reason.trim()),
    onSuccess: async () => { toast.success(t('lye.rollback.toast')); await queryClient.invalidateQueries({ queryKey: ['hrm', 'leave'] }); onDone(); onClose(); },
  });
  return (
    <Dialog title={t('lye.rollback.title', { year: digitsOf(String(run.leaveYear), numbers.digits) })} isOpen role="alertdialog" onOpenChange={(open) => { if (!open) onClose(); }}
      actions={(close) => <>
        <Button variant="quiet" onPress={close}>{t('lye.cancel')}</Button>
        <Button variant="danger" isPending={send.isPending} isDisabled={reason.trim() === ''} onPress={() => send.mutate()}>{t('lye.rollback.button')}</Button>
      </>}>
      <p>{t('lye.rollback.body')}</p>
      <TextAreaField label={t('lye.reason')} isRequired maxLength={500} value={reason} onChange={setReason} />
      {send.isError && <Banner tone="danger" title={t('lye.refused')}>{refusal(send.error)}</Banner>}
    </Dialog>
  );
}
