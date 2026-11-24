// The import centre end to end (slice S1.16a Acceptance; FR-KRN-INT-03, FR-KRN-INT-04, FR-ADM-DAT-01, FR-ADM-INT-04, FR-KRN-LOC-01, NFR-11, NFR-12):
// real API host on the real access engine and real PostgreSQL. An administrator downloads the template of an import type, uploads it and
// starts the check; an unsupported file is refused in plain words; the screens work in English and Urdu. The check and commit jobs (500 users,
// 50,000 rows, restart, rollback) are proven by the server tests: this host runs no job worker.
import { readFileSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { accessible, display, menu, noProblems, openPage, PASSWORD, person, shot } from './support';

test.describe.configure({ mode: 'serial' });

const ADMIN = ['itg.imports.manage', 'itg.imports.design', 'itg.import_batches.read', 'org.locations.create', 'org.locations.update', 'org.locations.read', 'org.companies.read',
  'org.branches.read', 'doc.files.read'];

async function choose(page: Page, label: string, option: string | RegExp): Promise<void> {
  await page.getByRole('button', { name: new RegExp(`\\s${label}$`) }).first().click();
  await page.getByRole('option', { name: option }).click();
}

test('FR-KRN-INT-03 FR-ADM-DAT-01 the template is downloaded and uploaded and the check starts; an unsupported file is refused in plain words', async ({ browser }, testInfo) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Ayesha Admin' });
  const page = admin.page;
  await page.reload();
  await menu(page).getByRole('link', { name: 'Imports' }).click();
  await expect(page.getByRole('heading', { name: 'Imports', level: 1 })).toBeVisible();
  await accessible(page, 'import centre');
  await shot(page, 'imports-centre-en-light-1280');

  await choose(page, 'What to import', /Locations/);
  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download template (CSV)' }).click()]);
  const path = testInfo.outputPath('locations-template.csv');
  await download.saveAs(path);
  expect(readFileSync(path, 'utf8')).toContain('LHR-HQ');

  // An unsupported file is refused before anything is read.
  await page.getByLabel(/^File to import/).setInputFiles({ name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('hello') });
  await page.getByRole('button', { name: 'Upload and continue' }).click();
  await expect(page.getByText(/CSV|Excel/).first()).toBeVisible();

  // The template as it is: its example row names a company that does not exist, so the check finds a problem and saves nothing.
  await page.getByLabel(/^File to import/).setInputFiles(path);
  await page.getByRole('button', { name: 'Upload and continue' }).click();
  await expect(page.getByRole('heading', { name: /import|Locations|locations/i }).first()).toBeVisible();
  await page.getByRole('button', { name: /^Check the file$/ }).click();
  // The browser tests run no job worker (its jobs are proven by the server tests): the screen shows the check as running, with progress.
  await expect(page.getByRole('progressbar', { name: 'Progress of the import' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save the records' })).toBeDisabled();
  await accessible(page, 'import batch while checking');
  await shot(page, 'imports-batch-en-light-1280');
  noProblems(page);
  await admin.context.close();
});

test('FR-KRN-LOC-01 NFR-11 NFR-12 the import centre and the template designer in English and Urdu, light and dark, at 360, 768 and 1280 pixels', async ({ browser }) => {
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
      for (const [name, path] of [['centre', '/admin/imports'], ['designer', '/admin/imports/templates']] as const) {
        for (const width of [360, 768, 1280] as const) {
          await page.setViewportSize({ width, height: 900 });
          await page.goto(path);
          await expect(page.locator('main h1')).toBeVisible();
          await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
          expect(await page.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
          const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
          expect(overflow, `${name} ${language} ${scheme} ${width}`).toBeLessThanOrEqual(0);
          if (width === 1280) await accessible(page, `${name} ${language} ${scheme}`);
          if (width === 1280 && scheme === 'light') await shot(page, `imports-${name}-${language}-${scheme}-${width}`);
        }
      }
      noProblems(page);
      await context.close();
    }
  }
});
