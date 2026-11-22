// The settings centre (slice S1.15a; FR-ADM-SET-01 to -04 and -06, FR-ADM-USR-20, -21, -24, FR-ADM-LOC-05): grouping and search,
// the value in force with its layer, typed editors with their ranges, overrides for the customer and for one company, reset,
// history revert, held changes, and the denied and read-only states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import SettingsScreen from './settings';
import { fromDraft, groupOf, matches, toDraft } from './settings-model';
import type { SettingItem } from '../../api/settings';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const C1 = '00000000-0000-7000-8000-000000000001';
const C2 = '00000000-0000-7000-8000-000000000002';
const ADMIN = ['cfg.setting_values.read', 'cfg.setting_values.update', 'iam.users.read'];

const base = { module: 'iam', help: null, unit: null, layers: ['tenant'], requiresApproval: false, selfService: false, sourceScopeId: null, lastChange: null as unknown };
const idle: SettingItem = { ...base, key: 'iam.session.idle_minutes', area: 'security.sessions', name: { en: 'Idle timeout', ur: 'غیر فعالیت کی حد' }, description: { en: 'Minutes without activity before a session ends', ur: 'غیر فعالیت کے منٹ' },
  help: { en: 'A session ends when it has not been used for this many minutes.', ur: 'اتنے منٹ' }, type: 'int', unit: 'minutes', validation: { min: 5 }, layers: ['tenant', 'company'], default: 30, value: 30, source: 'tenant',
  lastChange: { at: '2026-11-20T08:00:00Z', by: 'u1' } } as SettingItem;
const length: SettingItem = { ...base, key: 'iam.password.min_length', area: 'security.passwords', name: { en: 'Minimum password length', ur: 'پاس ورڈ کی لمبائی' }, description: { en: 'Minimum password length', ur: 'پاس ورڈ کی لمبائی' },
  type: 'int', unit: 'characters', validation: { min: 6 }, default: 10, value: 10, source: 'product' } as SettingItem;
const digits: SettingItem = { ...base, module: 'loc', key: 'loc.number.digits', area: 'localisation', name: { en: 'Digits', ur: 'ہندسے' }, description: { en: 'Digits used to show numbers', ur: 'ہندسے' },
  type: 'choice', validation: { choices: ['western', 'eastern_arabic_indic'] }, layers: ['tenant', 'company'], default: 'western', value: 'western', source: 'product' } as SettingItem;
const held: SettingItem = { ...base, module: 'iam', key: 'iam.maker_checker.expiry_days', area: 'security.approvals', name: { en: 'Approval expiry', ur: 'منظوری کی میعاد' }, description: { en: 'Days until a request expires', ur: 'دن' },
  type: 'int', unit: 'days', validation: { min: 1 }, requiresApproval: true, default: 7, value: 7, source: 'product' } as SettingItem;
const timezone: SettingItem = { ...base, module: 'org', key: 'org.time_zone', area: 'organization', name: { en: 'Time zone', ur: 'ٹائم زون' }, description: { en: 'Time zone of the company', ur: 'ٹائم زون' },
  type: 'text', validation: { format: 'time_zone' }, layers: ['tenant', 'company'], default: 'Asia/Karachi', value: 'Asia/Karachi', source: 'product' } as SettingItem;
const logo: SettingItem = { ...base, module: 'cfg', key: 'cfg.branding.logo_file_id', area: 'branding', name: { en: 'Logo file', ur: 'لوگو' }, description: { en: 'The uploaded logo', ur: 'لوگو' },
  type: 'text', validation: {}, default: '', value: '', source: 'product' } as SettingItem;
const all = [idle, length, digits, held, timezone, logo];

function shell(permissions: string[] = ADMIN, companies = [{ id: C1, code: 'C1', name: { en: 'First company' } }, { id: C2, code: 'C2', name: { en: 'Second company' } }]): ShellValue {
  const grants = permissions.map((code) => ({ code, companies: null }));
  return {
    me: { userId: 'u-me', displayName: 'Me', language: 'en', timeZone: 'Asia/Karachi', defaultCompanyId: C1, companies, permissions: grants, mfa: { required: false, methods: [], enrolBy: null }, numbers: { digits: 'western', grouping: 'international' } },
    capabilities: { modules: [], features: [], flags: [], state: 'active', nextState: null, nextStateOn: null, warnings: [] },
    facts: accessFacts([], grants, 'active'),
    can: (code: string) => permissions.includes(code),
    writable: true,
    activeCompany: null,
    setActiveCompany: () => undefined,
  };
}

interface Server { sent: { method: string; url: string; body: unknown }[]; answer?: Response; pending?: unknown; history?: unknown[]; detailLayers?: unknown[] }

