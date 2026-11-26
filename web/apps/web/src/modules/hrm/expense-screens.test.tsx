// The other expense screens (slice S1.42; FR-HRM-EXP-01, -03, -05, GHR-06, FR-XM-32): the card of the approvals inbox (lines, flags, receipts, the approver's line decisions with a reason below the
// claimed amount and 0 to reject), the payments list with its filters and hidden totals, my claims made by the engine, and the category and exchange rate lists and forms.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import ExpenseApprovalCard from './expense-approval-card';
import ExpenseFinanceScreen from './expense-finance';
import ExpensesScreen from './expenses';
import { CategoriesScreen, RatesScreen } from './expense-admin';
import { approvalCardFor } from '../kernel/approval-cards';
import { hrmScreens } from '../hrm';
import { CATEGORIES, claim, json, line, posts, problem, raw, sent, serve, shell, show, showRouted, wire } from './expense-test-kit';
import type { ClaimView } from '../../api/expenses';

const CARD = { documentType: 'hrm.expense_claim', documentId: 'k1', companyId: 'c', instanceId: 'w1', requesterUserId: 'u-x' };
const decider = (extra: Partial<ClaimView> = {}) => claim({
  status: 'submitted', canEdit: false, canDecide: true, instanceId: 'w1', totalAmount: '3105.0000',
  lines: [
    line('l1', { amount: '1000.0000', originalAmount: '1000.0000', flags: ['over_limit'], description: 'Taxi to airport', receiptFileId: 'f1' }),
    line('l2', { categoryCode: 'MEAL', categoryName: { en: 'Meals', ur: 'کھانا' }, amount: '2105.0000', originalAmount: '7.5000', originalCurrencyCode: 'USD', exchangeRate: '280.66666667', flags: ['duplicate', 'missing_receipt'] }),
  ],
  ...extra,
});

