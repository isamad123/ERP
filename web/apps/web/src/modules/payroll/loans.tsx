/* eslint-disable jsx-a11y/no-noninteractive-tabindex -- a scrolling table region must be reachable by keyboard (WCAG 2.1.1, R-318) */
// Loans and advances (slice S1.41 Build items 2, 3, 5 and 7; FR-HRM-LON-02, LON-03, LON-07, GHR-06, GHR-09, FR-XM-32): the list (the generated read, in the caller's data scope), the application
// with its check (eligibility reasons, warnings and the installments before anything is sent) and the loan page with its schedule, ledger and balance, where the right people record the payout,
// a repayment received outside payroll (a second person approves it) or cancel an approved loan that was not paid out. Amounts are salary-class data: the server leaves them out for a person
// without that field class, and the screen then says so instead of showing zeros. Money is shown from the exact decimal, never through a float.
import { useState } from 'react';
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { parseDate, type CalendarDate } from '@internationalized/date';
import { EngineProvider, ListPage, useDocumentTitle, type EngineApi, type Row, type TableMetadata } from '@erp/engine';
import { Banner, Button, CheckboxField, DateField, Dialog, EmptyState, ErrorPanel, NumberField, PageHeader, SelectField, Skeleton, StatusChip, TextAreaField, TextField, useToast, type Status } from '@erp/ui';
import { formatDate, formatInteger, formatMoney, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import metadataJson from '@erp/generated/metadata/hrm.loans.json';
import { loansApi, type LoanDetail, type LoanPreview, type LoanProblemItem } from '../../api/loans';
import { ApiProblem, sendText } from '../../api/client';
import { useShell } from '../../shell/context';
import './loans-messages';
import './loans.css';

const metadata = metadataJson as TableMetadata;
const api: EngineApi = { send: sendText };
export const LOANS_PATH = '/payroll/loans';
const MONEY = /^\d+(\.\d{1,4})?$/;
const STATUS_CHIP: Record<string, Status> = { applied: 'pending', approved: 'approved', rejected: 'rejected', disbursed: 'active', active: 'active', settled: 'inactive', written_off: 'inactive', cancelled: 'inactive' };
const INSTALLMENT_CHIP: Record<string, Status> = { scheduled: 'pending', deducted: 'approved', skipped: 'on_hold', settled_early: 'info' };

export default function LoansScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<LoanList />} />
        <Route path="apply" element={<ApplyForm />} />
        <Route path=":id" element={<LoanView />} />
      </Routes>
    </EngineProvider>
  );
}

/** The text of a refusal: a server key when there is one. */
function useRefusal() {
  const { t, server } = useText();
  return (error: unknown): string => {
    if (error instanceof ApiProblem) return error.status === 0 ? t('loan.error.network') : server(error.key) ?? t('loan.error.generic');
    return t('loan.error.generic');
  };
}

function LoanList() {
  const navigate = useNavigate();
  const { can, writable, activeCompany, me } = useShell();
  const { t } = useText();
  useDocumentTitle(t('loan.title'));
  const options = me.companies.map((c) => ({ id: c.id, label: c.name.en ?? c.code, detail: c.code }));
  return (
    <ListPage metadata={metadata} columns={['loanNo', 'loanTypeId', 'principal', 'installments', 'status', 'disbursedOn']}
      companies={{ activeId: activeCompany?.id ?? null, options }}
      canCreate={writable && can('hrm.loans.create')} canShare={can('cfg.saved_views.share')} canExport={can('itg.exports.run')}
      description={t('loan.list.description')}
      onCreate={() => { void navigate(`${LOANS_PATH}/apply`); }} onOpen={(row: Row) => { void navigate(`${LOANS_PATH}/${String(row.id)}`); }}
      rowLabel={(row) => String(row.loanNo ?? '')} />
  );
}

/** Money of a loan: the server's number as text, in the currency, with the person's digits. */
function useMoney(currency: string | null | undefined) {
  const { numbers, locale } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  return (value: number | string | undefined | null) => (value === undefined || value === null ? '' : formatMoney(String(value), currency ?? '', lang, numbers));
}

