// The form of a leave type (slice S1.33b Build item 6; FR-HRM-LVE-01): create and change. The server checks every rule again and its refusals are shown in plain words beside the field.
// The warnings of R-307 are always visible: a paid or unpaid-deduction change is NOT refused after first use (payroll reads past periods with the new flag), and code, counting and unit are
// fixed once a type is used (the server refuses the change). No rule is changed here; the form only says what the server does.
import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Banner, Button, Dialog, SelectField, SwitchField, TextField, useToast, type Option } from '@erp/ui';
import { useText, type MessageKey } from '@erp/i18n';
import { COLOUR_TOKENS, COUNTINGS, UNITS, leaveTypesApi, type LeaveTypeBody, type LeaveTypeRow } from '../../api/leave-types';
import type { Names } from '../../api/workflow';
import { NamePair, useRefused } from '../admin/config-parts';
import '../admin/config-messages';
import './leave-types-messages';
import './leave-types.css';

const NONE = '';

export function useLeaveTypeRefresh() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: ['hrm', 'leave-types-admin'] });
}

export function LeaveTypeForm({ companyId, row, onClose }: { companyId: string; row: LeaveTypeRow | null; onClose: () => void }) {
  const { t } = useText();
  const toast = useToast();
  const refused = useRefused();
  const refresh = useLeaveTypeRefresh();
  const editing = row !== null;
  const [code, setCode] = useState(row?.code ?? '');
  const [name, setName] = useState<Names>({ en: row?.nameI18n.en ?? '', ur: row?.nameI18n.ur ?? '' });
  const [paid, setPaid] = useState<string>(row ? (row.isPaid ? 'paid' : 'unpaid') : NONE);
  const [deduct, setDeduct] = useState(row?.isUnpaidDeduction ?? false);
  const [counting, setCounting] = useState<string>(row?.counting ?? NONE);
  const [unit, setUnit] = useState<string>(row?.unit ?? NONE);
  const [document, setDocument] = useState(row?.requiresDocument ?? false);
  const [compOff, setCompOff] = useState(row?.isCompOff ?? false);
  const [colour, setColour] = useState<string>(row?.colorToken ?? NONE);
  const [fields, setFields] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    if (paid === NONE) { setFields({ isPaid: t('lty.form.paid.choose') }); setSummary(t('config.error.fix')); return; }
    const body: LeaveTypeBody = {
      companyId, code: code.trim(), nameI18n: { en: (name.en ?? '').trim(), ur: (name.ur ?? '').trim() }, isPaid: paid === 'paid', isUnpaidDeduction: deduct,
      requiresDocument: document, isCompOff: compOff, colorToken: colour,
      ...(counting ? { counting: counting as LeaveTypeBody['counting'] } : {}), ...(unit ? { unit: unit as LeaveTypeBody['unit'] } : {}),
      ...(editing ? { rowVersion: row.rowVersion } : { isActive: true }),
    };
    setBusy(true);
    setSummary(null);
    setFields({});
    try {
      if (editing) await leaveTypesApi.update(row.id, body); else await leaveTypesApi.create(body);
      await refresh();
      toast.success(t(editing ? 'lty.saved.edit' : 'lty.saved.create'));
      onClose();
    } catch (error) {
      const r = refused(error);
      setFields(r.fields);
      setSummary(r.summary);
    } finally {
      setBusy(false);
    }
  };

  const countingOptions: Option[] = COUNTINGS.map((c) => ({ id: c, label: t(`lty.counting.${c}` as MessageKey) }));
  const unitOptions: Option[] = UNITS.map((u) => ({ id: u, label: t(`lty.unit.${u}` as MessageKey) }));
  const colourOptions: Option[] = [{ id: NONE, label: t('lty.colour.none') }, ...COLOUR_TOKENS.map((c) => ({ id: c, label: t(`lty.colour.${c}` as MessageKey) }))];
  const paidOptions: Option[] = [{ id: 'paid', label: t('lty.paid') }, { id: 'unpaid', label: t('lty.unpaid') }];
  return (
    <Dialog title={t(editing ? 'lty.form.edit' : 'lty.form.create')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (<><Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button><Button variant="primary" isPending={busy} onPress={() => { void submit(); }}>{t('lty.form.save')}</Button></>)}>
      <div className="erp-stack">
        {summary && <Banner tone="danger">{summary}</Banner>}
        <Banner tone="warning" title={t('lty.warn.paid.title')}>{t('lty.warn.paid')}</Banner>
        <Banner tone="info">{t('lty.warn.fixed')}</Banner>
        <TextField label={t('lty.col.code')} isCode isRequired value={code} onChange={setCode} maxLength={40} error={fields.code ?? null} help={t('lty.form.code.help')} />
        <NamePair label={t('lty.col.name')} value={name} onChange={setName} errors={{ en: fields.nameI18n }} />
        <SelectField label={t('lty.form.paid')} isRequired options={paidOptions} value={paid} onChange={setPaid} error={fields.isPaid ?? null} placeholder={t('lty.form.choose')} />
        <SwitchField label={t('lty.form.deduct')} isSelected={deduct} onChange={setDeduct} />
        {fields.isUnpaidDeduction && <p className="erp-field-error" role="alert">{fields.isUnpaidDeduction}</p>}
        <SelectField label={t('lty.col.counting')} isRequired options={countingOptions} value={counting} onChange={setCounting} error={fields.counting ?? null} placeholder={t('lty.form.choose')} help={t('lty.form.counting.help')} />
        <SelectField label={t('lty.col.unit')} isRequired options={unitOptions} value={unit} onChange={setUnit} error={fields.unit ?? null} placeholder={t('lty.form.choose')} help={t('lty.form.unit.help')} />
        <SwitchField label={t('lty.form.document')} isSelected={document} onChange={setDocument} />
        <SwitchField label={t('lty.form.compOff')} isSelected={compOff} onChange={setCompOff} />
        <SelectField label={t('lty.form.colour')} options={colourOptions} value={colour} onChange={setColour} error={fields.colorToken ?? null} help={t('lty.form.colour.help')} />
      </div>
    </Dialog>
  );
}
