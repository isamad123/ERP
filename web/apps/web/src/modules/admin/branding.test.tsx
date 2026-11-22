// The sign-in page screen and the branded sign-in frame (slice S1.15a; FR-ADM-SEC-08, S1.15 ruling Q3): each value is a setting
// change with a reason, the logo is a file of the branding owner, and the page shows only what the public branding answer holds.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import { DisplayProvider } from '../../app/display-context';
import { AuthLayout } from '../../pages/auth-layout';
import BrandingScreen from './branding';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const branding = { productName: 'Acme People', logoUrl: '/api/kernel/branding/logo?v=abc', banner: { en: 'Welcome to Acme', ur: 'ایکمی میں خوش آمدید' }, terms: { en: 'Use is monitored.', ur: '' } };

function shell(permissions: string[]): ShellValue {
  const grants = permissions.map((code) => ({ code, companies: null }));
  return {
    me: { userId: 'u-me', displayName: 'Me', language: 'en', timeZone: 'Asia/Karachi', defaultCompanyId: 'c1', companies: [{ id: 'c1', code: 'C1', name: { en: 'First company' } }],
      permissions: grants, mfa: { required: false, methods: [], enrolBy: null }, numbers: { digits: 'western', grouping: 'international' } },
    capabilities: { modules: [], features: [], flags: [], state: 'active', nextState: null, nextStateOn: null, warnings: [] },
    facts: accessFacts([], grants, 'active'),
    can: (code: string) => permissions.includes(code),
    writable: true,
    activeCompany: null,
    setActiveCompany: () => undefined,
  };
}

interface Sent { method: string; url: string; body: unknown }

function serve(sent: Sent[], publicBranding: unknown = branding) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.split('?')[0] ?? url;
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      sent.push({ method, url: path, body: init?.body instanceof FormData ? 'form' : init?.body ? JSON.parse(String(init.body)) : null });
      return path === '/api/doc/attachments' ? json({ id: 'new-file', fileName: 'logo.png', contentType: 'image/png', sizeBytes: 3 }, 201) : json({ outcome: 'done', id: 'x', requestId: null });
    }
    if (path === '/api/kernel/branding') return publicBranding instanceof Response ? publicBranding : json(publicBranding);
    if (path === '/api/cfg/settings/cfg.branding.logo_file_id') return json({ setting: { key: 'cfg.branding.logo_file_id', value: (publicBranding as { logoUrl: string | null }).logoUrl ? 'abc' : '' }, layers: [] });
    return json({ title: 'errors.data.not_found' }, 404);
  }));
}

