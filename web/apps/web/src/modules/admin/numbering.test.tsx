// Numbering screens (slice S1.15c Build item 9; FR-KRN-NUM-01 to -03, -05; S1.15 ruling Q11): the documents with their schemes, the next number, change before
// first use, "retire and replace" after it (and what happens when the new scheme is refused), the void report, and the read-only and denied states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import NumberingScreen from './numbering';
import type { NumberedType, SchemeRow, VoidReport } from '../../api/numbering';
import { COMPANY, json, shell, show } from './access-test-support';

const READ = ['cfg.numbering_schemes.read', 'cfg.numbering.void_report'];
const WRITE = [...READ, 'cfg.numbering_schemes.create', 'cfg.numbering_schemes.update', 'cfg.numbering_schemes.retire'];

const type = (code: string, extra: Partial<NumberedType> = {}): NumberedType => ({ code, module: 'hrm', assign: 'create', gapFreeAllowed: false, overrideAllowed: false, tenantWide: false, hasBranch: false,
  defaultPattern: 'DOC-{seq:5}', defaultReset: 'never', hasActiveScheme: false, ...extra });
const scheme = (id: string, documentType: string, pattern: string, extra: Partial<SchemeRow> = {}): SchemeRow => ({ id, companyId: COMPANY, branchId: null, documentType, pattern, resetRule: 'never',
  gapFree: false, startValue: 1, status: 'active', rowVersion: 1, ...extra });
const report = (schemeId: string, issued: VoidReport['issued'] = [], voids: VoidReport['voids'] = []): VoidReport => ({ schemeId, documentType: 'hrm.employee_no', pattern: 'EMP-{seq:5}', gapFree: false, from: null, to: null, issued, voids });

interface Server { sent: { method: string; url: string; body: unknown }[]; answer?: (method: string, url: string, body: unknown) => Response | undefined; schemes?: SchemeRow[]; reports?: Record<string, VoidReport> }

function serve(s: Server) {
  const types = [type('hrm.employee_no', { defaultPattern: 'EMP-{seq:5}', hasActiveScheme: true }), type('hrm.letter_no', { assign: 'post', gapFreeAllowed: true, hasBranch: true, defaultPattern: 'LTR-{seq:4}' })];
  const schemes = s.schemes ?? [scheme('s1', 'hrm.employee_no', 'EMP-{seq:5}', { startValue: 100 })];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      const body = init?.body ? JSON.parse(String(init.body)) : null;
      s.sent.push({ method, url: path, body });
      return s.answer?.(method, path, body) ?? json({ ...schemes[0], inUse: false });
    }
    if (path === '/api/cfg/numbering/document-types') return json(types);
    if (path === '/api/cfg/numbering-schemes') return json({ items: schemes, next: null });
    if (path === '/api/cfg/numbering/void-report') {
      const id = new URLSearchParams(url.split('?')[1]).get('schemeId') ?? '';
      return json(s.reports?.[id] ?? report(id));
    }
    if (path.startsWith('/api/cfg/settings/')) return json({ setting: { value: 7 }, layers: [] });
    if (path === '/api/org/branches') return json({ items: [{ id: 'b1', code: 'KHI', nameI18n: { en: 'Karachi' } }], next: null });
    return json({ title: 'errors.data.not_found' }, 404);
  }));
}

