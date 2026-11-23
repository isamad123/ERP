// The application shell end to end (slice S1.12b, Acceptance; FR-KRN-ENT-03, FR-KRN-ENT-04, FR-KRN-TEN-09, FR-ADM-ORG-06,
// FR-HRM-NTF-07, FR-HRM-NTF-09, FR-KRN-LOC-01, NFR-11, NFR-12; spec 13 SHL, STA, A11Y). Real API host, real PostgreSQL; the
// control server sets up roles, companies, the licence and notifications as later admin screens and the notification
// consumer would. The scenarios run in order and restore the licence they change.
import { expect, test } from '@playwright/test';
import { accessible, display, field, fitsWidth, landHome, mainText, menu, menuEntries, noProblems, openPage, PASSWORD, person, server, shot, signOut } from './support';

test.describe.configure({ mode: 'serial' });

const run = Math.random().toString(36).slice(2, 6).toUpperCase();
const WIDTHS = [360, 768, 1280] as const;
const ADMIN_PERMISSIONS = ['cfg.licence.read', 'aud.export', 'job.runs.read', 'aud.changes.view', 'hrm.persons.read', 'hrm.attendance_days.read',
  'hrm.leave_requests.read', 'hrm.pay_runs.read'];

test('FR-KRN-ENT-03 the menu shows only what is licensed and permitted; an unlicensed address is the same "not found" as a nonsense one', async ({ browser }) => {
  const { claims } = await server.licence({ features: ['platform.base', 'hrm.core'] }); // no hrm.payroll
  try {
    const admin = await person(browser, { permissions: ADMIN_PERMISSIONS, displayName: 'Ayesha Khan' });
    const page = admin.page;
    const entries = await menuEntries(page);
    expect(entries).toEqual(expect.arrayContaining(['Home', 'Notifications', 'Approvals', 'People', 'Attendance', 'Leave', 'Licence', 'Background jobs', 'Audit log'])); // S1.15a: Settings, Feature flags and Sign-in page need cfg.setting_values.read and cfg.feature_flag_overrides.read, which this administrator lacks; a Business User has none either, so both lists are unchanged
    expect(entries).not.toContain('Payroll');
    await accessible(page, 'home with menu');

    await page.goto('/payroll');
    await expect(page.getByRole('heading', { name: 'Page not found', level: 1 })).toBeVisible();
    const unlicensed = await mainText(page);
    await page.goto('/payroll/runs/2026-11');
    await expect(page.getByRole('heading', { name: 'Page not found', level: 1 })).toBeVisible();
    expect(await mainText(page)).toBe(unlicensed);
    await page.goto('/no-such-page');
    await expect(page.getByRole('heading', { name: 'Page not found', level: 1 })).toBeVisible();
    expect(await mainText(page)).toBe(unlicensed); // nothing tells an unlicensed screen from an unknown one

    // A screen of a later slice opens "not available yet"; the menu marks the current page.
    await menu(page).getByRole('link', { name: 'People' }).click();
    await expect(page.getByRole('heading', { name: 'People', level: 1 })).toBeVisible();
    await expect(page.getByText('This screen is not available yet.')).toBeVisible();
    await expect(menu(page).getByRole('link', { name: 'People' })).toHaveAttribute('aria-current', 'page');

    // Licensed again: the Payroll entry appears and opens.
    await server.restoreClaims(claims);
    await page.goto('/');
    await expect(menu(page).getByRole('link', { name: 'Payroll' })).toBeVisible();

    // A person without the permissions sees no such entries, and the address says what is missing.
    const basic = await person(browser);
    const plain = await menuEntries(basic.page);
    expect(plain).toEqual(['Home', 'Notifications', 'Approvals', 'Companies', 'Branches', 'Locations', 'Organization units', 'Holiday calendars']); // S1.14b: and the units it may read; S1.12c: Business User reads branches (org.branches.read); S1.14a: and the organization lists it may read
    await basic.page.goto('/settings/licence');
    await expect(basic.page.getByRole('heading', { name: 'You cannot open this page', level: 1 })).toBeVisible();
    await expect(basic.page.getByText('Permission needed: See the licence page and the usage statements')).toBeVisible();
    await accessible(basic.page, 'permission denied');
    await basic.page.getByRole('link', { name: 'Go to the start page' }).click();
    await landHome(basic.page);
    noProblems(page);
    noProblems(basic.page);
    await admin.context.close();
    await basic.context.close();
  } finally {
    await server.restoreClaims(claims);
  }
});

