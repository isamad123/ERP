// Shared formatting (slice S1.12a, Build item 4, ruling Q6; spec 13 section 11, FR-UX-CMP-04). Numbers and money are
// formatted from exact decimal strings, never through floating point. Digits (`western` or `eastern_arabic_indic`) and
// grouping (`international` 1,234,567 or `lakh_crore` 12,34,567) are the settings loc.number.digits and
// loc.number.grouping; defaults Western and international for both languages until tested with users (Q6). Dates show
// day, month name and year in the person's language and time zone. Stored and exported values never change.

export type Locale = 'en' | 'ur';
export type Digits = 'western' | 'eastern_arabic_indic';
export type Grouping = 'international' | 'lakh_crore';

export interface NumberPreferences {
  digits: Digits;
  grouping: Grouping;
}

export const DEFAULT_NUMBERS: NumberPreferences = { digits: 'western', grouping: 'international' };

/** Extended Arabic-Indic digits, as written in Urdu. */
const EASTERN = ['۰', '۱', '۲', '۳', '۴', '۵', '۶', '۷', '۸', '۹'];

const DECIMAL = /^(-)?(\d+)(?:\.(\d+))?$/;

export function digitsOf(text: string, digits: Digits): string {
  return digits === 'western' ? text : text.replace(/\d/g, (d) => EASTERN[Number(d)] ?? d);
}

function group(integer: string, grouping: Grouping, separator: string): string {
  if (integer.length <= 3) return integer;
  const last = integer.slice(-3);
  const rest = integer.slice(0, -3);
  const size = grouping === 'lakh_crore' ? 2 : 3;
  const parts: string[] = [];
  for (let end = rest.length; end > 0; end -= size) parts.unshift(rest.slice(Math.max(0, end - size), end));
  return [...parts, last].join(separator);
}

function separators(digits: Digits): { group: string; decimal: string; minus: string } {
  // With Urdu digits the Arabic thousands and decimal separators read correctly; with Western digits, comma and point.
  return digits === 'western' ? { group: ',', decimal: '.', minus: '-' } : { group: '٬', decimal: '٫', minus: '−' };
}

/**
 * A decimal string (as the API sends it, e.g. "-1234567.5") shown with grouping and digits. `scale` pads or rounds the
 * fraction half away from zero on the string itself, so no precision is ever lost. Invalid input is shown unchanged.
 */
export function formatDecimal(value: string, prefs: NumberPreferences = DEFAULT_NUMBERS, scale?: number): string {
  const m = DECIMAL.exec(value.trim());
  if (!m) return value;
  const sign = m[1] ?? '';
  let integer = m[2] ?? '0';
  let fraction = m[3] ?? '';
  if (scale !== undefined) [integer, fraction] = roundTo(integer, fraction, scale);
  integer = integer.replace(/^0+(?=\d)/, '');
  const negative = sign === '-' && /[1-9]/.test(integer + fraction);
  const s = separators(prefs.digits);
  const text = (negative ? s.minus : '') + group(integer, prefs.grouping, s.group) + (fraction ? s.decimal + fraction : '');
  return digitsOf(text, prefs.digits);
}

/** Rounds a decimal given as integer and fraction digits to `scale` places, half away from zero, without numbers. */
export function roundTo(integer: string, fraction: string, scale: number): [string, string] {
  if (fraction.length <= scale) return [integer, fraction.padEnd(scale, '0')];
  const keep = integer + fraction.slice(0, scale);
  const up = Number(fraction[scale]) >= 5;
  let digits = keep;
  if (up) {
    const chars = keep.split('');
    let i = chars.length - 1;
    while (i >= 0) {
      if (chars[i] === '9') {
        chars[i] = '0';
        i -= 1;
      } else {
        chars[i] = String(Number(chars[i]) + 1);
        break;
      }
    }
    digits = (i < 0 ? '1' : '') + chars.join('');
  }
  const cut = digits.length - scale;
  return [digits.slice(0, cut) || '0', digits.slice(cut)];
}

/** Money from an exact decimal string with its currency code (FR-UX-CMP-04): the currency's decimals, never a float. */
export function formatMoney(amount: string, currency: string, locale: Locale, prefs: NumberPreferences = DEFAULT_NUMBERS): string {
  const scale = currencyDecimals(currency);
  const number = formatDecimal(amount, prefs, scale);
  return isolate(locale === 'ur' ? `${number} ${currency}` : `${currency} ${number}`);
}

/** ISO 4217 minor units for the currencies the product handles first; others default to 2. */
export function currencyDecimals(currency: string): number {
  return ({ JPY: 0, KWD: 3, BHD: 3, OMR: 3 } as Record<string, number>)[currency.toUpperCase()] ?? 2;
}

/** A whole number (count) with grouping and digits. */
export function formatInteger(value: number | bigint, prefs: NumberPreferences = DEFAULT_NUMBERS): string {
  return formatDecimal(BigInt(value).toString(), prefs);
}

/** Day, month name and year in the person's language and time zone; digits follow the preference. */
export function formatDate(at: Date | string, locale: Locale, timeZone: string, prefs: NumberPreferences = DEFAULT_NUMBERS): string {
  return new Intl.DateTimeFormat(intlLocale(locale, prefs), { day: 'numeric', month: 'long', year: 'numeric', timeZone }).format(new Date(at));
}

/** Date and time (hours and minutes) in the person's language and time zone. */
export function formatDateTime(at: Date | string, locale: Locale, timeZone: string, prefs: NumberPreferences = DEFAULT_NUMBERS): string {
  return new Intl.DateTimeFormat(intlLocale(locale, prefs), { day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit', timeZone }).format(new Date(at));
}

/** The Intl locale for a language and digit choice (Urdu in Pakistan; Western digits unless chosen otherwise). */
export function intlLocale(locale: Locale, prefs: NumberPreferences = DEFAULT_NUMBERS): string {
  const base = locale === 'ur' ? 'ur-PK' : 'en-PK';
  return `${base}-u-nu-${prefs.digits === 'western' ? 'latn' : 'arabext'}`;
}

const FSI = '⁨';
const PDI = '⁩';

/**
 * Unicode isolation for codes, numbers, e-mail addresses and names inside text of the other direction (spec 13 I18,
 * FR-UX-TOK-13): "user@example.com" stays readable inside an Urdu sentence.
 */
export function isolate(text: string): string {
  return FSI + text + PDI;
}

export function directionOf(locale: string): 'ltr' | 'rtl' {
  return locale === 'ur' || locale === 'ar' || locale === 'pseudo' ? 'rtl' : 'ltr';
}
