// Helpers of the browser tests (slices S1.12a, S1.12b): the control server (test clock, e-mailed links and codes, settings;
// S1.12b: roles, companies, licence, notifications), the
// shared clock of server and browsers, accessibility checks (axe: no serious or critical violation), screenshots, and
// a guard that fails a test on any content-security-policy violation or script error.
import AxeBuilder from '@axe-core/playwright';
import { expect, type BrowserContext, type Locator, type Page } from '@playwright/test';

const control = process.env.E2E_CONTROL_URL ?? 'http://127.0.0.1:0';

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(control + path, { method, headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, body: body === undefined ? null : JSON.stringify(body) });
  if (!response.ok) throw new Error(`control ${method} ${path}: ${response.status} ${await response.text()}`);
  return (response.status === 204 ? undefined : await response.json()) as T;
}

export interface NotificationSeed {
  eventCode: string;
  title: string;
  body: string;
  link?: string | null;
  group: string;
}

export const server = {
  now: async () => (await call<{ now: number }>('GET', '/clock')).now,
  /** An invited person holding the business user role for all companies (S1.12b). */
  newUser: (options: { displayName?: string; language?: 'en' | 'ur' } = {}) =>
    call<{ userId: string; username: string; email: string }>('POST', '/users', options),
  /** A role with exactly these permissions for the person, for all companies or the named company codes (S1.12b). */
  role: (username: string, permissions: string[], companies?: string[], removeBaseline = false) =>
    call<undefined>('POST', '/roles', { username, permissions, companies: companies ?? null, removeBaseline }),
  company: (code: string, en: string, ur: string) => call<{ id: string }>('POST', '/companies', { code, en, ur }),
  /** Changes the licence in force; answers the claims before, for `restoreClaims` (S1.12b). */
  licence: (change: { daysAfterEnd?: number | null; features?: string[]; companiesLimit?: number }) =>
    call<{ claims: string }>('POST', '/licence', { setEnd: 'daysAfterEnd' in change, daysAfterEnd: change.daysAfterEnd ?? null, features: change.features ?? null, companiesLimit: change.companiesLimit ?? null }),
  restoreClaims: (claims: string) => call<undefined>('POST', '/licence/claims', { claims }),
  notify: (username: string, items: NotificationSeed[]) => call<undefined>('POST', '/notifications', { username, items }),
  mail: (event: string, username: string) => call<Record<string, string>>('GET', `/mail/${event}?username=${encodeURIComponent(username)}`),
  totp: async (secret: string) => (await call<{ code: string }>('GET', `/totp?secret=${encodeURIComponent(secret)}`)).code,
  setting: (key: string, json: string) => call<undefined>('POST', '/settings', { key, json }),
  facts: (username: string) => call<{ liveSessions: number; reusedSessions: number; trustedDevices: number }>('GET', `/facts?username=${encodeURIComponent(username)}`),
};

export const PASSWORD = 'Brave-Lantern-42';
export const NEW_PASSWORD = 'Quiet-River-58';

const clocked = new WeakSet<BrowserContext>();

/** A page whose clock follows the server's test clock; CSP violations and script errors fail the test. */
export async function openPage(context: BrowserContext): Promise<Page> {
  // The clock belongs to the browser context (all its tabs): installed once, at the server's time.
  if (!clocked.has(context)) {
    await context.clock.install({ time: await server.now() });
    clocked.add(context);
  }
  const page = await context.newPage();
  const problems: string[] = [];
  page.on('console', (m) => { if (m.type() === 'error' && /Content.Security.Policy|Refused to/i.test(m.text())) problems.push(m.text()); });
  page.on('pageerror', (e) => problems.push(e.message));
  (page as Page & { problems: string[] }).problems = problems;
  return page;
}

export function noProblems(page: Page): void {
  expect((page as Page & { problems?: string[] }).problems ?? []).toEqual([]);
}

/**
 * Moves the server's clock and every given page's clock forward together, in steps of at most 30 seconds, so the two
 * never drift apart by more than a step (a refresh a page makes during a step reaches the server at that step's time).
 */
export async function advance(pages: Page[], ms: number): Promise<void> {
  for (let left = ms; left > 0; left -= 30_000) {
    const step = Math.min(30_000, left);
    await call('POST', '/clock/advance', { milliseconds: step });
    for (const context of new Set(pages.map((p) => p.context()))) await context.clock.runFor(step); // once per context
    for (const page of pages) await page.waitForLoadState('networkidle').catch(() => undefined);
  }
}

/** The next authenticator code: moves both clocks past the step just used, then asks the server. */
export async function nextCode(pages: Page[], secret: string): Promise<string> {
  await advance(pages, 31_000);
  return server.totp(secret);
}

/** No accessibility violation of serious or critical level (Q20: axe-core, development only). */
export async function accessible(page: Page, label: string): Promise<void> {
  // React Aria's live announcer keeps its last screen-reader notes for a few seconds; a note that named a button of the
  // previous screen points at an id that no longer exists. It is invisible and transient, so it is left out of the check.
  const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).exclude('[data-live-announcer]').analyze();
  const bad = result.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(bad.map((v) => `${label}: ${v.id} (${v.impact}) ${v.nodes.map((n) => `${n.target.join(' ')} ${n.html.slice(0, 300)}`).join(', ')}`)).toEqual([]);
}

export async function shot(page: Page, name: string): Promise<void> {
  // No caret hiding or animation stopping: both inject an inline style, which the content security policy blocks.
  await page.screenshot({ path: `test-results/screenshots/${name}.png`, fullPage: true, animations: 'allow', caret: 'initial' });
}

/** The page fits the width: no sideways scrolling (spec 13 NFR-11). */
export async function fitsWidth(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

/** Display choices for the next page load: language, theme, density (kept in the browser, like a person's choice). */
export async function display(context: BrowserContext, choice: { language?: 'en' | 'ur'; theme?: string; pseudo?: boolean }): Promise<void> {
  await context.addInitScript((c) => {
    window.localStorage.setItem('erp.display', JSON.stringify({ language: c.language ?? 'en', theme: c.theme ?? 'system', density: 'standard' }));
    if (c.pseudo) window.localStorage.setItem('erp.test.pseudo', '1');
    else window.localStorage.removeItem('erp.test.pseudo');
  }, choice);
}

/** A form field by its visible label; a required field's label ends with the "*" marker (hidden from screen readers). */
export function field(page: Page, label: string): Locator {
  return page.getByLabel(new RegExp(`^${label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s*\\*?$`));
}
