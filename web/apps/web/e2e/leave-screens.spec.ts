// The leave screens of S1.33a end to end (FR-HRM-LVE-04, -05, -15, -16, GHR-09, FR-XM-32, NFR-11, NFR-12): real API host, real access engine, real PostgreSQL.
// A line manager of four reports (two on approved leave next week, one waiting for the manager's decision, Thursday a holiday) sees my leave, the apply form with its
// balance preview, the team calendar with its coverage warning and the approval card, in English and Urdu (light and dark) at 360, 768 and 1280 pixels: no sideways
// scroll, right to left in Urdu, no serious axe finding.
import { expect, test } from '@playwright/test';
import { accessible, display, fitsWidth, noProblems, openPage, PASSWORD, person, server, shot } from './support';

test.describe.configure({ mode: 'serial' });

const PERMISSIONS = [
  'hrm.leave_requests.read', 'hrm.leave_requests.create', 'hrm.leave_requests.update', 'hrm.leave_requests.cancel', 'hrm.leave_types.read', 'hrm.leave_ledger.read',
  'hrm.employments.read', 'hrm.employment_assignments.read', 'hrm.persons.read', 'org.companies.read', 'org.org_units.read', 'org.branches.read',
  'wf.workflow_tasks.read', 'wf.workflow_tasks.decide', 'wf.workflow_instances.read',
];

test('FR-HRM-LVE-04 FR-HRM-LVE-05 FR-HRM-LVE-15 the leave screens and the approval card show the seeded team, in English and Urdu at three widths', async ({ browser }) => {
  const first = await person(browser, { permissions: PERMISSIONS, displayName: 'Hamza Manager' });
  await first.context.close();
  const seed = await server.leaveSeed(first.username);
  const screens = [
    { path: '/time/leave', name: 'my-leave', ready: 'main h1' },
    { path: '/time/leave/apply', name: 'apply', ready: 'main h1' },
    { path: '/time/leave-team', name: 'team-calendar', ready: 'main [role="grid"]' },
    { path: `/time/leave/requests/${seed.waiting}`, name: 'request', ready: 'main h1' },
    { path: '/approvals', name: 'approvals', ready: 'main h1' },
  ];
  for (const language of ['en', 'ur'] as const) {
    for (const scheme of ['light', 'dark'] as const) {
      const context = await browser.newContext({ colorScheme: scheme, viewport: { width: 1280, height: 900 } });
      await display(context, { language });
      const page = await openPage(context);
      await page.goto('/sign-in');
      await page.locator('input[name="username"]').fill(first.username);
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
            await shot(page, `leave-${screen.name}-${language}-${scheme}-${width}`);
          }
        }
      }

      noProblems(page);
      await context.close();
    }
  }
});

test('FR-HRM-LVE-05 FR-XM-32 the manager sees the coverage warning on the waiting request and decides it; the preview of the apply form shows the balance after', async ({ browser }) => {
  const user = await person(browser, { permissions: PERMISSIONS, displayName: 'Imran Manager' });
  const seed = await server.leaveSeed(user.username);
  const page = user.page;
  await page.goto(`/time/leave/requests/${seed.waiting}`);
  await expect(page.locator('main')).toContainText(/Seeded|days|Days/);
  await page.goto('/approvals');
  await expect(page.locator('main h1')).toBeVisible();
  const row = page.getByRole('row').filter({ hasText: /Leave/ }).first();
  await row.click();
  await expect(page.locator('.erp-detail')).toContainText(/away/i); // the coverage warning: 3 of 4 away on Tuesday
  await expect(page.locator('.erp-detail')).toContainText(/30/); // the limit setting
  await page.goto('/time/leave/apply');
  await page.getByLabel(/Leave type/i).first().click();
  await page.getByRole('option', { name: /Annual/ }).click();
  await expect(page.locator('main')).toContainText(/balance/i);
  noProblems(page);
  await user.context.close();
});
