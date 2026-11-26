// The pure rules of the expense claim form (slice S1.42; FR-HRM-EXP-02): what a line needs, what the form checks before it asks the server (the server checks again), the body it
// sends, and how the server's per-field problems ("lines[0].amount") find their line. Amounts stay decimal strings, never numbers.
import { AMOUNT, QUANTITY, isPositive, type Category, type ClaimBody, type ClaimLine, type ClaimLineBody, type ClaimView, type ReimburseVia } from '../../api/expenses';

export interface LineDraft {
  key: string;
  /** The saved line this row is (kept on save); absent for a new row. */
  id?: string;
  categoryId: string;
  /** yyyy-MM-dd or empty. */
  expenseDate: string;
  amount: string;
  currencyCode: string;
  quantity: string;
  description: string;
  receiptFileId: string;
  project: string;
  costCentre: string;
}

export const MAX_LINES = 100;
export const CURRENCY = /^[A-Z]{3}$/;
export type LineField = 'category' | 'date' | 'amount' | 'currency' | 'quantity';
export type LineErrors = Partial<Record<LineField, string>>;

let counter = 0;
export const newKey = (): string => { counter += 1; return `line-${counter}`; };

export const emptyLine = (currency: string, today = ''): LineDraft => ({ key: newKey(), categoryId: '', expenseDate: today, amount: '', currencyCode: currency, quantity: '', description: '', receiptFileId: '', project: '', costCentre: '' });

/** A saved line as the form's row: the amount typed is what was claimed, in the currency it was claimed in. */
export function draftOf(line: ClaimLine): LineDraft {
  return {
    key: newKey(), id: line.id, categoryId: line.categoryId, expenseDate: line.expenseDate, amount: line.originalAmount ?? '', currencyCode: line.originalCurrencyCode,
    quantity: line.quantity && line.quantity !== '0' ? trimZeros(line.quantity) : '', description: line.description ?? '', receiptFileId: line.receiptFileId ?? '', project: line.project ?? '', costCentre: line.costCentre ?? '',
  };
}

/** "1200.0000" -> "1200", "12.5000" -> "12.5": the server keeps four places, the person typed fewer. */
export function trimZeros(value: string): string {
  return value.includes('.') ? value.replace(/0+$/, '').replace(/\.$/, '') : value;
}

export const needsQuantity = (category: Pick<Category, 'kind'> | undefined): boolean => category?.kind === 'per_diem' || category?.kind === 'mileage';

/** What each line is missing, by line key; an empty map means every line is complete. */
export function checkLines(lines: LineDraft[], categories: Map<string, Category>): Map<string, LineErrors> {
  const found = new Map<string, LineErrors>();
  for (const line of lines) {
    const errors: LineErrors = {};
    const category = categories.get(line.categoryId);
    if (!category) errors.category = 'exp.err.category';
    if (!/^\d{4}-\d{2}-\d{2}$/.test(line.expenseDate)) errors.date = 'exp.err.date';
    if (!isPositive(line.amount)) errors.amount = AMOUNT.test(line.amount.trim()) || line.amount.trim() === '' ? 'exp.err.amount.positive' : 'exp.err.amount';
    if (!CURRENCY.test(line.currencyCode)) errors.currency = 'exp.err.currency';
    if (needsQuantity(category)) {
      if (line.quantity.trim() === '') errors.quantity = 'exp.err.quantity.required';
      else if (!QUANTITY.test(line.quantity.trim()) || !/[1-9]/.test(line.quantity)) errors.quantity = 'exp.err.quantity';
    } else if (line.quantity.trim() !== '' && !QUANTITY.test(line.quantity.trim())) errors.quantity = 'exp.err.quantity';
    if (Object.keys(errors).length > 0) found.set(line.key, errors);
  }
  return found;
}

const text = (value: string): string | undefined => (value.trim() === '' ? undefined : value.trim());

