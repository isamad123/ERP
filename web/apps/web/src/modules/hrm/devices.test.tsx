// Attendance device screen (slice S1.27b; FR-HRM-ATT-02, ATT-03, ATT-20): devices are listed with status, last read and last error (never a secret);
// a mapping to a device sends the consent date and a refusal is shown in the server's words; the removal task is confirmed by a button; unmapped punches
// can be discarded; and a person without the permission sees "denied" and sends nothing.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import DevicesScreen from './devices';

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

const DEVICE = {
  id: 'd1', companyId: COMPANY, locationId: 'l1', serialNo: 'SN-100', model: 'F18', provider: 'zkteco', host: '10.0.0.5', port: 4370, protocol: 'tcp', hasCredentials: true, status: 'offline', online: false,
  lastSeenAt: '2026-11-23T03:00:00Z', lastSyncAt: null, lastError: 'errors.hrm.device.unreachable: timeout', syncRequestedAt: null, unmappedOpen: 2, rowVersion: 3,
};
const MAP = {
  id: 'm1', companyId: COMPANY, deviceId: 'd1', deviceUserNo: '17', employmentId: 'e1', employeeNo: 'E-1', validFrom: '2026-01-01', validTo: '2026-11-01', consentAt: '2026-01-01T00:00:00Z',
  removalDueAt: '2026-11-01T00:00:00Z', removalDoneAt: null, rowVersion: 4,
};

const ALL = ['hrm.attendance_devices.read', 'hrm.attendance_devices.create', 'hrm.attendance_devices.update', 'hrm.attendance_devices.sync', 'hrm.device_user_maps.read', 'hrm.device_user_maps.create',
  'hrm.device_user_maps.end', 'hrm.device_user_maps.removal', 'hrm.unmapped_punches.read', 'hrm.unmapped_punches.discard', 'hrm.attendance_events.read'];

