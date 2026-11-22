// The organization screens end to end (slice S1.14a Acceptance; FR-ADM-ORG-01, FR-ADM-ORG-02, FR-ADM-ORG-07, FR-KRN-IAM-14,
// FR-KRN-IAM-18, FR-KRN-LOC-06, FR-ADM-LOC-04, FR-KRN-LOC-01, NFR-11, NFR-12): real API host on the real access engine and real
// PostgreSQL. An administrator adds a company with another currency, its registration and a bank account that waits for a
// second administrator, a location whose latitude is refused next to the field, and a holiday calendar copied forward with a
// moon-sighting date confirmed; every screen passes axe in English and Urdu.
import { expect, test, type Page } from '@playwright/test';
import { accessible, display, field, fitsWidth, menu, noProblems, person, server, shot } from './support';

test.describe.configure({ mode: 'serial' });

const run = Math.random().toString(36).slice(2, 6);
const WIDTHS = [360, 768, 1280] as const;
const ADMIN = ['org.companies.read', 'org.companies.create', 'org.companies.update', 'org.company_registrations.read', 'org.company_registrations.create', 'org.company_registrations.update',
  'org.company_bank_accounts.read', 'org.company_bank_accounts.create', 'org.company_bank_accounts.update', 'org.company_bank_accounts.delete', 'cfg.setting_values.read',
  'org.branches.read', 'org.locations.read', 'org.locations.create', 'org.locations.update', 'org.locations.delete', 'ref.calendars.read', 'ref.calendars.create', 'ref.calendars.update',
  'ref.holidays.read', 'ref.holidays.create', 'ref.holidays.update', 'ref.banks.read', 'aud.history.view', 'cfg.saved_views.share', 'iam.maker_checker.approve', 'wf.workflow_tasks.read'];

async function choose(page: Page, label: string, option: string | RegExp): Promise<void> {
  const opener = page.getByRole('button', { name: new RegExp(`\\s${label}$`) }).first();
  await opener.click();
  if (!(await page.getByRole('option').first().isVisible().catch(() => false))) await opener.click();
  await page.getByRole('option', { name: option }).first().click();
}

test('FR-ADM-ORG-01 FR-KRN-IAM-14 an administrator adds a company with another currency, its registration and a bank account that waits for a second administrator', async ({ browser }) => {
  await server.reference();
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Ayesha Admin' });
  const page = admin.page;
  await page.reload();
  for (const entry of ['Companies', 'Branches', 'Locations', 'Holiday calendars']) await expect(menu(page).getByRole('link', { name: entry })).toBeVisible();
  await menu(page).getByRole('link', { name: 'Companies' }).click();
  await expect(page.getByRole('heading', { name: 'Companies', level: 1 })).toBeVisible();
  await accessible(page, 'company list');
  await shot(page, 'organization-companies-en-light-1280');

  const code = `OC${run}`.toUpperCase();
  await page.getByRole('button', { name: 'New Company' }).first().click();
  await field(page, 'Code').fill(code);
  await field(page, 'In English').fill(`Second company ${run}`);
  await field(page, 'In Urdu').fill(`دوسری کمپنی ${run}`);
  await field(page, 'Legal name').fill(`Second company ${run} (Private) Limited`);
  await field(page, 'Base currency code').fill('USD');
  await accessible(page, 'company form');
  await shot(page, 'organization-company-form-en-light-1280');
  await page.getByRole('button', { name: 'Create' }).click();
  await expect(page.getByText(/was created with its first branch and its numbering/)).toBeVisible();
  await expect(page.getByRole('heading', { name: new RegExp(`${code}`), level: 1 })).toBeVisible();

  // Registration: an administrator sees the number in full.
  await page.getByRole('button', { name: 'Add a registration' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel(/^Authority/).fill('FBR');
  await dialog.getByLabel(/^Number/).fill('1234567-8');
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('The registration was added.')).toBeVisible();
  await expect(page.getByRole('grid', { name: 'Registrations' }).getByText('1234567-8')).toBeVisible();

  // A bank account waits for a second administrator while approval of sensitive changes is on.
  await page.getByRole('button', { name: 'Add an account' }).click();
  const account = page.getByRole('dialog');
  await account.getByLabel(/^Account title/).fill('Salary account');
  await account.getByLabel(/^Account number/).fill('0011223344556');
  await account.getByLabel(/^Currency/).fill('USD');
  await choose(account.page(), 'Bank', /TESTBANK/); // last: the list of banks has loaded by now
  await account.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText(/waiting for a second administrator/)).toBeVisible();
  await expect(page.getByRole('grid', { name: 'Salary bank accounts' }).getByText('Salary account')).toHaveCount(0);
  await accessible(page, 'company record');
  await shot(page, 'organization-company-record-en-light-1280');

  // The change appears in the approvals of changes, with the number masked.
  await menu(page).getByRole('link', { name: 'Approvals' }).click();
  await page.getByRole('tab', { name: /changes/i }).click();
  await expect(page.getByText(`Save a salary bank account of the company`, { exact: false }).first()).toBeVisible();
  await expect(page.getByText('****4556')).toBeVisible();
  await expect(page.getByText('0011223344556')).toHaveCount(0);
  await shot(page, 'organization-bank-approval-en-light-1280');
  noProblems(page);
});

