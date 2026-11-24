// The export centre end to end (slice S1.16b Acceptance; FR-ADM-DAT-04, FR-ADM-NAV-02, FR-KRN-ENT-04, FR-KRN-LOC-01, NFR-11, NFR-12): real API host on the
// real access engine and real PostgreSQL. A person exports the branch list as it is (search and sort), sees the export in the centre, and the
// screens work in English and Urdu at three widths. The file itself, the second person's approval, the access log and the locked state are proven by the
// server tests: this host runs no job worker, so the export stays "Waiting to run" here.
import { expect, test } from '@playwright/test';
import { accessible, display, menu, noProblems, openPage, PASSWORD, person, server, shot } from './support';

test.describe.configure({ mode: 'serial' });

const run = Math.random().toString(36).slice(2, 6).toUpperCase();
const EXPORTER = ['org.branches.read', 'itg.exports.run', 'org.companies.read'];

test('FR-ADM-DAT-04 FR-ADM-NAV-02 the branch list is exported as it is and the export appears in the centre', async ({ browser }) => {
  await server.branches(`X${run}`, 6);
  const user = await person(browser, { permissions: EXPORTER, displayName: 'Ayesha Exporter' });
  const page = user.page;
  await page.goto('/organization/branches');
  await expect(page.getByRole('heading', { name: 'Branches', level: 1 })).toBeVisible();
  await page.getByRole('searchbox', { name: 'Search' }).fill(`X${run}`);
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await page.getByRole('button', { name: 'Export', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: /Export Branches/ });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Make the file' })).toBeEnabled();
  await accessible(page, 'export dialog');
  await shot(page, 'exports-dialog-en-light-1280');
  await dialog.getByRole('button', { name: 'Make the file' }).click();
  await expect(page.getByText(/The export was asked for/)).toBeVisible();

  await menu(page).getByRole('link', { name: 'Exports' }).click();
  await expect(page.getByRole('heading', { name: 'Exports', level: 1 })).toBeVisible();
  const table = page.getByRole('table', { name: 'Exports' });
  await expect(table.getByText('Waiting to run')).toBeVisible();
  await accessible(page, 'export centre');
  await shot(page, 'exports-centre-en-light-1280');
  noProblems(page);
  await user.context.close();
});

test('FR-KRN-LOC-01 NFR-11 NFR-12 the export centre in English and Urdu, light and dark, at 360, 768 and 1280 pixels', async ({ browser }) => {
  const viewer = await person(browser, { permissions: EXPORTER, displayName: 'Nadia Viewer' });
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
      for (const width of [360, 768, 1280] as const) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto('/admin/exports');
        await expect(page.locator('main h1')).toBeVisible();
        await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
        expect(await page.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
        expect(overflow, `centre ${language} ${scheme} ${width}`).toBeLessThanOrEqual(0);
        if (width === 1280) await accessible(page, `centre ${language} ${scheme}`);
        if (width === 1280 && scheme === 'light') await shot(page, `exports-centre-${language}-${scheme}-${width}`);
      }
      noProblems(page);
      await context.close();
    }
  }
});
