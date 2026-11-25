// The operations screens end to end (slice S1.16c Acceptance; FR-ADM-OPS-01, FR-ADM-OPS-04, FR-ADM-OPS-05, FR-ADM-DAT-08, FR-ADM-OPS-07, FR-KRN-ENT-05,
// FR-KRN-LOC-01, NFR-11, NFR-12): real API host on the real access engine and real PostgreSQL. An administrator opens the job centre (jobs, runs,
// notification log, health) and the licence page; a person without the licence permission has no licence entry and the page refuses; every screen
// works in English and Urdu, light and dark, at 360, 768 and 1280 pixels. This host runs no job worker, so health says the worker was not seen.
import { expect, test } from '@playwright/test';
import { accessible, display, menu, noProblems, openPage, PASSWORD, person, shot } from './support';

test.describe.configure({ mode: 'serial' });

const ADMIN = ['job.definitions.read', 'job.definitions.update', 'job.runs.read', 'job.runs.create', 'job.runs.update', 'ntf.outbound_messages.read', 'ntf.outbound_messages.resend',
  'cfg.licence.read', 'ntf.subscriptions.read'];

test('FR-ADM-OPS-01 FR-ADM-OPS-04 FR-ADM-OPS-05 FR-ADM-DAT-08 the job centre shows jobs, runs, messages and health, and the health page is honest about the worker and the backup', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Ayesha Admin' });
  const page = admin.page;
  await page.reload();
  await menu(page).getByRole('link', { name: 'Background jobs' }).click();
  await expect(page.getByRole('heading', { name: 'Background jobs', level: 1 })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'Schedule' })).toBeVisible();
  await accessible(page, 'job centre jobs');
  await shot(page, 'ops-jobs-en-light-1280');

  await page.getByRole('tab', { name: 'Runs' }).click();
  await expect(page).toHaveURL(/\/admin\/jobs\/runs$/);
  await accessible(page, 'job centre runs');

  await page.getByRole('tab', { name: 'Notification log' }).click();
  await expect(page).toHaveURL(/\/admin\/jobs\/messages$/);
  await expect(page.getByRole('heading', { name: 'Messages' })).toBeVisible();
  await accessible(page, 'notification log');

  await page.getByRole('tab', { name: 'System health' }).click();
  await expect(page).toHaveURL(/\/admin\/jobs\/health$/);
  await expect(page.getByText('Not seen recently').first()).toBeVisible();
  await expect(page.getByText('Database size')).toBeVisible();
  await expect(page.getByText('No backup has been recorded yet. Ask your provider.')).toBeVisible();
  await accessible(page, 'system health');
  await shot(page, 'ops-health-en-light-1280');
  noProblems(page);
  await admin.context.close();
});

test('FR-ADM-OPS-07 FR-KRN-ENT-05 the licence page shows the plan, state, limits and usage, and a person without the permission has no entry and is refused', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Ayesha Admin' });
  const page = admin.page;
  await page.reload();
  await menu(page).getByRole('link', { name: 'Licence' }).click();
  await expect(page.getByRole('heading', { name: 'Licence and usage', level: 1 })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Licence state' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Limits and use' })).toBeVisible();
  await accessible(page, 'licence page');
  await shot(page, 'ops-licence-en-light-1280');
  noProblems(page);
  await admin.context.close();

  const reader = await person(browser, { permissions: ['job.runs.read'], displayName: 'Omar Reader' });
  await reader.page.reload();
  await expect(menu(reader.page).getByRole('link', { name: 'Licence' })).toHaveCount(0);
  await reader.page.goto('/settings/licence');
  await expect(reader.page.getByRole('heading', { name: 'You cannot open this page', level: 1 })).toBeVisible();
  await expect(reader.page.getByText(/of \d+/)).toHaveCount(0);
  await reader.context.close();
});

test('FR-KRN-LOC-01 NFR-11 NFR-12 the operations screens in English and Urdu, light and dark, at 360, 768 and 1280 pixels', async ({ browser }) => {
  const viewer = await person(browser, { permissions: ADMIN, displayName: 'Nadia Viewer' });
  await viewer.context.close();
  for (const language of ['en', 'ur'] as const) {
    for (const scheme of ['light', 'dark'] as const) {
      const context = await browser.newContext({ colorScheme: scheme, viewport: { width: 1280, height: 900 } });
      await display(context, { language });
      const page = await openPage(context);
      await page.goto('/sign-in');
      await page.locator('input[name="username"]').fill(viewer.username);
      await page.locator('input[name="password"]').fill(PASSWORD);
      await page.locator('button[type="submit"]').click();
      await expect(page.locator('.erp-shell main h1')).toBeVisible();
      for (const [name, path] of [['jobs', '/admin/jobs'], ['runs', '/admin/jobs/runs'], ['messages', '/admin/jobs/messages'], ['health', '/admin/jobs/health'], ['licence', '/settings/licence']] as const) {
        for (const width of [360, 768, 1280] as const) {
          await page.setViewportSize({ width, height: 900 });
          await page.goto(path);
          await expect(page.locator('main h1')).toBeVisible();
          await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
          expect(await page.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
          const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
          expect(overflow, `${name} ${language} ${scheme} ${width}`).toBeLessThanOrEqual(0);
          if (width === 1280) await accessible(page, `${name} ${language} ${scheme}`);
          if (width === 1280 && scheme === 'light') await shot(page, `ops-${name}-${language}-${scheme}-${width}`);
        }
      }
      noProblems(page);
      await context.close();
    }
  }
});