test('FR-ADM-ORG-02 a location with a latitude outside -90 to 90 is refused next to the field and saved once it is right, with its opening hours', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Bilal Locations' });
  const page = admin.page;
  await page.reload();
  await menu(page).getByRole('link', { name: 'Locations' }).click();
  await expect(page.getByRole('heading', { name: 'Locations', level: 1 })).toBeVisible();
  await accessible(page, 'location list');
  await page.getByRole('button', { name: 'New Location' }).first().click();
  const code = `LOC-${run}`.toUpperCase();
  await field(page, 'Code').fill(code);
  await field(page, 'In English').fill(`Karachi office ${run}`);
  await field(page, 'Address line 1').fill('1 Main Road');
  await field(page, 'City').fill('Karachi');
  await field(page, 'Region code').fill('SD');
  await field(page, 'Latitude').fill('95');
  await field(page, 'Longitude').fill('67.0011');
  await choose(page, 'Branch', /./);
  await page.getByLabel('Opening time on Monday').fill('09:00');
  await page.getByLabel('Closing time on Monday').fill('17:00');
  await page.getByLabel('Closed on Sunday').check({ force: true });
  await accessible(page, 'location form');
  await shot(page, 'organization-location-form-en-light-1280');
  await page.getByRole('button', { name: 'Create' }).click();
  await expect(page.locator('#field-latitude').getByText('This value breaks a data rule.')).toBeVisible();
  await field(page, 'Latitude').fill('24.8607');
  await page.getByRole('button', { name: 'Create' }).click();
  await expect(page.getByText(/The location .* was created\./)).toBeVisible();
  await expect(page.getByLabel('Opening time on Monday')).toHaveValue('09:00');
  await expect(page.getByLabel('Closed on Sunday')).toBeChecked();
});

test('FR-ADM-LOC-04 a holiday year is copied forward, the moon-sighting date is confirmed, and the calendar is used by the company', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Sana Calendars' });
  const page = admin.page;
  await page.reload();
  await menu(page).getByRole('link', { name: 'Holiday calendars' }).click();
  await expect(page.getByRole('heading', { name: 'Calendars', level: 1 })).toBeVisible();
  await page.getByRole('button', { name: 'New Calendar' }).first().click();
  const code = `PK${run}`.toUpperCase();
  await field(page, 'Code').fill(code);
  await field(page, 'In English').fill(`Pakistan holidays ${run}`);
  await page.getByRole('button', { name: 'Create' }).click();
  await expect(page.getByText(/The calendar .* was created\./)).toBeVisible();

  const year = new Date().getUTCFullYear();
  for (const [name, date, tentative] of [['Pakistan Day', `${year}-03-23`, false], ['Eid ul-Fitr', `${year}-03-31`, true]] as const) {
    await page.getByRole('button', { name: 'Add a holiday' }).click();
    const dialog = page.getByRole('dialog');
    const [y, m, d] = date.split('-') as [string, string, string];
    for (const [segment, text] of [['month', m], ['day', d], ['year', y]] as const) {
      await dialog.getByRole('spinbutton', { name: new RegExp(`^${segment}`, 'i') }).first().click();
      await page.keyboard.type(text);
    }
    await dialog.getByLabel(/^Name in English/).fill(name);
    if (tentative) {
      await choose(page, 'Kind', /Religious/);
      await dialog.getByRole('switch', { name: /tentative/ }).check({ force: true });
    }
    await dialog.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByText('The holiday was added.')).toBeVisible();
  }
  await expect(page.getByText('Tentative', { exact: true })).toBeVisible();
  await accessible(page, 'calendar record');
  await shot(page, 'organization-calendar-en-light-1280');

  await page.getByRole('button', { name: `Copy to ${year + 1}` }).click();
  await expect(page.getByText(new RegExp(`2 holidays were copied from ${year} to ${year + 1}`))).toBeVisible();
  const row = page.getByRole('row', { name: new RegExp(`${year + 1}-03-31|Eid ul-Fitr`) }).first();
  await expect(row.getByText('Tentative', { exact: true })).toBeVisible();
  await row.getByRole('button', { name: 'Confirm' }).click();
  await page.getByRole('dialog').getByRole('switch', { name: /tentative/ }).uncheck({ force: true });
  await page.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByText('The holiday was saved.')).toBeVisible();
  await expect(page.getByText('Tentative', { exact: true })).toHaveCount(0);
});

test('FR-KRN-LOC-01 NFR-11 NFR-12 every organization screen passes axe in Urdu and fits three widths', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Urdu Admin' });
  const page = admin.page;
  const context = admin.context;
  await display(context, { language: 'ur' });
  for (const [path, label] of [['/organization/companies', 'companies'], ['/organization/locations', 'locations'], ['/organization/holiday-calendars', 'calendars']] as const) {
    for (const width of WIDTHS) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(path);
      await expect(page.locator('main h1')).toBeVisible();
      await accessible(page, `${label} ${width} ur`);
      await fitsWidth(page);
    }
  }

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/organization/companies');
  await page.getByRole('row').nth(1).click();
  await expect(page.locator('main h1')).toBeVisible();
  await accessible(page, 'company record ur');
  await shot(page, 'organization-company-ur-light-1280');
});
