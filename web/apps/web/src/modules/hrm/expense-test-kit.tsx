// Shared set-up of the expense screen tests (slice S1.42): a shell with chosen permissions, a stubbed server that records every call with its raw body (so exact decimals can be read),
// and a render helper with a path and a language.
import { render } from '@testing-library/react';
import { vi } from 'vitest';
import { MemoryRouter, RouterProvider, createMemoryRouter, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import type { ClaimLine, ClaimView } from '../../api/expenses';

export const COMPANY = '00000000-0000-7000-8000-000000000001';
export const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
/** A response whose body is written by hand (to put exact decimals as JSON numbers, as the server does). */
export const raw = (text: string, status = 200) => new Response(text, { status, headers: { 'Content-Type': 'application/json' } });
export const problem = (title: string, status = 409, errors?: unknown) => new Response(JSON.stringify({ title, status, ...(errors ? { errors } : {}) }), { status, headers: { 'Content-Type': 'application/problem+json' } });

export function shell(permissions: string[], writable = true): ShellValue {
  const grants = permissions.map((code) => ({ code, companies: null }));
  return {
    me: { userId: 'u-me', displayName: 'Mona Me', language: 'en', timeZone: 'Asia/Karachi', defaultCompanyId: COMPANY, companies: [{ id: COMPANY, code: 'C1', name: { en: 'First company' } }],
      permissions: grants, mfa: { required: false, methods: [], enrolBy: null }, numbers: { digits: 'western', grouping: 'international' } },
    capabilities: { modules: [], features: [], flags: [], state: 'active', nextState: null, nextStateOn: null, warnings: [] },
    facts: accessFacts([], grants, 'active'),
    can: (code: string) => permissions.includes(code),
    writable,
    activeCompany: null,
    setActiveCompany: () => undefined,
  };
}

export interface Call { method: string; url: string; body: unknown; text: string | undefined }
export const sent: Call[] = [];
export function serve(handler: (method: string, url: string, body: Record<string, unknown> | undefined) => Response) {
  sent.length = 0;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const text = typeof init?.body === 'string' ? init.body : undefined;
    const body = text ? JSON.parse(text) as Record<string, unknown> : undefined;
    sent.push({ method: init?.method ?? 'GET', url, body, text });
    return handler(init?.method ?? 'GET', url, body);
  }));
}
export const posts = () => sent.filter((c) => c.method === 'POST' || c.method === 'PUT');

function Where() {
  const { pathname } = useLocation();
  return <span data-testid="where">{pathname}</span>;
}

export function show(element: React.ReactNode, value: ShellValue, path = '/', locale: 'en' | 'ur' = 'en') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <I18nRoot locale={locale}>
        <ToastProvider>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <ShellValueProvider value={value}>{element}<Where /></ShellValueProvider>
          </QueryClientProvider>
        </ToastProvider>
      </I18nRoot>
    </MemoryRouter>,
  );
}

export const CONTEXT = { employmentId: 'e1', employeeNo: 'E-1', companyId: COMPANY, currencyCode: 'PKR', onBehalf: false, today: '2026-11-26' };
export const category = (id: string, code: string, en: string, kind = 'general', extra: Record<string, unknown> = {}) => ({
  id, code, nameI18n: { en, ur: `${en} (ur)` }, kind, requiresReceipt: false, flagsBlockSubmit: false, isActive: true, ...extra,
});
export const CATEGORIES = [category('c1', 'TRAVEL', 'Travel', 'general', { requiresReceipt: true }), category('c2', 'PD', 'Per diem', 'per_diem'), category('c3', 'KM', 'Mileage', 'mileage', { flagsBlockSubmit: true })];

export const line = (id: string, extra: Partial<ClaimLine> = {}): ClaimLine => ({
  id, categoryId: 'c1', categoryCode: 'TRAVEL', categoryName: { en: 'Travel', ur: 'سفر' }, taxTreatment: null, expenseDate: '2026-11-20', originalCurrencyCode: 'PKR', originalAmount: '1250.5000',
  exchangeRate: '1.00000000', rateDate: '2026-11-20', amount: '1250.5000', quantity: '0.0000', description: 'Taxi', receiptFileId: null, project: null, costCentre: null, flags: [], blocksSubmit: false,
  approvedAmount: null, decisionReason: null, decidedAt: null, ...extra,
});
export const claim = (extra: Partial<ClaimView> = {}): ClaimView => ({
  id: 'k1', claimNo: 'EXP-0001', status: 'draft', title: 'Lahore trip', employmentId: 'e1', employeeNo: 'E-1', currencyCode: 'PKR', reimburseVia: 'payroll', paymentStatus: 'none', paymentReference: null,
  payrollInput: null, needsFinance: false, submittedAt: null, reimbursedAt: null, instanceId: null, returned: false, totalAmount: '1250.5000', approvedTotal: null, amountsHidden: false, canEdit: true,
  canDecide: false, rowVersion: 3, lines: [line('l1')], ...extra,
});
/** A claim written as the server writes it: decimals as JSON numbers. */
export const wire = (view: ClaimView): string => JSON.stringify(view).replace(/"(amount|originalAmount|totalAmount|approvedTotal|approvedAmount|exchangeRate|quantity)":"([-0-9.]+)"/g, '"$1":$2');

/** A screen with forms needs a data router (it guards unsaved changes); the router is returned so a test can read where the screen went. */
export function showRouted(route: string, element: React.ReactNode, value: ShellValue, path: string, locale: 'en' | 'ur' = 'en') {
  const router = createMemoryRouter([{ path: route, element }], { initialEntries: [path] });
  render(
    <I18nRoot locale={locale}>
      <ToastProvider>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <ShellValueProvider value={value}><RouterProvider router={router} /></ShellValueProvider>
        </QueryClientProvider>
      </ToastProvider>
    </I18nRoot>,
  );
  return router;
}