function useDay() {
  const { numbers, locale, timeZone } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  return (iso: string | null | undefined) => (iso ? formatDate(`${iso}T12:00:00Z`, lang, timeZone, numbers) : '');
}

/** The sum of two exact, non-negative amounts of at most four decimals, worked in whole ten-thousandths so nothing passes through a float. */
export function addDecimal(a: number | string, b: number | string): string {
  const scaled = (v: number | string) => {
    const [whole = '0', frac = ''] = String(v).split('.');
    return BigInt(whole + frac.padEnd(4, '0').slice(0, 4));
  };
  const text = (scaled(a) + scaled(b)).toString().padStart(5, '0');
  return `${text.slice(0, -4)}.${text.slice(-4)}`;
}

/** A scrolling table that a keyboard can reach and a screen reader can name. */
function Region({ label, head, children }: { label: string; head: React.ReactNode; children: React.ReactNode }) {
  return (
    <div className="erp-loan-scroll" role="region" tabIndex={0} aria-label={label}>
      <table className="erp-loan-table">
        <caption className="erp-visually-hidden">{label}</caption>
        <thead><tr>{head}</tr></thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  );
}

export function Problems({ items, title }: { items: LoanProblemItem[]; title: string }) {
  const { server } = useText();
  if (items.length === 0) return null;
  return (
    <Banner tone={title.length > 0 ? 'warning' : 'info'} title={title}>
      <ul>{items.map((p) => <li key={p.key}>{server(p.key, { value: p.value ?? '' }) ?? p.key}</li>)}</ul>
    </Banner>
  );
}

function Schedule({ lines, currency, hidden }: { lines: { seq: number; periodStart: string | null; periodEnd: string | null; principalAmount?: number | undefined; interestAmount?: number | undefined; total?: number | undefined; status?: string | undefined }[]; currency: string | null; hidden: boolean }) {
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  const money = useMoney(currency);
  const day = useDay();
  if (hidden) return <p className="erp-muted" role="status">{t('loan.amounts.hidden')}</p>;
  const withStatus = lines.some((l) => l.status !== undefined);
  return (
    <Region label={t('loan.schedule')} head={<>
      <th scope="col">{t('loan.col.seq')}</th><th scope="col">{t('loan.col.period')}</th>
      <th scope="col" className="erp-loan-num">{t('loan.col.principal')}</th><th scope="col" className="erp-loan-num">{t('loan.col.interest')}</th><th scope="col" className="erp-loan-num">{t('loan.col.total')}</th>
      {withStatus && <th scope="col">{t('loan.col.status')}</th>}
    </>}>
      {lines.map((l) => (
        <tr key={l.seq}>
          <td>{formatInteger(l.seq, numbers)}</td>
          <td>{l.periodStart ? <bdi>{day(l.periodStart)}{l.periodEnd ? ` – ${day(l.periodEnd)}` : ''}</bdi> : ''}</td>
          <td className="erp-loan-num">{money(l.principalAmount)}</td>
          <td className="erp-loan-num">{money(l.interestAmount)}</td>
          <td className="erp-loan-num">{l.total !== undefined ? money(l.total) : l.principalAmount !== undefined ? money(addDecimal(l.principalAmount, l.interestAmount ?? 0)) : ''}</td>
          {withStatus && <td>{l.status ? <StatusChip status={INSTALLMENT_CHIP[l.status] ?? 'inactive'} label={t(`loan.inst.${l.status}` as MessageKey)} /> : ''}</td>}
        </tr>
      ))}
    </Region>
  );
}

// ------------------------------------------------------------------ the application

