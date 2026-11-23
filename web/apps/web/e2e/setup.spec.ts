// The first-run set-up guide and the readiness checklist end to end (slice S1.18b Acceptance; FR-ADM-ONB-01, -02, -03, -04, NFR-11, NFR-12): real API host on the real
// access engine and real PostgreSQL. A new administrator is taken to the guide at the first sign-in, ticks and skips steps and finds the same progress after a reload;
// the checklist shows what is missing with a link, and go live needs a reason while required items are open; every screen passes axe in English and Urdu and fits
// three widths.
import { expect, test } from '@playwright/test';
import { accessible, display, fitsWidth, menu, noProblems, person, shot } from './support';

test.describe.configure({ mode: 'serial' });

const WIDTHS = [360, 768, 1280] as const;
const SETUP = ['cfg.setup_tasks.read', 'cfg.setup_tasks.update', 'cfg.setup_tasks.go_live', 'org.companies.read', 'cfg.numbering_schemes.read'];

test('FR-ADM-ONB-01 FR-ADM-ONB-02 a new administrator is led to the set-up guide, skips and ticks steps, and finds the progress after a reload', async ({ browser }) => {
  const admin = await person(browser, { permissions: SETUP, displayName: 'Ayesha Admin', homeHeading: 'Set up your system' });
  const page = admin.page;
  // First sign-in: nothing has been touched, so the home page took the person to the guide.
  await expect(page).toHaveURL(/\/setup/);
  await expect(page.getByRole('heading', { name: 'Set up your system', level: 1 })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Steps', level: 2 })).toBeVisible(); // the pane opens at the first step still to do, which depends on what the customer already has
  await accessible(page, 'set-up guide');
  await shot(page, 'setup-wizard-en-light-1280');

  await page.getByRole('button', { name: 'Open the step Languages and formats' }).click();
  await page.getByRole('button', { name: 'Mark as done' }).click();
  await expect(page.getByText('Step marked as done.')).toBeVisible();
  await page.getByRole('button', { name: 'Open the step Modules to use' }).click();
  const why = page.getByRole('textbox', { name: /Why are you skipping/ });
  await why.click();
  await why.pressSequentially('Later this week');
  await expect(why).toHaveValue('Later this week');
  await page.getByRole('button', { name: 'Skip this step' }).click();
  await expect(page.getByText(/Step skipped/)).toBeVisible();

  // "Closes the browser and signs in again": a reload asks the server again and shows the same progress.
  await page.reload();
  const steps = page.getByRole('list').filter({ hasText: 'Languages and formats' }).first();
  await expect(steps.getByRole('listitem').filter({ hasText: 'Languages and formats' }).getByText('Done')).toBeVisible();
  await expect(steps.getByRole('listitem').filter({ hasText: 'Modules to use' }).getByText('Skipped')).toBeVisible();
  await page.getByRole('button', { name: 'Open the step Modules to use' }).click();
  await expect(page.getByText('Skipped: Later this week')).toBeVisible();
  await page.getByRole('button', { name: 'Do this step again' }).click();
  await expect(page.getByText(/set back to "to do"/)).toBeVisible();

  // The structure step offers the import centre (FR-ADM-ONB-04).
  await page.getByRole('button', { name: 'Open the step Structure' }).click();
  await expect(page.getByRole('link', { name: 'Open the import centre' })).toHaveAttribute('href', '/admin/imports?type=org_units');
  noProblems(page);
  await admin.context.close();
});

test('FR-ADM-ONB-03 the checklist shows what is missing with a link, and go live needs a reason while required items are open', async ({ browser }) => {
  // The customer is shared by the scenarios: the first one touched steps, so this person lands on the home page with the card, not on the guide.
  const admin = await person(browser, { permissions: SETUP, displayName: 'Carim Checklist' });
  const page = admin.page;
  await expect(page.getByRole('link', { name: 'Continue the set-up guide' })).toBeVisible();
  await menu(page).getByRole('link', { name: 'Set-up guide' }).click();
  await page.getByRole('button', { name: 'Readiness checklist' }).click();
  const table = page.getByRole('table', { name: 'Core platform' });
  // The customer is shared, so what is already set up varies: the items are always listed, and the manual one nobody has ticked is open with its link.
  await expect(table.getByRole('row').filter({ hasText: 'At least one location' })).toBeVisible();
  await expect(table.getByRole('row').filter({ hasText: 'Document numbers are set' })).toBeVisible();
  const rules = table.getByRole('row').filter({ hasText: 'Notification rules are reviewed' });
  await expect(rules.getByText('To do')).toBeVisible();
  await expect(rules.getByRole('link', { name: 'Fix this: Notification rules are reviewed' })).toHaveAttribute('href', '/settings');
  await expect(page.getByText('Set-up mode')).toBeVisible();
  await accessible(page, 'checklist');
  await shot(page, 'setup-checklist-en-light-1280');

  await page.getByRole('button', { name: 'Go live: Core platform' }).click();
  const dialog = page.getByRole('dialog', { name: 'Take Core platform live' });
  await expect(dialog.getByRole('button', { name: 'Go live' })).toBeDisabled();
  const reason = dialog.getByRole('textbox', { name: /Reason for going live/ });
  await reason.click();
  await reason.pressSequentially('Pilot with the head office only');
  await accessible(page, 'go-live dialog');
  await dialog.getByRole('button', { name: 'Go live' }).click();
  await expect(page.getByText('Core platform is now live for this company.')).toBeVisible();
  await expect(page.getByText(/Reason: Pilot with the head office only/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Go live: Core platform' })).toHaveCount(0);
  noProblems(page);
  await admin.context.close();
});

test('FR-KRN-LOC-01 NFR-11 NFR-12 the set-up screens read right to left in Urdu and fit three widths', async ({ browser }) => {
  const admin = await person(browser, { permissions: SETUP, displayName: 'Uzma Urdu' });
  await display(admin.context, { language: 'ur' });
  const page = admin.page;
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto('/setup');
    await expect(page.getByRole('heading', { name: 'اپنا نظام ترتیب دیں', level: 1 })).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await accessible(page, `setup ur ${width}`);
    await fitsWidth(page);
    await shot(page, `setup-wizard-ur-light-${width}`);
    await page.goto('/setup?view=checklist');
    await expect(page.getByRole('table', { name: 'بنیادی پلیٹ فارم' })).toBeVisible();
    await accessible(page, `checklist ur ${width}`);
    await fitsWidth(page);
    await shot(page, `setup-checklist-ur-light-${width}`);
  }
  await admin.context.close();
});
