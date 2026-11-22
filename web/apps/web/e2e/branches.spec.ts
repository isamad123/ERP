// Branches end to end: the first list and form screen of the engine (slice S1.12c, Acceptance; FR-KRN-ORG-02, FR-ADM-NAV-02,
// FR-ADM-UXA-02 to -05, FR-KRN-AUD-07, FR-ADM-OPS-03, FR-KRN-ORG-08, FR-KRN-LOC-01, NFR-01, NFR-11, NFR-12). Real API host,
// real PostgreSQL; the control server adds branches and companies in bulk and writes a label override as later admin
// screens will. The scenarios run in order and remove the label override they add.
import { expect, test, type Locator, type Page } from '@playwright/test';
import { accessible, display, field, fitsWidth, freshContext, menu, noProblems, openPage, PASSWORD, person, server, shot, signOut } from './support';

test.describe.configure({ mode: 'serial' });

const run = Math.random().toString(36).slice(2, 6).toUpperCase();
const WIDTHS = [360, 768, 1280] as const;
const EDITOR = ['org.branches.read', 'org.branches.create', 'org.branches.update', 'org.branches.delete', 'aud.history.view', 'cfg.saved_views.share'];
const LIST = '/organization/branches';

function grid(page: Page): Locator {
  return page.getByRole('grid', { name: 'Branches' });
}

/** A choice in a select: its button is named by the value shown and then the label ("Any Active"). */
async function choose(page: Page, label: string, option: string | RegExp): Promise<void> {
  await page.getByRole('button', { name: new RegExp(`\\s${label}$`) }).first().click();
  await page.getByRole('option', { name: option }).click();
}

async function search(page: Page, text: string): Promise<void> {
  await page.getByRole('searchbox', { name: 'Search' }).fill(text);
  await page.getByRole('button', { name: 'Search', exact: true }).click();
}

