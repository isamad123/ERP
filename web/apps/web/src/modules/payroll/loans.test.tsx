// The loan screens (slice S1.41; FR-HRM-LON-02, LON-03, LON-05, LON-07, GHR-06, FR-XM-32): the application with its check (reasons and warnings before anything is sent, the schedule in
// exact money), the loan page with schedule and ledger and the actions the right people have, amounts left out without the salary field class, the reports, and the texts in both languages.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import en from '@erp/i18n/messages/loans.en.json';
import ur from '@erp/i18n/messages/loans.ur.json';
import { json, problem, sent, serve, shell, show } from '../hrm/leave-test-kit';
import LoansScreen, { addDecimal } from './loans';
import LoanReportsScreen from './loan-reports';

const TYPE = { id: 't1', companyId: 'c', code: 'ADV', nameI18n: { en: 'Salary advance', ur: 'تنخواہ کی پیشگی' }, kind: 'advance', isActive: true };
const PREVIEW_OK = {
  employmentId: 'e1', employeeNo: 'E-1', loanType: { id: 't1', code: 'ADV', kind: 'advance' }, currencyCode: 'PKR', interestMethod: 'reducing', annualInterestPercent: 12, installments: 3, maxInstallments: 12,
  requiresGuarantor: true, eligible: true, problems: [], warnings: [{ key: 'errors.hrm.loan.recovery_above_share', value: '41.5' }], startPeriodId: 'p1', principal: 30000, maxAmount: 180000, totalInterest: 600, totalRepayable: 30600,
  schedule: [
    { seq: 1, payPeriodId: 'p1', periodStart: '2026-12-01', periodEnd: '2026-12-31', principalAmount: 9900.5, interestAmount: 300, total: 10200.5 },
    { seq: 2, payPeriodId: 'p2', periodStart: '2027-01-01', periodEnd: '2027-01-31', principalAmount: 10000, interestAmount: 200, total: 10200 },
    { seq: 3, payPeriodId: 'p3', periodStart: '2027-02-01', periodEnd: '2027-02-28', principalAmount: 10099.5, interestAmount: 100, total: 10199.5 },
  ],
};
const PREVIEW_NO = { ...PREVIEW_OK, eligible: false, problems: [{ key: 'errors.hrm.loan.not_eligible.service', value: '6' }, { key: 'errors.hrm.loan.amount_above_max', value: '25000' }], warnings: [], schedule: undefined, principal: undefined };
const DETAIL = (o: Record<string, unknown> = {}) => ({
  id: 'l1', loanNo: 'LN-2026-00001', status: 'active', employmentId: 'e1', employeeNo: 'E-1', loanType: { id: 't1', code: 'ADV', kind: 'advance', nameI18n: { en: 'Salary advance', ur: 'تنخواہ کی پیشگی' } },
  currencyCode: 'PKR', interestMethod: 'none', interestRate: 0, installments: 3, startPeriodId: 'p1', guarantorEmploymentId: null, disbursedOn: '2026-11-20', workflowInstanceId: 'w1', reason: 'Family need', rowVersion: 4,
  principal: 12000, balance: 8000, pendingReceipts: 0,
  schedule: [
    { id: 'i1', seq: 1, payPeriodId: 'p1', periodStart: '2026-11-01', periodEnd: '2026-11-30', status: 'deducted', recovered: true, principalAmount: 4000, interestAmount: 0 },
    { id: 'i2', seq: 2, payPeriodId: 'p2', periodStart: '2026-12-01', periodEnd: '2026-12-31', status: 'scheduled', recovered: false, principalAmount: 4000, interestAmount: 0 },
    { id: 'i3', seq: 3, payPeriodId: 'p3', periodStart: '2027-01-01', periodEnd: '2027-01-31', status: 'skipped', recovered: false, principalAmount: 4000, interestAmount: 0 },
  ],
  ledger: [
    { id: 'x1', kind: 'disbursement', txnDate: '2026-11-20', reference: 'TT-77', sourceType: 'hrm.loans', amount: 12000 },
    { id: 'x2', kind: 'recovery', txnDate: '2026-11-30', reference: null, sourceType: 'hrm.loan_installments', amount: -4000 },
  ],
  ...o,
});
const without = (row: Record<string, unknown>, ...keys: string[]) => Object.fromEntries(Object.entries(row).filter(([k]) => !keys.includes(k)));
const HIDDEN = () => {
  const d = DETAIL() as Record<string, unknown>;
  return {
    ...without(d, 'principal', 'balance'),
    schedule: (d.schedule as Record<string, unknown>[]).map((r) => without(r, 'principalAmount', 'interestAmount')),
    ledger: (d.ledger as Record<string, unknown>[]).map((r) => without(r, 'amount')),
  };
};

