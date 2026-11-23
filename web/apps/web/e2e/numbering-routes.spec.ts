// Numbering screens and the approval route editor end to end (slice S1.15c Acceptance; FR-KRN-NUM-01, -02, -03, -05, FR-KRN-WFL-01, -02, -03, -10, FR-XM-05,
// FR-XM-06, FR-KRN-LOC-01, NFR-11, NFR-12): real API host on the real access engine and real PostgreSQL. An administrator sets, changes and retires the scheme of
// a numbered document and sees the example and the next number; the void report opens; a route of the test document type gets a new draft with a second step
// that has a condition on a declared field, is checked and activated while a request keeps running on the old version; every screen passes axe in English and
// Urdu and fits three widths.
import { expect, test, type Locator, type Page } from '@playwright/test';
import { accessible, display, fitsWidth, menu, noProblems, person, server, shot } from './support';

test.describe.configure({ mode: 'serial' });

const WIDTHS = [360, 768, 1280] as const;
const NUMBERING = ['cfg.numbering_schemes.read', 'cfg.numbering_schemes.create', 'cfg.numbering_schemes.update', 'cfg.numbering_schemes.retire', 'cfg.numbering.void_report',
  'cfg.setting_values.read', 'org.branches.read'];
const ROUTES = ['wf.workflow_definitions.read', 'wf.workflow_definitions.update', 'iam.roles.read', 'iam.users.read', 'wf.workflow_instances.read'];

/** Opens a select (React Aria needs a second press when the first one only focuses it) and picks an option. */
async function choose(scope: Page | Locator, button: RegExp, option: string | RegExp, page: Page): Promise<void> {
  const opener = scope.getByRole('button', { name: button });
  await opener.click();
  if (!(await page.getByRole('option').first().isVisible().catch(() => false))) await opener.click();
  await page.getByRole('option', { name: option }).first().click();
}