test('FR-ADM-NAV-02 NFR-01 the branch list: 100 rows within a second, server sort and search, filters, columns and a view shared with a role', async ({ browser }) => {
  await server.branches(`P${run}`, 120);
  const admin = await person(browser, { permissions: EDITOR, displayName: 'Ayesha Khan' });
  const page = admin.page;
  await expect(menu(page).getByRole('link', { name: 'Branches' })).toBeVisible();

  // The screen's code is loaded once (the new-branch form); the first page of the list is then measured from the click.
  await page.goto(`${LIST}/new`);
  await expect(page.getByRole('heading', { name: 'New Branch', level: 1 })).toBeVisible();
  const started = Date.now();
  await menu(page).getByRole('link', { name: 'Branches' }).click();
  await expect(page.getByText('100 rows shown; more are available')).toBeVisible();
  await expect(grid(page).getByRole('row').nth(1)).toBeVisible();
  const elapsed = Date.now() - started;
  console.log(`first page of 100 branch rows: ${elapsed} ms`);
  expect(elapsed).toBeLessThan(1000);
  expect(await grid(page).getAttribute('aria-rowcount')).toBe('101');
  await accessible(page, 'branch list');

  // Load more: the next keyset page.
  await page.getByRole('button', { name: 'Load more' }).click();
  await expect(page.getByText(/^\d+ rows$/)).toBeVisible();

  // Sort on the server: descending by code puts the highest code first.
  await page.getByRole('button', { name: 'Sort by Code' }).click();
  await page.getByRole('button', { name: 'Sort by Code' }).click();
  await expect(grid(page).getByRole('columnheader', { name: /Code/ })).toHaveAttribute('aria-sort', 'descending');
  await search(page, `P${run}-11`);
  await expect(grid(page).getByRole('row')).toHaveCount(1 + 10); // P-110 to P-119, plus the header
  await expect(grid(page).getByRole('row').nth(1)).toContainText(`P${run}-119`);
  // Search finds the Urdu name too.
  await search(page, `برانچ P${run} 5`);
  await expect(grid(page).getByRole('row').nth(1)).toContainText(`P${run}-`);
  await search(page, `P${run}-0`);
  await choose(page, 'Active', 'Inactive');
  await expect(grid(page).getByRole('row').nth(1)).toContainText('Inactive');
  await expect(grid(page).getByRole('gridcell').filter({ hasText: /^Active$/ })).toHaveCount(0);

  // Columns: hide the company, then save the list as a view shared with the Business User role.
  await page.getByRole('button', { name: 'Columns' }).click();
  const chooser = page.getByRole('dialog', { name: 'Choose columns' });
  await chooser.getByText('Company', { exact: true }).click();
  await chooser.getByRole('button', { name: 'Move Active up' }).click(); // past the hidden Company
  await chooser.getByRole('button', { name: 'Move Active up' }).click(); // and past Name
  await accessible(page, 'columns chooser');
  await chooser.getByRole('button', { name: 'Done' }).click();
  await expect(grid(page).getByRole('columnheader', { name: /Company/ })).toHaveCount(0);
  const headers = await grid(page).getByRole('columnheader').allInnerTexts();
  expect(headers.map((h) => h.trim()).filter(Boolean)).toEqual(['Code', 'Active', 'Name']);

  await page.getByRole('button', { name: 'Save view' }).click();
  const save = page.getByRole('dialog', { name: 'Save this view' });
  await save.getByRole('textbox', { name: /Name of the view/ }).fill(`Inactive ${run}`);
  await save.getByText('Share with a role').click();
  await save.getByRole('button', { name: /\sRole$/ }).click();
  await page.getByRole('option', { name: /^Business User/ }).click();
  // Check the dialog once the role list has closed (while it fades out it lies over the dialog's buttons).
  await expect(page.getByRole('listbox')).toHaveCount(0);
  await expect(save.getByRole('button', { name: 'Save view' })).toBeEnabled();
  await accessible(page, 'save view dialog');
  await save.getByRole('button', { name: 'Save view' }).click();
  await expect(page.getByRole('button', { name: new RegExp(`Inactive ${run} \\(shared with Business User\\) View$`) })).toBeVisible();

  // Back to the standard view and on to the saved one again: search, filter, sort and columns come back.
  await choose(page, 'View', 'Standard view');
  await expect(grid(page).getByRole('columnheader', { name: /Company/ })).toBeVisible();
  await choose(page, 'View', new RegExp(`Inactive ${run}`));
  await expect(grid(page).getByRole('columnheader', { name: /Company/ })).toHaveCount(0);
  await expect(grid(page).getByRole('row').nth(1)).toContainText('Inactive');

  // Another person holding Business User finds the shared view and uses it, but cannot delete it.
  const colleague = await person(browser);
  await colleague.page.goto(LIST);
  await expect(grid(colleague.page)).toBeVisible();
  await choose(colleague.page, 'View', new RegExp(`Inactive ${run}`));
  await expect(grid(colleague.page).getByRole('columnheader', { name: /Company/ })).toHaveCount(0);
  await expect(colleague.page.getByRole('button', { name: 'Delete view' })).toHaveCount(0);
  await expect(colleague.page.getByRole('button', { name: /New Branch/ })).toHaveCount(0); // no create permission
  noProblems(colleague.page);
  await colleague.context.close();

  // The owner deletes it.
  await page.getByRole('button', { name: 'Delete view' }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Delete view' }).click();
  await expect(page.getByRole('button', { name: /^Standard view View$/ })).toBeVisible();
  noProblems(page);
  await signOut(page);
  await admin.context.close();
});

test('FR-ADM-UXA-02 to -05 FR-KRN-AUD-07 create, check, edit, stale version, history, unsaved changes, and the last branch of a company', async ({ browser }) => {
  const admin = await person(browser, { permissions: EDITOR, displayName: 'Ayesha Khan' });
  const page = admin.page;
  await page.goto(LIST);
  await page.getByRole('button', { name: 'New Branch' }).first().click();
  await expect(page.getByRole('heading', { name: 'New Branch', level: 1 })).toBeVisible();

  // Checks on save and when a field is left, in the person's language, next to the fields and in a summary.
  await page.getByRole('button', { name: 'Create' }).click();
  await expect(page.getByRole('alert')).toContainText('Check the fields marked below.');
  await expect(page.getByText('Fill in this field.').first()).toBeVisible();
  const codeError = page.locator('#field-code').getByText('Fill in this field.');
  await expect(codeError).toBeVisible();
  await field(page, 'Code').fill('K');
  await expect(codeError).toHaveCount(0); // typing clears the message
  await field(page, 'Code').fill('');
  await field(page, 'Code').blur();
  await expect(codeError).toBeVisible(); // leaving the field checks it again
  await expect(field(page, 'Code')).toHaveAttribute('maxlength', '40'); // the input itself keeps codes within 40 characters
  await accessible(page, 'form with errors');
  await shot(page, 'branch-form-errors-en-light-1280');

  const code = `KHI-${run}`;
  await field(page, 'Code').fill(` ${code} `);
  await field(page, 'In English').fill('Karachi');
  await field(page, 'In Urdu').fill('کراچی');
  await page.getByRole('button', { name: 'Create' }).click();
  await expect(page.getByText(`Branch ${code} Karachi created.`)).toBeVisible();
  await expect(page.getByRole('heading', { name: `Branch ${code} Karachi`, level: 1 })).toBeVisible();
  await expect(page.getByText(/^Last changed .* by Ayesha Khan$/)).toBeVisible();

  // A second branch with the same code is refused next to the code field (unique in the company).
  await page.goto(`${LIST}/new`);
  await field(page, 'Code').fill(code.toLowerCase());
  await field(page, 'In English').fill('Again');
  await page.getByRole('button', { name: 'Create' }).click();
  await expect(page.getByText('This is already used by another record. Choose another one.').first()).toBeVisible();
  await expect(page.getByRole('alert')).toContainText('Reference:');

  // Edit; then someone else (another tab) saves first: the saved version is shown next to the person's own.
  await page.goto(LIST);
  await search(page, code);
  await grid(page).getByRole('button', { name: `Open ${code} Karachi` }).click();
  await expect(page.getByRole('heading', { name: `Branch ${code} Karachi`, level: 1 })).toBeVisible();
  const url = page.url();
  const other = await admin.context.newPage();
  await other.goto(url);
  await field(other, 'In English').fill('Karachi Head Office');
  await other.getByRole('button', { name: 'Save changes' }).click();
  await expect(other.getByText(`Branch ${code} Karachi Head Office saved.`)).toBeVisible();
  await other.close();

  await field(page, 'In English').fill('Karachi Main');
  await page.getByRole('button', { name: 'Save changes' }).click();
  const conflict = page.getByRole('alertdialog', { name: 'Someone else saved this record' });
  await expect(conflict).toBeVisible();
  await expect(conflict.getByRole('row', { name: /Name/ })).toContainText('Karachi Head Office');
  await expect(conflict.getByRole('row', { name: /Name/ })).toContainText('Karachi Main');
  await accessible(page, 'conflict view');
  await shot(page, 'branch-conflict-en-light-1280');
  await conflict.getByRole('button', { name: 'Keep my changes' }).click();
  await expect(page.getByText(`Branch ${code} Karachi Main saved.`)).toBeVisible();

  // History: the changes with who and when, newest first.
  await page.goto(url);
  await page.getByRole('button', { name: 'Deactivate' }).click();
  await expect(page.getByRole('button', { name: 'Activate' })).toBeVisible();
  await page.getByRole('button', { name: 'History' }).click();
  const history = page.getByRole('dialog', { name: `History of ${code} Karachi Main` });
  await expect(history.getByRole('listitem').first()).toContainText('Changed');
  await expect(history.getByRole('list', { name: 'Changes, newest first' }).locator(':scope > li')).toHaveCount(4); // created, two name changes, deactivated
  await expect(history).toContainText('by Ayesha Khan');
  await expect(history).toContainText('Karachi Head Office');
  await accessible(page, 'history panel');
  await shot(page, 'branch-history-en-light-1280');
  await page.keyboard.press('Escape');

  // Unsaved changes: leaving asks first, by a link or by the browser's Back button; staying keeps the change. The record is
  // opened from the list inside the application, so Back stays inside it too.
  await menu(page).getByRole('link', { name: 'Branches' }).click();
  await search(page, code);
  await grid(page).getByRole('button', { name: `Open ${code} Karachi Main` }).click();
  await field(page, 'In English').fill('Karachi Unsaved');
  await menu(page).getByRole('link', { name: 'Home' }).click();
  const leave = page.getByRole('alertdialog', { name: 'Leave without saving?' });
  await expect(leave).toBeVisible();
  await leave.getByRole('button', { name: 'Stay on this page' }).click();
  await expect(field(page, 'In English')).toHaveValue('Karachi Unsaved');
  await page.goBack();
  await expect(page.getByRole('alertdialog', { name: 'Leave without saving?' })).toBeVisible();
  await page.getByRole('button', { name: 'Leave without saving' }).click();

  // Rule SC-2: a company's last branch cannot be deleted, and the screen says so plainly.
  await server.company(`L${run}`, `Lone Traders ${run}`, `اکیلے تاجر ${run}`);
  await page.goto(LIST);
  await choose(page, 'Companies', 'All my companies');
  await search(page, `L${run}`);
  await grid(page).getByRole('button', { name: new RegExp(`^Open L${run}`) }).click();
  await page.getByRole('button', { name: 'Delete' }).click();
  await expect(page.getByRole('alertdialog')).toContainText('is removed from lists');
  await page.getByRole('alertdialog').getByRole('button', { name: /^Delete Branch$/ }).click();
  await expect(page.getByRole('alert')).toContainText('A company must keep at least one branch. Add another branch to this company before you delete this one.');
  await shot(page, 'branch-last-branch-en-light-1280');

  // A branch that is not the last one is deleted after the confirmation.
  await page.goto(LIST);
  await search(page, code);
  await grid(page).getByRole('button', { name: `Open ${code} Karachi Main` }).click();
  await page.getByRole('button', { name: 'Delete' }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: /^Delete Branch$/ }).click();
  await expect(page.getByText('Branch deleted.')).toBeVisible();
  await search(page, code);
  await expect(page.getByText('Nothing matches')).toBeVisible();
  noProblems(page);
  await signOut(page);
  await admin.context.close();
});