function ApplyForm() {
  const navigate = useNavigate();
  const toast = useToast();
  const queryClient = useQueryClient();
  const { t, server } = useText();
  const refusal = useRefusal();
  const { me, can, writable, activeCompany } = useShell();
  const { numbers, locale } = useLocaleSettings();
  const company = activeCompany?.id ?? me.defaultCompanyId ?? me.companies[0]?.id ?? '';
  useDocumentTitle(t('loan.apply.title'));
  const [typeId, setTypeId] = useState('');
  const [principal, setPrincipal] = useState('');
  const [installments, setInstallments] = useState(12);
  const [employeeNo, setEmployeeNo] = useState('');
  const [guarantorNo, setGuarantorNo] = useState('');
  const [reason, setReason] = useState('');
  const [preview, setPreview] = useState<LoanPreview | null>(null);
  const types = useQuery({ queryKey: ['hrm', 'loan-types', company], queryFn: ({ signal }) => loansApi.types(company, signal), enabled: !!company && can('hrm.loans.create') });
  const valid = typeId !== '' && MONEY.test(principal.trim()) && Number(principal) > 0 && installments >= 1;
  const body = () => ({
    loanTypeId: typeId, principal: principal.trim(), installments,
    ...(employeeNo.trim() ? { employeeNo: employeeNo.trim() } : {}), ...(guarantorNo.trim() ? { guarantorEmployeeNo: guarantorNo.trim() } : {}), ...(reason.trim() ? { reason: reason.trim() } : {}),
  });
  const check = useMutation({ mutationFn: () => loansApi.preview(body()), onSuccess: setPreview });
  const send = useMutation({
    mutationFn: () => loansApi.apply(body()),
    onSuccess: (done) => {
      void queryClient.invalidateQueries({ queryKey: ['hrm', 'loans'] });
      toast.success(t('loan.sent', { no: done.loanNo }));
      void navigate(`${LOANS_PATH}/${done.id}`);
    },
  });
  const change = <T,>(set: (v: T) => void) => (v: T) => { set(v); setPreview(null); };
  const money = useMoney(preview?.currencyCode);
  if (!can('hrm.loans.create')) return <div className="erp-page"><PageHeader title={t('loan.apply.title')} /><EmptyState title={t('loan.denied')} /></div>;
  const typeOptions = (types.data ?? []).map((ty) => ({ id: ty.id, label: nameIn2(ty.nameI18n, locale) || ty.code }));
  const problem = send.error ?? check.error;
  const fieldError = (name: string) => (problem instanceof ApiProblem ? problem.fieldErrors[name]?.map((k) => server(k) ?? k).join(' ') : undefined);
  return (
    <div className="erp-page">
      <PageHeader title={t('loan.apply.title')} description={t('loan.apply.intro')} />
      <form className="erp-loan-form" aria-label={t('loan.apply.title')} onSubmit={(e) => { e.preventDefault(); if (valid) check.mutate(); }}>
        <SelectField label={t('loan.field.type')} isRequired options={typeOptions} value={typeId || null} onChange={change(setTypeId)} error={fieldError('loanTypeId') ?? null}
          {...(types.isSuccess && typeOptions.length === 0 ? { help: t('loan.type.none') } : {})} />
        <TextField label={t('loan.field.principal')} isRequired isCode value={principal} onChange={change(setPrincipal)} help={t('loan.field.principal.help')}
          error={fieldError('principal') ?? (principal.trim() !== '' && !MONEY.test(principal.trim()) ? t('loan.field.principal.invalid') : null)} />
        <NumberField label={t('loan.field.installments')} isRequired minValue={1} maxValue={360} value={installments} onChange={change(setInstallments)} help={t('loan.field.installments.help')} error={fieldError('installments') ?? null} />
        {writable && can('hrm.employments.read') && <TextField label={t('loan.field.employee')} isCode value={employeeNo} onChange={change(setEmployeeNo)} error={fieldError('employmentId') ?? null} />}
        <TextField label={t('loan.field.guarantor')} isCode value={guarantorNo} onChange={change(setGuarantorNo)} help={t('loan.field.guarantor.help')} error={fieldError('guarantorEmploymentId') ?? null} />
        <TextAreaField label={t('loan.field.reason')} maxLength={500} value={reason} onChange={setReason} error={fieldError('reason') ?? null} />
        <div className="erp-row">
          <Button type="submit" variant="secondary" isPending={check.isPending} isDisabled={!valid || !writable}>{t('loan.check')}</Button>
          <Button variant="primary" isPending={send.isPending} isDisabled={!valid || !writable || preview === null || !preview.eligible} onPress={() => send.mutate()}>{t('loan.send')}</Button>
        </div>
      </form>
      <div className="erp-stack" aria-live="polite">
        {check.isError && <Banner tone="danger">{refusal(check.error)}</Banner>}
        {send.isError && <Banner tone="danger">{refusal(send.error)}</Banner>}
        {preview && (
          <section aria-label={t('loan.result')} className="erp-stack">
            <Banner tone={preview.eligible ? 'success' : 'warning'} title={t(preview.eligible ? 'loan.result.eligible' : 'loan.result.notEligible')}>
              {preview.eligible && <p>{t(`loan.preview.method.${preview.interestMethod}` as MessageKey, { rate: formatInteger(Math.round(preview.annualInterestPercent), numbers) })}</p>}
              {preview.requiresGuarantor && <p>{t('loan.preview.guarantor')}</p>}
            </Banner>
            <Problems items={preview.problems} title={t('loan.problems')} />
            <Problems items={preview.warnings} title={t('loan.warnings')} />
            {preview.maxAmount != null && <p>{t('loan.preview.max', { amount: money(preview.maxAmount) })}</p>}
            {preview.schedule
              ? <>
                <p>{t('loan.preview.totals', { interest: money(preview.totalInterest), total: money(preview.totalRepayable), count: formatInteger(preview.schedule.length, numbers) })}</p>
                <Schedule lines={preview.schedule} currency={preview.currencyCode} hidden={false} />
              </>
              : <p className="erp-muted" role="status">{t('loan.preview.amountsHidden')}</p>}
          </section>
        )}
      </div>
    </div>
  );
}

