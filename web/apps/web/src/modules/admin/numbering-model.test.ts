// The pattern rules of the numbering screen (slice S1.15c; FR-KRN-NUM-01, -02): the same reading as the server, examples and the next number.
import { describe, expect, it } from 'vitest';
import { checkPattern, nextNumber, readPattern, renderExample, runningValue, scopeKey } from './numbering-model';

const day = (iso: string) => new Date(`${iso}T00:00:00Z`);
const ctx = (iso: string, fiscalStartMonth = 7, value = 7) => ({ date: day(iso), fiscalStartMonth, company: 'ACME', branch: 'KHI', value });
const company = { hasCompany: true, hasBranch: true, branchScheme: false };

describe('FR-KRN-NUM-01 reading a pattern', () => {
  it('accepts the tokens of the server and exactly one running number', () => {
    expect(readPattern('EMP-{seq:5}').error).toBeNull();
    expect(readPattern('{company}-{yyyy}/{mm}-{seq:4}').error).toBeNull();
  });

  it('gives the server text key of the first problem', () => {
    expect(readPattern('').error).toBe('errors.numbering.pattern_empty');
    expect(readPattern('EMP-{seq:5').error).toBe('errors.numbering.pattern_brace');
    expect(readPattern('EMP-}').error).toBe('errors.numbering.pattern_brace');
    expect(readPattern('EMP-{foo}').error).toBe('errors.numbering.pattern_token_unknown');
    expect(readPattern('EMP-{seq}').error).toBe('errors.numbering.pattern_seq_digits');
    expect(readPattern('EMP-{seq:13}').error).toBe('errors.numbering.pattern_seq_digits');
    expect(readPattern('EMP').error).toBe('errors.numbering.pattern_seq_count');
    expect(readPattern('{seq:3}{seq:3}').error).toBe('errors.numbering.pattern_seq_count');
  });

  it('checks the reset rule, the codes and their separators like the server', () => {
    expect(checkPattern('EMP-{seq:5}', 'yearly', company)).toBe('errors.numbering.pattern_year_needed');
    expect(checkPattern('EMP-{yyyy}-{seq:5}', 'monthly', company)).toBe('errors.numbering.pattern_month_needed');
    expect(checkPattern('EMP-{seq:5}', 'fiscal_yearly', company)).toBe('errors.numbering.pattern_fiscal_year_needed');
    expect(checkPattern('{company}-{seq:5}', 'never', { ...company, hasCompany: false })).toBe('errors.numbering.pattern_company_not_allowed');
    expect(checkPattern('{branch}-{seq:5}', 'never', company)).toBe('errors.numbering.pattern_branch_company_scheme');
    expect(checkPattern('EMP-{seq:5}', 'never', { ...company, branchScheme: true })).toBe('errors.numbering.branch_token_needed');
    expect(checkPattern('{company}{seq:5}', 'never', company)).toBe('errors.numbering.pattern_code_separator');
    expect(checkPattern('{company}-{seq:5}', 'never', company)).toBeNull();
  });
});

describe('FR-KRN-NUM-02 examples and the next number', () => {
  it('prints a number like the server (Latin digits, padded, fiscal year label)', () => {
    expect(renderExample('EMP-{seq:5}', ctx('2026-11-23'))).toBe('EMP-00007');
    expect(renderExample('{company}/{yy}{mm}/{seq:3}', ctx('2026-03-02'))).toBe('ACME/2603/007');
    expect(renderExample('INV-{fy}-{seq:4}', ctx('2026-11-23'))).toBe('INV-2026-27-0007');
    expect(renderExample('INV-{fy}-{seq:4}', ctx('2026-03-02'))).toBe('INV-2025-26-0007');
    expect(renderExample('INV-{fy}-{seq:4}', ctx('2026-03-02', 1))).toBe('INV-2026-0007');
    expect(renderExample('N{seq:2}', { ...ctx('2026-03-02'), value: 12345 })).toBe('N12345');
    expect(renderExample('bad', ctx('2026-03-02'))).toBeNull();
  });

  it('names the period of a date for each reset rule', () => {
    expect(scopeKey('never', day('2026-11-23'), 7)).toBe('all');
    expect(scopeKey('yearly', day('2026-11-23'), 7)).toBe('2026');
    expect(scopeKey('monthly', day('2026-11-23'), 7)).toBe('2026-11');
    expect(scopeKey('fiscal_yearly', day('2026-03-02'), 7)).toBe('fy2025');
    expect(scopeKey('fiscal_yearly', day('2026-07-01'), 7)).toBe('fy2026');
  });

  it('finds the running value inside a printed number', () => {
    expect(runningValue('EMP-{seq:5}', 'EMP-00042')).toBe(42);
    expect(runningValue('{company}-{yyyy}-{seq:4}', 'ACME-2026-0042')).toBe(42);
    expect(runningValue('INV-{fy}-{seq:4}', 'INV-2026-27-0042')).toBe(42);
    expect(runningValue('EMP-{seq:5}', 'XYZ-00042')).toBeNull();
  });

  it('shows the start value for a new period and one after the last number in a used one', () => {
    const base = { pattern: 'EMP-{seq:5}', reset: 'never' as const, start: 100, today: day('2026-11-23'), fiscalStartMonth: 7, company: 'ACME', branch: '' };
    expect(nextNumber({ ...base, ranges: [] })).toBe('EMP-00100');
    expect(nextNumber({ ...base, ranges: [{ period: 'all', lastNumber: 'EMP-00123' }] })).toBe('EMP-00124');
    const yearly = { ...base, pattern: 'EMP-{yyyy}-{seq:3}', reset: 'yearly' as const, start: 1 };
    expect(nextNumber({ ...yearly, ranges: [{ period: '2025', lastNumber: 'EMP-2025-310' }] })).toBe('EMP-2026-001');
    expect(nextNumber({ ...yearly, ranges: [{ period: '2026', lastNumber: 'EMP-2026-009' }] })).toBe('EMP-2026-010');
  });
});
