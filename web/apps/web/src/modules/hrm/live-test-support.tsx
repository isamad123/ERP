// Shared set-up of the S1.30b screen tests: a signed-in shell with chosen permissions, a recording fetch, and the providers a screen needs.
import { vi } from 'vitest';
import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';

export const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
export const COMPANY = '00000000-0000-7000-8000-000000000001';
export const UNIT = '00000000-0000-7000-8000-0000000000a1';
export const LOCATION = '00000000-0000-7000-8000-0000000000b1';

export function shell(permissions: string[], writable = true): ShellValue {
  const grants = permissions.map((code) => ({ code, companies: null }));
  return {
    me: { userId: 'u-me', displayName: 'Me', language: 'en', timeZone: 'Asia/Karachi', defaultCompanyId: COMPANY, companies: [{ id: COMPANY, code: 'C1', name: { en: 'First company' } }],
      permissions: grants, mfa: { required: false, methods: [], enrolBy: null }, numbers: { digits: 'western', grouping: 'international' } },
    capabilities: { modules: [], features: [], flags: [], state: 'active', nextState: null, nextStateOn: null, warnings: [] },
    facts: accessFacts([], grants, 'active'),
    can: (code: string) => permissions.includes(code),
    writable,
    activeCompany: null,
    setActiveCompany: () => undefined,
  };
}

export const sent: { method: string; url: string; body: unknown }[] = [];

export function serve(handler: (method: string, url: string, body: unknown) => Response) {
  sent.length = 0;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const body = typeof init?.body === 'string' ? JSON.parse(init.body) : init?.body ?? undefined;
    sent.push({ method: init?.method ?? 'GET', url, body });
    return handler(init?.method ?? 'GET', url, body);
  }));
}

export function show(element: React.ReactNode, value: ShellValue, locale: 'en' | 'ur' = 'en') {
  return render(
    <MemoryRouter>
      <I18nRoot locale={locale}>
        <ToastProvider>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <ShellValueProvider value={value}>{element}</ShellValueProvider>
          </QueryClientProvider>
        </ToastProvider>
      </I18nRoot>
    </MemoryRouter>,
  );
}

export const COUNTS = { headcount: 40, scheduled: 36, present: 25, late: 4, absent: 3, onLeave: 2, onDuty: 1, notYetPunched: 5, notDue: 2, notScheduled: 4, noPolicy: 0 };
export const DASHBOARD = {
  asOf: '2026-11-25T05:30:00Z', refreshSeconds: 60, companies: [{ companyId: COMPANY, date: '2026-11-25' }], totals: COUNTS,
  byUnit: [{ unitId: UNIT, counts: { ...COUNTS, headcount: 40 } }], byLocation: [{ locationId: LOCATION, counts: { ...COUNTS, headcount: 40 } }],
};
export const PERSON = {
  employmentId: 'e1', companyId: COMPANY, employeeNo: 'E-1', firstName: 'Amna', lastName: 'Khan', unitId: UNIT, branchId: null, locationId: LOCATION, status: 'late',
  shiftStart: '2026-11-25T04:00:00Z', shiftEnd: '2026-11-25T13:00:00Z', firstPunch: '2026-11-25T04:20:00Z',
};

/** The answers every live screen needs besides its own: unit and location names. */
export const orgAnswer = (url: string): Response | null => {
  if (url.includes('/api/org/org-units')) return json({ items: [{ id: UNIT, code: 'OPS', nameI18n: { en: 'Operations' } }] });
  if (url.includes('/api/org/locations')) return json({ items: [{ id: LOCATION, code: 'HQ', nameI18n: { en: 'Head office' } }] });
  return null;
};
