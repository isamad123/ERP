// HR's leave screens of S1.33b end to end (FR-HRM-LVE-01, -03, -10, -11, -14, GHR-06, NFR-11, NFR-12): real API host, real access engine, real PostgreSQL.
// A person with HR's leave rights opens the leave types, the rules of the leave policy, the ledger of a report with its balance check, the year-end runs and the
// leave reports over the seeded team, in English and Urdu (light and dark) at 360, 768 and 1280 pixels: no sideways scroll, right to left in Urdu, no serious axe finding.
import { expect, test } from '@playwright/test';
import { accessible, display, fitsWidth, noProblems, openPage, PASSWORD, person, server, shot } from './support';

test.describe.configure({ mode: 'serial' });

let hrName = '';
let seeded: Awaited<ReturnType<typeof server.leaveSeed>>;

const PERMISSIONS = [
  'hrm.leave_types.read', 'hrm.leave_types.create', 'hrm.leave_types.update', 'hrm.leave_ledger.read', 'hrm.leave_ledger.post', 'hrm.leave_ledger.recompute',
  'hrm.leave_year_end_runs.read', 'hrm.leave_year_end_runs.confirm', 'hrm.leave_reports.read', 'hrm.leave_requests.read', 'hrm.leave_request_days.read',
  'hrm.policies.read', 'hrm.employments.read', 'hrm.employment_assignments.read', 'hrm.persons.read', 'org.companies.read', 'org.org_units.read', 'org.branches.read',
  'itg.exports.run',
];

test('FR-HRM-LVE-01 FR-HRM-LVE-03 FR-HRM-LVE-10 FR-HRM-LVE-11 FR-HRM-LVE-14 the HR leave screens show the seeded team, in English and Urdu at three widths', async ({ browser }) => {
  const hr = await person(browser, { permissions: PERMISSIONS, displayName: 'Hira Leave Admin' });
  const seed = await server.leaveSeed(hr.username);
  hrName = hr.username;
  seeded = seed;
  await hr.context.close();
  const screens = [
    { path: '/time/leave-admin/types', name: 'types', ready: 'main table' },
    { path: '/time/leave-admin/rules', name: 'rules', ready: 'main h1' },
    { path: `/time/leave-admin/ledger?employment=${seed.reports[0]}`, name: 'ledger', ready: 'main [role="region"]' },
    { path: '/time/leave-admin/year-end', name: 'year-end', ready: 'main h1' },
    { path: '/time/leave-admin/reports', name: 'reports', ready: 'main [role="region"]' },
  ];
  for (const language of ['en', 'ur'] as const) {
    for (const scheme of ['light', 'dark'] as const) {
      const context = await browser.newContext({ colorScheme: scheme, viewport: { width: 1280, height: 900 } });
      await display(context, { language });
      const page = await openPage(context);
      await page.goto('/sign-in');
      await page.locator('input[name="username"]').fill(hr.username);
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
            await accessible(page, `${screen.name} ${language} ${scheme}`);
            await shot(page, `leave-admin-${screen.name}-${language}-${scheme}-${width}`);
          }
        }
      }

      noProblems(page);
      await context.close();
    }
  }
});

test('FR-HRM-LVE-10 FR-HRM-LVE-14 the ledger of a report has a running balance that matches the server and the recompute changes nothing by itself', async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  await display(context, { language: 'en' });
  const page = await openPage(context);
  await page.goto('/sign-in');
  await page.locator('input[name="username"]').fill(hrName);
  await page.locator('input[name="password"]').fill(PASSWORD);
  await page.locator('button[type="submit"]').click();
  await expect(page.locator('.erp-shell main h1')).toBeVisible();
  await page.goto(`/time/leave-admin/ledger?employment=${seeded.reports[0]}`);
  await expect(page.locator('main [role="region"]').first()).toBeVisible();
  await expect(page.locator('main')).not.toContainText(/does not match|differ from the server/i);
  const before = await page.locator('main tbody tr').count();
  await page.getByRole('button', { name: /Check balances/i }).click();
  await expect(page.locator('main')).toContainText(/Nothing was changed/i);
  await page.reload();
  await expect(page.locator('main tbody tr').first()).toBeVisible();
  expect(await page.locator('main tbody tr').count()).toBe(before);
  noProblems(page);
  await context.close();
});
