// Feature flags (slice S1.15a; FR-KRN-SET-05): state and source, overrides per company, an unlicensed flag, the empty and denied states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import FlagsScreen from './flags';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const C1 = '00000000-0000-7000-8000-000000000001';
const C2 = '00000000-0000-7000-8000-000000000002';

const flag = (key: string, extra: Record<string, unknown> = {}) => ({ key, module: 'hrm', name: { en: `Flag ${key}` }, description: { en: `About ${key}` }, help: null, entitlementKey: null, licensed: true, default: false, enabled: false, source: 'default', sourceScopeId: null, overrides: [], ...extra });

function shell(permissions: string[]): ShellValue {
  const grants = permissions.map((code) => ({ code, companies: null }));
  return {
    me: { userId: 'u-me', displayName: 'Me', language: 'en', timeZone: 'Asia/Karachi', defaultCompanyId: C1, companies: [{ id: C1, code: 'C1', name: { en: 'First company' } }, { id: C2, code: 'C2', name: { en: 'Second company' } }],
      permissions: grants, mfa: { required: false, methods: [], enrolBy: null }, numbers: { digits: 'western', grouping: 'international' } },
    capabilities: { modules: [], features: [], flags: [], state: 'active', nextState: null, nextStateOn: null, warnings: [] },
    facts: accessFacts([], grants, 'active'),
    can: (code: string) => permissions.includes(code),
    writable: true,
    activeCompany: null,
    setActiveCompany: () => undefined,
  };
}

function serve(flags: unknown[], sent: { method: string; url: string; body: unknown }[]) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.split('?')[0] ?? url;
    const method = init?.method ?? 'GET';
    if (method !== 'GET') { sent.push({ method, url: path, body: init?.body ? JSON.parse(String(init.body)) : null }); return json({ outcome: 'done', id: 'x', requestId: null }); }
    if (path === '/api/cfg/feature-flags') return json(flags);
    return json({ items: [], next: null });
  }));
}

function show(permissions = ['cfg.feature_flag_overrides.read', 'cfg.feature_flag_overrides.update']) {
  render(
    <MemoryRouter initialEntries={['/settings/flags']}>
      <I18nRoot locale="en"><ToastProvider><QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ShellValueProvider value={shell(permissions)}><Routes><Route path="/settings/flags" element={<FlagsScreen />} /></Routes></ShellValueProvider>
      </QueryClientProvider></ToastProvider></I18nRoot>
    </MemoryRouter>,
  );
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-KRN-SET-05 feature flags', () => {
  it('shows each flag with its state, its source, its overrides and whether the licence covers it', async () => {
    serve([flag('hrm.a', { enabled: true, source: 'company', overrides: [{ layer: 'company', scopeId: C2, enabled: true, changed: { at: '2026-11-20T08:00:00Z', by: 'u1' } }] }), flag('hrm.b', { licensed: false })], []);
    show();
    const first = (await screen.findByText('Flag hrm.a')).closest('.erp-settings-row') as HTMLElement;
    expect(within(first).getAllByText('On').length).toBeGreaterThan(0);
    expect(within(first).getByText(/Second company: On/)).toBeTruthy();
    const second = screen.getByText('Flag hrm.b').closest('.erp-settings-row') as HTMLElement;
    expect(within(second).getByText('Not covered by the licence')).toBeTruthy();
    expect(within(second).queryByRole('button', { name: /^Change the flag/ })).toBeNull();
  });

  it('overrides a flag for one company with a reason, and removes an override', async () => {
    const sent: { method: string; url: string; body: unknown }[] = [];
    serve([flag('hrm.a', { overrides: [{ layer: 'company', scopeId: C1, enabled: true, changed: { at: '2026-11-20T08:00:00Z', by: 'u1' } }] })], sent);
    show();
    await userEvent.click(await screen.findByRole('button', { name: 'Change the flag Flag hrm.a' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Change the flag Flag hrm.a' });
    await userEvent.click(within(dialog).getByRole('button', { name: /Change it for/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'Second company' }));
    await userEvent.click(within(dialog).getByRole('switch', { name: 'Switched on' }));
    await userEvent.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Pilot');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sent).toEqual([{ method: 'PUT', url: '/api/cfg/feature-flags/hrm.a', body: { layer: 'company', scopeId: C2, enabled: true, reason: 'Pilot' } }]));
    await userEvent.click(await screen.findByRole('button', { name: 'Remove the override of Flag hrm.a for First company' }));
    const confirm = await screen.findByRole('alertdialog', { name: 'Remove the override' });
    await userEvent.type(within(confirm).getByRole('textbox', { name: /^Reason/ }), 'Done');
    await userEvent.click(within(confirm).getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(sent[1]).toEqual({ method: 'POST', url: '/api/cfg/feature-flags/hrm.a/reset', body: { layer: 'company', scopeId: C1, reason: 'Done' } }));
  });

  it('shows the empty state and a read-only view', async () => {
    serve([], []);
    show(['cfg.feature_flag_overrides.read']);
    expect(await screen.findByText('No feature flags yet')).toBeTruthy();
  });

  it('shows the denied state', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ status: 403, title: 'errors.access.forbidden' }, 403)));
    show();
    expect(await screen.findByText('You may not see the feature flags.')).toBeTruthy();
  });
});
