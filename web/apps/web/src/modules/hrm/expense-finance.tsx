// Expense payments (slice S1.42; FR-HRM-EXP-05, EXP-06, GHR-06): the claims that are approved and wait to be paid, or are paid, with the payment status the payment returned. The list is the
// generated claims list with its status filter; the payment status is narrowed on the screen. Approved totals the person may not see are shown as "hidden", never as zero.
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useDocumentTitle } from '@erp/engine';
import { Button, EmptyState, ErrorPanel, Link, PageHeader, ScrollRegion, SelectField, Skeleton } from '@erp/ui';
import { formatDateTime, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { expensesApi, type ClaimStatus, type PaymentStatus } from '../../api/expenses';
import { useShell } from '../../shell/context';
import { ClaimStatusChip, Money, PaymentChip, useExpenseRefusal } from './expense-parts';
import { ApiProblem } from '../../api/client';
import './expenses.css';
import './expenses-messages';

const PAYMENTS: ('all' | PaymentStatus)[] = ['all', 'pending', 'failed', 'paid', 'none'];

export default function ExpenseFinanceScreen() {
  const { t } = useText();
  const { can } = useShell();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const refusal = useExpenseRefusal();
  const [status, setStatus] = useState<ClaimStatus>('approved');
  const [payment, setPayment] = useState<'all' | PaymentStatus>('all');
  useDocumentTitle(t('exp.finance.title'));
  const claims = useQuery({ queryKey: ['hrm', 'expenses', 'finance', status], queryFn: ({ signal }) => expensesApi.claims(status, signal), enabled: can('hrm.expense_claims.read') });
  const heading = <PageHeader title={t('exp.finance.title')} description={t('exp.finance.description')} />;
  if (!can('hrm.expense_claims.read')) return <div className="erp-page">{heading}<EmptyState title={t('exp.denied')}>{t('exp.denied.body')}</EmptyState></div>;
  const rows = (claims.data ?? []).filter((c) => payment === 'all' || c.paymentStatus === payment);
  const denied = claims.error instanceof ApiProblem && claims.error.status === 403;
  return (
    <div className="erp-page">
      {heading}
      <div className="erp-stack">
        <div className="erp-exp-fields">
          <SelectField label={t('exp.finance.status')} value={status} onChange={(v) => setStatus(v as ClaimStatus)}
            options={[{ id: 'approved', label: t('exp.finance.status.approved') }, { id: 'reimbursed', label: t('exp.finance.status.reimbursed') }]} />
          <SelectField label={t('exp.finance.payment')} value={payment} onChange={(v) => setPayment(v as 'all' | PaymentStatus)}
            options={PAYMENTS.map((p) => ({ id: p, label: t(p === 'all' ? 'exp.finance.payment.all' : (`exp.pay.${p}` as MessageKey)) }))} />
        </div>
        {claims.isPending && <Skeleton lines={5} />}
        {claims.isError && (denied ? <EmptyState title={t('exp.denied')}>{t('exp.denied.body')}</EmptyState>
          : <ErrorPanel title={t('exp.loadError')} message={refusal(claims.error)} action={<Button onPress={() => { void claims.refetch(); }}>{t('exp.retry')}</Button>} />)}
        {claims.data && rows.length === 0 && <EmptyState title={t('exp.finance.empty')}>{t('exp.finance.empty.body')}</EmptyState>}
        {rows.length > 0 && (
          <ScrollRegion label={t('exp.finance.title')}>
            <table className="erp-exp-table">
              <caption className="erp-visually-hidden">{t('exp.finance.title')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('exp.facts.claimNo')}</th><th scope="col">{t('exp.field.title')}</th><th scope="col">{t('exp.facts.status')}</th><th scope="col">{t('exp.facts.payment')}</th>
                  <th scope="col">{t('exp.facts.approvedTotal')}</th><th scope="col">{t('exp.field.via')}</th><th scope="col">{t('exp.facts.reference')}</th><th scope="col">{t('exp.facts.submitted')}</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((c) => (
                  <tr key={c.id}>
                    <th scope="row"><Link href={`/expenses/claims/${c.id}`}><bdi>{c.claimNo}</bdi></Link></th>
                    <td>{c.title ? <bdi>{c.title}</bdi> : <span className="erp-muted">{t('exp.facts.none')}</span>}</td>
                    <td><ClaimStatusChip status={c.status} /></td>
                    <td><PaymentChip status={c.paymentStatus} /></td>
                    <td><Money value={c.approvedTotal} currency={c.currencyCode} /></td>
                    <td>{t(`exp.via.${c.reimburseVia}` as MessageKey)}</td>
                    <td>{c.paymentReference ? <bdi>{c.paymentReference}</bdi> : <span className="erp-muted">{t('exp.facts.none')}</span>}</td>
                    <td>{c.submittedAt ? formatDateTime(c.submittedAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers) : t('exp.facts.none')}</td>
                  </tr>))}
              </tbody>
            </table>
          </ScrollRegion>)}
      </div>
    </div>
  );
}
