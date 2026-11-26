// Hand-written client of the expense claim services (slice S1.42; FR-HRM-EXP-02, EXP-03, EXP-05, GHR-06, FR-XM-32). The lists of claims, categories and exchange rates are
// the generated reads; making, changing, submitting and deciding goes through these endpoints, which check the rules again. Money is an exact decimal: amounts are written into
// the request as JSON numbers from validated decimal strings and read back as strings (never through a floating-point number). Texts in answers are server keys.
import { requestJson, sendText } from './client';

const enc = encodeURIComponent;

export type ClaimStatus = 'draft' | 'submitted' | 'approved' | 'rejected' | 'reimbursed' | 'cancelled';
export type PaymentStatus = 'none' | 'pending' | 'paid' | 'failed';
export type ReimburseVia = 'payroll' | 'payment_batch';
export type ExpenseFlag = 'over_limit' | 'duplicate' | 'missing_receipt' | 'out_of_policy';
export type CategoryKind = 'general' | 'per_diem' | 'mileage';

export interface NewClaimContext { employmentId: string | null; employeeNo: string | null; companyId: string | null; currencyCode: string | null; onBehalf: boolean; today: string }

export interface ClaimLineBody {
  id?: string; categoryId: string; expenseDate: string; amount: string; currencyCode: string; quantity?: string; description?: string; receiptFileId?: string; project?: string; costCentre?: string;
}
export interface ClaimBody { employmentId?: string; title?: string; reimburseVia: ReimburseVia; lines: ClaimLineBody[]; submit?: boolean; rowVersion?: number }

export interface ClaimLine {
  id: string; categoryId: string; categoryCode: string; categoryName: Record<string, string>; taxTreatment: string | null; expenseDate: string;
  originalCurrencyCode: string; originalAmount: string | null; exchangeRate: string | null; rateDate: string | null; amount: string | null; quantity: string | null;
  description: string | null; receiptFileId: string | null; project: string | null; costCentre: string | null; flags: ExpenseFlag[]; blocksSubmit: boolean;
  approvedAmount: string | null; decisionReason: string | null; decidedAt: string | null;
}

export interface ClaimView {
  id: string; claimNo: string; status: ClaimStatus; title: string | null; employmentId: string; employeeNo: string | null; currencyCode: string; reimburseVia: ReimburseVia;
  paymentStatus: PaymentStatus; paymentReference: string | null; payrollInput: string | null; needsFinance: boolean; submittedAt: string | null; reimbursedAt: string | null;
  instanceId: string | null; returned: boolean; totalAmount: string | null; approvedTotal: string | null; amountsHidden: boolean; canEdit: boolean; canDecide: boolean; rowVersion: number; lines: ClaimLine[];
}

export interface Submitted { id: string; claimNo: string; status: ClaimStatus; instanceId: string; needsFinance: boolean; flagged: number; rowVersion: number }
export interface ClaimRow {
  id: string; companyId: string; claimNo: string; employmentId: string; title: string | null; status: ClaimStatus; currencyCode: string; totalAmount?: string | null; approvedTotal?: string | null;
  reimburseVia: ReimburseVia; paymentStatus: PaymentStatus; paymentReference: string | null; submittedAt: string | null; reimbursedAt: string | null;
}
export interface Category { id: string; code: string; nameI18n: Record<string, string>; kind: CategoryKind; requiresReceipt: boolean; flagsBlockSubmit: boolean; isActive: boolean }
export interface LineDecision { lineId: string; approvedAmount: string; reason?: string }

// Every number of these members is a decimal that must stay exact.
const DECIMAL_MEMBERS = ['amount', 'originalAmount', 'totalAmount', 'approvedTotal', 'approvedAmount', 'exchangeRate', 'quantity'];
const READ = new RegExp(`"(${DECIMAL_MEMBERS.join('|')})"\\s*:\\s*(-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)`, 'g');
const WRITE_MARK = /\{"__dec":"(-?\d+(?:\.\d+)?)"\}/g;

/** Reads an answer's text with its decimal members as strings (exact digits). */
export function exactJson<T>(text: string): T {
  return JSON.parse(text.replace(READ, (_m, key: string, number: string) => `"${key}":"${number}"`)) as T;
}

/** Writes a body with its decimal members as JSON numbers with the digits typed (validated before: digits and one point). */
export function decimalJson(body: unknown): string {
  return JSON.stringify(body, (key, value: unknown) => (DECIMAL_MEMBERS.includes(key) && typeof value === 'string' && /^-?\d+(\.\d+)?$/.test(value) ? { __dec: value } : value)).replace(WRITE_MARK, '$1');
}

const get = async <T>(path: string, signal?: AbortSignal): Promise<T> => exactJson<T>(await sendText('GET', path, undefined, signal));
const post = async <T>(method: string, path: string, body: unknown): Promise<T> => exactJson<T>((await sendText(method, path, decimalJson(body))) || '{}');

export const expensesApi = {
  context: (employmentId?: string, signal?: AbortSignal) => requestJson<NewClaimContext>('GET', `/api/hrm/expense-claims/new-context${employmentId ? `?employmentId=${enc(employmentId)}` : ''}`, { signal }),
  create: (body: ClaimBody) => post<ClaimView | Submitted>('POST', '/api/hrm/expense-claims', body),
  update: (id: string, body: ClaimBody) => post<ClaimView | Submitted>('PUT', `/api/hrm/expense-claims/${enc(id)}`, body),
  submit: (id: string, rowVersion: number) => post<Submitted>('POST', `/api/hrm/expense-claims/${enc(id)}/submit`, { rowVersion }),
  cancel: (id: string, rowVersion: number) => post<ClaimView>('POST', `/api/hrm/expense-claims/${enc(id)}/cancel`, { rowVersion }),
  detail: (id: string, signal?: AbortSignal) => get<ClaimView>(`/api/hrm/expense-claims/${enc(id)}/detail`, signal),
  decide: (id: string, decisions: LineDecision[]) => post<ClaimView>('POST', `/api/hrm/expense-claims/${enc(id)}/line-decisions`, { decisions }),
  /** A waiting claim is withdrawn through the shared approval endpoint. */
  withdraw: (instanceId: string, reason: string) => requestJson<unknown>('POST', `/api/wf/instances/${enc(instanceId)}/withdraw`, { body: { reason } }),
  categories: async (companyId: string, signal?: AbortSignal) =>
    (await get<{ items: Category[] }>(`/api/hrm/expense-categories?companyId=${enc(companyId)}&limit=200&sort=code`, signal)).items,
  claims: async (status: ClaimStatus, signal?: AbortSignal) => (await get<{ items: ClaimRow[] }>(`/api/hrm/expense-claims?status=${enc(status)}&limit=200&sort=-claimNo`, signal)).items,
};

export const AMOUNT = /^\d{1,15}(\.\d{1,4})?$/;
export const QUANTITY = /^\d{1,9}(\.\d{1,4})?$/;
/** True when a typed decimal is a positive amount. */
export const isPositive = (text: string): boolean => AMOUNT.test(text.trim()) && /[1-9]/.test(text);
