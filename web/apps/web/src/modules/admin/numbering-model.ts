// The pattern rules of a numbering scheme for the screen (slice S1.15c; the server's `NumberPattern` is the judge: it checks again on
// every save). The screen reads a pattern the same way to show an example of the first number, to explain a refusal before the old
// scheme is retired in "retire and replace", and to warn about codes that change numbers. Numbers always use Latin digits.
import type { ResetRule } from '../../api/numbering';

export type PatternToken = 'seq' | 'yyyy' | 'yy' | 'mm' | 'fy' | 'company' | 'branch';
export type PatternPart = { kind: 'literal'; text: string } | { kind: 'token'; token: PatternToken; digits: number };

export const TOKENS: readonly PatternToken[] = ['seq', 'yyyy', 'yy', 'mm', 'fy', 'company', 'branch'];
const MAX_LENGTH = 40;

export interface PatternRead { parts: PatternPart[]; error: string | null }

/** Reads a pattern; `error` is the server's text key for the first problem found. */
export function readPattern(text: string): PatternRead {
  const parts: PatternPart[] = [];
  if (text.trim().length === 0) return { parts, error: 'errors.numbering.pattern_empty' };
  let literal = '';
  let i = 0;
  let seq = 0;
  while (i < text.length) {
    const c = text.charAt(i);
    if (c === '}') return { parts, error: 'errors.numbering.pattern_brace' };
    if (c !== '{') { literal += c; i += 1; continue; }
    const end = text.indexOf('}', i + 1);
    const open = text.indexOf('{', i + 1);
    if (end < 0 || (open >= 0 && open < end)) return { parts, error: 'errors.numbering.pattern_brace' };
    if (literal) { parts.push({ kind: 'literal', text: literal }); literal = ''; }
    const body = text.slice(i + 1, end);
    const [name = '', arg] = body.split(':');
    if (name === 'seq') {
      const digits = arg === undefined ? 0 : /^\d{1,3}$/.test(arg) ? Number(arg) : 0;
      if (digits < 1 || digits > 12) return { parts, error: 'errors.numbering.pattern_seq_digits' };
      seq += 1;
      parts.push({ kind: 'token', token: 'seq', digits });
    } else if (arg === undefined && (TOKENS as readonly string[]).includes(name)) {
      parts.push({ kind: 'token', token: name as PatternToken, digits: 0 });
    } else {
      return { parts, error: 'errors.numbering.pattern_token_unknown' };
    }
    i = end + 1;
  }
  if (literal) parts.push({ kind: 'literal', text: literal });
  return { parts, error: seq === 1 ? null : 'errors.numbering.pattern_seq_count' };
}

export interface Facts { hasCompany: boolean; hasBranch: boolean; branchScheme: boolean }

/** The checks the server makes on the pattern text and the reset rule, in its order, so that "retire and replace" never retires on a refusal it could have seen. */
export function checkPattern(text: string, reset: ResetRule, facts: Facts): string | null {
  const read = readPattern(text);
  if (read.error) return read.error;
  const has = (...tokens: PatternToken[]) => read.parts.some((p) => p.kind === 'token' && tokens.includes(p.token));
  if (reset === 'yearly' && !has('yyyy', 'yy')) return 'errors.numbering.pattern_year_needed';
  if (reset === 'monthly' && !(has('yyyy', 'yy') && has('mm'))) return 'errors.numbering.pattern_month_needed';
  if (reset === 'fiscal_yearly' && !has('fy')) return 'errors.numbering.pattern_fiscal_year_needed';
  if (has('company') && !facts.hasCompany) return 'errors.numbering.pattern_company_not_allowed';
  if (has('branch') && !facts.hasBranch) return 'errors.numbering.pattern_branch_not_allowed';
  if (has('branch') && !facts.branchScheme) return 'errors.numbering.pattern_branch_company_scheme';
  if (facts.branchScheme && !has('branch')) return 'errors.numbering.branch_token_needed';
  for (let i = 0; i < read.parts.length - 1; i += 1) {
    const part = read.parts[i];
    const next = read.parts[i + 1];
    if (part?.kind === 'token' && next && (part.token === 'company' || part.token === 'branch') && (next.kind !== 'literal' || /[\p{L}\p{N}]$/u.test(next.text))) return 'errors.numbering.pattern_code_separator';
  }
  return null;
}