function apply(preview: object = PREVIEW_OK) {
  serve((method, url) => {
    if (url.includes('/api/hrm/loan-types')) return json({ items: [TYPE], total: 1 });
    if (url.endsWith('/api/hrm/loans/preview')) return json(preview);
    if (url.endsWith('/api/hrm/loans') && method === 'POST') return json({ id: 'l9', loanNo: 'LN-2026-00009', status: 'applied', instanceId: 'w9', employmentId: 'e1', onBehalf: false }, 202);
    return json({});
  });
}
const fill = async (amount = '30000') => {
  await userEvent.click(screen.getByRole('button', { name: /Loan type/ }));
  await userEvent.click(await screen.findByRole('option', { name: 'Salary advance' }));
  await userEvent.type(screen.getByRole('textbox', { name: /Amount/ }), amount);
};

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-HRM-LON-02 the application', () => {
  it('checks first: an eligible application shows the schedule in exact money, the warning and the guarantor note, and only then sends', async () => {
    apply();
    show(<LoansScreen />, shell(['hrm.loans.create']), '/apply');
    await fill();
    const send = screen.getByRole('button', { name: 'Send application' });
    expect((send as HTMLButtonElement).disabled).toBe(true); // nothing is sent before the check
    await userEvent.click(screen.getByRole('button', { name: 'Check and show installments' }));
    expect(await screen.findByText('This loan can be applied for.')).toBeTruthy();
    expect(screen.getByText('A guarantor must agree.')).toBeTruthy();
    expect(screen.getByText(/installments take more of pay than the company's limit allows: 41.5 percent/)).toBeTruthy();
    const table = screen.getByRole('region', { name: 'Installments' });
    expect(within(table).getByText(/9,900\.50/)).toBeTruthy();
    expect(within(table).getAllByRole('row')).toHaveLength(4);
    const preview = sent.find((c) => c.url.endsWith('/api/hrm/loans/preview'))!;
    expect(preview.body).toMatchObject({ loanTypeId: 't1', principal: 30000, installments: 12 });
    expect((send as HTMLButtonElement).disabled).toBe(false);
    await userEvent.click(send);
    await vi.waitFor(() => expect(sent.some((c) => c.method === 'POST' && c.url.endsWith('/api/hrm/loans'))).toBe(true));
  });

  it('shows why an ineligible applicant cannot go ahead and keeps Send disabled', async () => {
    apply(PREVIEW_NO);
    show(<LoansScreen />, shell(['hrm.loans.create']), '/apply');
    await fill('90000');
    await userEvent.click(screen.getByRole('button', { name: 'Check and show installments' }));
    expect(await screen.findByText('This loan cannot be applied for yet.')).toBeTruthy();
    expect(screen.getByText('The employee needs at least this many months of service: 6.')).toBeTruthy();
    expect(screen.getByText('The amount is above the most this employee may borrow: 25000.')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Send application' }) as HTMLButtonElement).disabled).toBe(true);
    expect(sent.filter((c) => c.method === 'POST' && c.url.endsWith('/api/hrm/loans'))).toHaveLength(0);
  });

  it('refuses a malformed amount before the server is asked, and a person without the permission sees the denied state', async () => {
    apply();
    show(<LoansScreen />, shell(['hrm.loans.create']), '/apply');
    await fill('12,5');
    expect(screen.getByText('Give a number, with at most four decimals.')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Check and show installments' }) as HTMLButtonElement).disabled).toBe(true);
    cleanup();
    show(<LoansScreen />, shell([]), '/apply');
    expect(await screen.findByText('You may not see loans.')).toBeTruthy();
  });

  it('hides the amounts of the preview from a person without the salary field class and says so', async () => {
    apply({ ...PREVIEW_OK, principal: undefined, maxAmount: undefined, totalInterest: undefined, totalRepayable: undefined, schedule: undefined });
    show(<LoansScreen />, shell(['hrm.loans.create']), '/apply');
    await fill();
    await userEvent.click(screen.getByRole('button', { name: 'Check and show installments' }));
    expect(await screen.findByText('Amounts are hidden: you need access to salary data to see them.', { selector: 'p' })).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'Installments' })).toBeNull();
  });
});

