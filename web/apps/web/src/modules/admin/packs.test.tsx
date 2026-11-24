// The pack manager (slice S1.23b; FR-ADM-PKG-01, FR-KRN-CFG-02, FR-HRM-POL-06, FR-ADM-ONB-01): installed packs with version, contents and compatibility,
// the objects the customer changed against the pack's value, conflicts with "take the pack value", newer versions with release notes, installing from
// the catalogue, held actions, and the read-only, unlicensed and denied states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import PacksScreen from './packs';
import { describeValue, type AvailablePack, type InstalledPack, type PackItem, type PackManagerView } from '../../api/packs';
import { json, shell, show } from './access-test-support';

const READ = ['cfg.installed_packs.read'];
const ALL = [...READ, 'cfg.installed_packs.create', 'cfg.installed_packs.update'];

const installed = (extra: Partial<InstalledPack> = {}): InstalledPack => ({
  packId: 'country-pk', kind: 'country', country: 'PK', title: { en: 'Pakistan', ur: 'پاکستان' }, version: '1.0.0', previousVersion: null,
  installedAt: '2026-11-20T10:00:00Z', upgradedAt: null, compatibleFrom: '1.0', compatibleTo: null, compatible: true, releaseNotes: { en: 'First release' },
  contents: [{ type: 'holiday', count: 12 }, { type: 'bank', count: 30 }], overridden: 0, conflicts: 0, retired: 0, updates: [], ...extra,
});
const available = (extra: Partial<AvailablePack> = {}): AvailablePack => ({
  packId: 'industry-textile', kind: 'industry', country: null, title: { en: 'Textiles' }, version: '1.2.0', releasedAt: '2026-11-01T00:00:00Z',
  releaseNotes: { en: 'Shift lists' }, licensed: true, compatible: true, contents: [{ type: 'option_list', count: 2 }], ...extra,
});
const view = (extra: Partial<PackManagerView> = {}): PackManagerView => ({ productVersion: '1.0', installed: [], available: [], pending: [], approvalRequired: false, ...extra });
const conflict: PackItem = {
  id: '00000000-0000-7000-8000-0000000000aa', type: 'option_list_item', key: 'relations:spouse', companyId: null, companyCode: null, version: '1.0.0', changed: true,
  packValue: { label: { en: 'Spouse', ur: 'شریکِ حیات' }, seq: 10 }, customerValue: { label: { en: 'Husband or wife' }, seq: 10 },
  conflictVersion: '1.0.1', conflictValue: { label: { en: 'Spouse (married)' }, seq: 10 }, retiredIn: null,
};

interface Server { sent: { method: string; url: string; body: unknown }[]; overview: PackManagerView; items?: PackItem[]; answer?: Response }

function serve(s: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      s.sent.push({ method, url: path, body: init?.body ? JSON.parse(String(init.body)) : null });
      return s.answer ?? json({ status: 'applied', result: { added: 3, updated: 1, kept: 2, retired: 0 } });
    }
    if (path === '/api/cfg/packs') return json(s.overview);
    if (path.endsWith('/items')) return json({ packId: 'country-pk', version: '1.0.0', total: (s.items ?? []).length, truncated: false, items: s.items ?? [] });
    return json({ title: 'errors.data.not_found' }, 404);
  }));
}

