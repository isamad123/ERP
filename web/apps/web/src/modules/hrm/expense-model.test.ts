// The pure rules of the expense claim form (slice S1.42; FR-HRM-EXP-02, EXP-03): what a line needs, the body that is sent, how the server's field problems find their line, and the exact decimal reading and writing of money.
import { describe, expect, it } from 'vitest';
import { decimalJson, exactJson } from '../../api/expenses';
import { checkLines, claimBody, compareDecimal, decisionErrors, draftOf, emptyLine, serverProblems, trimZeros } from './expense-model';
import { CATEGORIES, line } from './expense-test-kit';

const categories = new Map(CATEGORIES.map((c) => [c.id, c as never]));
const ok = { ...emptyLine('PKR', '2026-11-26'), categoryId: 'c1', amount: '1250.50' };

describe('FR-HRM-EXP-02 the checks of a line before the server is asked', () => {
  it('accepts a complete general line', () => {
    expect(checkLines([ok], categories).size).toBe(0);
  });
  it('asks for a category, a date, a positive amount with at most four decimals and a three-letter currency', () => {
    const bad = { ...ok, categoryId: '', expenseDate: '', amount: '0', currencyCode: 'pk' };
    const errors = checkLines([bad], categories).get(bad.key);
    expect(errors).toEqual({ category: 'expc.err.category', date: 'expc.err.date', amount: 'expc.err.amount.positive', currency: 'expc.err.currency' });
    expect(checkLines([{ ...ok, amount: '1.23456' }], categories).values().next().value?.amount).toBe('expc.err.amount');
    expect(checkLines([{ ...ok, amount: '1,200' }], categories).values().next().value?.amount).toBe('expc.err.amount');
    expect(checkLines([{ ...ok, amount: '-5' }], categories).values().next().value?.amount).toBe('expc.err.amount');
  });
  it('FR-HRM-EXP-01 requires days for a per-diem and kilometres for a mileage category, and none for a general one', () => {
    const perDiem = { ...ok, categoryId: 'c2' };
    expect(checkLines([perDiem], categories).get(perDiem.key)?.quantity).toBe('expc.err.quantity.required');
    expect(checkLines([{ ...perDiem, quantity: '0' }], categories).values().next().value?.quantity).toBe('expc.err.quantity');
    expect(checkLines([{ ...perDiem, quantity: '3' }], categories).size).toBe(0);
    expect(checkLines([{ ...ok, categoryId: 'c3', quantity: '' }], categories).size).toBe(1);
    expect(checkLines([{ ...ok, quantity: '' }], categories).size).toBe(0);
  });
});

describe('FR-HRM-EXP-02 the body of a claim', () => {
  it('keeps amounts as typed decimal strings and leaves out what is empty', () => {
    const body = claimBody({ title: ' Trip ', reimburseVia: 'payroll' }, [{ ...ok, id: 'l1', description: ' Taxi ', project: '', receiptFileId: 'f1' }], categories, { rowVersion: 3 });
    expect(body).toEqual({ title: 'Trip', reimburseVia: 'payroll', rowVersion: 3, lines: [{ id: 'l1', categoryId: 'c1', expenseDate: '2026-11-26', amount: '1250.50', currencyCode: 'PKR', description: 'Taxi', receiptFileId: 'f1' }] });
  });
  it('sends the quantity of a per-diem line and the employment of a claim made for someone else', () => {
    const body = claimBody({ title: '', reimburseVia: 'payment_batch', employmentId: 'e9' }, [{ ...ok, categoryId: 'c2', quantity: '2.5' }], categories, { submit: true });
    expect(body.employmentId).toBe('e9');
    expect(body.submit).toBe(true);
    expect(body.lines[0]?.quantity).toBe('2.5');
  });
  it('shows a saved line as the form row with the amount claimed and zeros trimmed', () => {
    const row = draftOf(line('l1', { originalCurrencyCode: 'USD', originalAmount: '12.5000', quantity: '2.0000' }));
    expect(row).toMatchObject({ id: 'l1', amount: '12.5', currencyCode: 'USD', quantity: '2' });
    expect(trimZeros('1200.0000')).toBe('1200');
    expect(trimZeros('12.5000')).toBe('12.5');
    expect(trimZeros('1200')).toBe('1200');
  });
});