export function lineBody(line: LineDraft, categories: Map<string, Category>): ClaimLineBody {
  const quantity = needsQuantity(categories.get(line.categoryId)) || line.quantity.trim() !== '' ? text(line.quantity) : undefined;
  return {
    ...(line.id ? { id: line.id } : {}), categoryId: line.categoryId, expenseDate: line.expenseDate, amount: line.amount.trim(), currencyCode: line.currencyCode,
    ...(quantity ? { quantity } : {}),
    ...(text(line.description) ? { description: text(line.description) as string } : {}),
    ...(line.receiptFileId ? { receiptFileId: line.receiptFileId } : {}),
    ...(text(line.project) ? { project: text(line.project) as string } : {}),
    ...(text(line.costCentre) ? { costCentre: text(line.costCentre) as string } : {}),
  };
}

export interface HeaderDraft { title: string; reimburseVia: ReimburseVia; employmentId?: string }

export function claimBody(header: HeaderDraft, lines: LineDraft[], categories: Map<string, Category>, extra: { submit?: boolean; rowVersion?: number } = {}): ClaimBody {
  return {
    ...(header.employmentId ? { employmentId: header.employmentId } : {}),
    ...(text(header.title) ? { title: text(header.title) as string } : {}),
    reimburseVia: header.reimburseVia,
    lines: lines.map((l) => lineBody(l, categories)),
    ...(extra.submit ? { submit: true } : {}),
    ...(extra.rowVersion !== undefined ? { rowVersion: extra.rowVersion } : {}),
  };
}

/** The server's field problems keyed `lines[2].amount` become `{ line 2: { amount: [keys] } }`; the rest are claim-level. */
export function serverProblems(fieldErrors: Readonly<Record<string, readonly string[]>>): { byLine: Map<number, Record<string, string>>; general: string[] } {
  const byLine = new Map<number, Record<string, string>>();
  const general: string[] = [];
  for (const [path, keys] of Object.entries(fieldErrors)) {
    const match = /^lines\[(\d+)\]\.?(.*)$/i.exec(path);
    const first = keys[0];
    if (!first) continue;
    if (match) {
      const index = Number(match[1]);
      byLine.set(index, { ...(byLine.get(index) ?? {}), [(match[2] ?? '').toLowerCase() || 'line']: first });
    } else general.push(first);
  }
  return { byLine, general };
}

/** The form field a server member name belongs to. */
export const FIELD_OF: Record<string, LineField | 'receipt' | 'line'> = {
  categoryid: 'category', expensedate: 'date', amount: 'amount', currencycode: 'currency', quantity: 'quantity', receiptfileid: 'receipt', line: 'line',
};

/** The lines of a claim that block submit now (a category whose flags block, and a flag on the line). */
export const blocking = (view: Pick<ClaimView, 'lines'>): ClaimLine[] => view.lines.filter((l) => l.blocksSubmit);

/** The rows of the decision form: what the approver has typed against each line. */
export interface DecisionDraft { approved: string; reason: string }

export function decisionErrors(line: Pick<ClaimLine, 'amount'>, draft: DecisionDraft): { approved?: string; reason?: string } {
  const approved = draft.approved.trim();
  if (!AMOUNT.test(approved)) return { approved: 'exp.decide.err.amount' };
  const claimed = line.amount ?? '0';
  if (compareDecimal(approved, claimed) > 0) return { approved: 'exp.decide.err.above' };
  if (compareDecimal(approved, claimed) < 0 && draft.reason.trim() === '') return { reason: 'exp.decide.err.reason' };
  return {};
}

/** Compares two non-negative decimal strings exactly. */
export function compareDecimal(a: string, b: string): number {
  const split = (v: string): [string, string] => { const [i = '0', f = ''] = v.trim().split('.'); return [i.replace(/^0+(?=\d)/, ''), f]; };
  const [ai, af] = split(a);
  const [bi, bf] = split(b);
  if (ai.length !== bi.length) return ai.length > bi.length ? 1 : -1;
  if (ai !== bi) return ai > bi ? 1 : -1;
  const width = Math.max(af.length, bf.length);
  const x = af.padEnd(width, '0');
  const y = bf.padEnd(width, '0');
  return x === y ? 0 : x > y ? 1 : -1;
}
