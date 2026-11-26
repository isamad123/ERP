// The loan card of the shared approvals inbox (slice S1.41 Build item 7; FR-HRM-LON-02, FR-XM-32): under the generic fields of a loan application the approver sees the facts and the
// installments the loan will have. The decision buttons are the inbox's. Amounts are salary-class data: an approver without that field class sees the facts only.
import { useQuery } from '@tanstack/react-query';
import { Skeleton } from '@erp/ui';
import { formatInteger, formatMoney, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { loansApi } from '../../api/loans';
import type { ApprovalCardProps } from '../kernel/approval-cards';
import './loans-messages';
import './loans.css';

export default function LoanApprovalCard({ documentId }: ApprovalCardProps) {
  const { t } = useText();
  const { numbers, locale } = useLocaleSettings();
  const lang = locale === 'ur' ? 'ur' : 'en';
  const detail = useQuery({ queryKey: ['hrm', 'loans', 'card', documentId], queryFn: ({ signal }) => loansApi.detail(documentId, signal), retry: false });
  if (detail.isPending) return <Skeleton lines={4} />;
  if (detail.isError) return <section aria-label={t('loan.card.title')}><p className="erp-muted" role="status">{t('loan.card.unavailable')}</p></section>;
  const d = detail.data;
  const money = (v: number | undefined) => (v === undefined ? '' : formatMoney(String(v), d.currencyCode, lang, numbers));
  return (
    <section aria-labelledby="erp-loan-card" className="erp-stack">
      <h3 id="erp-loan-card">{t('loan.card.title')} <bdi>{d.loanNo}</bdi></h3>
      <dl className="erp-loan-facts">
        <dt>{t('loan.fact.employee')}</dt><dd><bdi>{d.employeeNo}</bdi></dd>
        <dt>{t('loan.fact.type')}</dt><dd>{d.loanType.nameI18n[locale] ?? d.loanType.nameI18n.en ?? d.loanType.code}</dd>
        {d.principal !== undefined && <><dt>{t('loan.fact.amount')}</dt><dd>{money(d.principal)}</dd></>}
        <dt>{t('loan.fact.installments')}</dt><dd>{formatInteger(d.installments, numbers)}</dd>
        <dt>{t('loan.fact.interest')}</dt><dd>{d.interestMethod ? t(`loan.preview.method.${d.interestMethod}` as MessageKey, { rate: formatInteger(Math.round(d.interestRate), numbers) }) : ''}</dd>
        {d.reason && <><dt>{t('loan.fact.reason')}</dt><dd>{d.reason}</dd></>}
      </dl>
      {d.principal === undefined && <p className="erp-muted" role="status">{t('loan.amounts.hidden')}</p>}
    </section>
  );
}
