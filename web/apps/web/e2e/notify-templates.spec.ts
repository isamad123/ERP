// Notification texts, rules and document templates end to end (slice S1.15d Acceptance; FR-KRN-NTF-02, -03, FR-KRN-DOC-05, FR-KRN-LOC-07, FR-KRN-LOC-01, NFR-11, NFR-12):
// real API host on the real access engine and real PostgreSQL. An administrator edits the Urdu text of a notification, previews it with sample values, sends a test to
// themself and restores the product text; adds a rule; writes a letterhead (a shared part), previews it and activates it; every screen passes axe in English and
// Urdu and fits three widths.
import { expect, test, type Locator, type Page } from '@playwright/test';
import { accessible, display, fitsWidth, menu, noProblems, person, shot } from './support';

test.describe.configure({ mode: 'serial' });

const WIDTHS = [360, 768, 1280] as const;
const run = Math.random().toString(36).slice(2, 7);
const NOTIFY = ['ntf.catalogue.read', 'ntf.templates.read', 'ntf.templates.create', 'ntf.templates.test_send', 'ntf.subscriptions.read', 'ntf.subscriptions.create', 'ntf.subscriptions.update', 'ntf.subscriptions.delete',
  'iam.roles.read', 'iam.users.read'];
/** Opens a select (React Aria needs a second press when the first one only focuses it) and picks an option. */
async function choose(scope: Page | Locator, button: RegExp, option: string | RegExp, page: Page): Promise<void> {
  const opener = scope.getByRole('button', { name: button });
  await opener.click();
  if (!(await page.getByRole('option').first().isVisible().catch(() => false))) await opener.click();
  await page.getByRole('option', { name: option }).first().click();
}
const TEMPLATES = ['doc.templates.read', 'doc.templates.create', 'doc.templates.update', 'doc.template_versions.read', 'doc.template_versions.create', 'doc.template_versions.update'];