function wrap(children: React.ReactNode, permissions: string[], at: string) {
  render(
    <MemoryRouter initialEntries={[at]}>
      <I18nRoot locale="en"><ToastProvider><QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DisplayProvider><ShellValueProvider value={shell(permissions)}>{children}</ShellValueProvider></DisplayProvider>
      </QueryClientProvider></ToastProvider></I18nRoot>
    </MemoryRouter>,
  );
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-ADM-SEC-08 the sign-in page screen', () => {
  it('shows the current branding and changes only what was edited, each as a setting change with the reason', async () => {
    const sent: Sent[] = [];
    serve(sent);
    wrap(<Routes><Route path="/settings/sign-in-page" element={<BrandingScreen />} /></Routes>, ['cfg.setting_values.read', 'cfg.setting_values.update'], '/settings/sign-in-page');
    const name = await screen.findByRole('textbox', { name: /^Product name/ });
    expect((name as HTMLInputElement).value).toBe('Acme People');
    expect(screen.getByRole('img', { name: 'The logo shown on the sign-in page' })).toBeTruthy();
    await userEvent.clear(name);
    await userEvent.type(name, 'Acme Workforce');
    await userEvent.type(screen.getByRole('textbox', { name: 'Login banner (English)' }), '!');
    await userEvent.type(screen.getByRole('textbox', { name: /^Reason/ }), 'Rebrand');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sent).toEqual([
      { method: 'PUT', url: '/api/cfg/settings/cfg.branding.product_name', body: { layer: 'tenant', scopeId: null, value: 'Acme Workforce', reason: 'Rebrand' } },
      { method: 'PUT', url: '/api/cfg/settings/cfg.branding.banner_en', body: { layer: 'tenant', scopeId: null, value: 'Welcome to Acme!', reason: 'Rebrand' } },
    ]));
  });

  it('uploads a logo as a file of the branding owner and sets the logo setting on save', async () => {
    const sent: Sent[] = [];
    serve(sent, { ...branding, logoUrl: null });
    wrap(<Routes><Route path="/settings/sign-in-page" element={<BrandingScreen />} /></Routes>, ['cfg.setting_values.read', 'cfg.setting_values.update'], '/settings/sign-in-page');
    await screen.findByRole('textbox', { name: /^Product name/ });
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await userEvent.upload(input, new File([new Uint8Array([137, 80, 78, 71])], 'logo.png', { type: 'image/png' }));
    await waitFor(() => expect(sent[0]).toEqual({ method: 'POST', url: '/api/doc/attachments', body: 'form' }));
    await userEvent.type(await screen.findByRole('textbox', { name: /^Reason/ }), 'New logo');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sent[1]).toEqual({ method: 'PUT', url: '/api/cfg/settings/cfg.branding.logo_file_id', body: { layer: 'tenant', scopeId: null, value: 'new-file', reason: 'New logo' } }));
  });

  it('asks for a name and a reason and sends nothing without them', async () => {
    const sent: Sent[] = [];
    serve(sent);
    wrap(<Routes><Route path="/settings/sign-in-page" element={<BrandingScreen />} /></Routes>, ['cfg.setting_values.read', 'cfg.setting_values.update'], '/settings/sign-in-page');
    const name = await screen.findByRole('textbox', { name: /^Product name/ });
    await userEvent.clear(name);
    await userEvent.type(screen.getByRole('textbox', { name: /^Reason/ }), 'x');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect((await screen.findAllByText('Enter a value.')).length).toBeGreaterThan(0);
    expect(sent).toEqual([]);
  });

  it('previews the page in Urdu and falls back to English where the Urdu text is empty', async () => {
    serve([]);
    wrap(<Routes><Route path="/settings/sign-in-page" element={<BrandingScreen />} /></Routes>, ['cfg.setting_values.read'], '/settings/sign-in-page');
    await screen.findByRole('textbox', { name: /^Product name/ });
    const preview = screen.getByRole('group', { name: 'How the sign-in page looks' });
    expect(within(preview).getByText('Welcome to Acme')).toBeTruthy();
    await userEvent.click(screen.getByRole('radio', { name: 'اردو' }));
    expect(within(preview).getByText('ایکمی میں خوش آمدید')).toBeTruthy();
    expect(within(preview).getByText('Use is monitored.')).toBeTruthy(); // no Urdu terms: the English notice
    expect(screen.queryByRole('button', { name: 'Save' })).toBeNull(); // read only
  });

  it('shows the denied state', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ status: 403, title: 'errors.access.forbidden' }, 403)));
    wrap(<Routes><Route path="/settings/sign-in-page" element={<BrandingScreen />} /></Routes>, ['cfg.setting_values.read'], '/settings/sign-in-page');
    expect(await screen.findByText('You may not see the sign-in page settings.')).toBeTruthy();
  });
});

describe('FR-ADM-SEC-08 the sign-in frame shows the customer\'s branding', () => {
  const frame = () => <Routes><Route path="/" element={<AuthLayout titleKey="auth.signIn.title"><p>form</p></AuthLayout>} /></Routes>;

  it('shows the product name, the logo, the banner and the terms of the customer', async () => {
    serve([]);
    wrap(frame(), [], '/');
    expect(await screen.findByText('Acme People')).toBeTruthy();
    expect(await screen.findByText('Welcome to Acme')).toBeTruthy();
    expect(screen.getByText('Use is monitored.')).toBeTruthy();
    expect(document.querySelector('img.erp-auth-logo')?.getAttribute('src')).toBe('/api/kernel/branding/logo?v=abc');
    await waitFor(() => expect(document.title).toBe('Sign in – Acme People'));
  });

  it('shows the standard product name when the branding cannot be read', async () => {
    serve([], json({ title: 'errors.server' }, 500));
    wrap(frame(), [], '/');
    expect(await screen.findByText('ERP')).toBeTruthy();
    expect(document.querySelector('img.erp-auth-logo')).toBeNull();
  });
});
