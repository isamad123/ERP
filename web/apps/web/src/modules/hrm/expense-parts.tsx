// Pieces shared by the expense screens (slice S1.42; FR-HRM-EXP-02, EXP-03, GHR-06): status and payment chips, policy flags as badges with text (colour is never the only sign), money
// from exact decimal strings with the "hidden" placeholder for a person who may not see amounts (never a zero), the categories of a company, and the refusal text of the server.
import './expenses.css';
import { useQuery } from '@tanstack/react-query';
import { EyeOff } from 'lucide-react';
import { StatusChip, type Status } from '@erp/ui';
import { formatMoney, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { expensesApi, type Category, type ClaimStatus, type ExpenseFlag, type PaymentStatus } from '../../api/expenses';
import { nameOf } from '../../api/workflow';
import './expenses-messages';

const CHIP: Record<ClaimStatus, Status> = { draft: 'draft', submitted: 'pending', approved: 'approved', rejected: 'rejected', reimbursed: 'approved', cancelled: 'inactive' };
const PAY_CHIP: Record<PaymentStatus, Status> = { none: 'inactive', pending: 'pending', paid: 'approved', failed: 'blocked' };

export function ClaimStatusChip({ status }: { status: ClaimStatus }) {
  const { t } = useText();
  return <StatusChip status={CHIP[status]} label={t(`exp.status.${status}` as MessageKey)} />;
}

export function PaymentChip({ status }: { status: PaymentStatus }) {
  const { t } = useText();
  return <StatusChip status={PAY_CHIP[status]} label={t(`exp.pay.${status}` as MessageKey)} />;
}

/** One policy flag: an icon and its words; a flag of a category that blocks submit says so (a second chip with its own words). */
export function FlagBadge({ flag, blocks = false }: { flag: ExpenseFlag; blocks?: boolean }) {
  const { t } = useText();
  return <StatusChip status={blocks ? 'blocked' : 'pending'} label={t(`exp.flag.${flag}` as MessageKey)} />;
}

export function Flags({ flags, blocksSubmit }: { flags: ExpenseFlag[]; blocksSubmit: boolean }) {
  const { t } = useText();
  if (flags.length === 0) return null;
  return (
    <ul className="erp-exp-flags" aria-label={t('exp.flags')}>
      {flags.map((f) => <li key={f}><FlagBadge flag={f} blocks={blocksSubmit} /></li>)}
      {blocksSubmit && <li><span className="erp-exp-blocks">{t('exp.flag.blocks')}</span></li>}
    </ul>
  );
}

/** An amount from an exact decimal string; null means the person may not see it and is shown as such, never as zero (GHR-06). */
export function Money({ value, currency }: { value: string | null | undefined; currency: string }) {
  const { t } = useText();
  const { locale, numbers } = useLocaleSettings();
  if (value === null || value === undefined) {
    return <span className="erp-exp-hidden" title={t('exp.hidden.title')}><EyeOff size={14} aria-hidden="true" /> {t('exp.hidden')}</span>;
  }
  return <bdi>{formatMoney(value, currency, locale === 'ur' ? 'ur' : 'en', numbers)}</bdi>;
}

/** The server's words for an answer that refused (a key), else a general line. */
export function useExpenseRefusal(): (error: unknown) => string {
  const { t, server } = useText();
  return (error) => {
    if (error instanceof ApiProblem) {
      if (error.status === 0) return t('exp.error.network');
      const first = Object.values(error.fieldErrors)[0]?.[0];
      return server(error.key) ?? (first ? server(first) : null) ?? (error.errors[0] ? server(error.errors[0]) : null) ?? t('exp.error.generic');
    }
    return t('exp.error.generic');
  };
}

/** The active categories of a company (kind, receipt rule, whether flags block) for the form's choices. */
export function useCategories(companyId: string | null) {
  return useQuery({ queryKey: ['hrm', 'expenses', 'categories', companyId], queryFn: ({ signal }) => expensesApi.categories(companyId as string, signal), enabled: !!companyId, staleTime: 60_000 });
}

export function useCategoryName() {
  const { locale } = useLocaleSettings();
  return (category: Pick<Category, 'nameI18n' | 'code'> | undefined) => (category ? nameOf(category.nameI18n, locale) || category.code : '');
}
