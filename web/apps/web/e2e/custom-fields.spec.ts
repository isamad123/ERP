// Labels, option lists and custom fields end to end (slice S1.15b Acceptance; FR-ADM-STU-25, FR-ADM-STU-05, FR-KRN-MET-22,
// FR-KRN-MET-01, FR-KRN-MET-02, FR-KRN-MET-05, FR-KRN-LOC-01, NFR-11, NFR-12): real API host on the real access engine and real
// PostgreSQL. An administrator renames "Organization units" to "Wings" in English and Urdu and both languages show it; an option
// list "Blood groups" is built; a required custom choice field "Blood group" is defined on org units: the form refuses a new
// unit without it with the field error in English and in Urdu, accepts one with it and keeps the value; the field matrix lists
// it and a role it is hidden from sees no custom section; retiring the field hides it on new records and keeps the stored
// values; every screen passes axe in English and Urdu and fits three widths.
import { expect, test, type Locator, type Page } from '@playwright/test';
import { accessible, display, field, fitsWidth, freshContext, menu, noProblems, person, server, shot } from './support';

test.describe.configure({ mode: 'serial' });

const run = Math.random().toString(36).slice(2, 6);
const CODE = run.toUpperCase();
const WIDTHS = [360, 768, 1280] as const;
const LIST = `bg_${run}`;
const KEY = `blood_group_${run}`;
const FIELD_EN = `Blood group ${run}`;
const FIELD_UR = `بلڈ گروپ ${run}`;

const ORG = ['org.org_units.read', 'org.org_units.create', 'org.org_units.update', 'org.org_unit_types.read', 'org.org_hierarchies.read', 'org.org_unit_links.read', 'org.locations.read',
  'org.companies.read', 'org.branches.read', 'ref.calendars.read', 'aud.history.view', 'cfg.saved_views.share'];
const CONFIG = ['cfg.labels.read', 'cfg.labels.update', 'cfg.option_lists.read', 'cfg.option_lists.create', 'cfg.option_lists.update', 'cfg.option_list_items.read', 'cfg.option_list_items.create',
  'cfg.option_list_items.update', 'cfg.custom_field_defs.read', 'cfg.custom_field_defs.create', 'cfg.custom_field_defs.update', 'iam.roles.read'];
const ADMIN = [...ORG, ...CONFIG];

/** Opens a select (React Aria needs a second press when the first one only focuses it) and picks an option. */
async function choose(scope: Page | Locator, button: RegExp, option: string | RegExp): Promise<void> {
  const page = 'page' in scope && typeof scope.page === 'function' ? scope.page() : (scope as Page);
  const opener = scope.getByRole('button', { name: button });
  await opener.click();
  if (!(await page.getByRole('option').first().isVisible().catch(() => false))) await opener.click();
  await page.getByRole('option', { name: option }).first().click();
}

test('FR-ADM-STU-25 FR-ADM-STU-05 FR-KRN-MET-22 an administrator renames a menu text in English and Urdu, and both languages show it, then resets it', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Lina Labels' });
  const page = admin.page;
  await page.reload();
  for (const entry of ['Labels', 'Option lists', 'Custom fields']) await expect(menu(page).getByRole('link', { name: entry })).toBeVisible();
  await menu(page).getByRole('link', { name: 'Labels', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Labels', level: 1 })).toBeVisible();
  await accessible(page, 'labels editor');
  await shot(page, 'labels-en-light-1280');

  // The link of the unit types screen opens the editor for that table.
  await page.goto('/organization/org-units/types');
  await page.getByRole('link', { name: 'Rename a standard term' }).click();
  await expect(page.getByRole('heading', { name: 'Labels', level: 1 })).toBeVisible();
  await expect(page.getByRole('table', { name: 'Labels' }).getByText('entities.org.org_units', { exact: false }).first()).toBeVisible();

  await page.goto('/settings/labels?q=nav.organizationUnits');
  await page.getByRole('button', { name: 'Rename the text Organization units' }).click();
  const drawer = page.getByRole('dialog', { name: 'Rename a text' });
  await expect(drawer.getByText('Product text: Organization units')).toBeVisible();
  await accessible(page, 'label drawer');
  await shot(page, 'labels-drawer-en-light-1280');
  await drawer.getByRole('textbox', { name: 'English text' }).fill('Wings');
  await drawer.getByRole('textbox', { name: 'Urdu text' }).fill('ونگز');
  await drawer.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('The label was saved.')).toBeVisible();
  await expect(menu(page).getByRole('link', { name: 'Wings' })).toBeVisible(); // at once on the screen that saved it
  await expect(page.locator('.erp-small.erp-muted', { hasText: 'Changed for the customer' }).or(page.getByText('Changed for the customer'))).toBeVisible();

  // Another person, in Urdu, sees the Urdu text after the next load (the shell also asks again every minute).
  const urdu = await person(browser, { permissions: ORG, displayName: 'Urdu Reader', context: await freshContext(browser, 'ur') });
  await urdu.page.reload();
  await expect(menu(urdu.page).getByRole('link', { name: 'ونگز' })).toBeVisible();
  await urdu.context.close();

  // Reset to the product text.
  await page.goto('/settings/labels?q=nav.organizationUnits');
  await page.getByRole('button', { name: 'Rename the text Wings' }).click();
  await page.getByRole('dialog', { name: 'Rename a text' }).getByRole('button', { name: 'Reset to product text' }).click();
  await expect(page.getByText('The label is back to the product text.')).toBeVisible();
  await expect(menu(page).getByRole('link', { name: 'Organization units' })).toBeVisible();
  noProblems(page);
  await admin.context.close();
});