function nameIn2(names: Record<string, string>, locale: string): string {
  return names[locale] ?? names.en ?? Object.values(names)[0] ?? '';
}

// ------------------------------------------------------------------ the loan page

function LoanView() {
  const { id = '' } = useParams();
  const { t } = useText();
  const navigate = useNavigate();
  const { can, writable } = useShell();
  const refusal = useRefusal();
  const detail = useQuery({ queryKey: ['hrm', 'loans', id], queryFn: ({ signal }) => loansApi.detail(id, signal), retry: false, enabled: can('hrm.loans.read') });
  useDocumentTitle(detail.data ? `${t('loan.title')} ${detail.data.loanNo}` : t('loan.title'));
  const [dialog, setDialog] = useState<'disburse' | 'receipt' | 'cancel' | null>(null);
  if (!can('hrm.loans.read')) return <div className="erp-page"><PageHeader title={t('loan.title')} /><EmptyState title={t('loan.denied')} /></div>;
  if (detail.isPending) return <div className="erp-page"><Skeleton lines={6} /></div>;
  if (detail.isError) {
    const gone = detail.error instanceof ApiProblem && detail.error.status === 404;
    return <div className="erp-page"><ErrorPanel title={t('loan.title')} message={gone ? t('loan.notFound') : refusal(detail.error)} action={<Button onPress={() => { void navigate(LOANS_PATH); }}>{t('loan.back')}</Button>} /></div>;
  }
  return <LoanPage d={detail.data} dialog={dialog} setDialog={setDialog} can={can} writable={writable} />;
}

