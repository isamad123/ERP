// Sign-in end to end (slice S1.12a, Acceptance; FR-KRN-IAM-01, FR-KRN-IAM-02, FR-KRN-IAM-04, FR-ADM-USR-02,
// FR-ADM-USR-24, FR-KRN-LOC-01; spec 13 FR-UX-SEC-04, FR-UX-SEC-07, A11Y, I18). Real API host, real PostgreSQL, the
// e-mailed links and codes as the sender job would send them. The scenarios build on each other and run in order.
import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { accessible, advance, display, field, fitsWidth, NEW_PASSWORD, nextCode, noProblems, openPage, PASSWORD, server, shot } from './support';

test.describe.configure({ mode: 'serial' });

/** The first person: activated, authenticator enrolled. Later scenarios sign this person in. */
const alice = { username: '', secret: '', password: PASSWORD };

async function signIn(page: Page, username: string, password: string): Promise<void> {
  await page.goto('/sign-in');
  await field(page, 'Username or e-mail address').fill(username);
  await field(page, 'Password').fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
}

async function verifyWithAuthenticator(pages: Page[], page: Page, secret: string, remember = false): Promise<void> {
  await expect(page.getByRole('heading', { name: 'Second step' })).toBeVisible();
  await field(page, 'Code from the app').fill(await nextCode(pages, secret));
  if (remember) await page.getByText(/Remember this browser for 30 days/).click();
  await page.getByRole('button', { name: 'Verify' }).click();
}

async function home(page: Page): Promise<void> {
  await expect(page.getByRole('heading', { name: 'You are signed in' })).toBeVisible();
}

async function signOut(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByText('You are signed out.')).toBeVisible();
}

async function freshContext(browser: Browser): Promise<BrowserContext> {
  const context = await browser.newContext();
  await display(context, { language: 'en' });
  return context;
}

test('FR-ADM-USR-02 FR-KRN-IAM-02 a new person activates from the e-mailed link, enrols an authenticator, signs out and signs in with the code', async ({ browser }) => {
  await server.setting('iam.mfa.required', 'true'); // grace period of 7 days: the reminder offers "Set up now"
  const context = await freshContext(browser);
  const page = await openPage(context);
  const user = await server.newUser();
  alice.username = user.username;
  const { link } = await server.mail('identity.invitation', user.username);
  expect(link).toMatch(/^\/activate\?token=/);

  await page.goto(link!);
  await expect(page.getByRole('heading', { name: 'Activate your account' })).toBeVisible();
  await accessible(page, 'activate');
  await shot(page, 'activate-en-light-1280');
  await field(page, 'New password').fill(PASSWORD);
  await field(page, 'Repeat the new password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Activate account' }).click();

  await expect(page.getByRole('heading', { name: 'Protect your account' })).toBeVisible();
  await accessible(page, 'reminder');
  await shot(page, 'enrol-reminder-en-light-1280');
  await page.getByRole('button', { name: 'Set up now' }).click();
  await expect(page.getByRole('heading', { name: 'Set up the second step' })).toBeVisible();
  await accessible(page, 'set-up choice');
  await shot(page, 'enrol-choice-en-light-1280');
  await field(page, 'Your current password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Continue' }).click();

  await expect(page.getByRole('img', { name: 'QR code for your authenticator app' })).toBeVisible();
  await accessible(page, 'set-up QR');
  await shot(page, 'enrol-qr-en-light-1280');
  alice.secret = (await page.locator('.erp-auth-secret').innerText()).replace(/\s/g, '');
  expect(alice.secret).toMatch(/^[A-Z2-7]{16,}$/);
  await field(page, 'Code shown in the app').fill(await server.totp(alice.secret));
  await page.getByRole('button', { name: 'Finish set-up' }).click();

  await expect(page.getByRole('heading', { name: 'Save your recovery codes' })).toBeVisible();
  expect(await page.getByRole('list', { name: 'Recovery codes' }).getByRole('listitem').count()).toBeGreaterThanOrEqual(5);
  await accessible(page, 'recovery codes');
  await shot(page, 'recovery-codes-en-light-1280');
  await page.getByRole('button', { name: 'I have saved these codes' }).click();
  await home(page);

  // The refresh token is out of reach of page scripts (ruling Q3).
  const visible = await page.evaluate(() => ({ cookie: document.cookie, local: JSON.stringify(window.localStorage), session: JSON.stringify(window.sessionStorage) }));
  expect(visible.cookie).not.toContain('erp_rt');
  expect(visible.local + visible.session).not.toMatch(/eyJ|erp_rt|refresh/i);
  const cookie = (await context.cookies()).find((c) => c.name === '__Secure-erp_rt');
  expect(cookie?.httpOnly).toBe(true);
  expect(cookie?.secure).toBe(true);
  expect(cookie?.sameSite).toBe('Strict');
  expect(cookie?.path).toBe('/api/iam/auth');

  await signOut(page);
  await signIn(page, alice.username, PASSWORD);
  await accessible(page, 'second step');
  await shot(page, 'second-step-en-light-1280');
  await verifyWithAuthenticator([page], page, alice.secret);
  await home(page);
  noProblems(page);
  await context.close();
});

