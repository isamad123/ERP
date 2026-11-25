// The attendance screens of S1.30 end to end (R-162; FR-HRM-ATT-11, ATT-14, ATT-18, ATT-19, SSP-02, FR-KRN-LOC-01, NFR-11, NFR-12): real API host, real access engine,
// real PostgreSQL. A person with an open exception, a waiting correction and a waiting duty request sees the request and exception screens, the live dashboard,
// the reports and the team view in English and Urdu (light and dark) at 360, 768 and 1280 pixels: no sideways scroll, right to left in Urdu, no serious axe finding.
import { expect, test } from '@playwright/test';
import { accessible, display, noProblems, openPage, PASSWORD, person, server, shot } from './support';

test.describe.configure({ mode: 'serial' });

const PERMISSIONS = [
  'hrm.attendance_days.read', 'hrm.attendance_exceptions.read', 'hrm.regularization_requests.read', 'hrm.regularization_requests.create', 'hrm.duty_requests.read', 'hrm.duty_requests.create',
  'hrm.attendance_dashboard.read', 'hrm.attendance_reports.read', 'hrm.attendance_events.read', 'hrm.employments.read', 'hrm.employment_assignments.read', 'hrm.persons.read',
  'org.companies.read', 'org.org_units.read', 'org.locations.read', 'org.branches.read', 'itg.exports.run',
];
const SCREENS = [
  { path: '/time/exceptions', name: 'exceptions' },
  { path: '/time/requests', name: 'requests' },
  { path: '/time/dashboard', name: 'dashboard' },
  { path: '/time/reports', name: 'reports' },
  { path: '/time/team', name: 'team' },
];

test('FR-HRM-ATT-11 FR-HRM-ATT-14 FR-HRM-ATT-18 FR-HRM-SSP-02 the attendance screens show the seeded person, in English and Urdu at three widths', async ({ browser }) => {
  const first = await person(browser, { permissions: PERMISSIONS, displayName: 'Hamza Attendance' });
  await first.context.close();
  await server.attendanceSeed(first.username);
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
        for (const screen of SCREENS) {
          await page.goto(screen.path);
          await expect(page.locator('main h1')).toBeVisible();
          await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
          expect(await page.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
          const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
          expect(overflow, `${screen.name} ${language} ${scheme} ${width}`).toBeLessThanOrEqual(0);
          if (width === 1280) {
            await accessible(page, `${screen.name} ${language} ${scheme}`);
            await shot(page, `attendance-${screen.name}-${language}-${scheme}-${width}`);
          }
        }
      }

      noProblems(page);
      await context.close();
    }
  }
});

test('FR-HRM-ATT-11 FR-HRM-ATT-18 the seeded exception, requests and dashboard counts are on the screens', async ({ browser }) => {
  const user = await person(browser, { permissions: PERMISSIONS, displayName: 'Sana Attendance' });
  await server.attendanceSeed(user.username);
  const page = user.page;
  await page.goto('/time/exceptions');
  await expect(page.locator('main')).toContainText(/missing|Missing/);
  await page.goto('/time/requests');
  await expect(page.locator('main')).toContainText('Seeded');
  await page.goto('/time/dashboard');
  await expect(page.locator('main')).toContainText('4'); // four people with a shift today
  noProblems(page);
  await user.context.close();
});