function LoanPage({ d, dialog, setDialog, can, writable }: { d: LoanDetail; dialog: 'disburse' | 'receipt' | 'cancel' | null; setDialog: (v: 'disburse' | 'receipt' | 'cancel' | null) => void; can: (code: string) => boolean; writable: boolean }) {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  const m = useMoney(d.currencyCode);
  const day = useDay();
  const hidden = d.principal === undefined;
  const typeName = nameIn2(d.loanType.nameI18n, locale) || d.loanType.code;
  return (
    <div className="erp-page">
      <PageHeader title={`${t('loan.title')} ${d.loanNo}`} breadcrumbs={[{ label: t('loan.title'), href: LOANS_PATH }, { label: d.loanNo }]}
        actions={writable && (
          <div className="erp-row">
            {d.status === 'approved' && can('hrm.loans.disburse') && <Button variant="primary" onPress={() => setDialog('disburse')}>{t('loan.disburse')}</Button>}
            {d.status === 'active' && can('hrm.loans.receipt') && d.pendingReceipts === 0 && <Button onPress={() => setDialog('receipt')}>{t('loan.receipt')}</Button>}
            {d.status === 'approved' && can('hrm.loans.cancel') && <Button variant="danger" onPress={() => setDialog('cancel')}>{t('loan.cancel')}</Button>}
          </div>)} />
      <div className="erp-stack">
        <dl className="erp-loan-facts">
          <dt>{t('loan.fact.number')}</dt><dd><bdi>{d.loanNo}</bdi> <StatusChip status={STATUS_CHIP[d.status] ?? 'inactive'} label={t(`loan.status.${d.status}` as MessageKey)} /></dd>
          <dt>{t('loan.fact.employee')}</dt><dd><bdi>{d.employeeNo}</bdi></dd>
          <dt>{t('loan.fact.type')}</dt><dd>{typeName}</dd>
          {!hidden && <><dt>{t('loan.fact.amount')}</dt><dd>{m(d.principal)}</dd></>}
          {d.balance !== undefined && <><dt>{t('loan.fact.balance')}</dt><dd><strong>{m(d.balance)}</strong></dd></>}
          <dt>{t('loan.fact.interest')}</dt><dd>{d.interestMethod ? t(`loan.preview.method.${d.interestMethod}` as MessageKey, { rate: formatInteger(Math.round(d.interestRate), numbers) }) : ''}</dd>
          <dt>{t('loan.fact.installments')}</dt><dd>{formatInteger(d.installments, numbers)}</dd>
          {d.disbursedOn && <><dt>{t('loan.fact.disbursed')}</dt><dd>{day(d.disbursedOn)}</dd></>}
          {d.reason && <><dt>{t('loan.fact.reason')}</dt><dd>{d.reason}</dd></>}
        </dl>
        {d.pendingReceipts > 0 && <Banner tone="info">{t('loan.receipt.waiting')}</Banner>}
        {hidden && <Banner tone="info">{t('loan.amounts.hidden')}</Banner>}
        <h2>{t('loan.schedule')}</h2>
        {d.schedule.length === 0 ? <p className="erp-muted" role="status">{t('loan.schedule.empty')}</p>
          : <Schedule hidden={hidden} currency={d.currencyCode} lines={d.schedule.map((s) => ({ seq: s.seq, periodStart: s.periodStart, periodEnd: s.periodEnd, principalAmount: s.principalAmount, interestAmount: s.interestAmount, status: s.status }))} />}
        <h2>{t('loan.ledger')}</h2>
        {d.ledger.length === 0 ? <p className="erp-muted" role="status">{t('loan.ledger.empty')}</p> : (
          <Region label={t('loan.ledger')} head={<>
            <th scope="col">{t('loan.col.date')}</th><th scope="col">{t('loan.col.kind')}</th><th scope="col" className="erp-loan-num">{t('loan.col.amount')}</th><th scope="col">{t('loan.col.reference')}</th>
          </>}>
            {d.ledger.map((e) => (
              <tr key={e.id}>
                <td>{day(e.txnDate)}</td><td>{t(`loan.kind.${e.kind}` as MessageKey)}</td><td className="erp-loan-num">{e.amount === undefined ? '' : m(e.amount)}</td><td><bdi>{e.reference ?? ''}</bdi></td>
              </tr>))}
          </Region>)}
      </div>
      {dialog === 'disburse' && <DisburseDialog d={d} onClose={() => setDialog(null)} />}
      {dialog === 'receipt' && <ReceiptDialog d={d} onClose={() => setDialog(null)} />}
      {dialog === 'cancel' && <CancelDialog d={d} onClose={() => setDialog(null)} />}
    </div>
  );
}

const today = () => new Date().toISOString().slice(0, 10);

