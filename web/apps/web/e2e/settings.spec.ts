// The settings centre, feature flags and sign-in page end to end (slice S1.15a Acceptance; FR-ADM-SET-01 to -04 and -06,
// FR-ADM-USR-20, FR-ADM-SEC-08, FR-KRN-SET-05, FR-KRN-LOC-01, NFR-11, NFR-12): real API host on the real access engine and real
// PostgreSQL. An administrator finds "idle timeout" by searching "idle", overrides it for the customer and for one company, resets
// it and restores an earlier value from the history; a change that needs approval waits for a second administrator; a changed
// minimum password length governs the next activation; the sign-in page shows the customer's logo, banner and terms in the
// chosen language and nothing else; every screen passes axe in English and Urdu and fits three widths.
import { expect, test, type Page } from '@playwright/test';
import { accessible, display, field, fitsWidth, freshContext, menu, noProblems, openPage, PASSWORD, person, server, shot, signOut } from './support';

test.describe.configure({ mode: 'serial' });

// Every customer-level value these scenarios change is removed again after each scenario, passed or failed, straight in the
// database through the control server, so a failure half-way cannot leak into the next scenario or another spec file (sign-in.spec.ts
// reads the idle time; the sign-in page reads the branding). Company-level values are made for a company of the scenario's own.
const CHANGED = ['iam.session.idle_minutes', 'iam.password.min_length', 'cfg.branding.product_name', 'cfg.branding.logo_file_id', 'cfg.branding.banner_en', 'cfg.branding.banner_ur', 'cfg.branding.terms_en', 'cfg.branding.terms_ur'];
test.afterEach(async () => {
  for (const key of CHANGED) await server.clearSetting(key);
});

const run = Math.random().toString(36).slice(2, 6);
const WIDTHS = [360, 768, 1280] as const;
const ADMIN = ['cfg.setting_values.read', 'cfg.setting_values.update', 'cfg.feature_flag_overrides.read', 'cfg.feature_flag_overrides.update', 'iam.users.read', 'iam.maker_checker.approve',
  'wf.workflow_tasks.read', 'org.companies.read', 'aud.history.view', 'cfg.saved_views.share', 'doc.files.read'];

/** A 1 by 1 pixel PNG. */
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64');

async function openSetting(page: Page, name: string): Promise<ReturnType<Page['getByRole']>> {
  await page.getByRole('button', { name: `Change the setting ${name}`, exact: true }).click();
  const drawer = page.getByRole('dialog', { name, exact: true });
  await expect(drawer).toBeVisible();
  await expect(drawer.getByRole('table', { name: 'Where the value comes from' })).toBeVisible();
  return drawer;
}

async function closeDrawer(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Close panel' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
}

async function change(drawer: ReturnType<Page['getByRole']>, label: RegExp, value: string, reason: string, button: string): Promise<void> {
  const input = drawer.getByRole('textbox', { name: label });
  await input.fill(value);
  await input.blur();
  await drawer.getByRole('textbox', { name: /^Reason/ }).fill(reason);
  await drawer.getByRole('button', { name: button }).click();
}