function serve(s: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = '', queryText = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      s.sent.push({ method, url: path, body: init?.body ? JSON.parse(String(init.body)) : null });
      return s.answer ?? json({ outcome: 'done', id: 'x', requestId: null });
    }
    if (path === '/api/cfg/settings') return json(all);
    if (path.startsWith('/api/cfg/settings/') && path.endsWith('/history')) return json(s.history ?? []);
    if (path.startsWith('/api/cfg/settings/')) {
      const key = decodeURIComponent(path.slice('/api/cfg/settings/'.length));
      const setting = all.find((x) => x.key === key);
      return setting ? json({ setting, layers: s.detailLayers ?? (setting === idle ? [{ layer: 'tenant', scopeId: null, value: 30, changed: { at: '2026-11-20T08:00:00Z', by: 'u1' }, ignored: null }] : []) }) : json({ title: 'errors.data.not_found' }, 404);
    }
    if (path === '/api/iam/maker-checker-requests/pending') return json(s.pending ?? { toDecide: [], mine: [] });
    if (path === '/api/iam/user-overview') return json({ items: [{ id: 'u1', username: 'amna', displayName: 'Amna Admin', email: 'a@x.test', status: 'active', lastLoginAt: null, defaultCompanyId: C1, accessValidTo: null, hasMfa: false, roles: [], companyIds: [], allCompanies: false }], next: null });
    void queryText;
    return json({ title: 'errors.data.not_found' }, 404);
  }));
}

function show(value = shell(), at = '/settings') {
  render(
    <MemoryRouter initialEntries={[at]}>
      <I18nRoot locale="en">
        <ToastProvider>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <ShellValueProvider value={value}>
              <Routes><Route path="/settings" element={<SettingsScreen />} /></Routes>
            </ShellValueProvider>
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

describe('FR-ADM-SET-01 the settings are grouped and searchable', () => {
  it('lists the settings under their groups with the iam keys and number formats in Sign-in and security', async () => {
    serve({ sent: [] });
    show();
    const signIn = await screen.findByRole('region', { name: 'Sign-in and security' });
    expect(within(signIn).getByText('Idle timeout')).toBeTruthy();
    expect(within(signIn).getByText('Minimum password length')).toBeTruthy();
    expect(within(signIn).getByText('Digits')).toBeTruthy();
    expect(screen.getByRole('region', { name: 'Organization' })).toBeTruthy();
    expect(groupOf(held)).toBe('signin');
    expect(groupOf(timezone)).toBe('organization');
  });

  it('finds "idle timeout" by searching "idle" and says so when nothing matches', async () => {
    serve({ sent: [] });
    show();
    await screen.findByText('Idle timeout');
    await userEvent.type(screen.getByRole('textbox', { name: 'Search settings' }), 'idle');
    expect(screen.getByText('Idle timeout')).toBeTruthy();
    expect(screen.queryByText('Minimum password length')).toBeNull();
    await userEvent.clear(screen.getByRole('textbox', { name: 'Search settings' }));
    await userEvent.type(screen.getByRole('textbox', { name: 'Search settings' }), 'zzzz');
    expect(await screen.findByText('No setting matches your search')).toBeTruthy();
    expect(matches(idle, 'IDLE')).toBe(true);
    expect(matches(idle, 'غیر فعالیت')).toBe(true);
  });

  it('never lists the raw logo file id (the sign-in page screen sets it)', async () => {
    serve({ sent: [] });
    show();
    await screen.findByText('Idle timeout');
    expect(screen.queryByText('Logo file')).toBeNull();
  });

  it('shows the permission-denied and error states', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ status: 403, title: 'errors.access.forbidden' }, 403)));
    show();
    expect(await screen.findByText('You may not see the settings of this customer.')).toBeTruthy();
  });
});

describe('FR-ADM-SET-02 each row shows the value in force, its layer and who changed it', () => {
  it('shows value, unit, layer and the last change', async () => {
    serve({ sent: [] });
    show();
    const row = (await screen.findByText('Idle timeout')).closest('.erp-settings-row') as HTMLElement;
    expect(within(row).getByText('30')).toBeTruthy();
    expect(within(row).getByText('minutes')).toBeTruthy();
    expect(within(row).getByText('Customer')).toBeTruthy();
    await waitFor(() => expect(within(row).getByText(/by Amna Admin/)).toBeTruthy());
    const standard = (await screen.findByText('Minimum password length')).closest('.erp-settings-row') as HTMLElement;
    expect(within(standard).getByText('Standard')).toBeTruthy();
    expect(within(standard).getByText(/Never changed/)).toBeTruthy();
  });
});

