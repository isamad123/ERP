// The loan screens of S1.41 end to end (FR-HRM-LON-01, -02, -07, GHR-06, NFR-11, NFR-12): real API host, real access engine, real PostgreSQL. A person with the loan rights opens the loan list,
// the application, the loan types and the reports, in English and Urdu (light and dark) at 360, 768 and 1280 pixels: no sideways scroll, right to left in Urdu, a name and a way in for every
// state, no serious axe finding. The application is checked with nothing chosen: the form refuses a send before the check, and a person without the rights sees the denied state.
import { expect, test } from '@playwright/test';
import { accessible, display, fitsWidth, noProblems, openPage, PASSWORD, person, shot } from './support';

test.describe.configure({ mode: 'serial' });

const PERMISSIONS = [
  'hrm.loans.read', 'hrm.loans.create', 'hrm.loan_types.read', 'hrm.loan_types.create', 'hrm.loan_types.update', 'hrm.loan_reports.read', 'hrm.loans.disburse', 'hrm.loans.receipt',
  'hrm.loan_installments.read', 'hrm.loan_transactions.read', 'hrm.policies.read', 'hrm.pay_components.read', 'hrm.employments.read', 'hrm.persons.read',
  'org.companies.read', 'itg.exports.run',
];

test('FR-HRM-LON-01 FR-HRM-LON-02 FR-HRM-LON-07 the loan screens open in English and Urdu at three widths with nothing to scan or scroll sideways', async ({ browser }) => {
  const user = await person(browser, { permissions: PERMISSIONS, displayName: 'Hira Loans' });
  await user.context.close();
  const screens = [
    { path: '/payroll/loans', name: 'list', ready: 'main h1' },
    { path: '/payroll/loans/apply', name: 'apply', ready: 'main form' },
    { path: '/payroll/loan-types', name: 'types', ready: 'main h1' },
    { path: '/payroll/loan-reports', name: 'reports', ready: 'main [role="tablist"]' },
  ];
  for (const language of ['en', 'ur'] as const) {
    for (const scheme of ['light', 'dark'] as const) {
      const context = await browser.newContext({ colorScheme: scheme, viewport: { width: 1280, height: 900 } });
      await display(context, { language });
      const page = await openPage(context);
      await page.goto('/sign-in');
      await page.locator('input[name="username"]').fill(user.username);
      await page.locator('input[name="password"]').fill(PASSWORD);
      await page.locator('button[type="submit"]').click();
      await expect(page.locator('.erp-shell main h1')).toBeVisible();
      for (const width of [360, 768, 1280] as const) {
        await page.setViewportSize({ width, height: 900 });
        for (const screen of screens) {
          await page.goto(screen.path);
          await expect(page.locator(screen.ready).first()).toBeVisible();
          await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
          expect(await page.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
          await fitsWidth(page).catch((e: Error) => { throw new Error(`${screen.name} ${language} ${scheme} ${width}: ${e.message}`); });
          if (width === 1280) {
            await accessible(page, `loans ${screen.name} ${language} ${scheme}`);
            await shot(page, `loans-${screen.name}-${language}-${scheme}-${width}`);
          }
        }
      }

      noProblems(page);
      await context.close();
    }
  }
});

test('FR-HRM-LON-02 the application cannot be sent before it is checked, and a person without the rights sees the denied state', async ({ browser }) => {
  const user = await person(browser, { permissions: PERMISSIONS, displayName: 'Hira Loans Two' });
  const page = user.page;
  await page.goto('/payroll/loans/apply');
  await expect(page.locator('main form')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Send application' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Check and show installments' })).toBeDisabled();
  await accessible(page, 'loans apply form');
  noProblems(page);
  await user.context.close();

  const nobody = await person(browser, { permissions: ['hrm.loans.read'], displayName: 'Nobody Loans' });
  await nobody.page.goto('/payroll/loans/apply');
  await expect(nobody.page.getByText('You may not see loans.')).toBeVisible();
  await expect(nobody.page.locator('main form')).toHaveCount(0);
  noProblems(nobody.page);
  await nobody.context.close();
});