test('FR-KRN-ORG-08 a customer label override shows in the menu, the title and the form', async ({ browser }) => {
  await server.label('nav.branches', 'en', 'Offices');
  await server.label('entities.org.branches', 'en', 'Offices');
  await server.label('entities.org.branches.one', 'en', 'Office');
  await server.label('fields.org.branches.code', 'en', 'Office code');
  try {
    const admin = await person(browser, { permissions: EDITOR });
    const page = admin.page;
    await expect(menu(page).getByRole('link', { name: 'Offices' })).toBeVisible();
    await menu(page).getByRole('link', { name: 'Offices' }).click();
    await expect(page.getByRole('heading', { name: 'Offices', level: 1 })).toBeVisible();
    await expect(page.getByRole('grid', { name: 'Offices' }).getByRole('columnheader', { name: /Office code/ })).toBeVisible();
    await shot(page, 'branches-label-override-en-light-1280');
    await page.getByRole('button', { name: 'New Office' }).first().click();
    await expect(field(page, 'Office code')).toBeVisible();
    noProblems(page);
    await signOut(page);
    await admin.context.close();
  } finally {
    for (const key of ['nav.branches', 'entities.org.branches', 'entities.org.branches.one', 'fields.org.branches.code']) await server.label(key, 'en', null);
  }
});