test('FR-ADM-SET-01 FR-ADM-SET-02 FR-ADM-SET-03 FR-ADM-SET-06 an administrator finds "idle timeout", overrides it for the customer and for one company, resets it and restores an earlier value', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Ayesha Admin' });
  const second = await server.company(`SC${run.toUpperCase()}`, `Second Co ${run}`, `دوسری کمپنی ${run}`);
  const page = admin.page;
  await page.reload();
  for (const entry of ['Settings', 'Feature flags', 'Sign-in page']) await expect(menu(page).getByRole('link', { name: entry })).toBeVisible();
  await menu(page).getByRole('link', { name: 'Settings', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Sign-in and security' })).toBeVisible();
  await accessible(page, 'settings centre');
  await shot(page, 'settings-centre-en-light-1280');

  // Search finds the setting by a word of its name; the value in force, its layer and who set it are shown.
  await page.getByRole('textbox', { name: 'Search settings' }).fill('idle');
  const row = page.locator('.erp-settings-row', { hasText: 'Idle timeout' });
  await expect(row).toHaveCount(1);
  await expect(page.locator('.erp-settings-row')).toHaveCount(1);
  await expect(row.getByText('Standard', { exact: true })).toBeVisible();

  // Override for the whole customer.
  let drawer = await openSetting(page, 'Idle timeout');
  await shot(page, 'settings-drawer-en-light-1280');
  await accessible(page, 'setting drawer');
  await change(drawer, /^Idle timeout/, '45', 'Slow approvals', 'Override for the whole customer');
  await expect(page.getByText('The setting was saved.').first()).toBeVisible();
  await expect(drawer.getByRole('table', { name: 'Where the value comes from' }).getByText('Customer', { exact: true })).toBeVisible();
  await closeDrawer(page);
  await expect(row.getByText('45', { exact: true })).toBeVisible(); // exact: the row also shows when it changed ("at 03:45 pm")
  await expect(row.getByText('Customer', { exact: true })).toBeVisible();
  await expect(row.getByText(/by Ayesha Admin/)).toBeVisible();

  // A company override wins for that company only (the time zone allows a value per company; the idle timeout does not).
  await page.getByRole('textbox', { name: 'Search settings' }).fill('zone');
  const zone = page.locator('.erp-settings-row', { hasText: 'Time zone' });
  await expect(zone).toHaveCount(1);
  await page.getByRole('button', { name: /Show values for/ }).click();
  await page.getByRole('option', { name: `Second Co ${run}` }).click();
  const inherited = (await zone.locator('bdi').first().innerText()).trim(); // whatever the customer value is now (other scenarios may have set it)
  expect(inherited).not.toBe('Asia/Dubai');
  drawer = await openSetting(page, 'Time zone');
  await change(drawer, /^Time zone/, 'Asia/Dubai', 'Branch in the Gulf', 'Override for this company');
  await expect(page.getByText('The setting was saved.').first()).toBeVisible();
  await closeDrawer(page);
  await expect(zone.getByText('Asia/Dubai', { exact: true })).toBeVisible();
  await expect(zone.getByText('Company', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: /Show values for/ }).click();
  await page.getByRole('option', { name: 'The whole customer' }).click();
  await expect(zone.getByText(inherited, { exact: true })).toBeVisible(); // the customer value is unchanged

  // Reset the company value to the inherited one.
  await page.getByRole('button', { name: /Show values for/ }).click();
  await page.getByRole('option', { name: `Second Co ${run}` }).click();
  drawer = await openSetting(page, 'Time zone');
  await drawer.getByRole('textbox', { name: /^Reason/ }).fill('Back to the customer value');
  await drawer.getByRole('button', { name: 'Reset to inherited' }).click();
  await expect(page.getByText('The setting was saved.').first()).toBeVisible();
  await closeDrawer(page);
  await expect(zone.getByText(inherited, { exact: true })).toBeVisible();
  await expect(zone.getByText('Company', { exact: true })).toHaveCount(0);

  // The history lists the changes; restoring the first one brings the standard value back.
  await page.getByRole('button', { name: /Show values for/ }).click();
  await page.getByRole('option', { name: 'The whole customer' }).click();
  await page.getByRole('textbox', { name: 'Search settings' }).fill('idle');
  drawer = await openSetting(page, 'Idle timeout');
  const history = drawer.getByRole('list', { name: 'History' });
  await expect(history.getByText('Slow approvals')).toBeVisible();
  await history.locator('li', { hasText: 'Slow approvals' }).getByRole('button', { name: 'Restore the earlier value' }).click();
  const confirm = page.getByRole('alertdialog', { name: 'Restore the earlier value' });
  await confirm.getByRole('textbox', { name: /^Reason/ }).fill('Wrong change');
  await confirm.getByRole('button', { name: 'Restore the earlier value' }).click();
  await expect(page.getByText('The earlier value was restored.').first()).toBeVisible();
  await closeDrawer(page);
  await expect(row.getByText('30', { exact: true })).toBeVisible(); // exact: the row also shows when it changed ("at 03:30 pm")
  expect(second.id).toBeTruthy();
  noProblems(page);
  await signOut(page);
  await admin.context.close();
});

test('FR-ADM-USR-20 FR-ADM-SET-02 a changed minimum password length governs the next activation', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Bilal Admin' });
  const page = admin.page;
  await page.goto('/settings?q=password');
  const drawer = await openSetting(page, 'Minimum password length');
  await change(drawer, /^Minimum password length/, '14', 'Policy review', 'Override for the whole customer');
  await expect(page.getByText('The setting was saved.').first()).toBeVisible();
  await closeDrawer(page);
  try {
    const invited = await server.newUser({ displayName: 'Hamza Newcomer' });
    const context = await freshContext(browser);
    const fresh = await openPage(context);
    const { link } = await server.mail('identity.invitation', invited.username);
    await fresh.goto(link!);
    await field(fresh, 'New password').fill('Brave-Lan-42'); // 12 characters
    await field(fresh, 'Repeat the new password').fill('Brave-Lan-42');
    await fresh.getByRole('button', { name: 'Activate account' }).click();
    await expect(fresh.getByText('The password is too short.')).toBeVisible(); // 14 is now the minimum
    await field(fresh, 'New password').fill(PASSWORD); // 17 characters
    await field(fresh, 'Repeat the new password').fill(PASSWORD);
    await fresh.getByRole('button', { name: 'Activate account' }).click();
    await expect(fresh.getByRole('heading', { name: 'Waiting for you', level: 1 }).or(fresh.getByRole('heading', { name: 'Protect your account' }))).toBeVisible();
    await context.close();
  } finally {
    const reset = await openSetting(page, 'Minimum password length');
    await reset.getByRole('textbox', { name: /^Reason/ }).fill('Back to standard');
    await reset.getByRole('button', { name: 'Reset to inherited' }).click();
    await expect(page.getByText('The setting was saved.').first()).toBeVisible();
  }
  await admin.context.close();
});