test('FR-KRN-NUM-01 FR-KRN-NUM-02 an administrator sets, changes and retires the scheme of a numbered document and sees the example and the next number', async ({ browser }) => {
  const admin = await person(browser, { permissions: NUMBERING, displayName: 'Nora Numbers' });
  const page = admin.page;
  await page.reload();
  await expect(menu(page).getByRole('link', { name: 'Numbering' })).toBeVisible();
  await menu(page).getByRole('link', { name: 'Numbering', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Numbering', level: 1 })).toBeVisible();
  const table = page.getByRole('table', { name: 'Numbered documents' });
  await expect(table.getByText('hrm.employee_no')).toBeVisible();
  await accessible(page, 'numbering list');
  await shot(page, 'numbering-en-light-1280');

  // Provisioning set the standard scheme, so the document shows it, unused, with the first number it would give.
  await page.getByRole('button', { name: 'Open the numbering of hrm.employee_no' }).click();
  await expect(page.getByText('EMP-{seq:5}', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('EMP-00001', { exact: true })).toBeVisible(); // the next number
  await expect(page.getByText('No number issued yet')).toBeVisible();

  // A pattern without a running number is refused where it is typed.
  await page.getByRole('button', { name: 'Change the scheme EMP-{seq:5}' }).click();
  const change = page.getByRole('dialog', { name: /Change the scheme of/ });
  await change.getByRole('textbox', { name: /^Pattern/ }).fill('STAFF');
  await change.getByRole('button', { name: 'Save the scheme' }).click();
  await expect(change.getByText(/running number/).first()).toBeVisible();
  await change.getByRole('textbox', { name: /^Pattern/ }).fill('STAFF-{seq:6}');
  await expect(change.getByText(/STAFF-000001/)).toBeVisible(); // the example
  await accessible(page, 'scheme form');
  await shot(page, 'numbering-form-en-light-1280');
  await change.getByRole('button', { name: 'Save the scheme' }).click();
  await expect(page.getByText('The scheme has been saved.')).toBeVisible();
  await expect(page.getByText('STAFF-{seq:6}', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('STAFF-000001', { exact: true })).toBeVisible();

  // The void report of a series that has issued nothing.
  await page.getByRole('button', { name: 'Void report of the scheme STAFF-{seq:6}' }).click();
  const report = page.getByRole('dialog', { name: /Void report/ });
  await expect(report.getByText('No number issued in this range.')).toBeVisible();
  await expect(report.getByText('No number has been voided in this range.')).toBeVisible();
  await accessible(page, 'void report');
  await report.getByRole('button', { name: 'Close', exact: true }).first().click();

  // Retire: the scheme moves to the retired list; a new one can be set, with its example.
  await page.getByRole('button', { name: 'Retire the scheme STAFF-{seq:6}' }).click();
  await page.getByRole('alertdialog', { name: 'Retire the scheme?' }).getByRole('button', { name: 'Retire the scheme' }).click();
  await expect(page.getByText('The scheme has been retired.')).toBeVisible();
  await expect(page.getByRole('list', { name: 'Retired schemes' }).getByText('STAFF-{seq:6}')).toBeVisible();
  await expect(page.getByText(/This document has no scheme for this company yet/)).toBeVisible();
  await page.getByRole('button', { name: 'Set a scheme', exact: true }).click();
  const created = page.getByRole('dialog', { name: /Set a scheme for/ });
  await created.getByRole('textbox', { name: /^Pattern/ }).fill('PER-{seq:4}');
  await expect(created.getByText(/PER-0001/)).toBeVisible();
  await created.getByRole('button', { name: 'Save the scheme' }).click();
  await expect(page.getByText('The scheme has been saved.')).toBeVisible();
  await expect(page.getByText('PER-0001', { exact: true })).toBeVisible();
  noProblems(page);
  await admin.context.close();
});

test('FR-KRN-WFL-01 FR-KRN-WFL-02 FR-KRN-WFL-03 FR-KRN-WFL-10 a draft route with a conditional second step is checked and activated while a running request keeps its version', async ({ browser }) => {
  const manager = await person(browser, { displayName: 'Imran Manager' });
  const requester = await person(browser, { displayName: 'Amna Requester' });
  const admin = await person(browser, { permissions: ROUTES, displayName: 'Rita Routes' });
  await server.route(manager.username, false);
  await server.request(requester.username, '300'); // running on version 1
  const page = admin.page;
  await page.reload();
  await expect(menu(page).getByRole('link', { name: 'Approval routes' })).toBeVisible();
  await menu(page).getByRole('link', { name: 'Approval routes', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Approval routes', level: 1 })).toBeVisible();
  const list = page.getByRole('list', { name: 'Approval routes' });
  await expect(list.getByText('Expense claim').first()).toBeVisible();
  await accessible(page, 'routes list');
  await list.getByRole('button', { name: /^Open the route Expense/ }).first().click();
  const versions = page.getByRole('list', { name: 'Versions' });
  await expect(versions.getByText('1 request is running on it')).toBeVisible();
  await expect(page.getByText(/cannot be edited/)).toBeVisible();
  await shot(page, 'routes-en-light-1280');

  await page.getByRole('button', { name: /^New draft from version/ }).click();
  await expect(page.getByText('A new draft has been created.')).toBeVisible();
  await page.getByRole('button', { name: 'Add a step' }).click();
  const second = page.getByRole('region', { name: 'Step 2' });
  await second.getByRole('textbox', { name: /^Step name in English/ }).fill('Director');
  await second.getByRole('textbox', { name: /^Step name in Urdu/ }).fill('ڈائریکٹر');
  await choose(second, /Who decides/, 'A named person', page);
  await second.getByRole('combobox', { name: /^Person/ }).fill('Imran');
  await page.getByRole('option', { name: /Imran Manager/ }).click();
  await second.getByRole('button', { name: 'Add a condition' }).click();
  await choose(second, /Field/, /^Amount/, page);
  await choose(second, /Comparison/, 'is more than', page);
  await second.getByRole('textbox', { name: 'Value' }).fill('1000');
  await expect(second.getByText(/When Amount is more than 1000/)).toBeVisible();
  await accessible(page, 'route editor');
  await shot(page, 'routes-editor-en-light-1280');
  await page.getByRole('button', { name: 'Save draft' }).click();
  await expect(page.getByText('The draft has been saved.')).toBeVisible();

  await page.getByRole('button', { name: 'Check', exact: true }).click();
  await expect(page.getByText('No problems: this version can be activated.')).toBeVisible();
  await page.getByRole('button', { name: 'Activate', exact: true }).click();
  const dialog = page.getByRole('alertdialog', { name: /^Activate version/ });
  await expect(dialog.getByText(/1 is running now/)).toBeVisible();
  await dialog.getByRole('button', { name: /^Activate version/ }).click();
  await expect(page.getByText(/is active\.|Waiting for a second administrator/).first()).toBeVisible();
  await expect(page.getByRole('list', { name: 'Versions' }).getByText('1 request is running on it')).toBeVisible(); // still on version 1
  noProblems(page);
  await admin.context.close();
  await manager.context.close();
  await requester.context.close();
});

test('FR-KRN-LOC-01 NFR-11 NFR-12 the numbering and route screens read right to left in Urdu and fit three widths', async ({ browser }) => {
  const admin = await person(browser, { permissions: [...NUMBERING, ...ROUTES], displayName: 'Uzma Urdu' });
  await display(admin.context, { language: 'ur' });
  const page = admin.page;
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto('/settings/numbering?type=hrm.employee_no');
    await expect(page.getByRole('heading', { name: 'نمبرنگ', level: 1 })).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await accessible(page, `numbering ur ${width}`);
    await fitsWidth(page);
    await shot(page, `numbering-ur-light-${width}`);
    await page.goto('/settings/approval-routes');
    await expect(page.getByRole('heading', { name: 'منظوری کے راستے', level: 1 })).toBeVisible();
    await accessible(page, `routes ur ${width}`);
    await fitsWidth(page);
    await shot(page, `routes-ur-light-${width}`);
  }
  await admin.context.close();
});
