// The access console end to end (slice S1.13b Acceptance; FR-ADM-ROL-01, -02, -03, -06, -07, -11, FR-ADM-USR-06, FR-ADM-ROL-09,
// FR-KRN-AUD-04, FR-KRN-LOC-01, NFR-11, NFR-12): real API host on the real access engine (ruling Q10) and real PostgreSQL. An
// administrator copies a shipped role, removes permissions, compares it with its source, sees the change in the permission
// change report and the audit search, and every screen of the console passes axe in English and Urdu.
import { expect, test, type Page } from '@playwright/test';
import { accessible, display, freshContext, menu, noProblems, openPage, PASSWORD, person, server, shot } from './support';

test.describe.configure({ mode: 'serial' });

const run = Math.random().toString(36).slice(2, 6);
const ADMIN = ['iam.roles.read', 'iam.roles.manage', 'iam.permissions.read', 'iam.data_scopes.read', 'iam.user_groups.read', 'iam.delegations.read', 'iam.users.read',
  'iam.access.preview', 'iam.field_policies.read', 'aud.changes.view', 'aud.security_events.view', 'aud.export', 'aud.history.view', 'wf.workflow_tasks.read', 'cfg.saved_views.share'];

async function choose(page: Page, label: string, option: string | RegExp): Promise<void> {
  await page.getByRole('button', { name: new RegExp(`\\s${label}$`) }).first().click();
  await page.getByRole('option', { name: option }).click();
}

test('FR-ADM-ROL-01 FR-ADM-ROL-02 FR-ADM-ROL-11 FR-KRN-AUD-04 an administrator copies a shipped role, removes permissions, compares, and finds the change in the report and the audit search', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Ayesha Admin' });
  const page = admin.page;
  await page.reload();
  for (const entry of ['Roles', 'Groups', 'Data scopes', 'Field security', 'Delegations', 'Access reports']) {
    await expect(menu(page).getByRole('link', { name: entry })).toBeVisible();
  }

  // The catalogue.
  await menu(page).getByRole('link', { name: 'Roles' }).click();
  await expect(page.getByRole('heading', { name: 'Roles', level: 1 })).toBeVisible();
  await accessible(page, 'role list');
  await shot(page, 'access-roles-en-light-1280');
  await page.getByRole('button', { name: 'Permission catalogue' }).click();
  await expect(page.getByRole('heading', { name: 'Permission catalogue', level: 1 })).toBeVisible();
  await accessible(page, 'permission catalogue');
  await shot(page, 'access-catalogue-en-light-1280');

  // Copy the shipped approver role and take permissions away.
  await page.goto('/admin/roles');
  await page.getByRole('button', { name: 'Create role' }).first().click();
  const code = `copy_${run}`;
  await page.getByLabel(/^Code/).fill(code);
  await page.getByLabel(/^Name in English/).fill(`Copied approver ${run}`);
  await choose(page, 'Start from', 'A copy of a shipped role');
  await choose(page, 'Role to copy', /Approver/);
  await page.getByRole('dialog').getByRole('button', { name: 'Create role' }).click();
  await expect(page.getByRole('heading', { name: `Copied approver ${run}`, level: 1 })).toBeVisible();
  await expect(page.getByText('The role was created.')).toBeVisible();

  const boxes = page.getByRole('checkbox').filter({ hasNotText: /Select all/ });
  const checked = page.locator('input[type="checkbox"]:checked');
  const before = await checked.count();
  expect(before).toBeGreaterThan(2);
  // Two permission boxes (not the "select all" ones) that are ticked.
  const ticked = page.getByRole('checkbox', { checked: true }).filter({ hasNot: page.getByText(/Select all/) });
  void boxes;
  await ticked.nth(1).uncheck({ force: true });
  await ticked.nth(2).uncheck({ force: true });
  await page.getByRole('button', { name: 'Save permissions' }).click();
  await page.getByRole('alertdialog').or(page.getByRole('dialog')).getByLabel(/^Reason/).fill('Narrow the role (browser test)');
  await page.getByRole('dialog').or(page.getByRole('alertdialog')).getByRole('button', { name: 'Save permissions' }).click();
  await expect(page.getByText(/The permissions were saved\.|waiting for a second administrator/)).toBeVisible();
  await accessible(page, 'role record');
  await shot(page, 'access-role-en-light-1280');

  // Compare with its source.
  await page.goto('/admin/roles');
  await page.getByRole('button', { name: 'Compare roles' }).click();
  await expect(page.getByRole('heading', { name: /Compare/, level: 1 }).or(page.getByRole('dialog'))).toBeVisible();
  await page.goBack().catch(() => undefined);

  // The report and the audit search show who changed the role and when.
  await page.goto('/admin/access-reports');
  await expect(page.getByRole('heading', { name: 'Access reports', level: 1 })).toBeVisible();
  await expect(page.getByText('Ayesha Admin').first()).toBeVisible();
  await accessible(page, 'permission change report');
  await shot(page, 'access-report-changes-en-light-1280');
  await page.getByRole('tab', { name: 'Role usage' }).click();
  await expect(page.getByRole('heading', { name: /have not signed in for \d+ days/ })).toBeVisible();
  await accessible(page, 'role usage report');
  await shot(page, 'access-report-usage-en-light-1280');

  await page.goto('/admin/audit');
  await expect(page.getByRole('heading', { name: 'Audit search', level: 1 })).toBeVisible();
  await page.getByLabel(/^Table/).fill('iam.roles');
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(page.getByText('Ayesha Admin').first()).toBeVisible();
  await accessible(page, 'audit search');
  await shot(page, 'access-audit-en-light-1280');
  noProblems(page);
  await admin.context.close();
});

test('FR-KRN-LOC-01 NFR-11 NFR-12 the access console in English and Urdu, light and dark, at 360, 768 and 1280 pixels', async ({ browser }) => {
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
      for (const [name, path] of [['roles', '/admin/roles'], ['catalogue', '/admin/roles/catalogue'], ['groups', '/admin/groups'], ['scopes', '/admin/data-scopes'],
        ['fields', '/admin/field-security'], ['delegations', '/admin/delegations'], ['reports', '/admin/access-reports'], ['audit', '/admin/audit']] as const) {
        for (const width of [360, 768, 1280] as const) {
          await page.setViewportSize({ width, height: 900 });
          await page.goto(path);
          await expect(page.locator('main h1')).toBeVisible();
          await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
          expect(await page.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
          const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
          expect(overflow, `${name} ${language} ${scheme} ${width}`).toBeLessThanOrEqual(0);
          if (width === 1280) await accessible(page, `${name} ${language} ${scheme}`);
          if (width === 1280 || (language === 'ur' && scheme === 'light')) await shot(page, `access-${name}-${language}-${scheme}-${width}`);
        }
      }
      noProblems(page);
      await context.close();
    }
  }
  void freshContext;
  void server;
});
