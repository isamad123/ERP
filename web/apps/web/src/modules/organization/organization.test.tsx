// The organization screens (slice S1.14a; FR-ADM-ORG-01, FR-ADM-ORG-02, FR-ADM-ORG-07, FR-KRN-LOC-06, FR-ADM-LOC-04, FR-KRN-IAM-14):
// registrations shown in full, masked or hidden as the server sends them and the bodies sent; a bank account change that waits for
// a second administrator; opening hours that cannot close before they open; a latitude refused next to its field; the holiday year
// copy and the confirm action. The server decides everything; these tests show what the person sees of its answers.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import CompaniesScreen from './companies';
import LocationsScreen from './locations';
import CalendarsScreen from './calendars';
import { dayProblem } from './location-editors';
import locationMetadata from '../../../../../generated/metadata/org.locations.json';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';
const ALL = ['org.companies.read', 'org.companies.create', 'org.companies.update', 'org.company_registrations.read', 'org.company_registrations.create', 'org.company_registrations.update',
  'org.company_bank_accounts.read', 'org.company_bank_accounts.create', 'org.company_bank_accounts.update', 'org.company_bank_accounts.delete', 'cfg.setting_values.read',
  'org.locations.read', 'org.locations.create', 'org.locations.update', 'ref.calendars.read', 'ref.calendars.create', 'ref.calendars.update', 'ref.holidays.create', 'ref.holidays.update'];

function shell(permissions: string[] = ALL): ShellValue {
  const grants = permissions.map((code) => ({ code, companies: null }));
  return {
    me: { userId: 'u-me', displayName: 'Me', language: 'en', timeZone: 'Asia/Karachi', defaultCompanyId: COMPANY, companies: [{ id: COMPANY, code: 'C1', name: { en: 'First company' } }],
      permissions: grants, mfa: { required: false, methods: [], enrolBy: null }, numbers: { digits: 'western', grouping: 'international' } },
    capabilities: { modules: [], features: [], flags: [], state: 'active', nextState: null, nextStateOn: null, warnings: [] },
    facts: accessFacts([], grants, 'active'),
    can: (code: string) => permissions.includes(code),
    writable: true,
    activeCompany: null,
    setActiveCompany: () => undefined,
  };
}

interface Call { method: string; path: string; body: unknown }
interface Server { calls: Call[]; routes: Record<string, (call: Call) => Response> }

function serve(server: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = '', query = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    const call = { method, path: path + (query ? `?${query}` : ''), body: init?.body && typeof init.body === 'string' ? JSON.parse(init.body) as unknown : null };
    server.calls.push(call);
    const handler = server.routes[`${method} ${path}`];
    return handler ? handler(call) : json({ status: 404, title: 'errors.data.not_found' }, 404);
  }));
}

function show(path: string, route: string, element: React.ReactNode, value = shell()) {
  const router = createMemoryRouter([{ path: route, element }], { initialEntries: [path] });
  render(
    <I18nRoot locale="en">
      <ToastProvider>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <ShellValueProvider value={value}>
            <RouterProvider router={router} />
          </ShellValueProvider>
        </QueryClientProvider>
      </ToastProvider>
    </I18nRoot>,
  );
}

