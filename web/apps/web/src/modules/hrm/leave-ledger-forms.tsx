// The two writes of the leave ledger screen (slice S1.33b; FR-HRM-LVE-10, FR-XM-05, FR-XM-06): reverse an entry (a new entry of the opposite quantity; nothing is edited) and post an opening
// balance, a grant or an adjustment (optionally settling a period the balance check listed). A reason is always required; the server checks every rule again and its refusals are shown in words.
import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import type { LeaveType } from '@erp/generated/api/hrm';
import { Banner, Button, DateField, Dialog, SelectField, TextAreaField, TextField, useToast } from '@erp/ui';
import { formatDate, useLocaleSettings } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import type { LedgerEntry } from '../../api/leave';
import { MANUAL_KINDS, leaveLedgerApi, type ManualKind } from '../../api/leave-ledger';
import { typeNameOf, useLeaveRefusal } from './leave-parts';
import { parseQuantity } from './leave-ledger-model';
import { useLedgerText } from './leave-ledger-messages';

const REASON_MAX = 500;
const dateText = (d: CalendarDate | null) => (d ? d.toString() : '');

/** The server's words for each field it refused (a key per field), so the field itself shows what is wrong. */
function useFieldErrors(error: unknown): (field: string) => string | undefined {
  const { server } = useLedgerText();
  return (field) => {
    if (!(error instanceof ApiProblem)) return undefined;
    const key = error.fieldErrors[field]?.[0];
    return key ? server(key) ?? undefined : undefined;
  };
}

export function ReverseDialog({ entry, label, onClose }: { entry: LedgerEntry; label: string; onClose: () => void }) {
  const { t } = useLedgerText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refusal = useLeaveRefusal();
  const [reason, setReason] = useState('');
  const [date, setDate] = useState<CalendarDate | null>(null);
  const send = useMutation({
    mutationFn: () => leaveLedgerApi.reverse(entry.id, { reason: reason.trim(), ...(date ? { entryDate: dateText(date) } : {}) }),
    onSuccess: async () => { toast.success(t('lled.reverse.done')); await queryClient.invalidateQueries({ queryKey: ['hrm', 'leave'] }); onClose(); },
  });
  const field = useFieldErrors(send.error);
  const lang = locale === 'ur' ? 'ur' : 'en';
  return (
    <Dialog title={t('lled.reverse.title')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={(close) => <>
        <Button variant="quiet" onPress={close}>{t('lled.cancel')}</Button>
        <Button variant="danger" isPending={send.isPending} isDisabled={reason.trim() === ''} onPress={() => send.mutate()}>{t('lled.reverse.confirm')}</Button>
      </>}>
      <p>{t('lled.reverse.body', { type: label, date: formatDate(`${entry.entryDate}T12:00:00Z`, lang, timeZone, numbers) })}</p>
      <p className="erp-muted">{t('lled.reverse.never')}</p>
      <TextAreaField label={t('lled.reason')} isRequired maxLength={REASON_MAX} value={reason} onChange={setReason} error={field('reason') ?? null} />
      <DateField label={t('lled.reverse.date')} help={t('lled.reverse.date.help')} value={date} onChange={(v) => setDate(v ? parseDate(v.toString()) : null)} error={field('entryDate') ?? null} />
      {send.isError && <Banner tone="danger">{refusal(send.error)}</Banner>}
    </Dialog>
  );
}

export interface PostPrefill { leaveTypeId?: string; quantity?: string; settlesPeriod?: string | null; entryDate?: string | null }

export function PostDialog({ employmentId, types, prefill, onClose }: { employmentId: string; types: LeaveType[]; prefill?: PostPrefill; onClose: () => void }) {
  const { t } = useLedgerText();
  const { locale } = useLocaleSettings();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refusal = useLeaveRefusal();
  const settles = prefill?.settlesPeriod ?? null;
  const [typeId, setTypeId] = useState(prefill?.leaveTypeId ?? '');
  const [kind, setKind] = useState<ManualKind>(settles || prefill ? 'adjustment' : 'grant');
  const [quantity, setQuantity] = useState(prefill?.quantity ?? '');
  const [date, setDate] = useState<CalendarDate | null>(prefill?.entryDate ? parseDate(prefill.entryDate) : null);
  const [expires, setExpires] = useState<CalendarDate | null>(null);
  const [reason, setReason] = useState('');
  const amount = parseQuantity(quantity);
  const valid = !!typeId && amount !== null && !!date && reason.trim() !== '';
  const send = useMutation({
    mutationFn: () => leaveLedgerApi.post(employmentId, {
      leaveTypeId: typeId, kind, quantity: amount as number, entryDate: dateText(date), reason: reason.trim(),
      ...(expires ? { expiresOn: dateText(expires) } : {}), ...(settles && kind === 'adjustment' ? { settlesPeriod: settles } : {}),
    }),
    onSuccess: async () => { toast.success(t('lled.post.done')); await queryClient.invalidateQueries({ queryKey: ['hrm', 'leave'] }); onClose(); },
  });
  const field = useFieldErrors(send.error);
  const typeOptions = types.filter((x) => x.isActive || x.id === typeId).map((x) => ({ id: x.id, label: typeNameOf(x, locale) || x.code, detail: x.code }));
  const kindOptions = MANUAL_KINDS.map((k) => ({ id: k, label: t(`lled.kind.${k}`), isDisabled: !!settles && k !== 'adjustment' }));
  return (
    <Dialog title={t(settles ? 'lled.post.titleAdjust' : 'lled.post.title')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={(close) => <>
        <Button variant="quiet" onPress={close}>{t('lled.cancel')}</Button>
        <Button variant="primary" isPending={send.isPending} isDisabled={!valid} onPress={() => send.mutate()}>{t('lled.post.confirm')}</Button>
      </>}>
      <p className="erp-muted">{t('lled.post.body')}</p>
      {settles && <Banner tone="info">{t('lled.post.settles', { period: settles })}</Banner>}
      <SelectField label={t('lled.col.type')} isRequired options={typeOptions} value={typeId || null} onChange={setTypeId} error={field('leaveTypeId') ?? null} />
      <SelectField label={t('lled.post.kind')} isRequired options={kindOptions} value={kind} onChange={(id) => setKind(id as ManualKind)} error={field('kind') ?? null} />
      <TextField label={t('lled.post.quantity')} help={t('lled.post.quantity.help')} isRequired isCode value={quantity} onChange={setQuantity}
        error={field('quantity') ?? (quantity.trim() !== '' && amount === null ? t('lled.post.quantity.invalid') : null)} />
      <DateField label={t('lled.post.date')} isRequired value={date} onChange={(v) => setDate(v ? parseDate(v.toString()) : null)} error={field('entryDate') ?? null} />
      <DateField label={t('lled.post.expires')} help={t('lled.post.expires.help')} value={expires} onChange={(v) => setExpires(v ? parseDate(v.toString()) : null)} error={field('expiresOn') ?? null} />
      <TextAreaField label={t('lled.reason')} isRequired maxLength={REASON_MAX} value={reason} onChange={setReason} error={field('reason') ?? null} />
      {send.isError && <Banner tone="danger">{refusal(send.error)}</Banner>}
    </Dialog>
  );
}