describe('FR-HRM-EXP-02 the server\'s problems find their line', () => {
  it('maps lines[2].amount to line 2 and keeps the other problems as general', () => {
    const { byLine, general } = serverProblems({ 'lines[2].amount': ['errors.hrm.expense.amount_positive'], 'lines[0].categoryId': ['errors.hrm.expense.category_unknown'], reimburseVia: ['errors.hrm.expense.reimburse_via_invalid'] });
    expect(byLine.get(2)).toEqual({ amount: 'errors.hrm.expense.amount_positive' });
    expect(byLine.get(0)).toEqual({ categoryid: 'errors.hrm.expense.category_unknown' });
    expect(general).toEqual(['errors.hrm.expense.reimburse_via_invalid']);
  });
});

describe('GHR-06 money is never a floating-point number', () => {
  it('reads decimals of an answer as the digits the server wrote', () => {
    const view = exactJson<{ amount: string; totalAmount: string; lines: { quantity: string; exchangeRate: string; note: string }[] }>('{"amount":0.1000,"totalAmount":12345678901234.1234,"lines":[{"quantity":2.5000,"exchangeRate":280.12345678,"note":"\\"amount\\": 5"}]}');
    expect(view.amount).toBe('0.1000');
    expect(view.totalAmount).toBe('12345678901234.1234');
    expect(view.lines[0]).toEqual({ quantity: '2.5000', exchangeRate: '280.12345678', note: '"amount": 5' });
  });
  it('leaves a null amount null (hidden) and writes decimals into a request as numbers with the digits typed', () => {
    expect(exactJson<{ amount: null }>('{"amount":null}').amount).toBeNull();
    expect(decimalJson({ reimburseVia: 'payroll', lines: [{ amount: '1250.50', quantity: '2', description: '1.5', currencyCode: 'USD' }] })).toBe('{"reimburseVia":"payroll","lines":[{"amount":1250.50,"quantity":2,"description":"1.5","currencyCode":"USD"}]}');
    expect(decimalJson({ amount: 'abc' })).toBe('{"amount":"abc"}');
  });
});

describe('FR-HRM-EXP-03 the approver\'s line decision', () => {
  const l = { amount: '1000.0000' };
  it('accepts the full amount, a lower amount with a reason, and 0 with a reason', () => {
    expect(decisionErrors(l, { approved: '1000', reason: '' })).toEqual({});
    expect(decisionErrors(l, { approved: '800.50', reason: 'Over the rate' })).toEqual({});
    expect(decisionErrors(l, { approved: '0', reason: 'Not business' })).toEqual({});
  });
  it('asks for a reason below the claimed amount, refuses more than claimed and refuses text', () => {
    expect(decisionErrors(l, { approved: '999.9999', reason: ' ' })).toEqual({ reason: 'expc.decide.err.reason' });
    expect(decisionErrors(l, { approved: '1000.0001', reason: 'x' })).toEqual({ approved: 'expc.decide.err.above' });
    expect(decisionErrors(l, { approved: 'abc', reason: '' })).toEqual({ approved: 'expc.decide.err.amount' });
    expect(decisionErrors(l, { approved: '', reason: '' })).toEqual({ approved: 'expc.decide.err.amount' });
  });
  it('compares decimals exactly', () => {
    expect(compareDecimal('10', '9.9999')).toBe(1);
    expect(compareDecimal('1000.0000', '1000')).toBe(0);
    expect(compareDecimal('0.1', '0.10')).toBe(0);
    expect(compareDecimal('2', '10')).toBe(-1);
  });
});