function DisburseDialog({ d, onClose }: { d: LoanDetail; onClose: () => void }) {
  const { t, server } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refusal = useRefusal();
  const [date, setDate] = useState<CalendarDate | null>(parseDate(today()));
  const [reference, setReference] = useState('');
  const send = useMutation({
    mutationFn: () => loansApi.disburse(d.id, { date: date?.toString() ?? '', reference: reference.trim() }),
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: ['hrm', 'loans'] }); toast.success(t('loan.disbursed')); onClose(); },
  });
  const field = (n: string) => (send.error instanceof ApiProblem ? send.error.fieldErrors[n]?.map((k) => server(k) ?? k).join(' ') ?? null : null);
  return (
    <Dialog title={t('loan.disburse.title', { no: d.loanNo })} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={(close) => <>
        <Button variant="quiet" onPress={close}>{t('loan.dialog.close')}</Button>
        <Button variant="primary" isPending={send.isPending} isDisabled={date === null || reference.trim() === ''} onPress={() => send.mutate()}>{t('loan.disburse.confirm')}</Button>
      </>}>
      <p className="erp-muted">{t('loan.disburse.help')}</p>
      <DateField label={t('loan.disburse.date')} isRequired value={date} onChange={(v) => setDate(v ? parseDate(v.toString()) : null)} error={field('date')} />
      <TextField label={t('loan.disburse.reference')} isRequired isCode maxLength={100} value={reference} onChange={setReference} error={field('reference')} />
      {send.isError && <Banner tone="danger">{refusal(send.error)}</Banner>}
    </Dialog>
  );
}

function ReceiptDialog({ d, onClose }: { d: LoanDetail; onClose: () => void }) {
  const { t, server } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refusal = useRefusal();
  const [date, setDate] = useState<CalendarDate | null>(parseDate(today()));
  const [reference, setReference] = useState('');
  const [amount, setAmount] = useState('');
  const [full, setFull] = useState(false);
  const [reason, setReason] = useState('');
  const valid = date !== null && reference.trim() !== '' && (full || (MONEY.test(amount.trim()) && Number(amount) > 0));
  const send = useMutation({
    mutationFn: () => loansApi.receipt(d.id, { fullSettlement: full, ...(full ? {} : { amount: amount.trim() }), date: date?.toString() ?? '', reference: reference.trim(), ...(reason.trim() ? { reason: reason.trim() } : {}) }),
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: ['hrm', 'loans'] }); toast.success(t('loan.receipt.sent')); onClose(); },
  });
  const field = (n: string) => (send.error instanceof ApiProblem ? send.error.fieldErrors[n]?.map((k) => server(k) ?? k).join(' ') ?? null : null);
  return (
    <Dialog title={t('loan.receipt.title', { no: d.loanNo })} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={(close) => <>
        <Button variant="quiet" onPress={close}>{t('loan.dialog.close')}</Button>
        <Button variant="primary" isPending={send.isPending} isDisabled={!valid} onPress={() => send.mutate()}>{t('loan.receipt.confirm')}</Button>
      </>}>
      <p className="erp-muted">{t('loan.receipt.help')}</p>
      <CheckboxField label={t('loan.receipt.full')} isSelected={full} onChange={setFull} />
      {!full && <TextField label={t('loan.receipt.amount')} isRequired isCode value={amount} onChange={setAmount} error={field('amount')} />}
      <DateField label={t('loan.receipt.date')} isRequired value={date} onChange={(v) => setDate(v ? parseDate(v.toString()) : null)} error={field('date')} />
      <TextField label={t('loan.receipt.reference')} isRequired isCode maxLength={100} value={reference} onChange={setReference} error={field('reference')} />
      <TextAreaField label={t('loan.receipt.reason')} maxLength={500} value={reason} onChange={setReason} />
      {send.isError && <Banner tone="danger">{refusal(send.error)}</Banner>}
    </Dialog>
  );
}

function CancelDialog({ d, onClose }: { d: LoanDetail; onClose: () => void }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refusal = useRefusal();
  const send = useMutation({
    mutationFn: () => loansApi.cancel(d.id),
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: ['hrm', 'loans'] }); toast.success(t('loan.cancelled')); onClose(); },
  });
  return (
    <Dialog title={t('loan.cancel')} role="alertdialog" isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={(close) => <>
        <Button variant="quiet" onPress={close}>{t('loan.dialog.close')}</Button>
        <Button variant="danger" isPending={send.isPending} onPress={() => send.mutate()}>{t('loan.cancel.confirm')}</Button>
      </>}>
      <p>{t('loan.cancel.body')}</p>
      {send.isError && <Banner tone="danger">{refusal(send.error)}</Banner>}
    </Dialog>
  );
}
