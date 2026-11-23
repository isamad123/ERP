// Localisation (slice S1.12a, Build item 4; FR-KRN-LOC-01, ruling Q6): exact decimal formatting, digits and grouping,
// isolation, the pseudo-locale and the completeness of the Urdu catalogue.
import { describe, expect, it } from 'vitest';
import en from '../messages/en.json';
import ur from '../messages/ur.json';
import configEn from '../messages/config.en.json';
import configUr from '../messages/config.ur.json';
import { directionOf, formatDecimal, formatMoney, isolate, roundTo, serverText, serverTextsReady, PSEUDO_OPEN, pseudoText } from './index';

describe('FR-KRN-LOC-01 number and money formatting (Q6)', () => {
  it('groups internationally by default and in lakh and crore when chosen', () => {
    expect(formatDecimal('1234567')).toBe('1,234,567');
    expect(formatDecimal('1234567', { digits: 'western', grouping: 'lakh_crore' })).toBe('12,34,567');
    expect(formatDecimal('123456789', { digits: 'western', grouping: 'lakh_crore' })).toBe('12,34,56,789');
    expect(formatDecimal('999')).toBe('999');
  });

  it('writes Urdu digits only when chosen (default Western for both languages)', () => {
    expect(formatDecimal('1234.5', { digits: 'eastern_arabic_indic', grouping: 'international' })).toBe('۱٬۲۳۴٫۵');
    expect(formatMoney('1234.5', 'PKR', 'ur')).toContain('1,234.50');
  });

  it('keeps exact decimals: no floating point anywhere', () => {
    expect(formatDecimal('9007199254740993.12345', undefined, 4)).toBe('9,007,199,254,740,993.1235');
    expect(formatDecimal('0.1')).toBe('0.1');
    expect(roundTo('2', '675', 2)).toEqual(['2', '68']);
    expect(roundTo('9', '995', 2)).toEqual(['10', '00']);
    expect(formatDecimal('-0.004', undefined, 2)).toBe('0.00');
    expect(formatDecimal('-1500', undefined, 2)).toBe('-1,500.00');
    expect(formatMoney('1000', 'JPY', 'en')).toBe(isolate('JPY 1,000'));
    expect(formatDecimal('not a number')).toBe('not a number');
  });

  it('isolates codes and names and sets the direction per language', () => {
    expect(isolate('a@b.c')).toBe('⁨a@b.c⁩');
    expect(directionOf('ur')).toBe('rtl');
    expect(directionOf('en')).toBe('ltr');
  });
});

describe('FR-KRN-LOC-01 catalogues', () => {
  it('Urdu has every English key and no key of its own', () => {
    const english = Object.keys({ ...en, ...configEn }).filter((k) => !k.startsWith('_'));
    const urdu = Object.keys({ ...ur, ...configUr }).filter((k) => !k.startsWith('_'));
    expect(english.filter((k) => !urdu.includes(k))).toEqual([]);
    expect(urdu.filter((k) => !english.includes(k))).toEqual([]);
  });

  it('server texts are found in the person\'s language and fall back to English', () => {
    expect(serverText('errors.auth.sign_in_failed', 'en')).toMatch(/Sign-in failed/);
    expect(serverText('errors.auth.sign_in_failed', 'ur')).not.toBe(serverText('errors.auth.sign_in_failed', 'en'));
    expect(serverText('no.such.key', 'ur')).toBeNull();
  });

  it('module server texts (S1.20c) arrive as their own chunk after start-up, in both languages', async () => {
    await serverTextsReady;
    expect(serverText('errors.hrm.transfer.same_company', 'en')).toMatch(/another company/);
    expect(serverText('errors.hrm.transfer.same_company', 'ur')).not.toBe(serverText('errors.hrm.transfer.same_company', 'en'));
  });

  it('the pseudo-locale accents text, lengthens it and keeps ICU arguments', () => {
    const text = pseudoText('Hello {name}, {count, plural, one {# item} other {# items}}');
    expect(text.startsWith(PSEUDO_OPEN)).toBe(true);
    expect(text).toContain('{name}');
    expect(text).toContain('{count, plural, one {# íťéɱ} other {# íťéɱš}}');
    expect(text).toContain('Ĥéľľó');
    expect(text.length).toBeGreaterThan('Hello {name}, {count, plural, one {# item} other {# items}}'.length);
  });
});