describe('devices screen', () => {
  it('FR-HRM-ATT-02 lists devices with status, last read and last error in words, and never shows a secret', async () => {
    serve(() => json({ devices: [DEVICE] }));
    show(<DevicesScreen />, shell(ALL));
    expect(await screen.findByText('SN-100')).toBeTruthy();
    expect(screen.getByText('Offline')).toBeTruthy();
    expect(screen.getByText(/The device could not be reached\.: timeout/)).toBeTruthy();
    expect(screen.getByText('Never')).toBeTruthy(); // never read
    expect(screen.getByText('ZKTeco family (read by the platform)')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/secret|password/i);
    expect(sent[0]?.url).toContain('/api/hrm/attendance/devices?companyId=');
  });

  it('FR-HRM-ATT-02 "read now" asks the server and says it will happen within a minute', async () => {
    serve((method, url) => (method === 'POST' ? json({ id: 'd1' }) : url.includes('/devices') ? json({ devices: [DEVICE] }) : json({})));
    show(<DevicesScreen />, shell(ALL));
    await userEvent.click(await screen.findByRole('button', { name: 'Read now' }));
    expect(await screen.findByText('The device will be read within a minute.')).toBeTruthy();
    expect(sent.find((c) => c.method === 'POST')?.url).toBe('/api/hrm/attendance/devices/d1/sync');
  });

  it('FR-HRM-ATT-02 a person without the permission is told so and nothing is sent', () => {
    serve(() => json({}));
    show(<DevicesScreen />, shell([]));
    expect(screen.getByText('You may not see attendance devices.')).toBeTruthy();
    expect(sent.length).toBe(0);
  });

  it('FR-HRM-ATT-02 a company with no device is invited to register one', async () => {
    serve(() => json({ devices: [] }));
    show(<DevicesScreen />, shell(ALL));
    expect(await screen.findByText('No devices registered')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Register a device' })).toBeTruthy();
  });
});

describe('device users tab', () => {
  async function openMaps(handler: (method: string, url: string, body: unknown) => Response) {
    serve(handler);
    show(<DevicesScreen />, shell(ALL));
    await userEvent.click(await screen.findByRole('tab', { name: 'Device users' }));
  }

  it('FR-HRM-ATT-20 shows the removal task and confirms it with one button', async () => {
    await openMaps((method, url) => (method === 'POST' ? json({ ...MAP, removalDoneAt: '2026-11-24T00:00:00Z' }) : url.includes('device-maps') ? json({ maps: [MAP] }) : json({ devices: [DEVICE] })));
    expect(await screen.findByText('To do: remove the user from the device')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Removed from the device' }));
    expect(await screen.findByText('Recorded: the user was removed from the device.')).toBeTruthy();
    const post = sent.find((c) => c.method === 'POST')!;
    expect(post.url).toBe('/api/hrm/attendance/device-maps/m1/removal-done');
    expect(post.body).toEqual({ rowVersion: 4 });
  });

  it('FR-HRM-ATT-20 a refused mapping is shown in the server\'s words and sends only the number and the employee', async () => {
    await openMaps((method, url) => (method === 'POST'
      ? json({ title: 'errors.data.invalid_request', status: 400, errors: { consentDate: ['errors.hrm.device.consent_required'] } }, 400)
      : url.includes('device-maps') ? json({ maps: [] }) : json({ devices: [{ ...DEVICE, status: 'active' }] })));
    await userEvent.click(await screen.findByRole('button', { name: 'Map a device user' }));
    await userEvent.type(await screen.findByLabelText(/Device user number/), '17');
    await userEvent.type(screen.getByLabelText(/Employee number/), 'E-1');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText(/Record the date the employee consented/)).toBeTruthy();
    const post = sent.find((c) => c.method === 'POST')!;
    expect(post.body).toMatchObject({ companyId: COMPANY, deviceUserNo: '17', employeeNo: 'E-1' });
    expect(JSON.stringify(post.body)).not.toMatch(/template|finger|photo/i);
  });
});

describe('unmapped punches tab', () => {
  it('ATT-03 lists waiting punches and discards one', async () => {
    serve((method, url) => (method === 'POST' ? json({ id: 'u1' }) : url.includes('unmapped') ? json({ punches: [{ id: 'u1', companyId: COMPANY, deviceId: 'd1', deviceUserNo: '88', punchedAt: '2026-11-23T03:00:00Z', direction: 'in', receivedAt: '2026-11-23T03:01:00Z', status: 'open', rowVersion: 2 }] }) : json({ devices: [DEVICE] })));
    show(<DevicesScreen />, shell(ALL));
    await userEvent.click(await screen.findByRole('tab', { name: 'Unmapped punches' }));
    expect(await screen.findByText('88')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Discard' }));
    expect(await screen.findByText('The punch was discarded.')).toBeTruthy();
    expect(sent.find((c) => c.method === 'POST')).toMatchObject({ url: '/api/hrm/attendance/unmapped/u1/discard', body: { rowVersion: 2 } });
  });
});

describe('punch log tab', () => {
  it('FR-HRM-ATT-02 shows the log with source and device, marks a duplicate and offers more when the server has more', async () => {
    serve((_m, url) => (url.includes('punch-log') ? json({ events: [
      { id: 'a', employmentId: 'e1', employeeNo: 'E-1', eventAt: '2026-11-23T03:00:00Z', receivedAt: '2026-11-23T03:01:00Z', direction: 'in', source: 'biometric', deviceId: 'd1', deviceSerial: 'SN-100', isDuplicate: false },
      { id: 'b', employmentId: 'e1', employeeNo: 'E-1', eventAt: '2026-11-23T03:00:10Z', receivedAt: '2026-11-23T03:01:00Z', direction: 'in', source: 'kiosk', deviceId: null, deviceSerial: null, isDuplicate: true },
    ], next: 'x|y' }) : json({ devices: [DEVICE] })));
    show(<DevicesScreen />, shell(ALL));
    await userEvent.click(await screen.findByRole('tab', { name: 'Punch log' }));
    expect(await screen.findByText('SN-100')).toBeTruthy();
    expect(screen.getByText(/In \(Duplicate\)/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Show more' })).toBeTruthy();
    expect(sent.some((c) => c.url.includes('/punch-log') && c.url.includes('includeDuplicates=false'))).toBe(true);
  });
});