const company = { id: COMPANY, code: 'C1', nameI18n: { en: 'First company' }, legalName: 'First (Pvt) Ltd', countryCode: 'PK', baseCurrencyCode: 'PKR', defaultLanguage: 'en', holidayCalendarId: null, logoFileId: null,
  costCenterModel: 'none', status: 'active', isActive: true, createdAt: '2026-01-01T00:00:00Z', createdBy: 'u', updatedAt: '2026-01-01T00:00:00Z', updatedBy: 'u', rowVersion: 3 };

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-ADM-ORG-01 registrations and bank accounts of a company', () => {
  const registrations = [
    { id: 'r1', companyId: COMPANY, kind: 'ntn', authority: 'FBR', value: '1234567-8', valueAccess: 'full', validFrom: '2026-01-01', validTo: null, rowVersion: 1 },
    { id: 'r2', companyId: COMPANY, kind: 'eobi', authority: 'EOBI', value: '****7788', valueAccess: 'masked', validFrom: '2026-01-01', validTo: '2026-12-31', rowVersion: 1 },
    { id: 'r3', companyId: COMPANY, kind: 'strn', authority: 'FBR', value: null, valueAccess: 'hidden', validFrom: '2026-01-01', validTo: null, rowVersion: 1 },
  ];
  const accounts = [{ id: 'b1', companyId: COMPANY, bankCode: 'HBL', accountTitle: 'Salary account', accountNumber: '****4556', iban: null, accountAccess: 'masked', currencyCode: 'PKR', salaryFileFormat: null, rowVersion: 2 }];

  function companyServer(extra: Server['routes'] = {}): Server {
    const server: Server = { calls: [], routes: {
      [`GET /api/org/companies/${COMPANY}`]: () => json(company),
      [`GET /api/org/companies/${COMPANY}/registrations`]: () => json(registrations),
      [`GET /api/org/companies/${COMPANY}/bank-accounts`]: () => json(accounts),
      'GET /api/cfg/settings': () => json([{ key: 'hrm.x', area: null, name: { en: 'Week starts on' }, description: { en: 'd' }, value: 'monday', source: 'company' }, { key: 'hrm.y', area: null, name: { en: 'Other' }, description: { en: 'd' }, value: 1, source: 'tenant' }]),
      'GET /api/ref/banks': () => json({ items: [{ id: 'k1', code: 'HBL', nameI18n: { en: 'Habib Bank' } }], next: null }),
      'GET /api/ref/calendars': () => json({ items: [], next: null }),
      ...extra,
    } };
    serve(server);
    return server;
  }

  it('shows values in full, masked or hidden as the server sends them, and only the settings the company overrides', async () => {
    companyServer();
    show(`/organization/companies/${COMPANY}`, '/organization/companies/*', <CompaniesScreen />);
    expect(await screen.findByText('1234567-8')).toBeTruthy();
    expect(screen.getByText('****7788')).toBeTruthy();
    expect(screen.getAllByText('Hidden by field security').length).toBeGreaterThan(0);
    expect(await screen.findByText('****4556')).toBeTruthy();
    expect(screen.getByText('Week starts on')).toBeTruthy();
    expect(screen.queryByText('Other')).toBeNull();
    expect(screen.getByRole('link', { name: 'Open the settings centre for this company' }).getAttribute('href')).toBe(`/settings?company=${COMPANY}`);
  });

  it('sends a new registration and shows the server refusal for an overlap in plain words', async () => {
    const user = userEvent.setup();
    const server = companyServer({
      [`POST /api/org/companies/${COMPANY}/registrations`]: () => json({ status: 409, title: 'errors.org.registration_overlap' }, 409),
    });
    show(`/organization/companies/${COMPANY}`, '/organization/companies/*', <CompaniesScreen />);
    await user.click(await screen.findByRole('button', { name: 'Add a registration' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText(/^Authority/), 'FBR');
    await user.type(within(dialog).getByLabelText(/^Number/), '999-1');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(server.calls.find((c) => c.method === 'POST')?.body).toMatchObject({ kind: 'ntn', authority: 'FBR', value: '999-1' }));
    expect(await within(dialog).findByText(/already has a registration of this kind/)).toBeTruthy();
  });

  it('says a bank account change is waiting for a second administrator, and sends the removal with its row version', async () => {
    const user = userEvent.setup();
    const server = companyServer({
      'PUT /api/org/company-bank-accounts/b1': () => json({ outcome: 'pending', id: null, requestId: 'q1' }, 202),
      'POST /api/org/company-bank-accounts/b1/remove': () => json({ outcome: 'pending', id: null, requestId: 'q2' }, 202),
    });
    show(`/organization/companies/${COMPANY}`, '/organization/companies/*', <CompaniesScreen />);
    const row = (await screen.findByText('Salary account')).closest('tr') as HTMLElement;
    await user.click(within(row).getByRole('button', { name: 'Change' }));
    const dialog = await screen.findByRole('dialog');
    const title = within(dialog).getByLabelText(/^Account title/);
    await user.clear(title);
    await user.type(title, 'Payroll account');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(server.calls.find((c) => c.method === 'PUT')?.body).toMatchObject({ accountTitle: 'Payroll account', rowVersion: 2 }));
    expect(await screen.findByText(/waiting for a second administrator/)).toBeTruthy();
    // The number is masked for this person: it cannot be changed here and is not sent.
    expect(server.calls.find((c) => c.method === 'PUT')?.body).not.toHaveProperty('accountNumber');

    await user.click(within(row).getByRole('button', { name: 'Remove' }));
    const confirm = await screen.findByRole('alertdialog');
    await user.click(within(confirm).getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(server.calls.find((c) => c.path.endsWith('/remove'))?.body).toEqual({ rowVersion: 2 }));
  });

  it('offers no add or change to a person who may only read', async () => {
    companyServer();
    show(`/organization/companies/${COMPANY}`, '/organization/companies/*', <CompaniesScreen />, shell(['org.companies.read', 'org.company_registrations.read', 'org.company_bank_accounts.read']));
    await screen.findByText('1234567-8');
    expect(screen.queryByRole('button', { name: 'Add a registration' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Add an account' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Remove' })).toBeNull();
  });
});

