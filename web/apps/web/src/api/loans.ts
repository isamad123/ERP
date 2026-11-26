// Hand-written client of the loan services (slice S1.41; FR-HRM-LON-01 to -03, -05, -07, GHR-06, FR-XM-32). The lists of loan types, loans, installments and ledger entries are the
// generated reads; everything that checks, saves or decides goes through these endpoints, which check the rules again. Texts in answers are server keys, shown through
// `useText().server(key)`. Amounts are salary-class data: an answer for a caller without that field class has no amount members, and a screen shows none. Money is written
// into the request as the exact decimal the person typed, never through a float.
import { requestJson } from './client';

const enc = encodeURIComponent;

export interface LoanProblemItem { key: string; value: string | null }
export interface LoanScheduleLine {
  seq: number; payPeriodId: string | null; periodStart: string | null; periodEnd: string | null;
  principalAmount?: number; interestAmount?: number; total?: number;
}
export interface LoanPreview {
  employmentId: string; employeeNo: string;
  loanType: { id: string; code: string; kind: string };
  currencyCode: string | null; interestMethod: 'none' | 'flat' | 'reducing'; annualInterestPercent: number;
  installments: number; maxInstallments: number; requiresGuarantor: boolean;
  eligible: boolean; problems: LoanProblemItem[]; warnings: LoanProblemItem[]; startPeriodId: string | null;
  principal?: number; maxAmount?: number | null; totalInterest?: number; totalRepayable?: number; schedule?: LoanScheduleLine[];
}
export interface LoanApplied { id: string; loanNo: string; status: string; instanceId: string; employmentId: string; onBehalf: boolean }

export interface LoanInstallmentView {
  id: string; seq: number; payPeriodId: string; periodStart: string; periodEnd: string; status: 'scheduled' | 'deducted' | 'skipped' | 'settled_early' | string; recovered: boolean;
  principalAmount?: number; interestAmount?: number;
}
export interface LoanLedgerView { id: string; kind: string; txnDate: string; reference: string | null; sourceType: string | null; amount?: number }
export interface LoanDetail {
  id: string; loanNo: string; status: string; employmentId: string; employeeNo: string;
  loanType: { id: string; code: string; kind: string; nameI18n: Record<string, string> };
  currencyCode: string; interestMethod: string | null; interestRate: number; installments: number; startPeriodId: string | null; guarantorEmploymentId: string | null;
  disbursedOn: string | null; workflowInstanceId: string | null; reason: string | null; rowVersion: number;
  principal?: number; balance?: number; schedule: LoanInstallmentView[]; ledger: LoanLedgerView[]; pendingReceipts: number;
}
export interface LoanRequestBody { employmentId?: string; loanTypeId: string; principal: string; installments: number; startPeriodId?: string; guarantorEmploymentId?: string; reason?: string }

export interface OutstandingRow {
  loanId: string; loanNo: string; status: string; employmentId: string; employeeNo: string; companyId: string; typeCode: string; kind: string; currencyCode: string;
  installmentsLeft: number; nextDue: string | null; disbursedOn: string | null; principal?: number; balance?: number; repayable?: number;
}
export interface ReconciliationCompany {
  companyId: string; currencyCode: string; loans: number; differenceCount: number; reconciles: boolean;
  disbursed?: number; recovered?: number; outstanding?: number; expectedOutstanding?: number; scheduledPrincipalOutstanding?: number;
  differences?: { loanId: string; loanNo: string; ledgerBalance: number; scheduledPrincipal: number; difference: number }[];
}
export interface LoanTypeRow { id: string; companyId: string; code: string; nameI18n: Record<string, string>; kind: string; isActive: boolean }

/** The body of a request written as JSON with the money exactly as typed (a decimal with at most four places). */
const body = (b: LoanRequestBody) => {
  const money = /^\d+(\.\d{1,4})?$/.test(b.principal.trim()) ? b.principal.trim() : '0';
  const rest: Record<string, unknown> = { loanTypeId: b.loanTypeId, installments: b.installments };
  for (const k of ['employmentId', 'startPeriodId', 'guarantorEmploymentId', 'reason'] as const) if (b[k]) rest[k] = b[k];
  return JSON.stringify(rest).replace(/}$/, `,"principal":${money}}`);
};

export const loansApi = {
  types: (companyId: string, signal?: AbortSignal) =>
    requestJson<{ items: LoanTypeRow[] }>('GET', `/api/hrm/loan-types?companyId=${enc(companyId)}&limit=200`, { signal }).then((r) => r.items.filter((x) => x.isActive)),
  preview: (b: LoanRequestBody) => requestJson<LoanPreview>('POST', '/api/hrm/loans/preview', { rawBody: body(b) }),
  apply: (b: LoanRequestBody) => requestJson<LoanApplied>('POST', '/api/hrm/loans', { rawBody: body(b) }),
  detail: (id: string, signal?: AbortSignal) => requestJson<LoanDetail>('GET', `/api/hrm/loans/${id}/detail`, { signal }),
  cancel: (id: string) => requestJson<{ id: string; status: string }>('POST', `/api/hrm/loans/${id}/cancel`, { body: {} }),
  disburse: (id: string, v: { date: string; reference: string }) => requestJson<{ id: string; status: string }>('POST', `/api/hrm/loans/${id}/disburse`, { body: v }),
  receipt: (id: string, v: { amount?: string; fullSettlement: boolean; date: string; reference: string; reason?: string }) => {
    const money = !v.fullSettlement && v.amount && /^\d+(\.\d{1,4})?$/.test(v.amount.trim()) ? `,"amount":${v.amount.trim()}` : '';
    const text = JSON.stringify({ fullSettlement: v.fullSettlement, date: v.date, reference: v.reference, ...(v.reason ? { reason: v.reason } : {}) }).replace(/}$/, `${money}}`);
    return requestJson<{ status: string; requestId: string }>('POST', `/api/hrm/loans/${id}/receipts`, { rawBody: text });
  },
  outstanding: (companyId: string, paging: { limit: number; offset: number }, signal?: AbortSignal) =>
    requestJson<{ total: number; items: OutstandingRow[] }>('GET', `/api/hrm/loan-reports/outstanding?companyId=${enc(companyId)}&limit=${paging.limit}&offset=${paging.offset}`, { signal }),
  reconciliation: (companyId: string, signal?: AbortSignal) =>
    requestJson<{ companies: ReconciliationCompany[] }>('GET', `/api/hrm/loan-reports/reconciliation?companyId=${enc(companyId)}`, { signal }),
};