test('FR-KRN-LOC-01 NFR-11 the branch screens in English and Urdu, light and dark, at 360, 768 and 1280 pixels', async ({ browser }) => {
  const admin = await person(browser, { permissions: EDITOR, displayName: 'Ayesha Khan' });
  await signOut(admin.page);
  await admin.context.close();
  for (const language of ['en', 'ur'] as const) {
    for (const scheme of ['light', 'dark'] as const) {
      const context = await browser.newContext({ colorScheme: scheme, viewport: { width: 1280, height: 900 } });
      await display(context, { language });
      const page = await openPage(context);
      await page.goto('/sign-in');
      await page.locator('input[name="username"]').fill(admin.username);
      await page.locator('input[name="password"]').fill(PASSWORD);
      await page.locator('button[type="submit"]').click();
      await expect(page).not.toHaveURL(/\/sign-in(\?|$)/);
      if (page.url().includes('/sign-in/reminder')) await page.locator('main button').last().click();
      await expect(page.locator('.erp-shell main h1')).toBeVisible();
      await display(context, { language });
      await page.goto(LIST);
      await expect(page.locator('main table, main ul.erp-grid-cards').first()).toBeVisible();
      const first = page.locator('main tbody tr, main .erp-grid-card').first();
      await first.locator('button').first().click();
      await expect(page.locator('main form')).toBeVisible();
      const record = page.url();
      for (const [name, path] of [['list', LIST], ['form', record], ['new', `${LIST}/new`]] as const) {
        for (const width of WIDTHS) {
          await page.setViewportSize({ width, height: 900 });
          await page.goto(path);
          await expect(page.locator('main h1')).toBeVisible();
          await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
          expect(await page.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
          await fitsWidth(page);
          if (width === 1280) await accessible(page, `branches ${name} ${language} ${scheme}`);
          await shot(page, `branches-${name}-${language}-${scheme}-${width}`);
        }
      }
      // Validation messages and the history panel in the language.
      await page.setViewportSize({ width: 1280, height: 900 });
      await page.goto(`${LIST}/new`);
      await page.locator('main form button[type="submit"]').click();
      await expect(page.getByRole('alert')).toBeVisible();
      await shot(page, `branches-errors-${language}-${scheme}-1280`);
      await page.goto(record);
      await page.getByRole('button', { name: language === 'en' ? 'History' : 'تاریخ' }).click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await accessible(page, `history ${language} ${scheme}`);
      await shot(page, `branches-history-${language}-${scheme}-1280`);
      noProblems(page);
      await page.keyboard.press('Escape');
      await page.getByRole('button', { name: /^(Account menu of|.* کا اکاؤنٹ مینو$)/ }).click();
      await page.getByRole('button', { name: language === 'en' ? 'Sign out' : 'سائن آؤٹ کریں' }).click();
      await expect(page).toHaveURL(/\/sign-in/);
      await context.close();
    }
  }
});

test('FR-KRN-LOC-01 pseudo-locale: every text of the branch screens comes from the catalogues or the data', async ({ browser }) => {
  const admin = await person(browser, { permissions: EDITOR });
  await signOut(admin.page);
  await admin.context.close();
  const context = await freshContext(browser);
  await display(context, { language: 'en', pseudo: true });
  const page = await openPage(context);
  await page.goto('/sign-in');
  await page.locator('input[name="username"]').fill(admin.username);
  await page.locator('input[name="password"]').fill(PASSWORD);
  await page.locator('button[type="submit"]').click();
  await expect(page).not.toHaveURL(/\/sign-in(\?|$)/);
  if (page.url().includes('/sign-in/reminder')) await page.locator('main button').last().click();
  await expect(page.locator('.erp-shell main h1')).toBeVisible();
  await page.goto(LIST);
  await expect(page.locator('main table')).toBeVisible();
  await page.locator('main tbody tr').first().locator('button').first().click();
  await expect(page.locator('main form')).toBeVisible();
  const record = page.url();
  for (const path of [LIST, record, `${LIST}/new`]) {
    await page.goto(path);
    await expect(page.locator('main h1')).toBeVisible();
    await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.dir)).toBe('rtl');
    const plain = await page.evaluate(() => {
      // Data is not text of the product: grid cells, isolated values (codes, names) and the company in hand come from
      // the customer; the company choice shows the same company name.
      const company = document.querySelector('.erp-topbar-company-name, .erp-topbar-company')?.textContent?.trim() ?? '';
      // A reference to a company shows the company's name: data too (the form field of kind Uuid).
      const customerData = '.erp-topbar-company, .erp-topbar-company-name, .erp-topbar-user-name, td, dd, bdi, .erp-code, [data-kind="Uuid"] .erp-select-button, [data-kind="Uuid"] select';
      const found: string[] = [];
      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        const text = node.textContent?.trim() ?? '';
        if (node.parentElement?.closest(customerData) || text === company) continue;
        if (/\p{L}/u.test(text) && !text.includes('⟦') && !text.includes('⟧')) found.push(text);
      }
      const labels = [...document.querySelectorAll('[aria-label],[placeholder],[title]')]
        .flatMap((el) => ['aria-label', 'placeholder', 'title'].map((a) => el.getAttribute(a) ?? ''))
        .filter((t) => /\p{L}/u.test(t) && !t.includes('⟦'));
      return [...found, ...labels];
    });
    expect(plain, `hard-coded text on ${path}`).toEqual([]);
    await shot(page, `pseudo-branches${new URL(path, page.url()).pathname.replace(/[^a-z0-9-]+/gi, '_').slice(0, 60)}`);
  }
  noProblems(page);
  await context.close();
});