test('FR-KRN-IAM-02 with e-mail codes allowed, a person enrols and signs in with an e-mailed code', async ({ browser }) => {
  await server.setting('iam.mfa.allowed_methods', '["totp","email"]');
  const context = await freshContext(browser);
  const page = await openPage(context);
  const user = await server.newUser();
  await page.goto((await server.mail('identity.invitation', user.username)).link!);
  await field(page, 'New password').fill(PASSWORD);
  await field(page, 'Repeat the new password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Activate account' }).click();
  await page.getByRole('button', { name: 'Set up now' }).click();
  await page.getByText('Code by e-mail', { exact: true }).click();
  await field(page, 'Your current password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Continue' }).click();
  await expect(page.getByText('We sent a code to your e-mail address. Enter it to finish.')).toBeVisible();
  await field(page, 'Code from the e-mail').fill((await server.mail('identity.email_code', user.username)).code!);
  await page.getByRole('button', { name: 'Finish set-up' }).click();
  await page.getByRole('button', { name: 'I have saved these codes' }).click();
  await home(page);
  await signOut(page);

  await signIn(page, user.username, PASSWORD);
  await expect(page.getByRole('heading', { name: 'Second step' })).toBeVisible();
  await page.getByRole('button', { name: 'Send code' }).click();
  await expect(page.getByText(/A code is on its way/)).toBeVisible();
  await accessible(page, 'e-mail code');
  await shot(page, 'second-step-email-en-light-1280');
  await field(page, 'Code from the e-mail').fill((await server.mail('identity.email_code', user.username)).code!);
  await page.getByRole('button', { name: 'Verify' }).click();
  await home(page);
  noProblems(page);
  await context.close();
});

test('FR-KRN-IAM-02 after the grace period the required enrolment asks for the authenticator code and an e-mailed code', async ({ browser }) => {
  await server.setting('iam.mfa.grace_days', '0');
  try {
    const context = await freshContext(browser);
    const page = await openPage(context);
    const user = await server.newUser();
    await page.goto((await server.mail('identity.invitation', user.username)).link!);
    await field(page, 'New password').fill(PASSWORD);
    await field(page, 'Repeat the new password').fill(PASSWORD);
    await page.getByRole('button', { name: 'Activate account' }).click();
    await expect(page.getByRole('heading', { name: 'Set up the second step' })).toBeVisible();
    await expect(field(page, 'Your current password')).toHaveCount(0); // the challenge proves the password
    await page.getByRole('button', { name: 'Continue' }).click();
    const secret = (await page.locator('.erp-auth-secret').innerText()).replace(/\s/g, '');
    await field(page, 'Code shown in the app').fill(await server.totp(secret));
    await field(page, 'Code sent to your e-mail address').fill((await server.mail('identity.enrolment_code', user.username)).code!);
    await accessible(page, 'required enrolment');
    await shot(page, 'enrol-required-en-light-1280');
    await page.getByRole('button', { name: 'Finish set-up' }).click();
    await page.getByRole('button', { name: 'I have saved these codes' }).click();
    await home(page);
    await context.close();
  } finally {
    await server.setting('iam.mfa.grace_days', '7');
  }
});

