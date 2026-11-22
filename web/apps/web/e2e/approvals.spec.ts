// The approval inbox end to end (slice S1.12d Acceptance; FR-KRN-WFL-08, FR-HRM-SSP-02, FR-KRN-IAM-14, FR-XM-32,
// FR-KRN-LOC-01, NFR-11, NFR-12): the test document type of the browser-test host, real API host and real PostgreSQL. The
// control server puts routes in force, starts requests as the requester, records a delegation and holds a setting change
// made by another administrator, as the modules and later screens will. The scenarios run in order.
import { expect, test, type Locator, type Page } from '@playwright/test';
import { accessible, display, fitsWidth, freshContext, menu, noProblems, openPage, PASSWORD, person, server, shot, signOut } from './support';

test.describe.configure({ mode: 'serial' });

const WIDTHS = [360, 768, 1280] as const;
const APPROVER = ['iam.maker_checker.approve'];

function grid(page: Page): Locator {
  return page.getByRole('grid', { name: 'Requests' });
}

function detail(page: Page): Locator {
  return page.getByRole('complementary', { name: 'Request details' });
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

test('FR-KRN-WFL-08 FR-XM-32 a manager decides requests, a delegate acts for Sara, bulk approval reports per task, the requester gets no decision', async ({ browser }) => {
  const manager = await person(browser, { permissions: APPROVER, displayName: 'Imran Manager' });
  const sara = await person(browser, { displayName: 'Sara Khan' });
  const requester = await person(browser, { displayName: 'Amna Requester' });

  // Three requests for the manager (bulk approval allowed), one for Sara, who has delegated to the manager.
  await server.route(manager.username, true);
  const first = await server.request(requester.username, '1250.5000');
  await server.request(requester.username, '300');
  await server.request(requester.username, '45');
  await server.route(sara.username, true);
  await server.delegate(sara.username, manager.username);
  await server.request(requester.username, '980');

  const page = manager.page;
  await menu(page).getByRole('link', { name: 'Approvals' }).click();
  await expect(page.getByRole('heading', { name: 'Approvals', level: 1 })).toBeVisible();
  await expect(page.getByText('4 requests waiting')).toBeVisible();
  await expect(grid(page).getByRole('row')).toHaveCount(1 + 4);
  await expect(grid(page).getByText(/For .Sara Khan./)).toHaveCount(1); // the delegated task says for whom
  await accessible(page, 'inbox');

  // The detail beside the list: what is asked, who asked, the history; only the decisions the engine allows.
  await grid(page).getByRole('button', { name: 'Open Expense claim of 1250.5000' }).click();
  await expect(page).toHaveURL(new RegExp(`/approvals/tasks/${first.taskIds[0]}$`));
  const panel = detail(page);
  await expect(panel.getByRole('heading', { name: 'Expense claim of 1250.5000' })).toBeVisible();
  await expect(panel.getByText('1,250.5000')).toBeVisible(); // the amount as an exact decimal
  await expect(panel.getByText('Amna Requester').first()).toBeVisible();
  await expect(panel.getByRole('group', { name: 'Your decision' }).getByRole('button')).toHaveText(['Approve', 'Reject', 'Return for changes', 'Ask for information']);
  await expect(panel.getByText('Submitted', { exact: true }).first()).toBeVisible();
  await accessible(page, 'task detail');
  await panel.getByRole('button', { name: 'Approve' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('textbox').fill('Receipts checked');
  await dialog.getByRole('button', { name: 'Approve' }).click();
  await expect(page.getByText('Approved. Expense claim of 1250.5000 is now fully approved.')).toBeVisible();
  await expect(grid(page).getByRole('row')).toHaveCount(1 + 3);

  // Bulk approval: the count first, then one result per task.
  await grid(page).getByRole('checkbox', { name: 'Select all rows shown' }).check();
  await page.getByRole('button', { name: 'Approve selected' }).click();
  const bulk = page.getByRole('dialog', { name: 'Approve several requests' });
  await expect(bulk.getByText(/3 requests will be approved/)).toBeVisible();
  await accessible(page, 'bulk approval');
  await bulk.getByRole('button', { name: 'Approve 3 requests' }).click();
  await expect(bulk.getByText('Approved: 3. Not approved: 0.')).toBeVisible();
  await expect(bulk.getByRole('listitem')).toHaveCount(3);
  await shot(page, 'approvals-bulk-result-en-light-1280');
  await bulk.getByRole('button', { name: 'Close' }).last().click();
  await expect(page.getByText('Nothing is waiting for you')).toBeVisible();
  noProblems(page);

  // The requester: their request is not in their inbox, and opening its task offers no decision (FR-XM-32).
  const own = await server.request(requester.username, '77');
  await requester.page.waitForLoadState('networkidle');
  await requester.page.goto('/approvals');
  await expect(requester.page.getByText('Nothing is waiting for you')).toBeVisible();
  await expect(requester.page.locator('main [aria-busy="true"]')).toHaveCount(0);
  await requester.page.waitForLoadState('networkidle');
  await requester.page.goto(`/approvals/tasks/${own.taskIds[0]}`);
  await expect(detail(requester.page).getByText('This request no longer waits for you')).toBeVisible();
  await expect(requester.page.getByRole('button', { name: 'Approve' })).toHaveCount(0);
  noProblems(requester.page);

  for (const p of [sara, requester]) {
    await signOut(p.page);
    await p.context.close();
  }
  await signOut(page);
  await manager.context.close();
});

test('FR-KRN-IAM-14 FR-XM-32 a setting change held for a second administrator is approved from the inbox and takes effect', async ({ browser }) => {
  // On the real access engine (S1.13b, Q10) approving a change to access settings needs the right to make it: the checker also holds cfg.setting_values.update.
  const checker = await person(browser, { permissions: [...APPROVER, 'cfg.setting_values.update'], displayName: 'Bilal Checker' });
  const held = await server.heldSetting(42, 'Longer drafts for the audit');
  const page = checker.page;
  await page.waitForLoadState('networkidle');
  await page.goto('/approvals/changes');
  const card = page.getByRole('listitem', { name: /Change the setting .Days to keep drafts./ }).first();
  await expect(card).toBeVisible();
  await expect(card.getByText('Their reason: Longer drafts for the audit')).toBeVisible();
  await expect(card.getByText('42', { exact: true })).toBeVisible(); // the new value (exact: the random key may contain "42")
  await accessible(page, 'approvals of changes');
  await shot(page, 'approvals-changes-en-light-1280');
  expect(await server.settingValue(held.key)).toBeNull(); // nothing changes before the approval
  await card.getByRole('button', { name: 'Approve' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Approve' }).click();
  await expect(page.getByText(/Approved and applied: Change the setting/)).toBeVisible();
  await expect.poll(() => server.settingValue(held.key)).toBe('42');
  await expect(page.getByRole('listitem', { name: /Change the setting .Days to keep drafts./ })).toHaveCount(0);
  noProblems(page);
  await signOut(page);
  await checker.context.close();
});

test('FR-KRN-LOC-01 NFR-11 NFR-12 the inbox in English and Urdu, light and dark, at 360, 768 and 1280 pixels', async ({ browser }) => {
  const viewer = await person(browser, { permissions: APPROVER, displayName: 'Nadia Viewer' });
  const asker = await person(browser, { displayName: 'Hamza Asker' });
  await server.route(viewer.username, true);
  const one = await server.request(asker.username, '1500');
  await server.request(asker.username, '20.25');
  await server.heldSetting(30, 'Shorter drafts');
  for (const p of [viewer, asker]) {
    await signOut(p.page);
    await p.context.close();
  }

  for (const language of ['en', 'ur'] as const) {
    for (const scheme of ['light', 'dark'] as const) {
      const context = await browser.newContext({ colorScheme: scheme, viewport: { width: 1280, height: 900 } });
      await display(context, { language });
      const page = await openPage(context);
      await signIn(page, viewer.username);
      for (const [name, path] of [['inbox', '/approvals'], ['task', `/approvals/tasks/${one.taskIds[0]}`], ['changes', '/approvals/changes']] as const) {
        for (const width of WIDTHS) {
          await page.setViewportSize({ width, height: 900 });
          await page.waitForLoadState('networkidle');
          await page.goto(path);
          await expect(page.locator('main h1')).toBeVisible();
          await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
          if (name === 'task') await expect(detail(page).or(page.getByRole('complementary')).locator('h2').first()).toBeVisible();
          else await expect(page.locator('main [role="tabpanel"]')).toBeVisible();
          expect(await page.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
          await fitsWidth(page);
          if (width === 1280) await accessible(page, `approvals ${name} ${language} ${scheme}`);
          await shot(page, `approvals-${name}-${language}-${scheme}-${width}`);
        }
      }
      noProblems(page);
      await page.getByRole('button', { name: /^(Account menu of|.* کا اکاؤنٹ مینو$)/ }).click();
      await page.getByRole('button', { name: language === 'en' ? 'Sign out' : 'سائن آؤٹ کریں' }).click();
      await expect(page).toHaveURL(/\/sign-in/);
      await context.close();
    }
  }

  // Pseudo-locale: every text of the inbox comes from the catalogues or the data.
  const context = await freshContext(browser);
  await display(context, { language: 'en', pseudo: true });
  const page = await openPage(context);
  await signIn(page, viewer.username);
  for (const path of ['/approvals', `/approvals/tasks/${one.taskIds[0]}`, '/approvals/changes']) {
    await page.waitForLoadState('networkidle');
    await page.goto(path);
    await expect(page.locator('main h1')).toBeVisible();
    await expect(page.locator('main [aria-busy="true"]')).toHaveCount(0);
    await page.waitForLoadState('networkidle');
    const plain = await page.evaluate(() => {
      const company = document.querySelector('.erp-topbar-company-name, .erp-topbar-company')?.textContent?.trim() ?? '';
      // Also left out: the hidden native select that mirrors a choice list for form filling (its options are the data
      // shown in the list: requesters, steps, kinds), and the date segments, whose placeholders and names come from React
      // Aria's own translations (S1.12d deviation ak); times are formatted values.
      const customerData = '.erp-topbar-company, .erp-topbar-company-name, .erp-topbar-user-name, td, dd, bdi, .erp-code, [role="gridcell"], select, .erp-date-segment, time';
      const found: string[] = [];
      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        const text = node.textContent?.trim() ?? '';
        if (node.parentElement?.closest(customerData) || text === company) continue;
        if (/\p{L}/u.test(text) && !text.includes('⟦') && !text.includes('⟧')) found.push(text);
      }
      const labels = [...document.querySelectorAll('[aria-label],[placeholder],[title]')].filter((el) => !el.closest('.erp-date-segment'))
        .flatMap((el) => ['aria-label', 'placeholder', 'title'].map((a) => el.getAttribute(a) ?? ''))
        .filter((t) => /\p{L}/u.test(t) && !t.includes('⟦'));
      return [...found, ...labels];
    });
    expect(plain, `hard-coded text on ${path}`).toEqual([]);
    await shot(page, `pseudo-approvals${new URL(path, page.url()).pathname.replace(/[^a-z0-9-]+/gi, '_').slice(0, 40)}`);
  }
  noProblems(page);
  await context.close();
});
