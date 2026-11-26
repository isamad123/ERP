// The expense claim screens of S1.42 end to end (FR-HRM-EXP-01 to -03, -05, GHR-06, FR-XM-32, NFR-11, NFR-12): real API host, real access engine, real PostgreSQL.
// A manager of four reports makes a category and an exchange rate; a colleague makes a claim for one of the reports (a receipt is required and missing, so the line carries a flag), the
// manager finds it in the approvals inbox with its card, saves a reduced amount with a reason on the line and approves; then every screen is opened in English and Urdu (light and dark)
// at 360, 768 and 1280 pixels: no sideways scroll, right to left in Urdu, no serious axe finding.
import { expect, test, type Page } from '@playwright/test';
import { accessible, display, field, fitsWidth, noProblems, openPage, PASSWORD, person, server, shot } from './support';

test.describe.configure({ mode: 'serial' });

const READS = ['hrm.employments.read', 'hrm.employment_assignments.read', 'hrm.persons.read', 'org.companies.read', 'org.org_units.read', 'org.branches.read', 'wf.workflow_instances.read'];
const MANAGER = [
  'hrm.expense_claims.read', 'hrm.expense_claims.create', 'hrm.expense_claims.decide', 'hrm.expense_categories.read', 'hrm.expense_categories.create', 'hrm.expense_categories.update',
  'hrm.exchange_rates.read', 'hrm.exchange_rates.create', 'hrm.exchange_rates.update', 'wf.workflow_tasks.read', 'wf.workflow_tasks.decide', 'itg.exports.run', ...READS,
];
const COLLEAGUE = ['hrm.expense_claims.read', 'hrm.expense_claims.create', 'hrm.expense_categories.read', ...READS];

let manager: Awaited<ReturnType<typeof person>>;
let colleague: Awaited<ReturnType<typeof person>>;
let seed: Awaited<ReturnType<typeof server.leaveSeed>>;
let claimId = '';

async function signIn(page: Page, username: string): Promise<void> {
  await page.goto('/sign-in');
  await page.locator('input[name="username"]').fill(username);
  await page.locator('input[name="password"]').fill(PASSWORD);
  await page.locator('button[type="submit"]').click();
  await expect(page.locator('.erp-shell main h1')).toBeVisible();
}

async function typeDate(page: Page, scope: ReturnType<Page['locator']>, iso: string): Promise<void> {
  const [y, m, d] = iso.split('-') as [string, string, string];
  for (const [segment, text] of [['month', m], ['day', d], ['year', y]] as const) {
    await scope.getByRole('spinbutton', { name: new RegExp(`^${segment}`, 'i') }).first().click();
    await page.keyboard.type(text);
  }
}

test('FR-HRM-EXP-01 FR-HRM-EXP-02 the manager makes a category and an exchange rate with the engine screens', async ({ browser }) => {
  manager = await person(browser, { permissions: MANAGER, displayName: 'Hamza Manager' });
  seed = await server.leaveSeed(manager.username);
  await server.reference(); // the currencies an exchange rate refers to
  const page = manager.page;

  await page.goto('/payroll/expense-categories/new');
  await field(page, 'Code').fill('TRAVEL');
  await field(page, 'In English').fill('Travel');
  await page.getByRole('switch', { name: /Requires receipt/ }).check({ force: true });
  await page.getByRole('button', { name: 'Create' }).click();
  await expect(page.getByText(/Category TRAVEL Travel was added\./)).toBeVisible();

  await page.goto('/payroll/exchange-rates/new');
  await field(page, 'From currency').fill('USD');
  await field(page, 'To currency').fill('PKR');
  await typeDate(page, page.locator('main'), '2026-01-01');
  await field(page, 'Rate').fill('280.5');
  await page.getByRole('button', { name: 'Create' }).click();
  await expect(page.getByText(/Rate USD PKR .* was added\./)).toBeVisible();
  noProblems(page);
});