test('FR-KRN-IAM-01 forgot password gives the same answer and timing for a known and an unknown login; the reset link sets a new password', async ({ browser }) => {
  const context = await freshContext(browser);
  const page = await openPage(context);
  const timeOf = async (login: string) => {
    await page.goto('/forgot-password');
    await field(page, 'Username or e-mail address').fill(login);
    const started = Date.now();
    await page.getByRole('button', { name: 'Send reset link' }).click();
    const message = page.getByText(/If an account exists for what you entered/);
    await expect(message).toBeVisible();
    return { ms: Date.now() - started, text: await message.innerText() };
  };
  const known = await timeOf(alice.username);
  await accessible(page, 'forgot done');
  await shot(page, 'forgot-done-en-light-1280');
  const unknown = await timeOf('nobody-' + alice.username);
  expect(unknown.text).toBe(known.text);
  expect(known.ms).toBeGreaterThanOrEqual(350);
  expect(unknown.ms).toBeGreaterThanOrEqual(350);
  expect(Math.abs(known.ms - unknown.ms)).toBeLessThan(1500);

  const { link } = await server.mail('identity.password_reset', alice.username);
  expect(link).toMatch(/^\/reset-password\?token=/);
  await page.goto(link!);
  await expect(page.getByRole('heading', { name: 'Set a new password' })).toBeVisible();
  await accessible(page, 'reset');
  await shot(page, 'reset-en-light-1280');
  await field(page, 'New password').fill(NEW_PASSWORD);
  await field(page, 'Repeat the new password').fill('Not-The-Same-77');
  await page.getByRole('button', { name: 'Save new password' }).click();
  await expect(page.getByText('The two passwords are not the same.')).toBeVisible();
  await field(page, 'Repeat the new password').fill(NEW_PASSWORD);
  await page.getByRole('button', { name: 'Save new password' }).click();
  await expect(page.getByText('Your password was changed. Sign in with the new password.')).toBeVisible();
  alice.password = NEW_PASSWORD;

  // A wrong password gets the one message that never says whether the account exists.
  await signIn(page, alice.username, 'Wrong-Password-11');
  await expect(page.getByText(/Sign-in failed/)).toBeVisible();
  await signIn(page, alice.username, alice.password);
  await verifyWithAuthenticator([page], page, alice.secret);
  await home(page);
  noProblems(page);
  await context.close();
});

test('FR-KRN-IAM-04 two tabs stay signed in for 30 minutes of activity; idle warning and end at the idle time; back to the same screen', async ({ browser }) => {
  const context = await freshContext(browser);
  const one = await openPage(context);
  await one.goto('/?tab=one');
  await expect(one).toHaveURL(/\/sign-in\?return=/);
  await field(one, 'Username or e-mail address').fill(alice.username);
  await field(one, 'Password').fill(alice.password);
  await one.getByRole('button', { name: 'Sign in', exact: true }).click();
  await verifyWithAuthenticator([one], one, alice.secret);
  await expect(one).toHaveURL(/\/\?tab=one$/);
  await home(one);

  const two = await openPage(context);
  await two.goto('/?tab=two');
  await home(two); // took the token from the first tab
  const pages = [one, two];

  for (let step = 0; step < 11; step += 1) {
    const active = step % 2 === 0 ? one : two;
    await active.mouse.click(5, 300);
    await active.keyboard.press('Shift');
    await advance(pages, 3 * 60_000);
  }
  await home(one);
  await home(two);
  const facts = await server.facts(alice.username);
  expect(facts.reusedSessions).toBe(0);
  expect(facts.liveSessions).toBeGreaterThanOrEqual(1);

  // No activity: the warning two minutes before the idle end, then the end at the server's idle time. The last
  // activity still earns one refresh (when the access token is due), so the idle time counts from that refresh.
  await advance(pages, 20 * 60_000);
  await expect(one.getByRole('alertdialog')).toHaveCount(0);
  const warning = one.getByRole('alertdialog', { name: 'Are you still there?' });
  let quiet = 20 * 60_000;
  while (quiet < 35 * 60_000 && !(await warning.isVisible())) {
    await advance(pages, 30_000);
    quiet += 30_000;
  }
  await expect(warning).toBeVisible();
  // 30 minutes after the last refresh, less 2; the refresh the last activity earned comes within one token life (5
  // minutes) before or after the start of the quiet time. The end two minutes later is checked next.
  expect(quiet).toBeGreaterThanOrEqual(22 * 60_000);
  expect(quiet).toBeLessThanOrEqual(33 * 60_000);
  await accessible(one, 'idle warning');
  await shot(one, 'idle-warning-en-light-1280');
  await advance(pages, 2 * 60_000);
  await expect(one).toHaveURL(/\/sign-in\?return=%2F%3Ftab%3Done&reason=expired/);
  await expect(one.getByText('You were signed out because the session ended. Sign in again to continue.')).toBeVisible();
  await expect(two).toHaveURL(/\/sign-in\?return=/);
  // The server ends it too: the refresh cookie is refused at the idle time (FR-KRN-IAM-04).
  const refused = await one.evaluate(async () => (await fetch('/api/iam/auth/refresh', { method: 'POST', headers: { 'X-Erp-Client': 'web', 'Content-Type': 'application/json' }, body: '{}' })).status);
  expect(refused).toBe(401);

  await field(one, 'Username or e-mail address').fill(alice.username);
  await field(one, 'Password').fill(alice.password);
  await one.getByRole('button', { name: 'Sign in', exact: true }).click();
  await verifyWithAuthenticator(pages, one, alice.secret);
  await expect(one).toHaveURL(/\/\?tab=one$/);
  noProblems(one);
  noProblems(two);
  await context.close();
});

