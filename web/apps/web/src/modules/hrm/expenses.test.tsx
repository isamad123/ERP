// The expense claim screens (slice S1.42; FR-HRM-EXP-01 to -03, -05, GHR-06, FR-XM-32): the form checks a line before the server is asked (a per-diem needs days), amounts go out as the exact
// digits typed, a saved line shows its converted amount, rate and policy flags as words, a draft is saved first and then submitted, a refusal is shown in the server's words and keeps the draft,
// a returned claim says so, a waiting claim is withdrawn through the approval endpoint, hidden amounts are shown as hidden (never zero), and every state is there: loading, empty, error, denied.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import ExpensesScreen from './expenses';
import { CATEGORIES, CONTEXT, claim, json, line, posts, problem, raw, sent, serve, shell, show, wire } from './expense-test-kit';
import type { ClaimView } from '../../api/expenses';

const MAKER = ['hrm.expense_claims.create', 'hrm.expense_claims.read'];

const screenAt = () => <Routes><Route path="/expenses/*" element={<ExpensesScreen />} /></Routes>;

interface Rig { view?: ClaimView; categories?: unknown[]; context?: unknown; extra?: (method: string, url: string, body: Record<string, unknown> | undefined) => Response | null | undefined }

function rig(options: Rig = {}) {
  let current = options.view;
  serve((method, url, body) => {
    const own = options.extra?.(method, url, body);
    if (own) return own;
    if (url.startsWith('/api/hrm/expense-claims/new-context')) return json(options.context ?? CONTEXT);
    if (url.startsWith('/api/hrm/expense-categories')) return json({ items: options.categories ?? CATEGORIES, next: null });
    if (url.startsWith('/api/doc/attachments')) return json([{ id: 'f1', fileName: 'taxi.pdf' }]);
    if (method === 'GET' && /\/api\/hrm\/expense-claims\/[^/]+\/detail$/.test(url)) return current ? raw(wire(current)) : problem('errors.hrm.expense.not_found', 404);
    if (method === 'POST' && url === '/api/hrm/expense-claims') { current = claim({ lines: [line('l1')] }); return raw(wire(current), 201); }
    if (method === 'PUT') return raw(wire({ ...(current ?? claim()), rowVersion: 4 }));
    if (method === 'POST' && url.endsWith('/submit')) return raw(JSON.stringify({ id: 'k1', claimNo: 'EXP-0001', status: 'submitted', instanceId: 'w1', needsFinance: false, flagged: 0, rowVersion: 5 }), 202);
    if (method === 'POST' && url.endsWith('/cancel')) return raw(wire({ ...(current ?? claim()), status: 'cancelled' }));
    if (method === 'POST' && url.includes('/withdraw')) return json({});
    return json({ items: [], next: null });
  });
}