function card(view: ClaimView | null, extra?: (m: string, u: string) => Response | null | undefined, permissions = ['hrm.expense_claims.read', 'hrm.expense_claims.decide'], locale: 'en' | 'ur' = 'en') {
  serve((method, url) => {
    const own = extra?.(method, url);
    if (own) return own;
    if (url.startsWith('/api/doc/attachments')) return json([{ id: 'f1', fileName: 'taxi.pdf' }]);
    if (url.endsWith('/detail')) return view ? raw(wire(view)) : problem('errors.hrm.expense.not_found', 404);
    if (url.endsWith('/line-decisions')) return raw(wire(view as ClaimView));
    return json({});
  });
  show(<ExpenseApprovalCard {...CARD} />, shell(permissions), '/', locale);
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-HRM-EXP-03 the approvals card', () => {
  it('is registered for the expense claim document type', () => {
    expect(hrmScreens.length).toBeGreaterThan(0);
    expect(approvalCardFor('hrm.expense_claim')).toBeTruthy();
  });

  it('shows every line with its flags as words, the converted amount, the rate and the receipt, and a link to the claim', async () => {
    card(decider());
    expect(await screen.findByRole('heading', { name: 'Expense claim' })).toBeTruthy();
    const list = await screen.findByRole('list', { name: 'Lines' });
    const items = within(list).getAllByRole('listitem').filter((li) => li.classList.contains('erp-exp-card-line'));
    const first = items[0] as HTMLElement;
    expect(within(first).getByText('Over the limit')).toBeTruthy();
    expect(within(first).getByText('Taxi to airport')).toBeTruthy();
    expect(await within(first).findByRole('button', { name: 'taxi.pdf' })).toBeTruthy();
    const second = items[1] as HTMLElement;
    expect(within(second).getByText('Receipt used before')).toBeTruthy();
    expect(within(second).getByText('Receipt missing')).toBeTruthy();
    expect(within(second).getByText(/rate 280\.66666667/)).toBeTruthy();
    expect(within(second).getByText('No receipt')).toBeTruthy();
    expect(screen.getByText('Policy flags on this claim')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Open the claim' }).getAttribute('href')).toBe('/expenses/claims/k1');
  });

  it('asks for a reason below the claimed amount and sends nothing until there is one', async () => {
    card(decider());
    const user = userEvent.setup();
    const group = await screen.findByRole('group', { name: 'Decision for line 1' });
    const approved = within(group).getByRole('textbox', { name: /Approved amount/ });
    expect(approved.getAttribute('value')).toBe('1000');
    await user.clear(approved);
    await user.type(approved, '800.25');
    await user.click(screen.getByRole('button', { name: 'Save line decisions' }));
    expect(await within(group).findByText('Give a reason when you approve less than was claimed.')).toBeTruthy();
    expect(posts()).toHaveLength(0);
    await user.type(within(group).getByRole('textbox', { name: /Reason/ }), 'Over the taxi rate');
    await user.click(screen.getByRole('button', { name: 'Save line decisions' }));
    await waitFor(() => expect(posts()).toHaveLength(1));
    const call = posts()[0] as { url: string; text?: string; body: { decisions: Record<string, unknown>[] } };
    expect(call.url).toBe('/api/hrm/expense-claims/k1/line-decisions');
    expect(call.text).toContain('"approvedAmount":800.25');
    expect(call.body.decisions).toEqual([{ lineId: 'l1', approvedAmount: 800.25, reason: 'Over the taxi rate' }, { lineId: 'l2', approvedAmount: 2105 }]);
  });

  it('lets a line be rejected with 0 and a reason, and refuses more than was claimed', async () => {
    card(decider());
    const user = userEvent.setup();
    const group = await screen.findByRole('group', { name: 'Decision for line 2' });
    const approved = within(group).getByRole('textbox', { name: /Approved amount/ });
    await user.clear(approved);
    await user.type(approved, '2105.0001');
    await user.click(screen.getByRole('button', { name: 'Save line decisions' }));
    expect(await within(group).findByText('The approved amount cannot be more than was claimed.')).toBeTruthy();
    await user.clear(approved);
    await user.type(approved, '0');
    await user.type(within(group).getByRole('textbox', { name: /Reason/ }), 'Not a business expense');
    await user.click(screen.getByRole('button', { name: 'Save line decisions' }));
    await waitFor(() => expect(posts()).toHaveLength(1));
    expect((posts()[0]?.body as { decisions: Record<string, unknown>[] }).decisions[1]).toEqual({ lineId: 'l2', approvedAmount: 0, reason: 'Not a business expense' });
  });

  it('says that the Approve button of the task then approves the saved amounts', async () => {
    card(decider());
    expect(await screen.findByText(/The Approve button of this task then approves the saved amounts/)).toBeTruthy();
  });

  it('shows the server\'s refusal in words', async () => {
    card(decider(), (m, u) => (m === 'POST' && u.endsWith('/line-decisions') ? problem('errors.hrm.expense.not_approver') : null));
    const user = userEvent.setup();
    await screen.findByRole('group', { name: 'Decision for line 1' });
    await user.click(screen.getByRole('button', { name: 'Save line decisions' }));
    expect(await screen.findByText('You cannot decide a claim that is yours or that was made for you.')).toBeTruthy();
  });

  it('shows no decision form to a person who may not decide, and the decisions already made', async () => {
    card(decider({ canDecide: false, lines: [line('l1', { amount: '1000.0000', approvedAmount: '900.0000', decisionReason: 'Rate', decidedAt: '2026-11-25T05:00:00Z' })] }));
    await screen.findByRole('list', { name: 'Lines' });
    expect(screen.queryByRole('button', { name: 'Save line decisions' })).toBeNull();
    expect(screen.getByText('Rate')).toBeTruthy();
  });

  it('GHR-06 shows no decision form and says amounts are hidden when the approver may not see amounts', async () => {
    card(decider({ amountsHidden: true, totalAmount: null, lines: [line('l1', { amount: null, originalAmount: null })] }));
    expect(await screen.findByText('Amounts are hidden from you on this claim.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Save line decisions' })).toBeNull();
    expect(screen.getAllByText('Hidden').length).toBeGreaterThan(0);
  });

  it('shows a loading state and an unavailable state', async () => {
    card(null);
    expect(document.querySelector('[aria-busy="true"]')).toBeTruthy();
    expect(await screen.findByText('The expense claim was not found.')).toBeTruthy();
  });

  it('shows the card in Urdu', async () => {
    card(decider(), undefined, ['hrm.expense_claims.read', 'hrm.expense_claims.decide'], 'ur');
    expect(await screen.findByRole('heading', { name: 'اخراجات کا دعویٰ' })).toBeTruthy();
    expect(await screen.findByRole('button', { name: 'لائنوں کے فیصلے محفوظ کریں' })).toBeTruthy();
    expect(screen.getByText('حد سے زیادہ')).toBeTruthy();
  });
});