describe('FR-ADM-SET-03 override and reset to inherited', () => {
  it('overrides the customer value with a typed number and a reason', async () => {
    const s: Server = { sent: [] };
    serve(s);
    show();
    await userEvent.click(await screen.findByRole('button', { name: 'Change the setting Minimum password length' }));
    const drawer = await screen.findByRole('dialog', { name: 'Minimum password length' });
    await within(drawer).findByRole('table', { name: 'Where the value comes from' });
    const number = within(drawer).getByRole('textbox', { name: /Minimum password length/ });
    await userEvent.clear(number);
    await userEvent.type(number, '12');
    await userEvent.tab();
    await userEvent.type(within(drawer).getByRole('textbox', { name: /^Reason/ }), 'Audit finding');
    await userEvent.click(within(drawer).getByRole('button', { name: 'Override for the whole customer' }));
    await waitFor(() => expect(s.sent).toEqual([{ method: 'PUT', url: '/api/cfg/settings/iam.password.min_length', body: { layer: 'tenant', scopeId: null, value: 12, reason: 'Audit finding' } }]));
  });

  it('refuses a value below the minimum before anything is sent, and asks for a reason', async () => {
    const s: Server = { sent: [] };
    serve(s);
    show();
    await userEvent.click(await screen.findByRole('button', { name: 'Change the setting Minimum password length' }));
    const drawer = await screen.findByRole('dialog', { name: 'Minimum password length' });
    await within(drawer).findByRole('table', { name: 'Where the value comes from' });
    const number = within(drawer).getByRole('textbox', { name: /Minimum password length/ });
    await userEvent.clear(number);
    await userEvent.type(number, '2');
    await userEvent.tab();
    await userEvent.type(within(drawer).getByRole('textbox', { name: /^Reason/ }), 'x');
    await userEvent.click(within(drawer).getByRole('button', { name: 'Override for the whole customer' }));
    expect(await within(drawer).findAllByText('The value must be at least 6.')).not.toHaveLength(0);
    expect(s.sent).toEqual([]);
  });

  it('overrides for one company only and resets it to the inherited value', async () => {
    const s: Server = { sent: [], detailLayers: [
      { layer: 'tenant', scopeId: null, value: 30, changed: { at: '2026-11-20T08:00:00Z', by: 'u1' }, ignored: null },
      { layer: 'company', scopeId: C2, value: 45, changed: { at: '2026-11-21T08:00:00Z', by: 'u1' }, ignored: null },
    ] };
    serve(s);
    show(shell(), `/settings?company=${C2}`);
    await userEvent.click(await screen.findByRole('button', { name: 'Change the setting Idle timeout' }));
    const drawer = await screen.findByRole('dialog', { name: 'Idle timeout' });
    const table = await within(drawer).findByRole('table', { name: 'Where the value comes from' });
    expect(within(table).getByText('Company')).toBeTruthy();
    expect(within(table).getByText('45')).toBeTruthy();
    const number = within(drawer).getByRole('textbox', { name: /Idle timeout/ });
    expect((number as HTMLInputElement).value).toBe('45');
    await userEvent.type(within(drawer).getByRole('textbox', { name: /^Reason/ }), 'Back to the customer value');
    await userEvent.click(within(drawer).getByRole('button', { name: 'Reset to inherited' }));
    await waitFor(() => expect(s.sent).toEqual([{ method: 'POST', url: '/api/cfg/settings/iam.session.idle_minutes/reset', body: { layer: 'company', scopeId: C2, reason: 'Back to the customer value' } }]));
  });

  it('writes a choice setting as its stored code', async () => {
    const s: Server = { sent: [] };
    serve(s);
    show();
    await userEvent.click(await screen.findByRole('button', { name: 'Change the setting Digits' }));
    const drawer = await screen.findByRole('dialog', { name: 'Digits' });
    await within(drawer).findByRole('table', { name: 'Where the value comes from' });
    await userEvent.click(within(drawer).getByRole('button', { name: /Digits/ }));
    await userEvent.click(await screen.findByRole('option', { name: /Eastern Arabic-Indic/ }));
    await userEvent.type(within(drawer).getByRole('textbox', { name: /^Reason/ }), 'Urdu customer');
    await userEvent.click(within(drawer).getByRole('button', { name: 'Override for the whole customer' }));
    await waitFor(() => expect(s.sent[0]?.body).toEqual({ layer: 'tenant', scopeId: null, value: 'eastern_arabic_indic', reason: 'Urdu customer' }));
  });
});

