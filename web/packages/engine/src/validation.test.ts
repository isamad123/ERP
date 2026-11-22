// Form checks from the generated schema and exact decimals (slice S1.12c Build items 14 and 18; FR-ADM-UXA-02,
// FR-ADM-UXA-04): the same rules as the server and database, and money never through a floating-point number.
import { describe, expect, it } from 'vitest';
import schemaJson from '@erp/generated/validation/org.branches.json';
import type { ValidationSchema } from './metadata';
import { checkValue, cleanValues, fromJson, toJson, validate } from './validation';
import { labelText } from './labels';

const branches = schemaJson as ValidationSchema;

describe('FR-ADM-UXA-02 checks from the generated validation schema of org.branches', () => {
  it('needs the company, the code and a name in at least one language', () => {
    const problems = validate({ companyId: null, code: '', nameI18n: { en: ' ', ur: '' } }, branches, ['companyId', 'code', 'nameI18n']);
    expect(Object.keys(problems).sort()).toEqual(['code', 'companyId', 'nameI18n']);
    expect(problems.code?.key).toBe('engine.error.required');
    expect(validate({ companyId: '0190a1b2-0000-7000-8000-000000000001', code: 'KHI', nameI18n: { ur: 'کراچی' } }, branches, ['companyId', 'code', 'nameI18n'])).toEqual({});
  });

  it('applies the database check on codes: at most 40 characters, spaces at the ends trimmed as the form sends it', () => {
    expect(checkValue(' KHI ', branches.properties.code, true)).toBeNull();
    expect(checkValue('   ', branches.properties.code, true)?.key).toBe('engine.error.required');
    expect(checkValue('K H I', branches.properties.code, true)).toBeNull();
    expect(checkValue('KHI', { type: 'string', pattern: '^[A-Z]+$' }, true)).toBeNull();
    expect(checkValue('khi', { type: 'string', pattern: '^[A-Z]+$' }, true)?.key).toBe('engine.error.invalid');
    expect(checkValue('X'.repeat(41), branches.properties.code, true)).toEqual({ key: 'engine.error.tooLong', values: { max: 40 } });
    expect(checkValue('X'.repeat(40), branches.properties.code, true)).toBeNull();
  });

  it('sends trimmed text and leaves out empty languages of a name', () => {
    expect(cleanValues({ code: ' KHI ', nameI18n: { en: ' Karachi ', ur: '' }, isActive: false }, branches, ['code', 'nameI18n', 'isActive']))
      .toEqual({ code: 'KHI', nameI18n: { en: 'Karachi' }, isActive: false });
  });
});

describe('FR-UX-CMP-04 decimals stay exact between the server and the screen', () => {
  const decimals = new Set(['amount']);
  it('reads a decimal field as its digits and writes it back as a JSON number', () => {
    const text = '{"id":"x","amount":1234567890123456.7891,"rowVersion":3,"note":"\\"amount\\":1"}';
    const row = fromJson<{ amount: string; rowVersion: number; note: string }>(text, decimals);
    expect(row.amount).toBe('1234567890123456.7891'); // a JavaScript number would read 1234567890123456.8
    expect(row.rowVersion).toBe(3);
    expect(row.note).toBe('"amount":1');
    expect(toJson({ amount: '1234567890123456.7891', code: '12' }, decimals)).toBe('{"amount":1234567890123456.7891,"code":"12"}');
    expect(checkValue('12.34567', { type: 'string', format: 'decimal', pattern: '^-?\\d{1,15}(\\.\\d{1,4})?$' }, true)?.key).toBe('engine.error.decimal');
  });
});

describe('FR-KRN-ORG-08 labels: override, then Urdu, then English', () => {
  const labels = { en: { 'fields.org.branches.code': 'Code' }, ur: { 'fields.org.branches.code': 'کوڈ' } };
  it('uses the customer override first and falls back to English when Urdu is missing', () => {
    expect(labelText('fields.org.branches.code', 'en', {}, labels)).toBe('Code');
    expect(labelText('fields.org.branches.code', 'ur', {}, labels)).toBe('کوڈ');
    expect(labelText('fields.org.branches.code', 'ur', { 'fields.org.branches.code': 'دفتر کوڈ' }, labels)).toBe('دفتر کوڈ');
    expect(labelText('fields.org.branches.code', 'en', { 'fields.org.branches.code': 'Office code' }, labels)).toBe('Office code');
    expect(labelText('fields.org.branches.missing', 'ur', {}, { en: { 'fields.org.branches.missing': 'Missing' }, ur: {} })).toBe('Missing');
    expect(labelText('fields.org.branches.code', 'pseudo', { 'fields.org.branches.code': 'Office code' }, labels)).toMatch(/^⟦Çóďé~+⟧$/);
  });
});