test('FR-HRM-EXP-02 FR-HRM-EXP-03 a colleague claims for a report; the missing receipt is flagged and the claim waits for the manager', async ({ browser }) => {
  colleague = await person(browser, { permissions: COLLEAGUE, displayName: 'Sana Colleague' });
  const page = colleague.page;
  await page.goto(`/expenses/new?employment=${seed.reports[0]}`);
  await expect(page.getByRole('heading', { name: 'New expense claim' })).toBeVisible();
  await expect(page.getByText(/you are making this claim for them/)).toBeVisible();
  const line = page.getByRole('group', { name: 'Line 1' });
  await line.getByRole('button', { name: /Category/ }).click();
  await page.getByRole('option', { name: /Travel/ }).click();
  await line.getByRole('textbox', { name: /Amount/ }).fill('1250.50');
  await page.getByRole('textbox', { name: /Title/ }).fill('Lahore visit');
  await page.getByRole('button', { name: /How you are paid/ }).click(); // the new category has no payroll component: it is paid separately
  await page.getByRole('option', { name: 'Separate payment' }).click();
  await page.getByRole('button', { name: 'Save draft' }).click();
  await expect(page.getByRole('heading', { name: /^Expense claim EXP/ })).toBeVisible();
  claimId = /\/expenses\/claims\/([^/?]+)/.exec(page.url())?.[1] ?? '';
  expect(claimId).not.toBe('');
  await expect(page.getByRole('group', { name: 'Line 1' }).getByText('Receipt missing')).toBeVisible();
  await page.getByRole('button', { name: 'Submit' }).click();
  await expect(page.getByText('Waiting for approval').first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Withdraw' })).toBeVisible();
  noProblems(page);
});

test('FR-HRM-EXP-03 FR-XM-32 the manager sees the card in the inbox, reduces the line with a reason and approves the saved amount', async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  await display(context, { language: 'en' });
  const page = await openPage(context);
  await signIn(page, manager.username);
  await page.goto('/approvals');
  await expect(page.getByRole('heading', { name: 'Approvals', level: 1 })).toBeVisible();
  await page.getByRole('grid', { name: 'Requests' }).getByRole('button', { name: /Open/ }).first().click();
  const card = page.getByRole('region', { name: 'Expense claim' }).or(page.locator('section[aria-labelledby="erp-exp-card"]'));
  await expect(card.getByText('Receipt missing')).toBeVisible();
  await card.getByRole('textbox', { name: /Approved amount/ }).fill('1000');
  await card.getByRole('button', { name: 'Save line decisions' }).click();
  await expect(card.getByText('Give a reason when you approve less than was claimed.')).toBeVisible();
  await card.getByRole('textbox', { name: /Reason/ }).fill('Over the travel limit');
  await accessible(page, 'approvals card');
  await card.getByRole('button', { name: 'Save line decisions' }).click();
  await expect(page.getByText('The line decisions were saved.')).toBeVisible();
  await page.getByRole('button', { name: 'Approve' }).first().click();
  await page.getByRole('dialog').getByRole('button', { name: 'Approve' }).click();
  await page.goto(`/expenses/claims/${claimId}`);
  await expect(page.getByText('Over the travel limit')).toBeVisible();
  noProblems(page);
  await context.close();
});

test('FR-HRM-EXP-02 FR-HRM-EXP-05 GHR-06 the expense screens in English and Urdu, light and dark, at three widths', async ({ browser }) => {
  const screens = (user: 'manager' | 'colleague') => (user === 'manager'
    ? [
      { path: '/expenses', name: 'claims', ready: 'main [role="grid"]' },
      { path: `/expenses/claims/${claimId}`, name: 'claim', ready: 'main table' },
      { path: '/expenses/finance', name: 'finance', ready: 'main h1' },
      { path: '/payroll/expense-categories', name: 'categories', ready: 'main [role="grid"]' },
      { path: '/payroll/exchange-rates', name: 'rates', ready: 'main [role="grid"]' },
    ]
    : [
      { path: `/expenses/new?employment=${seed.reports[1]}`, name: 'new', ready: 'main [role="group"]' },
    ]);
  for (const language of ['en', 'ur'] as const) {
    for (const scheme of ['light', 'dark'] as const) {
      for (const who of ['manager', 'colleague'] as const) {
        const context = await browser.newContext({ colorScheme: scheme, viewport: { width: 1280, height: 900 } });
        await display(context, { language });
        const page = await openPage(context);
        await signIn(page, who === 'manager' ? manager.username : colleague.username);
        for (const width of [360, 768, 1280] as const) {
          await page.setViewportSize({ width, height: 900 });
          for (const screen of screens(who)) {
            await page.goto(screen.path);
            await expect(page.locator(screen.ready).first()).toBeVisible();
            await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
            expect(await page.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
            await fitsWidth(page).catch((e: Error) => { throw new Error(`${screen.name} ${language} ${scheme} ${width}: ${e.message}`); });
            if (width === 1280) {
              await accessible(page, `${screen.name} ${language} ${scheme}`);
              await shot(page, `expenses-${screen.name}-${language}-${scheme}-${width}`);
            }
          }
        }
        noProblems(page);
        await context.close();
      }
    }
  }
});