test('FR-ADM-USR-24 remember this device skips the second factor in this browser only, until a password reset ends it', async ({ browser }) => {
  await server.setting('iam.session.remember_device_days', '30');
  try {
    const context = await freshContext(browser);
    const page = await openPage(context);
    await signIn(page, alice.username, alice.password);
    await expect(page.getByText('Remember this browser for 30 days')).toBeVisible();
    await accessible(page, 'second step with remember');
    await shot(page, 'second-step-remember-en-light-1280');
    await verifyWithAuthenticator([page], page, alice.secret, true);
    await home(page);
    expect((await server.facts(alice.username)).trustedDevices).toBe(1);
    expect((await context.cookies()).find((c) => c.name === '__Secure-erp_td')?.httpOnly).toBe(true);
    await signOut(page);

    await signIn(page, alice.username, alice.password);
    await home(page); // password only

    const other = await freshContext(browser);
    const otherPage = await openPage(other);
    await signIn(otherPage, alice.username, alice.password);
    await expect(otherPage.getByRole('heading', { name: 'Second step' })).toBeVisible();
    await other.close();

    // A password reset ends the trust.
    await signOut(page);
    await page.goto('/forgot-password');
    await field(page, 'Username or e-mail address').fill(alice.username);
    await page.getByRole('button', { name: 'Send reset link' }).click();
    await expect(page.getByText(/If an account exists/)).toBeVisible();
    await page.goto((await server.mail('identity.password_reset', alice.username)).link!);
    const third = 'Third-Harbour-73'; // a password not used before (the history rule refuses recent ones)
    await field(page, 'New password').fill(third);
    await field(page, 'Repeat the new password').fill(third);
    await page.getByRole('button', { name: 'Save new password' }).click();
    await expect(page.getByText(/Your password was changed/)).toBeVisible();
    alice.password = third;
    await signIn(page, alice.username, alice.password);
    await expect(page.getByRole('heading', { name: 'Second step' })).toBeVisible();
    expect((await server.facts(alice.username)).trustedDevices).toBe(0);
    noProblems(page);
    await context.close();
  } finally {
    await server.setting('iam.session.remember_device_days', '0');
  }
});

test('spec 13 A11Y-01: the sign-in screens work by keyboard alone with visible focus', async ({ browser }) => {
  const context = await freshContext(browser);
  const page = await openPage(context);
  await page.goto('/sign-in');
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeFocused();
  await page.keyboard.press('Tab');
  const login = field(page, 'Username or e-mail address');
  await expect(login).toBeFocused();
  expect(await login.evaluate((el) => getComputedStyle(el).outlineStyle)).not.toBe('none');
  await page.keyboard.type(alice.username);
  await page.keyboard.press('Tab');
  await expect(field(page, 'Password')).toBeFocused();
  await page.keyboard.type(alice.password);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Second step' })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(field(page, 'Code from the app')).toBeFocused();
  await page.keyboard.type(await nextCode([page], alice.secret));
  await page.keyboard.press('Enter');
  await home(page);
  await context.close();
});

