// The labels editor (slice S1.15b Build item 5; FR-ADM-STU-25, FR-ADM-STU-05, FR-KRN-MET-22, FR-KRN-ORG-08): searchable texts with
// English and Urdu side by side, where each appears, the product text, the customer's and one company's change, reset to the
// product text, the read-only and denied states, and the screen's filter by table (the link of the unit types screen).
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import LabelsScreen from './labels';
import { catalogue, tableOfKey, viewOf, overridesByKey } from './labels-model';
import type { LabelOverride } from '../../api/config';
import { COMPANY, json, shell, show } from './access-test-support';

const C2 = '00000000-0000-7000-8000-000000000002';
const READ = ['cfg.labels.read'];
const WRITE = ['cfg.labels.read', 'cfg.labels.update'];
const row = (id: string, key: string, language: 'en' | 'ur', layer: LabelOverride['layer'], text: string, scopeId: string | null = null): LabelOverride => ({ id, key, language, layer, scopeId, text, rowVersion: 1 });

interface Server { sent: { method: string; url: string; body: unknown }[]; items: LabelOverride[]; refuse?: Response }

function serve(s: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    if (url === '/api/cfg/label-overrides' && method === 'GET') return s.refuse ?? json({ items: s.items });
    if (url === '/api/cfg/label-overrides' && method === 'PUT') {
      s.sent.push({ method, url, body: JSON.parse(String(init?.body)) });
      return s.refuse ?? json({ items: [] });
    }
    return json({ title: 'errors.data.not_found' }, 404);
  }));
}

function open(permissions = WRITE, at = '/settings/labels', companies = 2) {
  const value = shell(permissions);
  if (companies > 1) value.me.companies.push({ id: C2, code: 'C2', name: { en: 'Second company' } });
  show('/settings/labels', at, <LabelsScreen />, value);
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-ADM-STU-25 the label catalogue', () => {
  it('holds engine labels and web texts without placeholders, each once', () => {
    const all = catalogue();
    expect(new Set(all.map((e) => e.key)).size).toBe(all.length);
    expect(all.find((e) => e.key === 'nav.organizationUnits')).toMatchObject({ kind: 'message', en: 'Organization units' });
    expect(all.find((e) => e.key === 'entities.org.org_unit_types')).toMatchObject({ kind: 'engine', table: 'org.org_unit_types', en: 'Org unit types' });
    expect(all.some((e) => /[{}]/.test(e.en))).toBe(false);
    expect(all.some((e) => e.key === 'fields.org.org_units.created_at')).toBe(false);
    expect(tableOfKey('fields.org.org_units.code')).toEqual({ table: 'org.org_units', column: 'code' });
    expect(tableOfKey('options.hrm.employments.status.active')).toEqual({ table: 'hrm.employments', column: 'status' });
  });

  it('stacks the layers: product, customer, then the company (a company text wins for that company only)', () => {
    const entry = catalogue().find((e) => e.key === 'nav.organizationUnits')!;
    const rows = overridesByKey([row('1', entry.key, 'en', 'tenant', 'Wings'), row('2', entry.key, 'en', 'company', 'Wing', COMPANY)]);
    expect(viewOf(entry, rows, null).effective.en).toBe('Wings');
    expect(viewOf(entry, rows, COMPANY).effective.en).toBe('Wing');
    expect(viewOf(entry, rows, C2).effective.en).toBe('Wings');
    expect(viewOf(entry, rows, C2).base.en).toBe('Wings');
    expect(viewOf(entry, rows, null).base.en).toBe('Organization units');
  });
});