const ROW = (id: string, extra: Record<string, unknown> = {}) => ({
  id, companyId: 'c', claimNo: `EXP-${id}`, employmentId: 'e1', title: `Trip ${id}`, status: 'approved', currencyCode: 'PKR', approvedTotal: 1250.5, reimburseVia: 'payroll', paymentStatus: 'pending',
  paymentReference: null, submittedAt: '2026-11-24T05:00:00Z', reimbursedAt: null, ...extra,
});

describe('FR-HRM-EXP-05 the payments list', () => {
  function finance(rows: Record<string, unknown>[], extra?: (m: string, u: string) => Response | null | undefined, permissions = ['hrm.expense_claims.read']) {
    serve((method, url) => extra?.(method, url) ?? (url.startsWith('/api/hrm/expense-claims?') ? raw(JSON.stringify({ items: rows, next: null })) : json({})));
    show(<ExpenseFinanceScreen />, shell(permissions));
  }

  it('lists approved claims with payment status, approved total, route and reference, and asks for the approved status', async () => {
    finance([ROW('1'), ROW('2', { paymentStatus: 'failed', reimburseVia: 'payment_batch' })]);
    const table = await screen.findByRole('table', { name: 'Expense payments' });
    expect(within(table).getAllByRole('row')).toHaveLength(3);
    expect(within(table).getByText('Payment pending')).toBeTruthy();
    expect(within(table).getByText('Payment failed')).toBeTruthy();
    expect(within(table).getByText('Separate payment')).toBeTruthy();
    expect(within(table).getAllByText(/PKR 1,250\.50/)).toHaveLength(2);
    expect(within(table).getByRole('link', { name: 'EXP-1' }).getAttribute('href')).toBe('/expenses/claims/1');
    expect(sent[0]?.url).toContain('status=approved');
    expect(screen.getByRole('region', { name: 'Expense payments' }).getAttribute('tabindex')).toBe('0');
  });

  it('narrows by payment status on the screen and asks for the paid claims when the status changes', async () => {
    finance([ROW('1'), ROW('2', { paymentStatus: 'failed' })]);
    const user = userEvent.setup();
    await screen.findByRole('table', { name: 'Expense payments' });
    await user.click(screen.getByRole('button', { name: /Payment status/ }));
    await user.click(await screen.findByRole('option', { name: 'Payment failed' }));
    await waitFor(() => expect(within(screen.getByRole('table', { name: 'Expense payments' })).getAllByRole('row')).toHaveLength(2));
    await user.click(screen.getByRole('button', { name: /Claims/ }));
    await user.click(await screen.findByRole('option', { name: 'Paid' }));
    await waitFor(() => expect(sent.some((c) => c.url.includes('status=reimbursed'))).toBe(true));
  });

  it('GHR-06 shows an approved total the person may not see as hidden, never as zero', async () => {
    finance([ROW('1', { approvedTotal: undefined })]);
    expect(await screen.findByText('Hidden')).toBeTruthy();
    expect(screen.queryByText(/PKR 0/)).toBeNull();
  });

  it('says so when nothing matches', async () => {
    finance([]);
    expect(await screen.findByText('No claims here')).toBeTruthy();
  });

  it('shows the server\'s refusal with a retry', async () => {
    finance([], (_m, u) => (u.startsWith('/api/hrm/expense-claims?') ? problem('errors.server', 500) : null));
    expect(await screen.findByText('The expense claim could not be loaded.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });

  it('is denied without the permission to read claims and asks nothing', async () => {
    finance([], undefined, []);
    expect(await screen.findByText('You may not see this')).toBeTruthy();
    expect(sent).toHaveLength(0);
  });

  it('shows the list in Urdu', async () => {
    serve((_m, url) => (url.startsWith('/api/hrm/expense-claims?') ? raw(JSON.stringify({ items: [ROW('1')], next: null })) : json({})));
    show(<ExpenseFinanceScreen />, shell(['hrm.expense_claims.read']), '/', 'ur');
    expect(await screen.findByRole('table', { name: 'اخراجات کی ادائیگیاں' })).toBeTruthy();
    expect(screen.getAllByText('ادائیگی زیرِ التوا').length).toBeGreaterThan(0);
  });
});

const engineRow = (extra: Record<string, unknown>) => ({ id: 'r1', companyId: '00000000-0000-7000-8000-000000000001', rowVersion: 1, ...extra });

const views = (url: string) => (url.startsWith('/api/cfg/saved-views') ? json([]) : null);

describe('FR-HRM-EXP-02 my claims, made by the engine', () => {
  it('lists the claims with the status and a button to make a new claim, which opens the form', async () => {
    serve((_m, url) => views(url) ?? (url.startsWith('/api/hrm/expense-claims?') ? json({ items: [engineRow({ claimNo: 'EXP-0007', title: 'Karachi', status: 'draft', currencyCode: 'PKR', totalAmount: '500.0000', reimburseVia: 'payroll', paymentStatus: 'none', submittedAt: null })], next: null }) : json({ items: [] })));
    show(<Routes><Route path="/expenses/*" element={<ExpensesScreen />} /></Routes>, shell(['hrm.expense_claims.read', 'hrm.expense_claims.create']), '/expenses');
    expect(await screen.findByText('EXP-0007')).toBeTruthy();
    expect(screen.getByText('Karachi')).toBeTruthy();
    await userEvent.setup().click(screen.getByRole('button', { name: /New expense claim/i }));
    expect(screen.getByTestId('where').textContent).toBe('/expenses/new');
  });

  it('has no new-claim button without the permission to make claims', async () => {
    serve((_m, url) => views(url) ?? json({ items: [engineRow({ claimNo: 'EXP-0007', title: 'Karachi', status: 'draft', currencyCode: 'PKR', reimburseVia: 'payroll', paymentStatus: 'none' })], next: null }));
    show(<Routes><Route path="/expenses/*" element={<ExpensesScreen />} /></Routes>, shell(['hrm.expense_claims.read']), '/expenses');
    expect(await screen.findByText('EXP-0007')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /New expense claim/i })).toBeNull();
  });
});

describe('FR-HRM-EXP-01 FR-HRM-EXP-02 categories and exchange rates, made by the engine', () => {
  it('lists the categories with their kind and receipt rule and opens the form to make one', async () => {
    serve((_m, url) => views(url) ?? (url.startsWith('/api/hrm/expense-categories') ? json({ items: CATEGORIES.map((c) => engineRow({ ...c, id: c.id, companyId: '00000000-0000-7000-8000-000000000001', taxTreatment: null, reimbursementComponentId: null })), next: null }) : json({ items: [] })));
    const router = showRouted('/payroll/expense-categories/*', <CategoriesScreen />, shell(['hrm.expense_categories.read', 'hrm.expense_categories.create', 'hrm.expense_categories.update']), '/payroll/expense-categories');
    expect(await screen.findByText('TRAVEL')).toBeTruthy();
    expect(screen.getAllByText('Per diem').length).toBeGreaterThan(0);
    await userEvent.setup().click(screen.getByRole('button', { name: /New expense category/i }));
    expect(router.state.location.pathname).toBe('/payroll/expense-categories/new');
    expect(await screen.findByRole('textbox', { name: /Code/ })).toBeTruthy();
  });

  it('lists the exchange rates and opens the form to add one', async () => {
    serve((_m, url) => views(url) ?? (url.startsWith('/api/hrm/exchange-rates') ? json({ items: [engineRow({ fromCurrency: 'USD', toCurrency: 'PKR', rateDate: '2026-11-20', rate: '280.500000' })], next: null }) : json({ items: [] })));
    const router = showRouted('/payroll/exchange-rates/*', <RatesScreen />, shell(['hrm.exchange_rates.read', 'hrm.exchange_rates.create']), '/payroll/exchange-rates');
    expect(await screen.findByText('USD')).toBeTruthy();
    expect(screen.getByText('PKR')).toBeTruthy();
    await userEvent.setup().click(screen.getByRole('button', { name: /New exchange rate/i }));
    expect(router.state.location.pathname).toBe('/payroll/exchange-rates/new');
    expect(await screen.findByRole('textbox', { name: /Rate/ })).toBeTruthy();
  });
});