export interface ExampleContext { date: Date; fiscalStartMonth: number; company: string; branch: string; value: number }

const two = (n: number) => String(n % 100).padStart(2, '0');

/** The number for a running value, as the server prints it (a value wider than the digits prints in full); null when the pattern cannot be read. */
export function renderExample(text: string, ctx: ExampleContext): string | null {
  const read = readPattern(text);
  if (read.error) return null;
  const year = ctx.date.getUTCFullYear();
  const month = ctx.date.getUTCMonth() + 1;
  const fiscalStart = month >= ctx.fiscalStartMonth ? year : year - 1;
  return read.parts.map((p) => {
    if (p.kind === 'literal') return p.text;
    switch (p.token) {
      case 'seq': return String(ctx.value).padStart(p.digits, '0');
      case 'yyyy': return String(year).padStart(4, '0');
      case 'yy': return two(year);
      case 'mm': return two(month);
      case 'fy': return ctx.fiscalStartMonth === 1 ? String(fiscalStart).padStart(4, '0') : `${String(fiscalStart).padStart(4, '0')}-${two(fiscalStart + 1)}`;
      case 'company': return ctx.company;
      default: return ctx.branch;
    }
  }).join('');
}

export const usesCodes = (text: string): { company: boolean; branch: boolean } => {
  const parts = readPattern(text).parts;
  const has = (t: PatternToken) => parts.some((p) => p.kind === 'token' && p.token === t);
  return { company: has('company'), branch: has('branch') };
};

/** Numbers longer than 40 characters are refused by the server; the example warns early. */
export const tooLong = (example: string | null): boolean => example !== null && example.length > MAX_LENGTH;

/** The default of the scheme form for a document type. */
export const defaultsOf = (type: { defaultPattern: string; defaultReset: ResetRule }) => ({ pattern: type.defaultPattern, resetRule: type.defaultReset });

/** The period a date falls in, the counter's scope key (as the server's `ScopeKey`): `all`, `2026`, `2026-07` or `fy2026`. */
export function scopeKey(reset: ResetRule, date: Date, fiscalStartMonth: number): string {
  const year = date.getUTCFullYear();
  const month = date.getUTCMonth() + 1;
  switch (reset) {
    case 'never': return 'all';
    case 'yearly': return String(year).padStart(4, '0');
    case 'monthly': return `${String(year).padStart(4, '0')}-${two(month)}`;
    default: return `fy${String(month >= fiscalStartMonth ? year : year - 1).padStart(4, '0')}`;
  }
}

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** The running value inside a printed number of the pattern, or null when the number does not look like it. */
export function runningValue(text: string, printed: string): number | null {
  const read = readPattern(text);
  if (read.error) return null;
  const source = read.parts.map((p) => {
    if (p.kind === 'literal') return escape(p.text);
    switch (p.token) {
      case 'seq': return '(\\d+)';
      case 'yyyy': return '\\d{4}';
      case 'yy': case 'mm': return '\\d{2}';
      case 'fy': return '\\d{4}(?:-\\d{2})?';
      default: return '.+?';
    }
  }).join('');
  const found = new RegExp(`^${source}$`, 'u').exec(printed);
  return found?.[1] ? Number(found[1]) : null;
}

export interface NextInput { pattern: string; reset: ResetRule; start: number; ranges: { period: string; lastNumber: string }[]; today: Date; fiscalStartMonth: number; company: string; branch: string }

/** The number the series gives next (S1.15c Build item 9): the start value in a period with no number yet, else one after the last number printed in the period; a gap-allowed series may skip one after a failed save. */
export function nextNumber(input: NextInput): string | null {
  const key = scopeKey(input.reset, input.today, input.fiscalStartMonth);
  const last = input.ranges.find((r) => r.period === key);
  const lastValue = last ? runningValue(input.pattern, last.lastNumber) : null;
  const value = last ? (lastValue === null ? null : lastValue + 1) : input.start;
  return value === null ? null : renderExample(input.pattern, { date: input.today, fiscalStartMonth: input.fiscalStartMonth, company: input.company, branch: input.branch, value });
}

/** Today's date in a time zone, as a UTC date at midnight (the examples and periods are by day). */
export function todayIn(timeZone: string, now: Date = new Date()): Date {
  const [y = 1970, m = 1, d = 1] = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(now).split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d));
}