const open = (permissions = WRITE, at = '/settings/numbering') => show('/settings/numbering', at, <NumberingScreen />, shell(permissions));

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-KRN-NUM-01 the numbering screen', () => {
  it('lists each numbered document with its pattern and says where the standard pattern is still to be set', async () => {
    serve({ sent: [] });
    open();
    const table = await screen.findByRole('table', { name: 'Numbered documents' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);
    expect(within(rows[0]!).getByText('EMP-{seq:5}')).toBeTruthy();
    expect(within(rows[0]!).getByText('Scheme set')).toBeTruthy();
    expect(within(rows[1]!).getByText('Standard pattern, set at first use')).toBeTruthy();
  });

  it('FR-KRN-NUM-02 shows the next number of a scheme that has issued numbers and of one that has not', async () => {
    serve({ sent: [], reports: { s1: report('s1', [{ period: 'all', firstNumber: 'EMP-00100', lastNumber: 'EMP-00123', count: null, gapFree: false }]) } });
    open(WRITE, '/settings/numbering?type=hrm.employee_no');
    expect(await screen.findByText('EMP-00124')).toBeTruthy();
    expect(screen.getByText(/Last number issued/)).toBeTruthy();
    cleanup();
    serve({ sent: [] });
    open(WRITE, '/settings/numbering?type=hrm.employee_no');
    expect(await screen.findByText('EMP-00100')).toBeTruthy();
    expect(screen.getByText('No number issued yet')).toBeTruthy();
  });

  it('changes a scheme that has issued no number, and shows the server\'s refusal of a pattern that is too close to an earlier one', async () => {
    const s: Server = { sent: [], answer: () => json({ title: 'errors.numbering.pattern_too_close' }, 400) };
    serve(s);
    open(WRITE, '/settings/numbering?type=hrm.employee_no');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Change the scheme EMP-{seq:5}' }));
    const dialog = await screen.findByRole('dialog', { name: /Change the scheme of/ });
    const pattern = within(dialog).getByRole('textbox', { name: /^Pattern/ });
    await user.clear(pattern);
    await user.type(pattern, 'EMP-0{{seq:4}');
    expect(within(dialog).getByText(/Example of the first number/)).toBeTruthy();
    await user.click(within(dialog).getByRole('button', { name: 'Save the scheme' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ method: 'PUT', url: '/api/cfg/numbering/schemes/s1', body: { pattern: 'EMP-0{seq:4}', rowVersion: 1 } });
    expect(await within(dialog).findByText(/can print the same numbers as an earlier scheme/)).toBeTruthy();
  });

  it('refuses a pattern without a running number before anything is sent', async () => {
    const s: Server = { sent: [] };
    serve(s);
    open(WRITE, '/settings/numbering?type=hrm.employee_no');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Change the scheme EMP-{seq:5}' }));
    const dialog = await screen.findByRole('dialog', { name: /Change the scheme of/ });
    const pattern = within(dialog).getByRole('textbox', { name: /^Pattern/ });
    await user.clear(pattern);
    await user.type(pattern, 'EMP');
    await user.click(within(dialog).getByRole('button', { name: 'Save the scheme' }));
    expect(await within(dialog).findAllByText(/running number/)).not.toHaveLength(0);
    expect(s.sent).toHaveLength(0);
  });
});

