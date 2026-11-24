// Roster grid, optional holidays and the shift of a day (slice S1.28b; FR-HRM-ATT-07, ATT-15, ATT-16): the grid shows each person's days with the
// holidays of the calendar and the check messages, publishing is blocked while a check is an error; optional holidays show Hijri dates and are chosen
// within the limit the server reports; the effective shift shows the server's reasons; a person without the permission sees "denied".
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import OptionalHolidaysScreen from './optional-holidays';
import RostersScreen from './rosters';

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

function show(element: React.ReactNode, value: ShellValue, entry = '/') {
  render(
    <MemoryRouter initialEntries={[entry]}>
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

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const ROSTER = { id: 'r1', companyId: COMPANY, name: 'March', orgUnitId: null, periodStart: '2026-03-02', periodEnd: '2026-03-04', publishedAt: null, publishedBy: null, copiedFromRosterId: null, status: 'draft', rowVersion: 3 };
const LINES = [
  { id: 'l1', companyId: COMPANY, rosterId: 'r1', employmentId: 'e1', workDate: '2026-03-02', shiftId: 's1', dayType: 'working', rosterStatus: 'draft', reason: null, rowVersion: 1 },
  { id: 'l2', companyId: COMPANY, rosterId: 'r1', employmentId: 'e1', workDate: '2026-03-03', shiftId: null, dayType: 'weekly_off', rosterStatus: 'draft', reason: null, rowVersion: 1 },
];
const HOLIDAY = { id: 'h1', calendarId: 'c1', date: '2026-03-03', name: { en: 'Pakistan Day' }, kind: 'public', isTentative: false, isOptional: false, hijri: { year: 1447, month: 9, day: 12 } };

function gridServer(errors: string[]) {
  serve((_method, url) => {
    if (url.includes('/checks')) return json({ messages: errors.map((code) => ({ code, severity: 'error', employmentId: 'e1', date: '2026-03-02', detail: null })) });
    if (url.includes('/api/hrm/roster-lines')) return json({ items: LINES, next: null });
    if (url.match(/\/api\/hrm\/rosters\/r1$/)) return json(ROSTER);
    if (url.includes('/api/hrm/shifts')) return json({ items: [{ id: 's1', code: 'DAY', nameI18n: { en: 'Day' } }], next: null });
    if (url.includes('/api/hrm/employments')) return json({ items: [{ id: 'e1', employeeNo: 'E-1', status: 'active', companyId: COMPANY }] });
    if (url.includes('/api/hrm/holidays')) return json({ companyId: COMPANY, calendarId: 'c1', year: 2026, hijriOffsetDays: 0, holidays: [HOLIDAY] });
    return json({}, 404);
  });
}

const GRID_PERMISSIONS = ['hrm.rosters.read', 'hrm.rosters.update', 'hrm.rosters.publish', 'hrm.rosters.create', 'hrm.holidays.read'];

function showGrid(permissions: string[]) {
  show(<Routes><Route path="/time/rosters/*" element={<RostersScreen />} /></Routes>, shell(permissions), '/time/rosters/r1');
}

describe('roster grid', () => {
  it('FR-HRM-ATT-07 shows each person day by day, with the weekly off, and the holiday in the header with its Hijri date', async () => {
    gridServer([]);
    showGrid(GRID_PERMISSIONS);
    expect(await screen.findByRole('heading', { name: 'March' })).toBeTruthy();
    expect(await screen.findByRole('button', { name: /E-1, .*: DAY\./ })).toBeTruthy();
    expect(screen.getAllByRole('button', { name: /E-1, .*: Off\./ }).length).toBeGreaterThan(0);
    await waitFor(() => expect(screen.getByRole('columnheader', { name: '03' }).getAttribute('title')).toBe('Pakistan Day, 12 Ramzan 1447 AH'));
    expect(await screen.findByText('No problems found.')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Publish' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('FR-HRM-ATT-07 blocks publishing while a check is an error and says so', async () => {
    gridServer(['double_assignment']);
    showGrid(GRID_PERMISSIONS);
    expect(await screen.findByText(/Two shifts on the same day/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Publish' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText('Fix the errors in the checks first.')).toBeTruthy();
  });

  it('FR-HRM-ATT-07 sends the day change of a cell to the server and asks for a reason only once the roster is published', async () => {
    gridServer([]);
    showGrid(GRID_PERMISSIONS);
    const cell = await screen.findByRole('button', { name: /E-1, .*: DAY\./ });
    await userEvent.click(cell);
    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Save' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('FR-HRM-ATT-07 disables every change for a person who may only read', async () => {
    gridServer([]);
    showGrid(['hrm.rosters.read']);
    const cell = await screen.findByRole('button', { name: /E-1, .*: DAY\./ });
    expect((cell as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Assign a shift' }) as HTMLButtonElement).disabled).toBe(true);
  });
});

const CHOICES = {
  employmentId: 'e1', calendarId: 'c1', year: 2026, hijriOffsetDays: 0, optionalLimit: 1, optionalChosen: 0, cutoffDays: 7,
  holidays: [
    { holiday: { ...HOLIDAY, id: 'h1', kind: 'public' }, chosen: false, open: false },
    { holiday: { id: 'h2', calendarId: 'c1', date: '2026-06-20', name: { en: 'Optional day' }, kind: 'optional', isTentative: false, isOptional: true, hijri: { year: 1447, month: 12, day: 5 } }, chosen: false, open: true },
  ],
};

describe('optional holidays', () => {
  const OPTIONAL = ['hrm.holidays.read', 'hrm.optional_holidays.choose', 'hrm.shift_effective.read'];
  function choicesServer() {
    serve((method, url, body) => {
      if (url.includes('/optional-holidays') && method === 'PUT') return json({ ...CHOICES, optionalChosen: (body as { holidayIds: string[] }).holidayIds.length });
      if (url.includes('/holidays?year=')) return json(CHOICES);
      if (url.includes('/api/hrm/employments')) return json({ items: [{ id: 'e1', employeeNo: 'E-1', status: 'active', companyId: COMPANY }] });
      return json({}, 404);
    });
  }

  it('FR-HRM-ATT-15 shows the Hijri date of each holiday, the limit and chosen count, and sends the choice', async () => {
    choicesServer();
    show(<OptionalHolidaysScreen />, shell(OPTIONAL));
    await userEvent.click(await screen.findByRole('button', { name: /Employee/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'E-1' }));
    expect(await screen.findByText('12 Ramzan 1447 AH')).toBeTruthy();
    expect(screen.getByText('5 Dhul Hijjah 1447 AH')).toBeTruthy();
    expect(screen.getByText('0 of 1 chosen. Choices close 7 days before each holiday.')).toBeTruthy();
    await userEvent.click(screen.getByRole('checkbox', { name: 'Choose Optional day' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sent.find((s) => s.method === 'PUT')?.body).toEqual({ year: new Date().getFullYear(), holidayIds: ['h2'] }));
  });

  it('FR-HRM-ATT-15 says nothing can be chosen while the company has set no yearly limit', async () => {
    serve((_method, url) => (url.includes('/holidays?year=') ? json({ ...CHOICES, optionalLimit: 0 }) : url.includes('/api/hrm/employments') ? json({ items: [{ id: 'e1', employeeNo: 'E-1', status: 'active', companyId: COMPANY }] }) : json({}, 404)));
    show(<OptionalHolidaysScreen />, shell(OPTIONAL));
    await userEvent.click(await screen.findByRole('button', { name: /Employee/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'E-1' }));
    expect(await screen.findByText('No optional holidays can be chosen until the company sets a yearly limit.')).toBeTruthy();
  });

  it('FR-HRM-ATT-15 shows "denied" and sends nothing without the permission to see holidays', async () => {
    serve(() => json({}, 404));
    show(<OptionalHolidaysScreen />, shell([]));
    expect(await screen.findByText('You may not see holidays.')).toBeTruthy();
    expect(sent).toHaveLength(0);
  });
});
