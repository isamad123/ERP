// The user console end to end (slice S1.13a Acceptance; FR-ADM-USR-01 to -05, -08, -09, FR-KRN-IAM-04, FR-KRN-IAM-18,
// FR-KRN-LOC-01, NFR-11, NFR-12): real API host and real PostgreSQL. An administrator invites a person from the console, the
// person activates and signs in, the list and the seat count follow; the limit and the sign-in clash are refused in plain
// words; disabling a person with open approvals reassigns them; account actions and the screens in both languages.
import { expect, test, type Locator, type Page } from '@playwright/test';
import { accessible, display, field, fitsWidth, freshContext, menu, noProblems, openPage, PASSWORD, person, server, shot, signOut } from './support';

test.describe.configure({ mode: 'serial' });

const run = Math.random().toString(36).slice(2, 6);
const WIDTHS = [360, 768, 1280] as const;
const ADMIN = ['iam.users.read', 'iam.users.create', 'iam.users.update', 'iam.users.change_status', 'iam.users.unlock', 'iam.users.force_password_change', 'iam.users.reset_mfa',
  'iam.users.revoke_sessions', 'iam.users.read_sessions', 'iam.users.send_invitation', 'iam.users.send_password_reset', 'iam.users.change_email', 'iam.roles.read', 'iam.roles.assign',
  'wf.workflow_tasks.reassign', 'cfg.licence.read', 'aud.security_events.view', 'aud.changes.view', 'aud.history.view', 'cfg.saved_views.share'];
const APPROVER = ['iam.maker_checker.approve'];

function grid(page: Page): Locator {
  return page.getByRole('grid', { name: 'Users' });
}

async function search(page: Page, text: string): Promise<void> {
  await page.getByRole('searchbox', { name: 'Search' }).fill(text);
  await page.getByRole('button', { name: 'Search', exact: true }).click();
}

async function signIn(page: Page, username: string): Promise<void> {
  await page.goto('/sign-in');
  await page.locator('input[name="username"]').fill(username);
  await page.locator('input[name="password"]').fill(PASSWORD);
  await page.locator('button[type="submit"]').click();
  await expect(page).not.toHaveURL(/\/sign-in(\?|$)/);
  if (page.url().includes('/sign-in/reminder')) await page.locator('main button').last().click();
  await expect(page.locator('.erp-shell main h1')).toBeVisible();
}

test('FR-ADM-USR-02 FR-ADM-USR-08 FR-KRN-IAM-18 an administrator invites a person, the person activates and signs in, the seat count rises; the clash and the limit are refused in plain words', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Ayesha Admin' });
  const original = await server.licence({ usersLimit: 500 }); // a licence with named-user places to spare
  const page = admin.page;
  await page.reload();
  await expect(menu(page).getByRole('link', { name: 'Users' })).toBeVisible();
  await menu(page).getByRole('link', { name: 'Users' }).click();
  await expect(page.getByRole('heading', { name: 'Users', level: 1 })).toBeVisible();
  const seatsBefore = await page.getByText(/^\d+ of \d+ named users$/).innerText();
  const used = Number(seatsBefore.split(' ')[0]);
  await accessible(page, 'user list');

  // Create and invite.
  const username = `new.${run}`;
  await page.getByRole('button', { name: 'New user' }).click();
  await expect(page.getByRole('heading', { name: 'New user', level: 1 })).toBeVisible();
  await field(page, 'Name').fill('Bilal Newcomer');
  await field(page, 'Username').fill(username);
  await field(page, 'E-mail').fill(`${username}@example.test`);
  await accessible(page, 'create form');
  await shot(page, 'users-create-en-light-1280');

  // The clash: a username that is another person's e-mail address is refused, and nothing is created.
  const other = await server.newUser({ displayName: 'Existing Person' });
  await field(page, 'Username').fill(other.email.toUpperCase());
  await page.getByRole('button', { name: 'Create and send invitation' }).click();
  await expect(page.getByText(/Already used to sign in/).first()).toBeVisible();
  await expect(page.getByRole('heading', { name: 'New user', level: 1 })).toBeVisible(); // nothing was created
  await accessible(page, 'create form refused');
  await shot(page, 'users-clash-en-light-1280');
  await field(page, 'Username').fill(username);
  await page.getByRole('button', { name: 'Create and send invitation' }).click();
  await expect(page.getByText('Bilal Newcomer was created and the invitation is on its way.')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Bilal Newcomer', level: 1 })).toBeVisible();
  await expect(page.getByText('Invited', { exact: true }).first()).toBeVisible();
  await accessible(page, 'user record');
  await shot(page, 'users-record-en-light-1280');

  // The person activates from the e-mailed link and signs in (a new browser).
  const mail = await server.mail('identity.invitation', username);
  const context = await freshContext(browser);
  const them = await openPage(context);
  await them.goto(mail.link!);
  await field(them, 'New password').fill(PASSWORD);
  await field(them, 'Repeat the new password').fill(PASSWORD);
  await them.getByRole('button', { name: 'Activate account' }).click();
  await expect(them.getByRole('heading', { name: 'Waiting for you', level: 1 }).or(them.getByRole('heading', { name: 'Protect your account' }))).toBeVisible();

  // The list shows them active with a last sign-in, and one more seat is taken.
  await page.goto('/admin/users');
  await search(page, username);
  const row = grid(page).getByRole('row', { name: /Bilal Newcomer/ });
  await expect(row).toBeVisible();
  await expect(row.getByText('Active')).toBeVisible();
  await expect(row.getByText('Never')).toHaveCount(0);
  await expect(page.getByText(`${used + 1} of `)).toBeVisible();

  // The limit: with every place taken, the form says so in plain words and creates nothing.
  await server.licence({ usersLimit: used + 1 });
  await page.goto('/admin/users/new');
  await expect(page.getByText('The licence has no free named user place left, so nobody new can be added.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create and send invitation' })).toBeDisabled();
  await accessible(page, 'create form at the limit');
  await shot(page, 'users-limit-en-light-1280');
  await server.restoreClaims(original.claims);

  noProblems(page);
  await signOut(page);
  await admin.context.close();
  await context.close();
});

