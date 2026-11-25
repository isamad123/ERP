// The attendance lock console of S1.31 end to end (FR-XM-26, FR-HRM-ATT-17, FR-KRN-LOC-01, NFR-11, NFR-12): real API host, real access engine, real PostgreSQL.
// An HR person with the lock permissions opens the periods and the adjustments in English and Urdu at 360 and 1280 pixels: the data region has rendered (no
// busy region), no sideways scroll, right to left in Urdu, no serious or critical axe finding.
import { expect, test } from '@playwright/test';
import { accessible, display, fitsWidth, noProblems, openPage, PASSWORD, person, shot } from './support';

test.describe.configure({ mode: 'serial' });

const PERMISSIONS = [
  'hrm.attendance_periods.read', 'hrm.attendance_periods.lock', 'hrm.attendance_periods.reopen', 'hrm.attendance_adjustments.read', 'hrm.attendance_adjustments.submit',
  'hrm.attendance_events.read', 'hrm.employments.read', 'hrm.pay_periods.read', 'hrm.pay_groups.read', 'org.companies.read',
];
const SCREENS = [
  { path: '/time/periods', name: 'periods', ready: /No pay periods have started yet|ابھی کوئی پے پیریڈ شروع نہیں ہوا|Attendance periods|حاضری کی مدتیں/ },
  { path: '/time/adjustments', name: 'adjustments', ready: /No adjustments yet|ابھی کوئی درستی نہیں|Attendance adjustments|حاضری کی درستیاں/ },
];

test('FR-XM-26 FR-HRM-ATT-17 the lock console and the adjustments show in English and Urdu at two widths', async ({ browser }) => {
  const first = await person(browser, { permissions: PERMISSIONS, displayName: 'Lubna Lock' });
  await first.context.close();
  for (const language of ['en', 'ur'] as const) {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    await display(context, { language });
    const page = await openPage(context);
    await page.goto('/sign-in');
    await page.locator('input[name="username"]').fill(first.username);
    await page.locator('input[name="password"]').fill(PASSWORD);
    await page.locator('button[type="submit"]').click();
    await expect(page.locator('.erp-shell main h1')).toBeVisible();
    for (const width of [360, 1280] as const) {
      await page.setViewportSize({ width, height: 900 });
      for (const screen of SCREENS) {
        await page.goto(screen.path);
        await expect(page.locator('main h1')).toBeVisible();
        await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
        await expect(page.locator('main').getByText(screen.ready).first()).toBeVisible();
        expect(await page.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
        await fitsWidth(page).catch((e: Error) => { throw new Error(`${screen.name} ${language} ${width}: ${e.message}`); });
        if (width === 1280) {
          await accessible(page, `${screen.name} ${language}`);
          await shot(page, `attendance-${screen.name}-${language}-${width}`);
        }
      }
    }

    noProblems(page);
    await context.close();
  }
});