test('FR-KRN-LOC-01 pseudo-locale: every text comes from the catalogues and the layout mirrors', async ({ browser }) => {
  const context = await browser.newContext();
  await display(context, { language: 'en', pseudo: true });
  const page = await openPage(context);
  for (const path of ['/sign-in', '/forgot-password', '/reset-password?token=x', '/activate?token=x', '/activate', '/no-such-page']) {
    await page.goto(path);
    await expect(page.locator('h1')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.dir)).toBe('rtl');
    const plain = await page.evaluate(() => {
      const found: string[] = [];
      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        const text = node.textContent?.trim() ?? '';
        if (/\p{L}/u.test(text) && !text.includes('⟦') && !text.includes('⟧')) found.push(text);
      }
      const labels = [...document.querySelectorAll('[aria-label],[placeholder],[title]')]
        .flatMap((el) => ['aria-label', 'placeholder', 'title'].map((a) => el.getAttribute(a) ?? ''))
        .filter((t) => /\p{L}/u.test(t) && !t.includes('⟦'));
      return [...found, ...labels];
    });
    expect(plain, `hard-coded text on ${path}`).toEqual([]);
    await shot(page, `pseudo${path.replace(/[/?=]/g, '_')}`);
  }
  await context.close();
});

const WIDTHS = [360, 768, 1280] as const;

test('FR-KRN-LOC-01 NFR-11 sign-in screens in English and Urdu, light and dark, at 360, 768 and 1280 pixels', async ({ browser }) => {
  for (const language of ['en', 'ur'] as const) {
    for (const scheme of ['light', 'dark'] as const) {
      const context = await browser.newContext({ colorScheme: scheme });
      await display(context, { language });
      const page = await openPage(context);
      for (const width of WIDTHS) {
        await page.setViewportSize({ width, height: 900 });
        for (const [name, path] of [['sign-in', '/sign-in'], ['forgot', '/forgot-password'], ['reset', '/reset-password?token=x'], ['activate', '/activate?token=x'], ['not-found', '/no-such-page']] as const) {
          await page.goto(path);
          await expect(page.locator('h1')).toBeVisible();
          expect(await page.evaluate(() => document.documentElement.dir)).toBe(language === 'ur' ? 'rtl' : 'ltr');
          await fitsWidth(page);
          if (width === 1280) await accessible(page, `${name} ${language} ${scheme}`);
          await shot(page, `${name}-${language}-${scheme}-${width}`);
        }
      }

      // The second-step screen (needs a sign-in) and the mirrored layout in Urdu.
      await page.setViewportSize({ width: 1280, height: 900 });
      await page.goto('/sign-in');
      await page.locator('input[name="username"]').fill(alice.username);
      await page.locator('input[name="password"]').fill(alice.password);
      await page.locator('button[type="submit"]').click();
      await expect(page.locator('input[autocomplete="one-time-code"]')).toBeVisible();
      await accessible(page, `second step ${language} ${scheme}`);
      for (const width of WIDTHS) {
        await page.setViewportSize({ width, height: 900 });
        await fitsWidth(page);
        await shot(page, `second-step-${language}-${scheme}-${width}`);
      }
      if (language === 'ur') {
        const product = await page.locator('.erp-auth-product').boundingBox();
        expect(product!.x).toBeGreaterThan(640); // the product name sits at the start of the line: the right in Urdu
      }
      noProblems(page);
      await context.close();
    }
  }
});

test('spec 13 FR-UX-CMP-01 component catalogue in English and Urdu, light, dark and high contrast', async ({ browser }) => {
  for (const language of ['en', 'ur'] as const) {
    for (const theme of ['light', 'dark', 'contrast'] as const) {
      const context = await browser.newContext();
      await display(context, { language, theme });
      const page = await openPage(context);
      await page.goto('/catalogue');
      await expect(page.locator('h1')).toBeVisible();
      await accessible(page, `catalogue ${language} ${theme}`);
      await shot(page, `catalogue-${language}-${theme}-1280`);
      noProblems(page);
      await context.close();
    }
  }
});