describe('FR-ADM-ORG-02 locations', () => {
  const location = { id: 'l1', companyId: COMPANY, code: 'KHI', nameI18n: { en: 'Karachi office' }, branchId: '00000000-0000-7000-8000-0000000000b1', address: { line1: '1 Main Road', city: 'Karachi', country: 'PK' }, regionCode: 'SD',
    latitude: '24.860700', longitude: '67.001100', timeZone: 'Asia/Karachi', holidayCalendarId: null, operatingHours: { mon: { open: '09:00', close: '17:00' }, sun: { closed: true } }, isActive: true,
    createdAt: '2026-01-01T00:00:00Z', createdBy: 'u', updatedAt: '2026-01-01T00:00:00Z', updatedBy: 'u', rowVersion: 5 };

  it('refuses a latitude outside -90 to 90 next to the field, using the database rule named by the server', async () => {
    const user = userEvent.setup();
    expect(locationMetadata.constraints).toMatchObject({ ck_locations_latitude_range: 'latitude', ck_locations_operating_hours: 'operatingHours' });
    const server: Server = { calls: [], routes: {
      'GET /api/org/locations/l1': () => json(location),
      'GET /api/org/branches': () => json({ items: [{ id: '00000000-0000-7000-8000-0000000000b1', code: 'MAIN', nameI18n: { en: 'Main branch' } }], next: null }),
      'GET /api/ref/calendars': () => json({ items: [], next: null }),
      'PUT /api/org/locations/l1': () => json({ status: 400, title: 'errors.data.rule_violated', detail: 'ck_locations_latitude_range' }, 400),
    } };
    serve(server);
    show('/organization/locations/l1', '/organization/locations/*', <LocationsScreen />);
    const latitude = await screen.findByLabelText(/^Latitude/);
    await user.clear(latitude);
    await user.type(latitude, '95');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(server.calls.find((c) => c.method === 'PUT')?.body).toMatchObject({ latitude: 95, operatingHours: { mon: { open: '09:00', close: '17:00' }, sun: { closed: true } }, timeZone: 'Asia/Karachi' }));
    const field = document.getElementById('field-latitude') as HTMLElement;
    expect(await within(field).findByText('This value breaks a data rule.')).toBeTruthy();
  });

  it('shows the opening hours by weekday, closing a day clears its times, and a closing time before the opening is marked', async () => {
    const user = userEvent.setup();
    serve({ calls: [], routes: {
      'GET /api/org/locations/l1': () => json(location),
      'GET /api/org/branches': () => json({ items: [], next: null }),
      'GET /api/ref/calendars': () => json({ items: [], next: null }),
    } });
    show('/organization/locations/l1', '/organization/locations/*', <LocationsScreen />);
    const monday = await screen.findByLabelText('Opening time on Monday');
    expect((monday as HTMLInputElement).value).toBe('09:00');
    expect((screen.getByLabelText('Closing time on Monday') as HTMLInputElement).value).toBe('17:00');
    expect((screen.getByLabelText('Closed on Sunday') as HTMLInputElement).checked).toBe(true);
    await user.clear(screen.getByLabelText('Closing time on Monday'));
    await user.type(screen.getByLabelText('Closing time on Monday'), '08:00');
    expect(await screen.findByText('The opening time must be before the closing time.')).toBeTruthy();
    await user.click(screen.getByLabelText('Closed on Monday'));
    await waitFor(() => expect((screen.getByLabelText('Opening time on Monday') as HTMLInputElement).value).toBe(''));
  });

  it('checks a day by its times', () => {
    expect(dayProblem(undefined)).toBeNull();
    expect(dayProblem({ closed: true })).toBeNull();
    expect(dayProblem({ open: '09:00', close: '17:00' })).toBeNull();
    expect(dayProblem({ open: '17:00', close: '09:00' })).toBe('order');
    expect(dayProblem({ open: '9am', close: '17:00' })).toBe('format');
  });
});