test('FR-KRN-NTF-02 FR-KRN-NTF-03 an administrator edits the Urdu text of a notification, previews it, sends a test, restores the product text and adds a rule', async ({ browser }) => {
  const admin = await person(browser, { permissions: NOTIFY, displayName: 'Nadia Notices' });
  const page = admin.page;
  await page.reload();
  await menu(page).getByRole('link', { name: 'Notification texts', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Notification texts', level: 1 })).toBeVisible();
  await accessible(page, 'notification list');
  await shot(page, 'notify-en-light-1280');
  await page.getByRole('button', { name: 'Open the notification workflow.task_assigned' }).click();
  await expect(page.getByText("The product's text (version 1).")).toBeVisible();

  // The Urdu in-app text: change it, preview with a sample value, save as our own version.
  await page.getByRole('button', { name: /Language/ }).click();
  await page.getByRole('option', { name: 'Urdu' }).click();
  const body = page.getByRole('textbox', { name: /^Text/ });
  await expect(body).toBeVisible();
  await body.fill(`فیصلے کے لیے کھولیں ${run}`);
  await page.getByRole('button', { name: 'Preview' }).click();
  await expect(page.getByTestId('ntfx-preview').getByText(`فیصلے کے لیے کھولیں ${run}`)).toBeVisible();
  await accessible(page, 'notification text');
  await shot(page, 'notify-text-en-light-1280');
  await page.getByRole('button', { name: 'Save as new version' }).click();
  await expect(page.getByText('The text is saved.')).toBeVisible();
  await expect(page.getByText('Your own text (version 2).')).toBeVisible();
  await page.getByRole('button', { name: 'Send a test to me' }).click();
  await expect(page.getByText('A test e-mail is on its way to you.')).toBeVisible();

  // A text with a salary field is refused by the server and nothing changes.
  await body.fill('{salary}');
  await page.getByRole('button', { name: 'Save as new version' }).click();
  await expect(page.getByText('Your own text (version 2).')).toBeVisible();

  // Restore the product text.
  await body.fill(`فیصلے کے لیے کھولیں ${run}`);
  await page.getByRole('button', { name: 'Restore the product text' }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Restore' }).click();
  await expect(page.getByText('The product text is active again.')).toBeVisible();
  await expect(page.getByText("The product's text (version 1).")).toBeVisible();

  // A rule that tells a role as well.
  await page.getByRole('tab', { name: 'Who is told' }).click();
  await page.getByRole('button', { name: 'Add a rule' }).click();
  await page.getByRole('textbox', { name: 'Name' }).fill(`Copy ${run}`);
  await page.getByText('Tenant Admin', { exact: true }).click(); // the box is the label of a hidden input
  await accessible(page, 'notification rule form');
  await shot(page, 'notify-rule-en-light-1280');
  await page.getByRole('button', { name: 'Save the rule' }).click();
  await expect(page.getByText('The rule is saved.')).toBeVisible();
  await expect(page.getByRole('list', { name: 'Rules' }).getByText(`Copy ${run}`)).toBeVisible();
  noProblems(page);
  await admin.context.close();
});

test('FR-KRN-DOC-05 an administrator writes a letterhead, previews it and activates it', async ({ browser }) => {
  const admin = await person(browser, { permissions: TEMPLATES, displayName: 'Tariq Templates' });
  const page = admin.page;
  await page.reload();
  await menu(page).getByRole('link', { name: 'Document templates', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Document templates', level: 1 })).toBeVisible();
  await page.getByRole('button', { name: 'New template' }).first().click();
  const dialog = page.getByRole('dialog', { name: 'New template' });
  await dialog.getByRole('textbox', { name: /^Code/ }).fill(`letterhead-${run}`);
  await dialog.getByRole('textbox', { name: /^Name in English/ }).fill(`Letterhead ${run}`);
  await choose(dialog, /Kind/, 'Shared part', page);
  await choose(dialog, /Output/, 'Web page (HTML)', page); // the build machine's PDF printer is the operator's set-up (runbook pdf-rendering), the PDF itself is tested on the server
  await dialog.getByRole('textbox', { name: /^Text/ }).fill('<div>Our company</div>{{ content }}');
  await accessible(page, 'new template');
  await shot(page, 'templates-new-en-light-1280');
  await dialog.getByRole('button', { name: 'Create the template' }).click();
  await expect(page.getByRole('heading', { name: `Letterhead ${run}`, level: 2 })).toBeVisible();
  await expect(page.getByRole('list', { name: 'Versions' }).getByText('Draft')).toBeVisible();
  await page.getByRole('button', { name: 'Preview' }).click();
  await expect(page.getByText('This output is not a PDF; the text was checked and rendered without error.')).toBeVisible();
  await accessible(page, 'template editor');
  await shot(page, 'templates-en-light-1280');
  await page.getByRole('button', { name: 'Activate', exact: true }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: /^Activate version/ }).click();
  await expect(page.getByText('Version 1 is active.')).toBeVisible();
  await expect(page.getByText(/cannot be edited/)).toBeVisible();
  noProblems(page);
  await admin.context.close();
});

test('FR-KRN-LOC-01 NFR-11 NFR-12 the notification and template screens read right to left in Urdu and fit three widths', async ({ browser }) => {
  const admin = await person(browser, { permissions: [...NOTIFY, ...TEMPLATES], displayName: 'Uzma Urdu' });
  await display(admin.context, { language: 'ur' });
  const page = admin.page;
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto('/settings/notification-texts?event=workflow.task_assigned');
    await expect(page.getByRole('heading', { name: 'اطلاعات کے متن', level: 1 })).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await accessible(page, `notify ur ${width}`);
    await fitsWidth(page);
    await shot(page, `notify-ur-light-${width}`);
    await page.goto('/settings/document-templates');
    await expect(page.getByRole('heading', { name: 'دستاویزی سانچے', level: 1 })).toBeVisible();
    await accessible(page, `templates ur ${width}`);
    await fitsWidth(page);
    await shot(page, `templates-ur-light-${width}`);
  }
  await admin.context.close();
});