describe('FR-HRM-LON-03 LON-07 the loan page', () => {
  const view = (detail: object, permissions: string[]) => {
    serve((_m, url) => (url.endsWith('/api/hrm/loans/l1/detail') ? json(detail) : json({})));
    show(<LoansScreen />, shell(['hrm.loans.read', ...permissions]), '/l1');
  };

  it('shows the facts, the installments with their state and the ledger with its reference', async () => {
    view(DETAIL(), []);
    expect(await screen.findByRole('heading', { name: 'Loans and advances LN-2026-00001' })).toBeTruthy();
    expect(screen.getAllByText(/8,000\.00/).length).toBeGreaterThan(0); // the balance
    const schedule = screen.getByRole('region', { name: 'Installments' });
    expect(within(schedule).getByText('Deducted')).toBeTruthy();
    expect(within(schedule).getByText('Skipped, carried forward')).toBeTruthy();
    const ledger = screen.getByRole('region', { name: 'Ledger' });
    expect(within(ledger).getByText('TT-77')).toBeTruthy();
    expect(within(ledger).getByText('Recovered through payroll')).toBeTruthy();
    // Without the permissions there is no action.
    expect(screen.queryByRole('button', { name: 'Record payout' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Record repayment' })).toBeNull();
  });

  it('leaves every amount out without the salary field class and says so, never showing zeros', async () => {
    view(HIDDEN(), []);
    expect(await screen.findByText('LN-2026-00001', { selector: 'bdi' })).toBeTruthy();
    expect(screen.queryByText(/PKR/)).toBeNull();
    expect(screen.getAllByText('Amounts are hidden: you need access to salary data to see them.').length).toBeGreaterThan(0);
  });

  it('records a payout with its date and payment reference, for a person who may', async () => {
    serve((method, url) => (url.endsWith('/api/hrm/loans/l1/detail') ? json(DETAIL({ status: 'approved', balance: 0, ledger: [], disbursedOn: null })) : method === 'POST' ? json({ id: 'l1', status: 'active' }) : json({})));
    show(<LoansScreen />, shell(['hrm.loans.read', 'hrm.loans.disburse', 'hrm.loans.cancel']), '/l1');
    await userEvent.click(await screen.findByRole('button', { name: 'Record payout' }));
    const dialog = await screen.findByRole('dialog');
    const confirm = within(dialog).getByRole('button', { name: 'Record payout' });
    expect((confirm as HTMLButtonElement).disabled).toBe(true); // a reference is needed
    await userEvent.type(within(dialog).getByRole('textbox', { name: /Payment reference/ }), 'TT-2026-9');
    await userEvent.click(confirm);
    await vi.waitFor(() => expect(sent.some((c) => c.method === 'POST' && c.url.endsWith('/api/hrm/loans/l1/disburse'))).toBe(true));
    expect(sent.find((c) => c.url.endsWith('/disburse'))!.body).toMatchObject({ reference: 'TT-2026-9' });
  });

  it('holds a repayment for a second person: the full settlement sends no amount, and a waiting one blocks another', async () => {
    serve((method, url) => (url.endsWith('/api/hrm/loans/l1/detail') ? json(DETAIL()) : method === 'POST' ? json({ status: 'pending', requestId: 'r1' }, 202) : json({})));
    show(<LoansScreen />, shell(['hrm.loans.read', 'hrm.loans.receipt']), '/l1');
    await userEvent.click(await screen.findByRole('button', { name: 'Record repayment' }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Settles the whole loan' }));
    expect(within(dialog).queryByRole('textbox', { name: /Amount received/ })).toBeNull();
    await userEvent.type(within(dialog).getByRole('textbox', { name: /Receipt reference/ }), 'RCPT-1');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Send for approval' }));
    await vi.waitFor(() => expect(sent.some((c) => c.url.endsWith('/receipts'))).toBe(true));
    const body = sent.find((c) => c.url.endsWith('/receipts'))!.body!;
    expect(body).toMatchObject({ fullSettlement: true, reference: 'RCPT-1' });
    expect(body).not.toHaveProperty('amount');
    cleanup();
    view(DETAIL({ pendingReceipts: 1 }), ['hrm.loans.receipt']);
    expect(await screen.findByText('A repayment is waiting for approval.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Record repayment' })).toBeNull();
  });

  it('answers a loan that is not in the scope in words', async () => {
    serve(() => problem('errors.data.not_found', 404));
    show(<LoansScreen />, shell(['hrm.loans.read']), '/l1');
    expect(await screen.findByText('This loan does not exist or is not in your scope.')).toBeTruthy();
  });
});

describe('FR-HRM-LON-05 LON-07 the reports', () => {
  const OUT = { total: 1, items: [{ loanId: 'l1', loanNo: 'LN-2026-00001', status: 'active', employmentId: 'e1', employeeNo: 'E-1', companyId: 'c', typeCode: 'ADV', kind: 'advance', currencyCode: 'PKR', installmentsLeft: 2, nextDue: '2026-12-01', disbursedOn: '2026-11-20', principal: 12000, balance: 8000, repayable: 8000 }] };
  const REC = { companies: [{ companyId: 'c', currencyCode: 'PKR', loans: 1, differenceCount: 0, reconciles: true, disbursed: 12000, recovered: 4000, outstanding: 8000, expectedOutstanding: 8000, scheduledPrincipalOutstanding: 8000, differences: [] }] };

  it('lists the outstanding loans and shows the reconciliation, and a person without the permission is told so', async () => {
    serve((_m, url) => (url.includes('/outstanding') ? json(OUT) : url.includes('/reconciliation') ? json(REC) : json({})));
    show(<LoanReportsScreen />, shell(['hrm.loan_reports.read']));
    const table = await screen.findByRole('region', { name: 'Outstanding loans' });
    expect(within(table).getByRole('link', { name: 'LN-2026-00001' }).getAttribute('href')).toBe('/payroll/loans/l1');
    expect(within(table).getAllByText(/8,000\.00/).length).toBe(2);
    await userEvent.click(screen.getByRole('tab', { name: 'Reconciliation' }));
    expect(await screen.findByText('The ledger agrees with the schedule.')).toBeTruthy();
    expect(screen.getByText(/12,000\.00/)).toBeTruthy();
    cleanup();
    show(<LoanReportsScreen />, shell([]));
    expect(await screen.findByText('You may not see the loan reports.')).toBeTruthy();
  });

  it('lists a difference by loan when the ledger and the schedule disagree', async () => {
    serve((_m, url) => (url.includes('/reconciliation') ? json({ companies: [{ ...REC.companies[0], reconciles: false, differenceCount: 1, differences: [{ loanId: 'l1', loanNo: 'LN-2026-00001', ledgerBalance: 8000, scheduledPrincipal: 7000, difference: 1000 }] }] }) : json(OUT)));
    show(<LoanReportsScreen />, shell(['hrm.loan_reports.read']));
    await userEvent.click(await screen.findByRole('tab', { name: 'Reconciliation' }));
    expect(await screen.findByText('The ledger and the schedule differ for 1 loans:')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'LN-2026-00001' })).toBeTruthy();
  });

  it('shows no amount column to a person without the salary field class', async () => {
    serve((_m, url) => (url.includes('/outstanding') ? json({ total: 1, items: [{ ...OUT.items[0], principal: undefined, balance: undefined, repayable: undefined }] }) : json({})));
    show(<LoanReportsScreen />, shell(['hrm.loan_reports.read']));
    expect(await screen.findByText('Amounts are hidden: you need access to salary data to see them.')).toBeTruthy();
    expect(screen.queryByText(/PKR/)).toBeNull();
  });
});

describe('exact money and both languages', () => {
  it('adds installment parts in ten-thousandths, never through a float', () => {
    expect(addDecimal(7884.8789, 1000)).toBe('8884.8789');
    expect(addDecimal('0.1', '0.2')).toBe('0.3000');
    expect(addDecimal(0, 0)).toBe('0.0000');
  });

  it('has an Urdu text for every English text, in Urdu script', () => {
    expect(Object.keys(ur).sort()).toEqual(Object.keys(en).sort());
    for (const [key, text] of Object.entries(ur)) expect(text, key).toMatch(/\p{Script=Arabic}/u);
    // The placeholders match, so no value is lost in translation.
    for (const [key, text] of Object.entries(en)) {
      const names = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();
      expect(names((ur as Record<string, string>)[key] ?? ''), key).toEqual(names(text));
    }
  });
});
