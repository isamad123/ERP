// Shared set-up of the HR leave screen tests (slice S1.33b): a shell with chosen permissions, a stubbed server that records every call, and render helpers.
import { render } from '@testing-library/react';
import { vi } from 'vitest';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';

export const COMPANY = '00000000-0000-7000-8000-000000000001';
export const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
export const problem = (title: string, status = 409, errors?: unknown) => new Response(JSON.stringify({ title, status, ...(errors ? { errors } : {}) }), { status, headers: { 'Content-Type': 'application/problem+json' } });

export function shell(permissions: string[], writable = true): ShellValue {
  const grants = permissions.map((code) => ({ code, companies: null }));
  return {
    me: { userId: 'u-hr', displayName: 'Hira HR', language: 'en', timeZone: 'Asia/Karachi', defaultCompanyId: COMPANY, companies: [{ id: COMPANY, code: 'C1', name: { en: 'First company' } }],
      permissions: grants, mfa: { required: false, methods: [], enrolBy: null }, numbers: { digits: 'western', grouping: 'international' } },
    capabilities: { modules: [], features: [], flags: [], state: 'active', nextState: null, nextStateOn: null, warnings: [] },
    facts: accessFacts([], grants, 'active'),
    can: (code: string) => permissions.includes(code),
    writable,
    activeCompany: null,
    setActiveCompany: () => undefined,
  };
}

export type Body = Record<string, unknown> | undefined;
export const sent: { method: string; url: string; body: Body }[] = [];
export function serve(handler: (method: string, url: string, body: Body) => Response) {
  sent.length = 0;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const body = (typeof init?.body === 'string' ? JSON.parse(init.body) : init?.body ?? undefined) as Body;
    sent.push({ method: init?.method ?? 'GET', url, body });
    return handler(init?.method ?? 'GET', url, body);
  }));
}
export const posts = () => sent.filter((c) => c.method === 'POST');

export function show(element: React.ReactNode, value: ShellValue, path = '/') {
  render(
    <MemoryRouter initialEntries={[path]}>
      <I18nRoot locale="en">
        <ToastProvider>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <ShellValueProvider value={value}>{element}</ShellValueProvider>
          </QueryClientProvider>
        </ToastProvider>
      </I18nRoot>
    </MemoryRouter>,
  );
}
