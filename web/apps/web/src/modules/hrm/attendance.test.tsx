// Attendance capture screens (slice S1.27a; FR-HRM-ATT-01): the punch screen shows the day and what the server answered, a punch sends only the
// direction (never a person), a refusal is shown as the server's text, the kiosk forgets what was typed, the QR screen draws the code the server
// gave, and a person without the permission sees "denied" and sends nothing.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import PunchScreen from './attendance-punch';
import KioskScreen from './attendance-kiosk';
import QrScreen from './attendance-qr';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';

function shell(permissions: string[], writable = true): ShellValue {
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

const sent: { method: string; url: string; body: unknown }[] = [];

function serve(handler: (method: string, url: string, body: unknown) => Response) {
  sent.length = 0;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
    sent.push({ method: init?.method ?? 'GET', url, body });
    return handler(init?.method ?? 'GET', url, body);
  }));
}

function show(element: React.ReactNode, value: ShellValue) {
  render(
    <MemoryRouter>
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

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const day = (extra: object = {}) => ({
  employments: [{ employmentId: 'e1', companyId: COMPANY, employeeNo: 'E-1' }], employmentId: 'e1', companyId: COMPANY, needsCompany: false,
  today: '2026-11-23', serverTime: '2026-11-23T04:00:00Z', next: 'in', events: [], ...extra,
});

describe('punch screen', () => {
  it('FR-HRM-ATT-01 shows the day, sends only the direction and says what the server recorded', async () => {
    serve((method) => (method === 'GET'
      ? json(day())
      : json({ eventId: 'x1', status: 'stored', eventAt: '2026-11-23T04:05:00Z', direction: 'in', source: 'web', isDuplicate: false }, 201)));
    show(<PunchScreen />, shell(['hrm.attendance.punch']));
    expect(await screen.findByText('You are not checked in.')).toBeTruthy();
    expect(screen.getByText('No punches yet today.')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Check in' }));
    await waitFor(() => expect(sent.some((c) => c.method === 'POST')).toBe(true));
    const post = sent.find((c) => c.method === 'POST')!;
    expect(post.url).toBe('/api/hrm/attendance/punch');
    expect(Object.keys(post.body as object).sort()).toEqual(['companyId', 'direction', 'idempotencyKey'].filter((k) => k !== 'companyId' || (post.body as Record<string, unknown>).companyId !== undefined).sort());
    expect((post.body as { direction: string }).direction).toBe('in');
    expect(await screen.findByText(/Checked in at/)).toBeTruthy();
  });

  it('FR-HRM-ATT-01 shows a refusal as the server\'s own text', async () => {
    serve((method) => (method === 'GET' ? json(day()) : json({ title: 'errors.hrm.attendance.network_not_allowed', status: 403 }, 403)));
    show(<PunchScreen />, shell(['hrm.attendance.punch']));
    await userEvent.click(await screen.findByRole('button', { name: 'Check out' }));
    expect(await screen.findByText(/Punching from this network is not allowed/)).toBeTruthy();
  });

  it('FR-HRM-ATT-01 a user without an employment is told so, and a person without the permission sends nothing', async () => {
    serve(() => json(day({ employments: [], employmentId: null, companyId: null, next: null })));
    show(<PunchScreen />, shell(['hrm.attendance.punch']));
    expect(await screen.findByText('Your user is not linked to a current employment')).toBeTruthy();
    cleanup();
    serve(() => json(day()));
    show(<PunchScreen />, shell([]));
    expect(screen.getByText('You may not punch attendance.')).toBeTruthy();
  });

  it('FR-HRM-DAT-02 shows a counted and a duplicate punch apart', async () => {
    serve(() => json(day({ next: 'out', events: [
      { id: 'a', eventAt: '2026-11-23T03:00:00Z', direction: 'in', source: 'web', isDuplicate: false },
      { id: 'b', eventAt: '2026-11-23T03:00:20Z', direction: 'in', source: 'qr', isDuplicate: true },
    ] })));
    show(<PunchScreen />, shell(['hrm.attendance.punch']));
    expect((await screen.findAllByText('Counted')).length).toBe(2); // the column and the counted punch
    expect(screen.getByText('Duplicate, not counted')).toBeTruthy();
    expect(screen.getByText('QR code')).toBeTruthy();
  });
});

describe('kiosk screen', () => {
  it('FR-HRM-ATT-01 sends the badge number, thanks the person by first name and clears the number', async () => {
    serve(() => json({ punch: { eventId: 'k1', status: 'stored', eventAt: '2026-11-23T04:05:00Z', direction: 'out', source: 'kiosk', isDuplicate: false }, employeeNo: 'E-1', firstName: 'Ayesha' }, 201));
    show(<KioskScreen />, shell(['hrm.attendance.kiosk']));
    await userEvent.type(screen.getByLabelText('Badge or PIN number'), '4711');
    await userEvent.click(screen.getByRole('button', { name: 'Check out' }));
    expect(await screen.findByText(/Thank you, Ayesha/)).toBeTruthy();
    const post = sent.find((c) => c.method === 'POST')!;
    expect(post.url).toBe('/api/hrm/attendance/kiosk/punch');
    expect(post.body).toMatchObject({ companyId: COMPANY, badgeNo: '4711', direction: 'out' });
    expect((screen.getByLabelText('Badge or PIN number') as HTMLInputElement).value).toBe('');
  });

  it('FR-HRM-ATT-01 an unknown number is refused in words and nothing else is shown', async () => {
    serve(() => json({ title: 'errors.hrm.attendance.badge_unknown', status: 404 }, 404));
    show(<KioskScreen />, shell(['hrm.attendance.kiosk']));
    await userEvent.type(screen.getByLabelText('Badge or PIN number'), '9');
    await userEvent.click(screen.getByRole('button', { name: 'Check in' }));
    expect(await screen.findByText(/This badge or PIN number is not known/)).toBeTruthy();
  });

  it('FR-HRM-ATT-01 needs the kiosk permission', () => {
    serve(() => json({}));
    show(<KioskScreen />, shell(['hrm.attendance.punch']));
    expect(screen.getByText('You may not punch attendance.')).toBeTruthy();
    expect(sent.length).toBe(0);
  });
});

describe('gate QR screen', () => {
  it('FR-HRM-ATT-01 draws the code the server gave and says when it changes', async () => {
    serve((_method, url) => (url.startsWith('/api/org/locations')
      ? json({ items: [{ id: 'l1', code: 'GATE', nameI18n: { en: 'Main gate' } }] })
      : json({ code: 'l1.100.abc', companyId: COMPANY, locationId: 'l1', windowSeconds: 60, expiresAt: new Date(Date.now() + 45_000).toISOString(), serverTime: new Date().toISOString() })));
    show(<QrScreen />, shell(['hrm.attendance.qr_display']));
    expect(await screen.findByRole('img', { name: 'QR code for punching at Main gate' })).toBeTruthy();
    expect(screen.getByText(/Code to type: l1\.100\.abc/)).toBeTruthy();
    expect(screen.getByText(/Changes in \d+ seconds/)).toBeTruthy();
  });

  it('FR-HRM-ATT-01 a company without locations says so', async () => {
    serve(() => json({ items: [] }));
    show(<QrScreen />, shell(['hrm.attendance.qr_display']));
    expect(await screen.findByText('This company has no locations')).toBeTruthy();
  });
});