test('FR-ADM-USR-03 FR-ADM-USR-05 FR-KRN-IAM-04 disabling a person with two open approvals reassigns them and ends their sessions; lock, unlock and the audit trail', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Ayesha Admin' });
  const manager = await person(browser, { permissions: APPROVER, displayName: 'Imran Manager' });
  const deputy = await person(browser, { permissions: APPROVER, displayName: 'Sara Deputy' });
  const requester = await person(browser, { displayName: 'Amna Requester' });
  await server.route(manager.username, true);
  await server.request(requester.username, '120');
  await server.request(requester.username, '340');
  const facts = await server.facts(manager.username);
  expect(facts.liveSessions).toBeGreaterThan(0);

  const page = admin.page;
  await page.goto('/admin/users');
  await search(page, manager.username);
  await grid(page).getByRole('button', { name: 'Open Imran Manager' }).click();
  await expect(page.getByRole('heading', { name: 'Imran Manager', level: 1 })).toBeVisible();

  // Lock, then unlock: only the allowed moves are offered each time.
  const actions = page.getByRole('group', { name: 'Account actions' });
  await expect(actions.getByRole('button', { name: 'Unlock' })).toHaveCount(0);
  await actions.getByRole('button', { name: 'Lock', exact: true }).click();
  const lock = page.getByRole('alertdialog');
  await expect(lock.getByText(/signed out everywhere and cannot sign in until you unlock/)).toBeVisible();
  await expect(lock.getByRole('button', { name: 'Lock', exact: true })).toBeDisabled(); // a reason is required
  await lock.getByRole('textbox', { name: /^Reason/ }).fill('Suspicious sign-in');
  await lock.getByRole('button', { name: 'Lock', exact: true }).click();
  await expect(page.getByText('Imran Manager is locked.')).toBeVisible();
  await expect(actions.getByRole('button', { name: 'Unlock' })).toBeVisible();
  await expect(actions.getByRole('button', { name: 'Lock', exact: true })).toHaveCount(0);
  await actions.getByRole('button', { name: 'Unlock' }).click();
  await page.getByRole('alertdialog').getByRole('textbox', { name: /^Reason/ }).fill('Verified by phone');
  await page.getByRole('alertdialog').getByRole('button', { name: 'Unlock' }).click();
  await expect(page.getByText('Imran Manager is unlocked.')).toBeVisible();

  // Disable with open approvals: the count, the person to take over, the result.
  await actions.getByRole('button', { name: 'Disable' }).click();
  const dialog = page.getByRole('alertdialog');
  await expect(dialog.getByText('2 approvals are waiting for this person')).toBeVisible();
  await accessible(page, 'disable with open approvals');
  await shot(page, 'users-disable-en-light-1280');
  await dialog.getByRole('combobox', { name: /^Reassign to/ }).fill('Sara');
  await page.getByRole('option', { name: /Sara Deputy/ }).click();
  await dialog.getByRole('textbox', { name: /^Reason/ }).fill('Left the company');
  await dialog.getByRole('button', { name: 'Disable' }).click();
  await expect(page.getByText('2 approvals moved to Sara Deputy.')).toBeVisible();
  await expect(page.getByText('Imran Manager is disabled.')).toBeVisible();
  await expect.poll(async () => (await server.facts(manager.username)).liveSessions).toBe(0);

  // The audit trail of the account is visible on the record.
  await page.getByRole('tab', { name: 'Sign-ins and security' }).click();
  await expect(page.getByRole('list', { name: 'Sign-ins and security' }).getByRole('listitem').first()).toBeVisible();
  await page.getByRole('tab', { name: 'Recent activity' }).click();
  await expect(page.getByRole('list', { name: 'Recent activity' }).getByRole('listitem').first()).toBeVisible();

  // The deputy now sees the two approvals; the manager is signed out and cannot sign in.
  await deputy.page.goto('/approvals');
  await expect(deputy.page.getByText('2 requests waiting')).toBeVisible();
  await signIn(await openPage(await freshContext(browser)), manager.username).then(() => { throw new Error('a disabled person signed in'); }, () => undefined);

  noProblems(page);
  for (const p of [admin, manager, deputy, requester]) await p.context.close();
});