test('FR-ADM-SET-04 FR-KRN-IAM-14 a change held for a second administrator shows as waiting until it is approved', async ({ browser }) => {
  const checker = await person(browser, { permissions: [...ADMIN], displayName: 'Nadia Checker' });
  const held = await server.heldSetting(42, 'Longer drafts for the audit');
  const page = checker.page;
  await page.goto('/settings?q=drafts');
  // Other scenarios register settings of the same name; the one with this request is the one that waits.
  const row = page.locator('.erp-settings-row', { hasText: 'Days to keep drafts' }).filter({ hasText: 'Waiting for a second administrator' }).first();
  await expect(row).toBeVisible();
  await row.getByRole('button', { name: /^Change the setting Days to keep drafts$/ }).click();
  const drawer = page.getByRole('dialog', { name: 'Days to keep drafts', exact: true });
  await expect(drawer.getByRole('table', { name: 'Where the value comes from' })).toBeVisible();
  await expect(drawer.getByText(/must be approved by a second administrator/)).toBeVisible();
  await accessible(page, 'a setting waiting for approval');
  expect(await server.settingValue(held.key)).toBeNull();
  await closeDrawer(page);
  noProblems(page);
  await signOut(page);
  await checker.context.close();
});

test('FR-ADM-SEC-08 the sign-in page shows the customer\'s logo, banner and terms in the chosen language and nothing else', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Ayesha Admin' });
  const page = admin.page;
  try {
    await page.goto('/settings/sign-in-page');
    await expect(page.getByRole('heading', { name: 'Sign-in page', level: 1 })).toBeVisible();
    await field(page, 'Product name').fill(`Acme People ${run}`);
    await page.locator('input[type="file"]').setInputFiles({ name: 'logo.png', mimeType: 'image/png', buffer: PNG });
    await expect(page.getByText('A new logo is chosen; save to use it.').or(page.getByRole('img', { name: 'The logo shown on the sign-in page' }))).toBeVisible();
    await page.getByRole('textbox', { name: 'Login banner (English)' }).fill('Welcome to Acme');
    await page.getByRole('textbox', { name: 'Login banner (Urdu)' }).fill('ایکمی میں خوش آمدید');
    await page.getByRole('textbox', { name: 'Terms notice (English)' }).fill('Use of this system is monitored.');
    await page.getByRole('textbox', { name: 'Terms notice (Urdu)' }).fill('اس نظام کا استعمال زیر نگرانی ہے۔');
    await page.getByRole('textbox', { name: /^Reason/ }).fill('Brand the sign-in page');
    await accessible(page, 'sign-in page screen');
    await shot(page, 'sign-in-page-settings-en-light-1280');
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByText('The sign-in page was saved.')).toBeVisible();

    // Anyone, signed out, sees the branding and nothing else; the Urdu texts follow the chosen language.
    for (const language of ['en', 'ur'] as const) {
      const context = await freshContext(browser, language);
      const visitor = await openPage(context);
      await visitor.goto('/sign-in');
      await expect(visitor.locator('.erp-auth-product')).toHaveText(`Acme People ${run}`);
      await expect(visitor.getByText(language === 'en' ? 'Welcome to Acme' : 'ایکمی میں خوش آمدید')).toBeVisible();
      await expect(visitor.getByText(language === 'en' ? 'Use of this system is monitored.' : 'اس نظام کا استعمال زیر نگرانی ہے۔')).toBeVisible();
      const logo = visitor.locator('img.erp-auth-logo');
      await expect(logo).toBeVisible();
      expect(await logo.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);
      await expect(visitor).toHaveTitle(new RegExp(`Acme People ${run}`)); // the title follows the branding once it has loaded
      expect(await visitor.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
      for (const width of WIDTHS) {
        await visitor.setViewportSize({ width, height: 900 });
        await fitsWidth(visitor);
        await shot(visitor, `sign-in-branded-${language}-light-${width}`);
      }

      await accessible(visitor, `branded sign-in ${language}`);
      await context.close();
    }
  } finally {
    await page.goto('/settings/sign-in-page');
    await field(page, 'Product name').fill('ERP');
    await page.getByRole('button', { name: 'Remove the logo' }).click();
    for (const label of ['Login banner (English)', 'Login banner (Urdu)', 'Terms notice (English)', 'Terms notice (Urdu)']) await page.getByRole('textbox', { name: label }).fill('');
    await page.getByRole('textbox', { name: /^Reason/ }).fill('Restore the standard page');
    await page.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByText('The sign-in page was saved.')).toBeVisible();
  }
  await admin.context.close();
});

test('FR-KRN-SET-05 FR-KRN-LOC-01 NFR-11 NFR-12 the settings screens pass axe in Urdu and fit three widths', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Urdu Admin' });
  const page = admin.page;
  await display(admin.context, { language: 'ur' });
  for (const [name, path] of [['settings', '/settings'], ['flags', '/settings/flags'], ['sign-in-page', '/settings/sign-in-page']] as const) {
    for (const width of WIDTHS) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(path);
      await expect(page.locator('main h1')).toBeVisible();
      await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
      expect(await page.evaluate(() => document.documentElement.dir)).toBe('rtl');
      await fitsWidth(page);
      if (width === 1280) await accessible(page, `${name} ur`);
      await shot(page, `${name}-ur-light-${width}`);
    }
  }

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/settings');
  await page.getByRole('button', { name: /^ترتیب .* تبدیل کریں$/ }).first().click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await accessible(page, 'settings drawer ur');
  await shot(page, 'settings-drawer-ur-light-1280');
  noProblems(page);
  await admin.context.close();
});