test('FR-ADM-ORG-06 the company switcher shows the company in hand, changes it with a confirmation and remembers it', async ({ browser }) => {
  await server.company(`N${run}`, `North Traders ${run}`, `شمالی تاجر ${run}`);
  await server.company(`S${run}`, `South Mills ${run}`, `جنوبی ملز ${run}`);
  const admin = await person(browser, { permissions: ['iam.self.manage'] });
  const page = admin.page;
  const switcher = page.getByRole('button', { name: /^Company: .*\. Change company$/ });
  await expect(switcher).toBeVisible();
  await switcher.click();
  await page.getByRole('menuitem', { name: `North Traders ${run}` }).click();
  await expect(page.getByText(`Now working in North Traders ${run}`)).toBeVisible();
  await expect(page.getByRole('button', { name: `Company: North Traders ${run}. Change company` })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('button', { name: `Company: North Traders ${run}. Change company` })).toBeVisible(); // remembered in this browser
  await accessible(page, 'company switcher');

  // One company only: the name is shown, without a switcher.
  const single = await person(browser, { permissions: ['iam.self.manage', 'ntf.in_app_items.read'], companies: [`S${run}`], removeBaseline: true });
  await expect(single.page.locator('.erp-topbar-company')).toHaveText(`South Mills ${run}`);
  await expect(single.page.getByRole('button', { name: /Change company$/ })).toHaveCount(0);
  noProblems(page);
  await admin.context.close();
  await single.context.close();
});

test('FR-KRN-ENT-04 FR-KRN-TEN-09 licence banners for Warning, Grace and Read-only, limit warnings, and the locked page', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN_PERMISSIONS });
  const basic = await person(browser);
  const { claims } = await server.licence({ daysAfterEnd: -10, companiesLimit: 3 });
  try {
    // Warning: licence readers only, with the limit warnings beside it; hidden for the day on request.
    await admin.page.reload();
    await expect(admin.page.getByText('The licence ends soon')).toBeVisible();
    await expect(admin.page.getByText('A licence limit is almost reached')).toBeVisible();
    await expect(admin.page.getByText(/^Companies: \d+ of 3 in use/)).toBeVisible();
    await accessible(admin.page, 'warning banner');
    await shot(admin.page, 'banner-warning-en-light-1280');
    await basic.page.reload();
    await landHome(basic.page);
    await expect(basic.page.getByText('The licence ends soon')).toHaveCount(0);
    await expect(basic.page.getByText('A licence limit is almost reached')).toHaveCount(0);
    await admin.page.getByRole('button', { name: 'Hide for today' }).first().click();
    await expect(admin.page.getByText('The licence ends soon')).toHaveCount(0);
    await admin.page.reload();
    await landHome(admin.page);
    await expect(admin.page.getByText('The licence ends soon')).toHaveCount(0);

    // Grace and Read-only: everyone, never hidden; the data stays readable.
    for (const [days, title] of [[5, 'The licence has ended'], [20, 'Read-only: the licence has ended']] as const) {
      await server.licence({ daysAfterEnd: days });
      for (const p of [admin.page, basic.page]) {
        await p.reload();
        await landHome(p);
        await expect(p.getByText(title, { exact: true })).toBeVisible();
        await expect(p.getByRole('button', { name: 'Hide for today' })).toHaveCount(0);
      }
      await shot(basic.page, `banner-${days === 5 ? 'grace' : 'read-only'}-en-light-1280`);
    }
    await basic.page.goto('/notifications');
    await expect(basic.page.getByRole('heading', { name: 'Notifications', level: 1 })).toBeVisible(); // read-only keeps reading

    // Locked: a full page; licence readers get the licence page and the export, everyone else only the explanation.
    await server.licence({ daysAfterEnd: 60 });
    await admin.page.goto('/');
    await expect(admin.page.getByRole('heading', { name: 'The licence is locked', level: 1 })).toBeVisible();
    await expect(menu(admin.page)).toHaveCount(0);
    await accessible(admin.page, 'locked admin');
    await shot(admin.page, 'locked-admin-en-light-1280');
    await admin.page.getByRole('link', { name: 'Open the licence page' }).click();
    await expect(admin.page.getByRole('heading', { name: 'Licence', level: 1 })).toBeVisible();
    await basic.page.goto('/notifications');
    await expect(basic.page.getByRole('heading', { name: 'The licence is locked', level: 1 })).toBeVisible();
    await expect(basic.page.getByRole('link', { name: 'Open the licence page' })).toHaveCount(0);
    await expect(basic.page.getByRole('link', { name: 'Export data' })).toHaveCount(0);
    await shot(basic.page, 'locked-user-en-light-1280');
    noProblems(admin.page);
    noProblems(basic.page);
  } finally {
    await server.restoreClaims(claims);
    await server.licence({ daysAfterEnd: null });
  }
  await admin.context.close();
  await basic.context.close();
});