async function choose(user: ReturnType<typeof userEvent.setup>, group: HTMLElement, label: RegExp, option: RegExp) {
  await user.click(within(group).getByRole('button', { name: label }));
  await user.click(await screen.findByRole('option', { name: option }));
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-HRM-EXP-02 the new claim form', () => {
  it('shows the employee, the claim currency, one empty line dated today, and every action by name', async () => {
    rig();
    show(screenAt(), shell(MAKER), '/expenses/new');
    expect(await screen.findByRole('heading', { name: 'New expense claim' })).toBeTruthy();
    expect(await screen.findByText('E-1')).toBeTruthy();
    expect(screen.getByText(/paid in PKR/)).toBeTruthy();
    const first = await screen.findByRole('group', { name: 'Line 1' });
    expect(within(first).getByRole('textbox', { name: /Amount/ })).toBeTruthy();
    expect(within(first).getByRole('textbox', { name: /Currency/ }).getAttribute('value')).toBe('PKR');
    expect(within(first).getAllByRole('spinbutton').length).toBeGreaterThan(0);
    for (const name of ['Save draft', 'Submit', 'Discard', 'Add a line', 'Remove line 1']) expect(screen.getByRole('button', { name })).toBeTruthy();
    expect(screen.getByText('Save the draft first, then add receipts to it.')).toBeTruthy();
  });

  it('adds and removes lines', async () => {
    rig();
    show(screenAt(), shell(MAKER), '/expenses/new');
    const user = userEvent.setup();
    await screen.findByRole('group', { name: 'Line 1' });
    await user.click(screen.getByRole('button', { name: 'Add a line' }));
    expect(screen.getByRole('group', { name: 'Line 2' })).toBeTruthy();
    await user.click(screen.getByRole('button', { name: 'Remove line 1' }));
    expect(screen.queryByRole('group', { name: 'Line 2' })).toBeNull();
    expect(screen.getByRole('group', { name: 'Line 1' })).toBeTruthy();
  });

  it('FR-HRM-EXP-01 asks for days on a per-diem category before it asks the server, and nothing is sent', async () => {
    rig();
    show(screenAt(), shell(MAKER), '/expenses/new');
    const user = userEvent.setup();
    const group = await screen.findByRole('group', { name: 'Line 1' });
    expect(within(group).queryByRole('textbox', { name: /Days/ })).toBeNull();
    await choose(user, group, /Category/, /Per diem/);
    expect(within(group).getByRole('textbox', { name: /Days/ })).toBeTruthy();
    await user.type(within(group).getByRole('textbox', { name: /Amount/ }), '3000');
    await user.click(screen.getByRole('button', { name: 'Submit' }));
    expect(await screen.findByText('Give the number of days or kilometres for this category.')).toBeTruthy();
    expect(screen.getByText('Some lines are not complete. Fix the marked fields.')).toBeTruthy();
    expect(posts()).toHaveLength(0);
    await user.type(within(group).getByRole('textbox', { name: /Days/ }), '2');
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]?.text).toContain('"quantity":2');
  });

  it('GHR-06 sends the amount as the exact digits typed and saves the draft, then shows it on the claim page', async () => {
    rig();
    show(screenAt(), shell(MAKER), '/expenses/new');
    const user = userEvent.setup();
    const group = await screen.findByRole('group', { name: 'Line 1' });
    await choose(user, group, /Category/, /Travel/);
    await user.type(within(group).getByRole('textbox', { name: /Amount/ }), '1250.50');
    await user.type(screen.getByRole('textbox', { name: /Title/ }), 'Lahore trip');
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(posts()).toHaveLength(1));
    const call = posts()[0] as { text?: string; body: Record<string, unknown> };
    expect(call.text).toContain('"amount":1250.50');
    expect(call.body).toMatchObject({ title: 'Lahore trip', reimburseVia: 'payroll' });
    expect(call.text).not.toContain('"submit"');
    expect(await screen.findByRole('heading', { name: 'Expense claim EXP-0001' })).toBeTruthy();
    expect(screen.getByTestId('where').textContent).toBe('/expenses/claims/k1');
  });

  it('FR-HRM-EXP-03 saves first and then submits with the saved row version; the claim page then shows it waiting', async () => {
    rig();
    show(screenAt(), shell(MAKER), '/expenses/new');
    const user = userEvent.setup();
    const group = await screen.findByRole('group', { name: 'Line 1' });
    await choose(user, group, /Category/, /Travel/);
    await user.type(within(group).getByRole('textbox', { name: /Amount/ }), '800');
    await user.click(screen.getByRole('button', { name: 'Submit' }));
    await waitFor(() => expect(posts()).toHaveLength(2));
    expect(posts()[0]?.url).toBe('/api/hrm/expense-claims');
    expect(posts()[1]?.url).toBe('/api/hrm/expense-claims/k1/submit');
    expect(posts()[1]?.body).toEqual({ rowVersion: 3 });
    expect(screen.getByTestId('where').textContent).toBe('/expenses/claims/k1');
  });

  it('keeps the saved draft and says why when the server refuses the submit (the category blocks flagged lines)', async () => {
    rig({ extra: (method, url) => (method === 'POST' && url.endsWith('/submit') ? problem('errors.hrm.expense.blocked_by_flags') : null) });
    show(screenAt(), shell(MAKER), '/expenses/new');
    const user = userEvent.setup();
    const group = await screen.findByRole('group', { name: 'Line 1' });
    await choose(user, group, /Category/, /Travel/);
    await user.type(within(group).getByRole('textbox', { name: /Amount/ }), '800');
    await user.click(screen.getByRole('button', { name: 'Submit' }));
    expect(await screen.findByText('The claim was saved but not submitted')).toBeTruthy();
    expect(await screen.findByText(/a line breaks a rule of its category/)).toBeTruthy();
    expect(screen.getByTestId('where').textContent).toBe('/expenses/claims/k1');
    expect(screen.queryByText(/errors\.hrm\.expense/)).toBeNull();
  });

  it('FR-HRM-EXP-02 shows the server\'s field problem beside the line it belongs to, never the raw key', async () => {
    rig({ extra: (method, url) => (method === 'POST' && url === '/api/hrm/expense-claims' ? problem('errors.validation', 400, { 'lines[0].amount': ['errors.hrm.expense.amount_positive'] }) : null) });
    show(screenAt(), shell(MAKER), '/expenses/new');
    const user = userEvent.setup();
    const group = await screen.findByRole('group', { name: 'Line 1' });
    await choose(user, group, /Category/, /Travel/);
    await user.type(within(group).getByRole('textbox', { name: /Amount/ }), '5');
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(await within(group).findByText('The amount must be more than zero.')).toBeTruthy();
    expect(screen.queryByText(/errors\.hrm/)).toBeNull();
  });

  it('says in the form that another currency is converted at the company rate', async () => {
    rig();
    show(screenAt(), shell(MAKER), '/expenses/new');
    const user = userEvent.setup();
    const group = await screen.findByRole('group', { name: 'Line 1' });
    const currency = within(group).getByRole('textbox', { name: /Currency/ });
    await user.clear(currency);
    await user.type(currency, 'usd');
    expect(currency.getAttribute('value')).toBe('USD');
    expect(within(group).getByText("Converted to PKR at the company's rate when you save.")).toBeTruthy();
  });
});