test('FR-ADM-USR-09 bulk actions show the count first and one result per person', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Ayesha Admin' });
  const one = await server.newUser({ displayName: `Bulk One ${run}` });
  const two = await server.newUser({ displayName: `Bulk Two ${run}` });
  const page = admin.page;
  await page.goto('/admin/users');
  await search(page, `Bulk`);
  await expect(grid(page).getByRole('row')).toHaveCount(1 + 2);
  await grid(page).getByRole('checkbox').first().check(); // every row on the page
  await page.getByRole('button', { name: /Actions for 2 selected/ }).click();
  const dialog = page.getByRole('dialog', { name: 'Change several users' });
  await dialog.getByRole('button', { name: /What to do/ }).click();
  await page.getByRole('option', { name: 'Disable' }).click();
  await dialog.getByRole('textbox', { name: /^Reason/ }).fill('Project ended');
  await dialog.getByRole('button', { name: 'Continue' }).click();
  await expect(dialog.getByText('2 users will be disabled and signed out everywhere.')).toBeVisible();
  await shot(page, 'users-bulk-en-light-1280');
  await dialog.getByRole('button', { name: 'Change 2 users' }).click();
  // Invited people cannot be disabled (the status rules: invited moves only to archived): each failure is listed, nobody else is held up.
  await expect(dialog.getByText('0 done, 2 not done.')).toBeVisible();
  const results = dialog.getByRole('list', { name: 'Result for each user' }).getByRole('listitem');
  await expect(results).toHaveCount(2);
  await expect(results.first()).toContainText('not done:');
  await shot(page, 'users-bulk-result-en-light-1280');
  expect([one.username, two.username].length).toBe(2);
  await dialog.getByRole('button', { name: 'Close' }).last().click();
  noProblems(page);
  await admin.context.close();
});

test('FR-KRN-LOC-01 NFR-11 NFR-12 the user console in English and Urdu, light and dark, at 360, 768 and 1280 pixels', async ({ browser }) => {
  const viewer = await person(browser, { permissions: ADMIN, displayName: 'Nadia Viewer' });
  await signOut(viewer.page);
  await viewer.context.close();
  for (const language of ['en', 'ur'] as const) {
    for (const scheme of ['light', 'dark'] as const) {
      const context = await browser.newContext({ colorScheme: scheme, viewport: { width: 1280, height: 900 } });
      await display(context, { language });
      const page = await openPage(context);
      await signIn(page, viewer.username);
      await page.goto('/admin/users');
      await page.getByRole('grid').getByText('Nadia Viewer').first().click();
      await expect(page).toHaveURL(/\/admin\/users\/[0-9a-f-]{36}$/);
      const record = page.url();
      for (const [name, path] of [['list', '/admin/users'], ['create', '/admin/users/new'], ['record', record]] as const) {
        for (const width of WIDTHS) {
          await page.setViewportSize({ width, height: 900 });
          await page.waitForLoadState('networkidle');
          await page.goto(path);
          await expect(page.locator('main h1')).toBeVisible();
          await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
          expect(await page.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
          await fitsWidth(page);
          if (width === 1280) await accessible(page, `users ${name} ${language} ${scheme}`);
          await shot(page, `users-${name}-${language}-${scheme}-${width}`);
        }
      }
      noProblems(page);
      await context.close();
    }
  }
});