describe('FR-ADM-STU-05 FR-KRN-MET-22 the labels screen', () => {
  it('shows English and Urdu side by side with where the text appears, and finds a text by a word in either language', async () => {
    serve({ sent: [], items: [] });
    open();
    const table = await screen.findByRole('table', { name: 'Labels' });
    expect(within(table).getByRole('columnheader', { name: 'English' })).toBeTruthy();
    expect(within(table).getByRole('columnheader', { name: 'Urdu' })).toBeTruthy();
    const user = userEvent.setup();
    await user.type(screen.getByRole('textbox', { name: 'Search labels' }), 'nav.organizationUnits');
    const found = (await within(await screen.findByRole('table', { name: 'Labels' })).findAllByRole('row')).slice(1);
    expect(found).toHaveLength(1);
    expect(within(found[0]!).getAllByText('Organization units').length).toBe(2); // the name and the English column
    expect(within(found[0]!).getByText('Main menu')).toBeTruthy();
    await user.clear(screen.getByRole('textbox', { name: 'Search labels' }));
    await user.type(screen.getByRole('textbox', { name: 'Search labels' }), 'Org unit types');
    expect(await screen.findByText('entities.org.org_unit_types')).toBeTruthy();
    expect(screen.getAllByText(/Organization units/).length).toBeGreaterThan(0); // the screen that shows the table
  });

  it('filters by the table of the link from the unit types screen (R-210)', async () => {
    serve({ sent: [], items: [] });
    open(WRITE, '/settings/labels?table=org.org_unit_types');
    const table = await screen.findByRole('table', { name: 'Labels' });
    const keys = within(table).getAllByText(/^(entities|fields)\./).map((n) => n.textContent);
    expect(keys.length).toBeGreaterThan(3);
    expect(keys.every((k) => k!.includes('org.org_unit_types'))).toBe(true);
  });

  it('renames a text for the customer in both languages (a change of the English and the Urdu text in one save)', async () => {
    const s: Server = { sent: [], items: [] };
    serve(s);
    open(WRITE, '/settings/labels?q=nav.organizationUnits');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Rename the text Organization units' }));
    const drawer = await screen.findByRole('dialog', { name: 'Rename a text' });
    expect(within(drawer).getByText('Product text: Organization units')).toBeTruthy();
    const en = within(drawer).getByRole('textbox', { name: 'English text' });
    const ur = within(drawer).getByRole('textbox', { name: 'Urdu text' });
    await user.clear(en);
    await user.type(en, 'Wings');
    await user.clear(ur);
    await user.type(ur, 'ونگز');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]!.body).toEqual({ key: 'nav.organizationUnits', layer: 'tenant', texts: { en: 'Wings', ur: 'ونگز' } });
  });

  it('renames a text for one company only and sends the company', async () => {
    const s: Server = { sent: [], items: [row('1', 'nav.organizationUnits', 'en', 'tenant', 'Wings')] };
    serve(s);
    open(WRITE, `/settings/labels?q=nav.organizationUnits&company=${C2}`);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Rename the text Wings' }));
    const drawer = await screen.findByRole('dialog', { name: 'Rename a text' });
    expect(within(drawer).getByText('The change applies to: Company Second company.')).toBeTruthy();
    expect(within(drawer).getByText('Product text: Wings')).toBeTruthy(); // what is below the company: the customer's text
    const en = within(drawer).getByRole('textbox', { name: 'English text' });
    await user.clear(en);
    await user.type(en, 'Wing');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]!.body).toEqual({ key: 'nav.organizationUnits', layer: 'company', companyId: C2, texts: { en: 'Wing', ur: null } });
  });

  it('resets a changed text to the product text', async () => {
    const s: Server = { sent: [], items: [row('1', 'nav.organizationUnits', 'en', 'tenant', 'Wings'), row('2', 'nav.organizationUnits', 'ur', 'tenant', 'ونگز')] };
    serve(s);
    open(WRITE, '/settings/labels?q=nav.organizationUnits');
    const user = userEvent.setup();
    expect(await screen.findByText('Changed for the customer')).toBeTruthy();
    await user.click(await screen.findByRole('button', { name: 'Rename the text Wings' }));
    const drawer = await screen.findByRole('dialog', { name: 'Rename a text' });
    await user.click(within(drawer).getByRole('button', { name: 'Reset to product text' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]!.body).toEqual({ key: 'nav.organizationUnits', layer: 'tenant', texts: { en: null, ur: null } });
  });

  it('shows the server\'s refusal next to the text', async () => {
    const s: Server = { sent: [], items: [] };
    serve(s);
    open(WRITE, '/settings/labels?q=nav.organizationUnits');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Rename the text Organization units' }));
    const drawer = await screen.findByRole('dialog', { name: 'Rename a text' });
    await user.type(within(drawer).getByRole('textbox', { name: 'English text' }), ' X');
    s.refuse = json({ title: 'errors.data.invalid_request', errors: { 'texts.en': ['errors.labels.text_invalid'] } }, 400);
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    expect(await within(drawer).findByText('Check the texts marked below.')).toBeTruthy();
  });

  it('lets a reader open a text but not change it', async () => {
    serve({ sent: [], items: [] });
    open(READ, '/settings/labels?q=nav.organizationUnits');
    expect(await screen.findByText(/You can read the labels but not change them/)).toBeTruthy();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Open the text Organization units' }));
    const drawer = await screen.findByRole('dialog', { name: 'Rename a text' });
    expect(within(drawer).queryByRole('button', { name: 'Save' })).toBeNull();
    expect((within(drawer).getByRole('textbox', { name: 'English text' }) as HTMLInputElement).readOnly).toBe(true);
  });

  it('says so when the person may not see the labels', async () => {
    serve({ sent: [], items: [], refuse: json({ title: 'errors.access.forbidden' }, 403) });
    open(READ);
    expect(await screen.findByText('You cannot see the labels')).toBeTruthy();
  });
});