describe('FR-KRN-NUM-01 retire and replace (ruling Q11)', () => {
  const used = { s1: report('s1', [{ period: 'all', firstNumber: 'EMP-00100', lastNumber: 'EMP-00123', count: null, gapFree: false }]) };

  it('never edits a used scheme: it offers retire and replace, which retires the old scheme and sets the new one', async () => {
    const s: Server = { sent: [], reports: used, answer: (_m, u) => (u.endsWith('/schemes') ? json(scheme('s2', 'hrm.employee_no', 'EMPL-{seq:5}'), 201) : json(scheme('s1', 'hrm.employee_no', 'EMP-{seq:5}', { status: 'retired' }))) };
    serve(s);
    open(WRITE, '/settings/numbering?type=hrm.employee_no');
    const user = userEvent.setup();
    expect(await screen.findByText(/cannot be changed/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Change the scheme/ })).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Retire and replace the scheme EMP-{seq:5}' }));
    const dialog = await screen.findByRole('dialog', { name: /Retire and replace the scheme of/ });
    const pattern = within(dialog).getByRole('textbox', { name: /^Pattern/ });
    await user.clear(pattern);
    await user.type(pattern, 'EMPL-{{seq:5}');
    await user.click(within(dialog).getByRole('button', { name: 'Retire and replace' }));
    await waitFor(() => expect(s.sent).toHaveLength(2));
    expect(s.sent[0]).toMatchObject({ method: 'POST', url: '/api/cfg/numbering/schemes/s1/retire', body: { rowVersion: 1 } });
    expect(s.sent[1]).toMatchObject({ method: 'POST', url: '/api/cfg/numbering/schemes', body: { documentType: 'hrm.employee_no', companyId: COMPANY, pattern: 'EMPL-{seq:5}', startValue: 100 } });
  });

  it('sets the old pattern again when the new scheme is refused, so numbering does not stop', async () => {
    let creates = 0;
    const s: Server = { sent: [], reports: used, answer: (_m, u) => {
      if (u.endsWith('/retire')) return json(scheme('s1', 'hrm.employee_no', 'EMP-{seq:5}', { status: 'retired' }));
      creates += 1;
      return creates === 1 ? json({ title: 'errors.numbering.pattern_too_close' }, 400) : json(scheme('s3', 'hrm.employee_no', 'EMP-{seq:5}'), 201);
    } };
    serve(s);
    open(WRITE, '/settings/numbering?type=hrm.employee_no');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Retire and replace the scheme EMP-{seq:5}' }));
    const dialog = await screen.findByRole('dialog', { name: /Retire and replace the scheme of/ });
    const pattern = within(dialog).getByRole('textbox', { name: /^Pattern/ });
    await user.clear(pattern);
    await user.type(pattern, 'EMP-0{{seq:4}');
    await user.click(within(dialog).getByRole('button', { name: 'Retire and replace' }));
    await waitFor(() => expect(s.sent).toHaveLength(3));
    expect(s.sent[2]).toMatchObject({ url: '/api/cfg/numbering/schemes', body: { pattern: 'EMP-{seq:5}', startValue: 100 } });
    expect(await within(dialog).findByText(/The old pattern has been set again/)).toBeTruthy();
  });

  it('says plainly when neither the new nor the old scheme could be set', async () => {
    const s: Server = { sent: [], reports: used, answer: (_m, u) => (u.endsWith('/retire') ? json(scheme('s1', 'hrm.employee_no', 'EMP-{seq:5}', { status: 'retired' })) : json({ title: 'errors.numbering.busy' }, 400)) };
    serve(s);
    open(WRITE, '/settings/numbering?type=hrm.employee_no');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Retire and replace the scheme EMP-{seq:5}' }));
    const dialog = await screen.findByRole('dialog', { name: /Retire and replace the scheme of/ });
    await user.click(within(dialog).getByRole('button', { name: 'Retire and replace' }));
    expect(await within(dialog).findByText(/Numbering of this document is stopped/)).toBeTruthy();
  });

  it('retires a scheme after a confirmation', async () => {
    const s: Server = { sent: [], answer: () => json(scheme('s1', 'hrm.employee_no', 'EMP-{seq:5}', { status: 'retired' })) };
    serve(s);
    open(WRITE, '/settings/numbering?type=hrm.employee_no');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Retire the scheme EMP-{seq:5}' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Retire the scheme?' });
    await user.click(within(dialog).getByRole('button', { name: 'Retire the scheme' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ url: '/api/cfg/numbering/schemes/s1/retire' });
  });
});

describe('FR-KRN-NUM-05 the void report and permissions', () => {
  it('lists the numbers issued by period and the voided numbers with their reasons', async () => {
    const withVoid = report('s1', [{ period: 'all', firstNumber: 'EMP-00100', lastNumber: 'EMP-00123', count: null, gapFree: false }],
      [{ id: 'v1', number: 'EMP-00110', reason: 'Offer withdrawn', documentId: null, schemeId: 's1', voidedAt: '2026-11-20T08:00:00Z', voidedBy: 'u1' }]);
    serve({ sent: [], reports: { s1: withVoid } });
    open(WRITE, '/settings/numbering?type=hrm.employee_no');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Void report of the scheme EMP-{seq:5}' }));
    const dialog = await screen.findByRole('dialog', { name: /Void report/ });
    const voids = await within(dialog).findByRole('table', { name: 'Void numbers' });
    expect(within(voids).getByText('EMP-00110')).toBeTruthy();
    expect(within(voids).getByText('Offer withdrawn')).toBeTruthy();
    expect(within(dialog).getByRole('table', { name: 'Numbers issued' })).toBeTruthy();
  });

  it('shows no change buttons to a person who may only read', async () => {
    serve({ sent: [] });
    open(['cfg.numbering_schemes.read'], '/settings/numbering?type=hrm.employee_no');
    expect(await screen.findByText('Not known')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Change the scheme/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Retire/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Void report/ })).toBeNull();
  });

  it('shows a denied state when the server refuses', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ title: 'errors.access.forbidden' }, 403)));
    open();
    expect(await screen.findByText('You may not see the numbering schemes.')).toBeTruthy();
  });
});