test('FR-HRM-NTF-09 FR-HRM-NTF-07 bell, panel and notification centre; a link to a screen the person may not open shows "permission denied"', async ({ browser }) => {
  const basic = await person(browser);
  const items = Array.from({ length: 24 }, (_, i) => ({ eventCode: 'workflow.request_approved', title: `Leave request ${i + 1} approved`, body: `Your request number ${i + 1} was approved.`, link: '/approvals', group: i % 2 === 0 ? 'my_requests' : 'approvals' }));
  await server.notify(basic.username, [...items, { eventCode: 'licence.warning', title: 'Licence ends soon', body: 'The licence ends in 10 days.', link: '/settings/licence', group: 'system' }]);
  const page = basic.page;
  await page.reload();
  await landHome(page);
  const bell = page.getByRole('button', { name: 'Notifications, 25 unread' });
  await expect(bell).toBeVisible();
  await expect(page.getByRole('heading', { name: '25 unread notifications' })).toBeVisible(); // the home page counts them too

  await bell.click();
  const panel = page.getByRole('dialog', { name: 'Notifications' });
  await expect(panel.getByRole('listitem')).toHaveCount(10);
  await accessible(page, 'bell panel');
  await shot(page, 'bell-panel-en-light-1280');
  await panel.getByRole('button', { name: /Licence ends soon/ }).click();
  await expect(page.getByRole('heading', { name: 'You cannot open this page', level: 1 })).toBeVisible(); // not the licence page
  await expect(page.getByRole('button', { name: 'Notifications, 24 unread' })).toBeVisible(); // marked read, refreshed on the page change

  await page.goto('/notifications');
  await expect(page.getByText('1–20 of 25')).toBeVisible();
  await accessible(page, 'notification centre');
  await page.getByRole('button', { name: 'Next' }).click();
  await expect(page.getByText('21–25 of 25')).toBeVisible();
  await field(page, 'Search').fill('request 7 ');
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(page.getByText('1–1 of 1')).toBeVisible();
  await field(page, 'Search').fill('');
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await page.getByRole('button', { name: /Group/ }).click();
  await page.getByRole('option', { name: 'System' }).click();
  await expect(page.getByText('1–1 of 1')).toBeVisible();
  await expect(page.getByRole('button', { name: /Licence ends soon/ })).toBeVisible();
  await page.getByRole('button', { name: 'Mark as unread' }).click();
  await expect(page.getByRole('button', { name: 'Mark as read' })).toBeVisible();
  await page.getByRole('button', { name: /Group/ }).click();
  await page.getByRole('option', { name: 'All groups' }).click();
  await page.getByText('Unread only').click();
  await expect(page.getByText('1–20 of 25')).toBeVisible();
  await page.getByRole('button', { name: 'Mark all as read' }).click();
  await expect(page.getByText('No notifications match')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Notifications', exact: true })).toBeVisible(); // the bell shows no count
  noProblems(page);
  await basic.context.close();
});

test('FR-KRN-LOC-01 FR-KRN-LOC-02 each sign-in starts in the language stored for the person; the menu choice lasts until the next sign-in', async ({ browser }) => {
  const context = await browser.newContext(); // no display choice stored in this browser
  const urdu = await person(browser, { language: 'ur', context, homeHeading: 'آپ کے منتظر' });
  const page = urdu.page;
  expect(await page.evaluate(() => document.documentElement.dir)).toBe('rtl');
  await page.getByRole('button', { name: /کا اکاؤنٹ مینو$/ }).click();
  await page.getByRole('button', { name: /زبان/ }).click();
  await page.getByRole('option', { name: 'English' }).click();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('heading', { name: 'Waiting for you', level: 1 })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Waiting for you', level: 1 })).toBeVisible(); // not a new sign-in
  await signOut(page);
  await field(page, 'Username or e-mail address').fill(urdu.username);
  await field(page, 'Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await landHome(page, 'آپ کے منتظر'); // the stored language again
  noProblems(page);
  await context.close();
});

test('spec 13 A11Y-01 the shell works by keyboard: skip link, menu, bell and user menu with visible focus', async ({ browser }) => {
  const basic = await person(browser);
  const page = basic.page;
  await page.reload();
  await landHome(page);
  await page.keyboard.press('Tab');
  const skip = page.getByRole('link', { name: 'Skip to content' });
  await expect(skip).toBeFocused();
  await expect(skip).toBeInViewport();
  await page.keyboard.press('Enter');
  await expect(page.locator('main')).toBeFocused();

  const userMenu = page.getByRole('button', { name: /^Account menu of / });
  await userMenu.focus();
  expect(await userMenu.evaluate((el) => getComputedStyle(el).outlineStyle)).not.toBe('none');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('dialog', { name: 'Your account and display' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: 'Your account and display' })).toHaveCount(0);
  await expect(userMenu).toBeFocused(); // focus comes back to the button

  const bell = page.getByRole('button', { name: /^Notifications/ });
  await bell.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('dialog', { name: 'Notifications' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(bell).toBeFocused();

  await menu(page).getByRole('link', { name: 'Notifications' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Notifications', level: 1 })).toBeVisible();
  await expect(page.locator('main')).toBeFocused(); // a page change moves focus to the new content
  noProblems(page);
  await basic.context.close();
});

test('FR-KRN-IAM-04 R-071 sign-out that cannot reach the server keeps the person signed in and says so; "Try again" then signs out', async ({ browser }) => {
  const basic = await person(browser);
  const page = basic.page;
  await page.route('**/api/iam/auth/sign-out', (route) => route.abort('failed'));
  await page.getByRole('button', { name: /^Account menu of / }).click();
  await page.getByRole('button', { name: 'Sign out' }).click();
  const alert = page.getByRole('alertdialog', { name: 'Sign-out did not work' });
  await expect(alert).toBeVisible();
  await expect(alert).toContainText('If it keeps failing, close the browser.');
  await expect(page.getByText('You are signed out.')).toHaveCount(0);
  await accessible(page, 'sign-out failure');
  await shot(page, 'r071-sign-out-failed');
  await page.reload(); // the server session and its cookie remain, so the person is still signed in
  await landHome(page);
  await page.getByRole('button', { name: /^Account menu of / }).click();
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(alert).toBeVisible();
  await page.unroute('**/api/iam/auth/sign-out');
  await alert.getByRole('button', { name: 'Try again' }).click();
  await expect(page.getByText('You are signed out.')).toBeVisible();
  await page.reload(); // really ended at the server: no way back in without signing in
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  noProblems(page);
  await basic.context.close();
});

test('FR-KRN-LOC-01 NFR-11 the shell in English and Urdu, light and dark, at 360, 768 and 1280 pixels', async ({ browser }) => {
  // Without job.runs.read, so the job screens show "permission denied".
  const admin = await person(browser, { permissions: ADMIN_PERMISSIONS.filter((p) => p !== 'job.runs.read'), displayName: 'Ayesha Khan' });
  await server.notify(admin.username, [
    { eventCode: 'workflow.task_assigned', title: 'Leave request of Bilal waits for your approval', body: 'Annual leave, 3 days from 1 December.', link: '/approvals', group: 'approvals' },
    { eventCode: 'licence.warning', title: 'The licence ends in 30 days', body: 'Renew the licence to keep full use.', link: '/settings/licence', group: 'system' },
  ]);
  await signOut(admin.page); // a person holds at most 3 sessions (iam.session.max_concurrent)
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
      await expect(page).not.toHaveURL(/\/sign-in(\?|$)/); // the sign-in page has a main heading too
      if (page.url().includes('/sign-in/reminder')) await page.getByRole('button', { name: 'Remind me later' }).click();
      await expect(page.locator('.erp-shell main h1')).toBeVisible();
      // The person's stored language (English) applies at sign-in; the screenshots then switch to the language asked for.
      await display(context, { language });
      for (const [name, path] of [['home', '/'], ['notifications', '/notifications'], ['not-found', '/no-such-page'], ['not-yet', '/payroll'], ['denied', '/admin/jobs/health']] as const) {
        for (const width of WIDTHS) {
          await page.setViewportSize({ width, height: 900 });
          await page.goto(path);
          await expect(page.locator('main h1')).toBeVisible();
          expect(await page.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
          await fitsWidth(page);
          if (width === 1280) await accessible(page, `${name} ${language} ${scheme}`);
          await shot(page, `shell-${name}-${language}-${scheme}-${width}`);
        }
      }

      // The menu: beside the content from 600 pixels, mirrored in Urdu; a drawer on phones.
      await page.setViewportSize({ width: 1280, height: 900 });
      await page.goto('/');
      const side = await page.locator('.erp-shell-side').boundingBox();
      if (language === 'ur') expect(side!.x).toBeGreaterThan(640);
      else expect(side!.x).toBeLessThan(10);
      await page.getByRole('button', { name: /^(Notifications|اطلاعات)/ }).first().click();
      await expect(page.getByRole('dialog').getByRole('listitem')).toHaveCount(2);
      await accessible(page, `bell panel ${language} ${scheme}`);
      await shot(page, `shell-bell-${language}-${scheme}-1280`);
      await page.keyboard.press('Escape');
      await page.getByRole('button', { name: /^(Account menu of|.* کا اکاؤنٹ مینو$)/ }).click();
      await accessible(page, `user menu ${language} ${scheme}`);
      await shot(page, `shell-user-menu-${language}-${scheme}-1280`);
      await page.keyboard.press('Escape');
      await page.setViewportSize({ width: 360, height: 800 });
      await page.getByRole('button', { name: language === 'en' ? 'Open the menu' : 'مینو کھولیں' }).click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await accessible(page, `menu drawer ${language} ${scheme}`);
      await shot(page, `shell-drawer-${language}-${scheme}-360`);
      noProblems(page);
      await page.keyboard.press('Escape');
      await page.setViewportSize({ width: 1280, height: 900 });
      await page.getByRole('button', { name: /^(Account menu of|.* کا اکاؤنٹ مینو$)/ }).click();
      await page.getByRole('button', { name: language === 'en' ? 'Sign out' : 'سائن آؤٹ کریں' }).click(); // a person holds at most 3 sessions
      await expect(page).toHaveURL(/\/sign-in/);
      await context.close();
    }
  }
});

test('FR-KRN-LOC-01 pseudo-locale: every text of the shell comes from the catalogues and the layout mirrors', async ({ browser }) => {
  const admin = await person(browser, { permissions: ADMIN_PERMISSIONS.filter((p) => p !== 'job.runs.read') });
  const context = await browser.newContext();
  await display(context, { language: 'en', pseudo: true });
  const page = await openPage(context);
  await page.goto('/sign-in');
  await page.locator('input[name="username"]').fill(admin.username);
  await page.locator('input[name="password"]').fill(PASSWORD);
  await page.locator('button[type="submit"]').click();
  await expect(page).not.toHaveURL(/\/sign-in(\?|$)/);
  if (page.url().includes('/sign-in/reminder')) await page.locator('main button').last().click();
  await expect(page.locator('.erp-shell main h1')).toBeVisible();
  for (const path of ['/', '/notifications', '/no-such-page', '/payroll', '/admin/jobs/health']) {
    await page.goto(path);
    await expect(page.locator('main h1')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.dir)).toBe('rtl');
    const plain = await page.evaluate(() => {
      // Data is not text of the product: the person's name and the company names come from the customer.
      const customerData = '.erp-topbar-company, .erp-topbar-company-name, .erp-topbar-user-name';
      const found: string[] = [];
      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        const text = node.textContent?.trim() ?? '';
        if (node.parentElement?.closest(customerData)) continue;
        if (/\p{L}/u.test(text) && !text.includes('⟦') && !text.includes('⟧')) found.push(text);
      }
      const labels = [...document.querySelectorAll('[aria-label],[placeholder],[title]')]
        .flatMap((el) => ['aria-label', 'placeholder', 'title'].map((a) => el.getAttribute(a) ?? ''))
        .filter((t) => /\p{L}/u.test(t) && !t.includes('⟦'));
      return [...found, ...labels];
    });
    expect(plain, `hard-coded text on ${path}`).toEqual([]);
    await shot(page, `pseudo-shell${path.replace(/[/?=]/g, '_')}`);
  }
  await admin.context.close();
  await context.close();
});