describe('FR-HRM-EXP-02 the states of the new claim form', () => {
  it('says so when the person has no employment', async () => {
    rig({ context: { ...CONTEXT, employmentId: null, employeeNo: null } });
    show(screenAt(), shell(MAKER), '/expenses/new');
    expect(await screen.findByText('You have no employment')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Submit' })).toBeNull();
  });
  it('says so when the company has no categories', async () => {
    rig({ categories: [] });
    show(screenAt(), shell(MAKER), '/expenses/new');
    expect(await screen.findByText('There are no expense categories')).toBeTruthy();
  });
  it('is denied without the permission to make claims and asks nothing', async () => {
    rig();
    show(screenAt(), shell(['hrm.expense_claims.read']), '/expenses/new');
    expect(await screen.findByText('You may not see this')).toBeTruthy();
    expect(sent.some((c) => c.url.includes('new-context'))).toBe(false);
  });
  it('shows the server\'s answer with a retry when the context cannot be loaded', async () => {
    rig({ extra: (_m, url) => (url.startsWith('/api/hrm/expense-claims/new-context') ? problem('errors.hrm.expense.no_currency', 409) : null) });
    show(screenAt(), shell(MAKER), '/expenses/new');
    expect(await screen.findByText('The expense claim could not be loaded.')).toBeTruthy();
    expect(await screen.findByText(/Your claim currency cannot be found/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });
  it('shows a loading state first', () => {
    rig();
    show(screenAt(), shell(MAKER), '/expenses/new');
    expect(screen.getByRole('heading', { name: 'New expense claim' })).toBeTruthy();
    expect(document.querySelector('[aria-busy="true"]')).toBeTruthy();
  });
});

describe('FR-HRM-EXP-03 a saved draft', () => {
  const flagged = claim({
    returned: true,
    lines: [line('l1', { flags: ['over_limit', 'missing_receipt'], blocksSubmit: true, originalCurrencyCode: 'USD', originalAmount: '10.0000', exchangeRate: '280.50000000', amount: '2805.0000', receiptFileId: 'f1' })],
  });

  it('says it was returned, and shows the converted amount, the rate and every flag as words', async () => {
    rig({ view: flagged });
    show(screenAt(), shell(MAKER), '/expenses/claims/k1');
    expect(await screen.findByText('This claim was returned to you')).toBeTruthy();
    const group = await screen.findByRole('group', { name: 'Line 1' });
    expect(within(group).getByText('Over the limit')).toBeTruthy();
    expect(within(group).getByText('Receipt missing')).toBeTruthy();
    expect(within(group).getByText('This category does not allow it: correct the line before you submit.')).toBeTruthy();
    expect(within(group).getByText(/Converted at the rate 280\.5/)).toBeTruthy();
    expect(within(group).getByText(/PKR 2,805\.00/)).toBeTruthy();
    expect(screen.getByText('1 line cannot be submitted as it is. Correct them or remove them, then submit again.')).toBeTruthy();
    expect(within(group).getByRole('textbox', { name: /Amount/ }).getAttribute('value')).toBe('10');
  });

  it('shows the uploaded receipts, lets the line choose one, and offers to add more', async () => {
    rig({ view: flagged });
    show(screenAt(), shell(MAKER), '/expenses/claims/k1');
    await screen.findByRole('group', { name: 'Line 1' });
    expect(await screen.findByRole('button', { name: 'Download taxi.pdf' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Add receipts' })).toBeTruthy();
    expect(screen.getByTestId('receipt-input')).toBeTruthy();
  });

  it('puts the change in a PUT with the row version and the saved line ids', async () => {
    rig({ view: claim() });
    show(screenAt(), shell(MAKER), '/expenses/claims/k1');
    const user = userEvent.setup();
    const group = await screen.findByRole('group', { name: 'Line 1' });
    const amount = within(group).getByRole('textbox', { name: /Amount/ });
    await user.clear(amount);
    await user.type(amount, '99.95');
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]?.method).toBe('PUT');
    expect(posts()[0]?.url).toBe('/api/hrm/expense-claims/k1');
    expect(posts()[0]?.text).toContain('"amount":99.95');
    expect(posts()[0]?.body).toMatchObject({ rowVersion: 3, lines: [{ id: 'l1', categoryId: 'c1' }] });
  });

  it('cancels a draft after a confirmation', async () => {
    rig({ view: claim() });
    show(screenAt(), shell(MAKER), '/expenses/claims/k1');
    const user = userEvent.setup();
    await screen.findByRole('group', { name: 'Line 1' });
    await user.click(screen.getByRole('button', { name: 'Cancel draft' }));
    expect(posts()).toHaveLength(0);
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Cancel the draft' }));
    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]?.url).toBe('/api/hrm/expense-claims/k1/cancel');
    expect(posts()[0]?.body).toEqual({ rowVersion: 3 });
  });
});

describe('FR-HRM-EXP-05 FR-XM-32 a claim that is not a draft', () => {
  it('shows a waiting claim read-only and withdraws it through the approval endpoint', async () => {
    rig({ view: claim({ status: 'submitted', canEdit: false, instanceId: 'w1', submittedAt: '2026-11-25T05:00:00Z' }) });
    show(screenAt(), shell(MAKER), '/expenses/claims/k1');
    const user = userEvent.setup();
    expect(await screen.findByText('Waiting for approval')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Submit' })).toBeNull();
    expect(screen.getByRole('table', { name: 'Lines' })).toBeTruthy();
    await user.click(screen.getByRole('button', { name: 'Withdraw' }));
    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]?.url).toBe('/api/wf/instances/w1/withdraw');
    expect(posts()[0]?.body).toEqual({ reason: 'Withdrawn by the claimant' });
  });

  it('shows the payment status and reference of a paid claim, the approved amounts and the approver\'s reason', async () => {
    rig({ view: claim({ status: 'reimbursed', canEdit: false, paymentStatus: 'paid', paymentReference: 'PAY-77', approvedTotal: '1000.0000', reimbursedAt: '2026-11-26T05:00:00Z',
      lines: [line('l1', { approvedAmount: '1000.0000', decisionReason: 'Over the taxi rate', decidedAt: '2026-11-25T05:00:00Z' })] }) });
    show(screenAt(), shell(MAKER), '/expenses/claims/k1');
    expect(await screen.findByText('PAY-77')).toBeTruthy();
    expect(screen.getAllByText('Paid').length).toBeGreaterThan(0);
    expect(screen.getByText('Over the taxi rate')).toBeTruthy();
    expect(screen.getAllByText(/PKR 1,000\.00/).length).toBeGreaterThanOrEqual(2);
    expect(screen.queryByRole('button', { name: 'Withdraw' })).toBeNull();
  });

  it('GHR-06 shows hidden amounts as hidden, never as zero', async () => {
    rig({ view: claim({ status: 'approved', canEdit: false, amountsHidden: true, totalAmount: null, approvedTotal: null, lines: [line('l1', { amount: null, originalAmount: null })] }) });
    show(screenAt(), shell(MAKER), '/expenses/claims/k1');
    expect(await screen.findByText('Amounts are hidden from you on this claim.')).toBeTruthy();
    expect(screen.getAllByText('Hidden').length).toBeGreaterThanOrEqual(3);
    expect(screen.queryByText(/PKR 0/)).toBeNull();
  });

  it('shows a failed payment in words', async () => {
    rig({ view: claim({ status: 'approved', canEdit: false, paymentStatus: 'failed', approvedTotal: '1250.5000' }) });
    show(screenAt(), shell(MAKER), '/expenses/claims/k1');
    expect(await screen.findByText('Payment failed')).toBeTruthy();
  });

  it('says a claim that is not there is not there, and a forbidden one is denied', async () => {
    rig();
    show(screenAt(), shell(MAKER), '/expenses/claims/zzz');
    expect(await screen.findByText('This claim was not found')).toBeTruthy();
    cleanup();
    rig({ extra: (_m, url) => (url.endsWith('/detail') ? problem('errors.data.forbidden', 403) : null) });
    show(screenAt(), shell(MAKER), '/expenses/claims/k1');
    expect(await screen.findByText('You may not see this')).toBeTruthy();
  });

  it('shows a server error with a retry', async () => {
    rig({ extra: (_m, url) => (url.endsWith('/detail') ? problem('errors.server', 500) : null) });
    show(screenAt(), shell(MAKER), '/expenses/claims/k1');
    expect(await screen.findByText('The expense claim could not be loaded.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });
});

describe('FR-HRM-EXP-02 in Urdu', () => {
  it('shows the form in Urdu with the category names of the language (the browser scenario checks the direction)', async () => {
    rig();
    show(screenAt(), shell(MAKER), '/expenses/new', 'ur');
    expect(await screen.findByRole('heading', { name: 'اخراجات کا نیا دعویٰ' })).toBeTruthy();
    expect(await screen.findByRole('button', { name: 'مسودہ محفوظ کریں' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'جمع کرائیں' })).toBeTruthy();
    const group = await screen.findByRole('group', { name: 'لائن 1' });
    const user = userEvent.setup();
    await user.click(within(group).getByRole('button', { name: /زمرہ/ }));
    expect(await screen.findByRole('option', { name: /Travel \(ur\)/ })).toBeTruthy();
  });

  it('shows the flags of a saved line in Urdu words', async () => {
    rig({ view: claim({ lines: [line('l1', { flags: ['duplicate', 'out_of_policy'] })] }) });
    show(screenAt(), shell(MAKER), '/expenses/claims/k1', 'ur');
    expect(await screen.findByText('رسید پہلے استعمال ہو چکی')).toBeTruthy();
    expect(screen.getByText('دعوے کی مدت سے باہر')).toBeTruthy();
  });
});