const open = (permissions = ALL, writable = true) => show('/settings/packs', '/settings/packs', <PacksScreen />, shell(permissions, writable));

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-ADM-PKG-01 the pack manager', () => {
  it('lists an installed pack with its version, contents, compatibility and a newer version with its release notes', async () => {
    serve({ sent: [], overview: view({ installed: [installed({ updates: [{ version: '1.0.1', releasedAt: '2026-12-01T00:00:00Z', releaseNotes: { en: 'Adds Pakistan Day' }, compatible: true, licensed: true }] })] }) });
    open();
    const card = await screen.findByRole('article', { name: 'Pakistan' });
    expect(within(card).getByText(/12 holidays · 30 banks/)).toBeTruthy();
    expect(within(card).getByText('Compatible')).toBeTruthy();
    expect(within(card).getByText(/Works with product 1.0 or later/)).toBeTruthy();
    expect(within(card).getByText(/Adds Pakistan Day/)).toBeTruthy();
    expect(within(card).getByRole('button', { name: 'Install version 1.0.1 of Pakistan' })).toBeTruthy();
  });

  it('FR-KRN-CFG-02 installs an update after confirming and reports what was kept', async () => {
    const s: Server = { sent: [], overview: view({ installed: [installed({ updates: [{ version: '1.0.1', releasedAt: '2026-12-01T00:00:00Z', releaseNotes: {}, compatible: true, licensed: true }] })] }) };
    serve(s);
    open();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Install version 1.0.1 of Pakistan' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(/objects you changed keep your values/)).toBeTruthy();
    await user.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'new holidays');
    await user.click(within(dialog).getByRole('button', { name: 'Go ahead' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ method: 'POST', url: '/api/cfg/packs/country-pk/upgrade', body: { version: '1.0.1', reason: 'new holidays' } });
    expect(await screen.findByText('Upgraded: 3 added, 1 updated, 2 kept as you had them, 0 switched off.')).toBeTruthy();
  });

  it('FR-HRM-POL-06 shows a conflict with the pack value, the customer value and the newer value, and takes the pack value', async () => {
    const s: Server = { sent: [], overview: view({ installed: [installed({ version: '1.0.1', overridden: 1, conflicts: 1 })] }), items: [conflict] };
    serve(s);
    open();
    const table = await screen.findByRole('table', { name: 'Objects of Pakistan' });
    const row = within(table).getAllByRole('row')[1]!;
    expect(within(row).getByText('label: Spouse · seq: 10')).toBeTruthy();
    expect(within(row).getByText('label: Husband or wife · seq: 10')).toBeTruthy();
    expect(within(row).getByText('Version 1.0.1: label: Spouse (married) · seq: 10')).toBeTruthy();
    expect(within(row).getByText('Changed by you')).toBeTruthy();
    const user = userEvent.setup();
    await user.click(within(row).getByRole('button', { name: 'Take the pack value of relations:spouse' }));
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Go ahead' }));
    await waitFor(() => expect(s.sent[0]?.url).toBe(`/api/cfg/packs/items/${conflict.id}/take-pack-value`));
  });

  it('installs a licensed pack from the catalogue and says when the install waits for approval', async () => {
    const s: Server = { sent: [], overview: view({ available: [available()], approvalRequired: true }), answer: json({ status: 'pending', requestId: 'r1' }, 202) };
    serve(s);
    open();
    expect(await screen.findByText(/wait for a second person's approval/)).toBeTruthy();
    expect(screen.getByText('No pack is installed yet.')).toBeTruthy();
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Install Textiles' }));
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Go ahead' }));
    await waitFor(() => expect(s.sent[0]).toMatchObject({ url: '/api/cfg/packs/install', body: { packId: 'industry-textile', version: '1.2.0', reason: null } }));
    expect(await screen.findByText(/Sent for approval/)).toBeTruthy();
  });

  it('offers no install for a pack outside the licence', async () => {
    serve({ sent: [], overview: view({ available: [available({ licensed: false })] }) });
    open();
    const table = await screen.findByRole('table', { name: 'Packs you can install' });
    expect(within(table).getByText('Not in your licence')).toBeTruthy();
    expect(within(table).queryByRole('button', { name: /Install/ })).toBeNull();
  });

  it('shows what waits for approval', async () => {
    serve({ sent: [], overview: view({ pending: [{ requestId: 'r1', action: 'upgrade', packId: 'country-pk', version: '1.0.1', itemId: null, requestedAt: '2026-11-24T00:00:00Z', mine: true }] }) });
    open();
    expect(await screen.findByText('Install update: country-pk 1.0.1')).toBeTruthy();
    expect(screen.getByText(/asked by you/)).toBeTruthy();
  });

  it('a person who may only read sees the packs without buttons that change them', async () => {
    serve({ sent: [], overview: view({ installed: [installed({ updates: [{ version: '1.0.1', releasedAt: '2026-12-01T00:00:00Z', releaseNotes: {}, compatible: true, licensed: true }] })], available: [available()] }) });
    open(READ);
    expect(await screen.findByText(/look at the packs but not install/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Install/ })).toBeNull();
  });

  it('a read-only licence shows no actions either', async () => {
    serve({ sent: [], overview: view({ available: [available()] }) });
    open(ALL, false);
    expect(await screen.findByText(/look at the packs but not install/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Install Textiles' })).toBeNull();
  });

  it('shows a denied message for a refused read', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ title: 'errors.access.forbidden' }, 403)));
    open(READ);
    expect(await screen.findByText('You may not see the packs.')).toBeTruthy();
  });

  it('describes values in the person language and keeps other values as stored', () => {
    expect(describeValue({ name: { en: 'Independence Day', ur: 'یومِ آزادی' }, kind: 'public', tentative: false }, 'ur')).toBe('name: یومِ آزادی · kind: public · tentative: false');
    expect(describeValue({ value: 7 }, 'en')).toBe('value: 7');
    expect(describeValue(null, 'en')).toBe('');
  });
});