describe('FR-ADM-LOC-04 holiday calendars', () => {
  const calendar = { id: 'c1', code: 'PK', nameI18n: { en: 'Pakistan holidays' }, countryCode: 'PK', source: 'tenant', isActive: true, createdAt: '2026-01-01T00:00:00Z', createdBy: 'u', updatedAt: '2026-01-01T00:00:00Z', updatedBy: 'u', rowVersion: 1 };
  const holiday = (id: string, date: string, name: string, kind: string, tentative: boolean) => ({ id, calendarId: 'c1', holidayDate: date, nameI18n: { en: name }, kind, isTentative: tentative, rowVersion: 1 });
  const year = new Date().getUTCFullYear();

  function calendarServer(extra: Server['routes'] = {}): Server {
    const server: Server = { calls: [], routes: {
      'GET /api/ref/calendars/c1': () => json(calendar),
      'GET /api/ref/holidays': () => json({ items: [holiday('h1', `${year}-03-23`, 'Pakistan Day', 'public', false), holiday('h2', `${year}-03-31`, 'Eid ul-Fitr', 'religious', true), holiday('h3', `${year - 1}-03-23`, 'Pakistan Day', 'public', false)], next: null }),
      'GET /api/org/companies': () => json({ items: [{ id: COMPANY, code: 'C1', nameI18n: { en: 'First company' } }], next: null }),
      'GET /api/org/locations': () => json({ items: [], next: null }),
      ...extra,
    } };
    serve(server);
    return server;
  }

  it('lists the holidays of the chosen year with the tentative mark and where the calendar is used', async () => {
    calendarServer();
    show('/organization/holiday-calendars/c1', '/organization/holiday-calendars/*', <CalendarsScreen />);
    expect(await screen.findByText('Pakistan Day')).toBeTruthy();
    expect(screen.getByText('Eid ul-Fitr')).toBeTruthy();
    expect(screen.getByText('Tentative')).toBeTruthy();
    expect(screen.getByText('Confirmed')).toBeTruthy();
    expect((await screen.findByRole('link', { name: 'First company' })).getAttribute('href')).toBe(`/organization/companies/${COMPANY}`);
  });

  it('copies the year forward and reports what was created and kept', async () => {
    const user = userEvent.setup();
    const server = calendarServer({ 'POST /api/ref/calendars/c1/copy-year': () => json({ created: 2, skipped: 0 }) });
    show('/organization/holiday-calendars/c1', '/organization/holiday-calendars/*', <CalendarsScreen />);
    await user.click(await screen.findByRole('button', { name: `Copy to ${year + 1}` }));
    await waitFor(() => expect(server.calls.find((c) => c.method === 'POST')?.body).toEqual({ fromYear: year, toYear: year + 1 }));
    expect(await screen.findByText(new RegExp(`2 holidays were copied from ${year} to ${year + 1}`))).toBeTruthy();
  });

  it('confirms a tentative date by saving it as confirmed with the new date', async () => {
    const user = userEvent.setup();
    const server = calendarServer({ 'PUT /api/ref/holidays/h2': (c) => json({ ...holiday('h2', `${year}-04-01`, 'Eid ul-Fitr', 'religious', false), ...(c.body as object), rowVersion: 2 }) });
    show('/organization/holiday-calendars/c1', '/organization/holiday-calendars/*', <CalendarsScreen />);
    const row = (await screen.findByText('Eid ul-Fitr')).closest('tr') as HTMLElement;
    await user.click(within(row).getByRole('button', { name: 'Confirm' }));
    const dialog = await screen.findByRole('dialog');
    expect((within(dialog).getByRole('switch', { name: /tentative/ }) as HTMLInputElement).checked).toBe(true);
    await user.click(within(dialog).getByRole('switch', { name: /tentative/ }));
    await user.click(within(dialog).getByRole('button', { name: 'Confirm' }));
    await waitFor(() => expect(server.calls.find((c) => c.method === 'PUT')?.body).toMatchObject({ calendarId: 'c1', isTentative: false, kind: 'religious', rowVersion: 1 }));
  });

  it('says a calendar of a country pack cannot be changed', async () => {
    calendarServer({ 'GET /api/ref/calendars/c1': () => json({ ...calendar, source: 'pack' }) });
    show('/organization/holiday-calendars/c1', '/organization/holiday-calendars/*', <CalendarsScreen />);
    expect(await screen.findByText('This calendar comes with a country pack')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Add a holiday' })).toBeNull();
  });
});