describe('FR-ADM-SET-04 a change that needs approval waits for a second administrator', () => {
  it('says so after the answer and while the request is pending', async () => {
    const s: Server = { sent: [], answer: json({ outcome: 'pending', id: null, requestId: 'r1' }, 202) };
    serve(s);
    show();
    await userEvent.click(await screen.findByRole('button', { name: 'Change the setting Approval expiry' }));
    const drawer = await screen.findByRole('dialog', { name: 'Approval expiry' });
    expect(within(drawer).getByText(/must be approved by a second administrator/)).toBeTruthy();
    const number = within(drawer).getByRole('textbox', { name: /Approval expiry/ });
    await userEvent.clear(number);
    await userEvent.type(number, '14');
    await userEvent.tab();
    await userEvent.type(within(drawer).getByRole('textbox', { name: /^Reason/ }), 'Longer holidays');
    await userEvent.click(within(drawer).getByRole('button', { name: 'Override for the whole customer' }));
    expect(await screen.findByText('Sent for approval. It takes effect when a second administrator approves it.')).toBeTruthy();
  });

  it('marks a setting with a pending request as waiting', async () => {
    serve({ sent: [], pending: { toDecide: [], mine: [{ id: 'r1', kind: 'cfg.setting.change', requestedBy: 'u-me', requestedByName: 'Me', requestedAt: '2026-11-22T08:00:00Z', expiresAt: '2026-11-29T08:00:00Z', reason: 'x', subject: null,
      details: [{ label: 'changes.detail.settingKey', type: 'text', text: 'iam.maker_checker.expiry_days', date: null, names: null, items: null }], canApprove: false, approveProblem: 'errors.maker_checker.own_request' }] } });
    show();
    const row = (await screen.findByText('Approval expiry')).closest('.erp-settings-row') as HTMLElement;
    expect(await within(row).findByText('Waiting for a second administrator')).toBeTruthy();
    expect(within((await screen.findByText('Idle timeout')).closest('.erp-settings-row') as HTMLElement).queryByText('Waiting for a second administrator')).toBeNull();
  });
});

describe('FR-ADM-SET-06 history and revert', () => {
  it('shows the history and restores an earlier value with a reason', async () => {
    const s: Server = { sent: [], history: [{ id: 'h1', key: 'iam.session.idle_minutes', layer: 'tenant', scopeId: null, oldValue: 20, newValue: 30, reason: 'Slow screens', at: '2026-11-20T08:00:00Z', by: 'u1' }] };
    serve(s);
    show();
    await userEvent.click(await screen.findByRole('button', { name: 'Change the setting Idle timeout' }));
    const drawer = await screen.findByRole('dialog', { name: 'Idle timeout' });
    const list = await within(drawer).findByRole('list', { name: 'History' });
    expect(within(list).getByText('Slow screens')).toBeTruthy();
    await userEvent.click(within(list).getByRole('button', { name: 'Restore the earlier value' }));
    const confirm = await screen.findByRole('alertdialog', { name: 'Restore the earlier value' });
    await userEvent.type(within(confirm).getByRole('textbox', { name: /^Reason/ }), 'Wrong change');
    await userEvent.click(within(confirm).getByRole('button', { name: 'Restore the earlier value' }));
    await waitFor(() => expect(s.sent).toEqual([{ method: 'POST', url: '/api/cfg/settings/history/h1/revert', body: { reason: 'Wrong change' } }]));
  });
});

describe('read-only access', () => {
  it('lets a person who may only read see the setting without editing it', async () => {
    serve({ sent: [] });
    show(shell(['cfg.setting_values.read']));
    await userEvent.click(await screen.findByRole('button', { name: 'View the setting Idle timeout' }));
    const drawer = await screen.findByRole('dialog', { name: 'Idle timeout' });
    expect(await within(drawer).findByText('You may read this setting but not change it.')).toBeTruthy();
    expect(within(drawer).queryByRole('button', { name: 'Override for the whole customer' })).toBeNull();
    expect(within(drawer).queryByRole('textbox', { name: /^Reason/ })).toBeNull();
  });
});

describe('the value rules', () => {
  it('turns drafts back into typed values and refuses what the definition forbids', () => {
    expect(fromDraft(idle, '45')).toEqual({ ok: true, value: 45 });
    expect(fromDraft(idle, '4')).toEqual({ ok: false, problem: 'min', limit: 5 });
    expect(fromDraft(idle, '4.5')).toEqual({ ok: false, problem: 'whole' });
    expect(fromDraft(digits, 'roman')).toEqual({ ok: false, problem: 'choice' });
    expect(toDraft(idle, 30)).toBe('30');
    expect(fromDraft({ ...idle, type: 'list', validation: { item_type: 'int' } } as SettingItem, '30, 7,0')).toEqual({ ok: true, value: [30, 7, 0] });
    expect(fromDraft({ ...idle, type: 'text', validation: { max_length: 3 } } as SettingItem, 'abcd')).toEqual({ ok: false, problem: 'length', limit: 3 });
    expect(fromDraft({ ...idle, type: 'bool' } as SettingItem, true)).toEqual({ ok: true, value: true });
  });
});