test('FR-KRN-MET-01 FR-KRN-MET-02 FR-KRN-MET-05 a required custom choice field on org units is refused when missing in both languages, kept when given, hidden by a field rule, and retired with its values kept', async ({ browser }) => {
  await server.orgStructure(CODE);
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Carla Custom' });
  const reader = await person(browser, { permissions: ORG, displayName: 'Rana Reader' });
  const page = admin.page;
  await page.reload();

  // 1. An option list "Blood groups" with two items, in both languages.
  await page.goto('/settings/option-lists');
  await expect(page.getByRole('heading', { name: 'Option lists', level: 1 })).toBeVisible();
  await page.getByRole('button', { name: 'New list' }).first().click();
  let dialog = page.getByRole('dialog', { name: 'New list' });
  await dialog.getByRole('textbox', { name: /^Code/ }).fill(LIST);
  await dialog.getByRole('textbox', { name: /^List name in English/ }).fill(`Blood groups ${run}`);
  await dialog.getByRole('textbox', { name: /^List name in Urdu/ }).fill(`بلڈ گروپ فہرست ${run}`);
  await dialog.getByRole('button', { name: 'Create list' }).click();
  await expect(page.getByRole('heading', { name: `Blood groups ${run}`, level: 2 })).toBeVisible();
  for (const [code, en, ur] of [['a_pos', 'A positive', 'اے پازیٹو'], ['b_pos', 'B positive', 'بی پازیٹو']]) {
    await page.getByRole('button', { name: 'Add item' }).first().click();
    dialog = page.getByRole('dialog', { name: 'Add item' });
    await dialog.getByRole('textbox', { name: /^Code/ }).fill(code!);
    await dialog.getByRole('textbox', { name: /^Item name in English/ }).fill(en!);
    await dialog.getByRole('textbox', { name: /^Item name in Urdu/ }).fill(ur!);
    await dialog.getByRole('button', { name: 'Add item' }).click();
    await expect(page.getByRole('row', { name: new RegExp(en!) })).toBeVisible();
  }
  await accessible(page, 'option list');
  await shot(page, 'option-lists-en-light-1280');

  // 2. The custom field "Blood group": required choice from that list, in a "Health" section.
  await page.goto('/settings/custom-fields');
  await expect(page.getByRole('heading', { name: 'Custom fields', level: 1 })).toBeVisible();
  await choose(page, /Record type/, 'Org units');
  await page.getByRole('button', { name: 'New field' }).first().click();
  const drawer = page.getByRole('dialog', { name: /^New custom field of/ });
  await drawer.getByRole('textbox', { name: /^Key/ }).fill(KEY);
  await choose(drawer, /Type/, 'Choice from a list');
  await choose(drawer, /Option list/, new RegExp(`Blood groups ${run}`));
  await drawer.getByRole('textbox', { name: /^Name in English/ }).fill(FIELD_EN);
  await drawer.getByRole('textbox', { name: /^Name in Urdu/ }).fill(FIELD_UR);
  await drawer.getByRole('textbox', { name: 'Section' }).fill('Health');
  await drawer.getByRole('switch', { name: 'Required' }).click();
  await accessible(page, 'custom field drawer');
  await shot(page, 'custom-field-drawer-en-light-1280');
  await drawer.getByRole('button', { name: 'Create field' }).click();
  await expect(page.getByText('The field was created.')).toBeVisible();
  await expect(page.getByRole('row', { name: new RegExp(KEY) })).toBeVisible();
  await accessible(page, 'custom field list');
  await shot(page, 'custom-fields-en-light-1280');

  // 3. The unit form shows the field in its custom section and refuses a unit without it, in English and in Urdu.
  const fillUnit = async (p: Page, code: string) => {
    await field(p, 'Code').fill(code);
    await p.getByLabel(/^In English/).fill(`Unit ${code}`);
    await p.getByRole('button', { name: /Unit type$/ }).click();
    await p.getByRole('option').first().click();
  };
  /** Opens a unit from the list by searching its code. */
  const openUnit = async (p: Page, code: string) => {
    await p.goto('/organization/org-units');
    await p.getByRole('searchbox', { name: 'Search' }).fill(code);
    await p.getByRole('button', { name: 'Search', exact: true }).click();
    await p.getByRole('gridcell', { name: code }).first().click();
  };
  await page.goto('/organization/org-units/new');
  const section = page.getByRole('region', { name: 'Additional fields' });
  await expect(section).toBeVisible();
  await expect(section.getByRole('group', { name: 'Health' })).toBeVisible();
  await expect(section.getByRole('button', { name: new RegExp(FIELD_EN) })).toBeVisible();
  await fillUnit(page, `${CODE}-C1`);
  await page.getByRole('button', { name: 'Create' }).click();
  const fieldBox = page.locator(`[id="field-custom.${KEY}"]`);
  await expect(fieldBox.getByText('Fill in this field.')).toBeVisible();
  await accessible(page, 'org unit form with a custom field error');
  await shot(page, 'org-unit-custom-error-en-light-1280');
  // The same refusal in Urdu.
  const urduAdmin = await person(browser, { permissions: ADMIN, displayName: 'Urdu Custom', context: await freshContext(browser, 'ur') });
  await urduAdmin.page.goto('/organization/org-units/new');
  await expect(urduAdmin.page.getByRole('region', { name: 'اضافی خانے' })).toBeVisible();
  await urduAdmin.page.locator('button[type="submit"]').click();
  await expect(urduAdmin.page.locator(`[id="field-custom.${KEY}"]`).getByText('یہ خانہ پُر کریں۔')).toBeVisible();
  await shot(urduAdmin.page, 'org-unit-custom-error-ur-light-1280');
  await accessible(urduAdmin.page, 'org unit form with a custom field error ur');
  await urduAdmin.context.close();

  // Given a value, the unit is created and keeps it.
  await choose(section, new RegExp(FIELD_EN), 'A positive');
  await page.getByRole('button', { name: 'Create' }).click();
  await expect(page.getByText(/The unit .* was created/)).toBeVisible();
  await openUnit(page, `${CODE}-C1`);
  await expect(page.getByRole('region', { name: 'Additional fields' }).getByRole('button', { name: /A positive/ })).toBeVisible();

  // 4. The field matrix lists the custom field of org units; a role it is hidden from sees no custom section.
  await page.goto('/admin/field-security');
  await choose(page, /Table or resource|Table/, /org\.org_units/);
  await expect(page.getByRole('row', { name: new RegExp(`custom\\.${KEY}`) })).toBeVisible();
  await server.fieldAccess(reader.username, 'org.org_units', `custom.${KEY}`, 'hidden');
  await openUnit(reader.page, `${CODE}-C1`);
  await expect(reader.page.getByRole('heading', { level: 1 })).toBeVisible();
  await expect(reader.page.getByRole('region', { name: 'Additional fields' })).toHaveCount(0);
  await expect(reader.page.getByText(FIELD_EN)).toHaveCount(0);

  // 5. Retire: new units no longer ask for it; the stored value is kept on the old unit (read-only, marked retired).
  await page.goto('/settings/custom-fields');
  await choose(page, /Record type/, 'Org units');
  await page.getByRole('button', { name: new RegExp(`^Retire the field ${FIELD_EN}`) }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Retire field' }).click();
  await expect(page.getByText('The field was retired. Its values are kept.')).toBeVisible();
  await page.goto('/organization/org-units/new');
  await expect(page.getByRole('textbox', { name: /^Code/ })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Additional fields' })).toHaveCount(0);
  await openUnit(page, `${CODE}-C1`);
  const kept = page.getByRole('region', { name: 'Additional fields' }).getByRole('group', { name: new RegExp(FIELD_EN) });
  await expect(kept).toContainText('A positive');
  await expect(kept).toContainText('retired');
  noProblems(page);
  await admin.context.close();
  await reader.context.close();
});

test('FR-KRN-LOC-01 NFR-11 NFR-12 the labels, option list and custom field screens pass axe in Urdu and fit three widths', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Urdu Config Admin' });
  const page = admin.page;
  await display(admin.context, { language: 'ur' });
  for (const [name, path] of [['labels', '/settings/labels'], ['option-lists', '/settings/option-lists'], ['custom-fields', '/settings/custom-fields']] as const) {
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
  await page.goto('/settings/labels?q=nav.organizationUnits');
  await page.getByRole('button', { name: /^متن .* کا نام بدلیں$/ }).first().click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await accessible(page, 'label drawer ur');
  await shot(page, 'labels-drawer-ur-light-1280');
  noProblems(page);
  await admin.context.close();
});
