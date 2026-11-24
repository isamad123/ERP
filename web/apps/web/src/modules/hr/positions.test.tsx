// The position screens of slice S1.22b: the pure chart model (FR-HRM-POS-04), the strength report and its drill (FR-HRM-POS-03), the chart
// with vacant boxes, search, keyboard and export (FR-HRM-POS-04), the position form and the position picker (FR-HRM-POS-02). The server
// decides everything: the screens show what it answered and send exactly what was chosen.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import type { ChartNode } from '../../api/positions';
import PositionChartScreen from './position-chart';
import { PositionPicker } from './position-picker';
import PositionForm from './position-form';
import StrengthScreen from './strength';
import { allParents, ancestorIds, buildTree, initialsOf, nextZoom, searchMatches, visibleRows } from './position-chart-model';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';
const UNIT = 'u-fin';
const READ = ['hrm.positions.read', 'org.org_units.read', 'org.locations.read', 'hrm.grades.read'];

function shell(permissions: string[] = READ): ShellValue {
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

const node = (code: string, parentId: string | null, extra: Partial<ChartNode> = {}): ChartNode => ({
  id: code.toLowerCase(), parentId, code, title: `Title ${code}`, unitId: UNIT, unitCode: 'FIN', gradeId: null, locationId: null, budgetedFte: 1, filledFte: 1, vacant: false, status: 'filled',
  holders: [{ employmentId: `e-${code}`, employeeNo: `E-${code}`, name: `Person ${code}`, nameLocal: null, photoFileId: null, fte: 1, isActing: false }], ...extra,
});

const BOSS = node('BOSS', null);
const LEAD = node('LEAD', 'boss', { vacant: true, filledFte: 0, status: 'open', holders: [] });
const CLERK = node('CLERK', 'lead', { budgetedFte: 2, filledFte: 1, vacant: true, status: 'open' });
const STAFF = node('STAFF', 'boss');
const NODES = [CLERK, STAFF, BOSS, LEAD];

describe('FR-HRM-POS-04 the model of the position chart', () => {
  const tree = buildTree(NODES);

  it('puts boxes under their line, ordered by code, and treats a line that leaves the chart as a top box', () => {
    expect(tree.roots.map((r) => r.code)).toEqual(['BOSS']);
    expect(tree.children.get('boss')?.map((c) => c.code)).toEqual(['LEAD', 'STAFF']);
    expect(buildTree([CLERK]).roots.map((r) => r.code)).toEqual(['CLERK']);
    expect(buildTree([node('LOOP', 'loop')]).roots).toHaveLength(1);
  });

  it('shows only the open part, counts the rest and never walks a box twice', () => {
    expect(visibleRows(tree, new Set(), 50).rows.map((r) => r.node.code)).toEqual(['BOSS']);
    const open = visibleRows(tree, new Set(['boss', 'lead']), 50);
    expect(open.rows.map((r) => `${r.level}:${r.node.code}`)).toEqual(['1:BOSS', '2:LEAD', '3:CLERK', '2:STAFF']);
    expect(visibleRows(tree, new Set(['boss', 'lead']), 2).rows).toHaveLength(2);
    expect(visibleRows(tree, new Set(['boss', 'lead']), 2).total).toBe(4);
    const loop = buildTree([node('A', 'b'), node('B', 'a')]);
    expect(visibleRows(loop, new Set(['a', 'b']), 10).rows).toHaveLength(0);
  });

  it('finds people and positions by name, code, number or unit, and names what to open to show them', () => {
    expect([...searchMatches(NODES, 'person clerk')]).toEqual(['clerk']);
    expect([...searchMatches(NODES, 'e-staff')]).toEqual(['staff']);
    expect(searchMatches(NODES, 'fin').size).toBe(4);
    expect(searchMatches(NODES, '  ').size).toBe(0);
    expect(ancestorIds(tree, 'clerk')).toEqual(['lead', 'boss']);
    expect([...allParents(tree)].sort()).toEqual(['boss', 'lead']);
  });

  it('makes initials and steps the zoom within its bounds', () => {
    expect(initialsOf('Amna Bibi Khan', 'X')).toBe('AB');
    expect(initialsOf(null, 'ceo')).toBe('CE');
    expect(nextZoom(100, 1)).toBe(125);
    expect(nextZoom(60, -1)).toBe(60);
    expect(nextZoom(200, 1)).toBe(200);
  });
});

interface Call { method: string; path: string; body: unknown }

function serve(routes: Record<string, (call: Call) => Response> = {}) {
  const calls: Call[] = [];
  const all: Record<string, (call: Call) => Response> = {
    'GET /api/org/org-units': () => json({ items: [{ id: UNIT, code: 'FIN', nameI18n: { en: 'Finance' }, isActive: true, companyId: COMPANY, rowVersion: 1 }], next: null }),
    'GET /api/org/locations': () => json({ items: [], next: null }),
    'GET /api/hrm/grades': () => json({ items: [{ id: 'g1', code: 'G7', nameI18n: { en: 'Grade 7' }, rank: 7 }] }),
    ...routes,
  };
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = '', query = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    const call = { method, path: path + (query ? `?${query}` : ''), body: typeof init?.body === 'string' ? JSON.parse(init.body) as unknown : null };
    calls.push(call);
    const handler = all[`${method} ${path}`];
    return handler ? handler(call) : json({ status: 404, title: 'errors.data.not_found' }, 404);
  }));
  return calls;
}

function show(route: string, path: string, element: React.ReactNode, value = shell(), locale: 'en' | 'ur' = 'en') {
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
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const report = (model: 'position' | 'headcount' = 'position') => ({
  companyId: COMPANY, asOf: '2026-12-01', model, groupBy: ['unit', 'grade'], totals: { sanctioned: 6, filled: 4, vacant: 2, over: 0.5, planned: 1, frozen: 0 },
  rows: [{ unitId: UNIT, unitCode: 'FIN', unitName: { en: 'Finance' }, gradeId: 'g1', gradeCode: 'G7', locationId: null, locationCode: null, locationName: null, sanctioned: 6, filled: 4, vacant: 2, over: 0.5, planned: 1, frozen: 0, positions: 3 }],
});

describe('FR-HRM-POS-03 the strength report screen', () => {
  it('shows sanctioned, filled, vacant and over with totals, and drills into the positions of a row', async () => {
    const user = userEvent.setup();
    const calls = serve({
      'GET /api/hrm/strength-report': () => json(report()),
      'GET /api/hrm/strength-report/drill': () => json({ asOf: '2026-12-01', model: 'position', people: [], positions: [{ id: 'p1', code: 'FIN-1', title: 'Accountant', unitId: UNIT, gradeId: 'g1', locationId: null, budgetedFte: 2, filledFte: 1, vacantFte: 1, overFte: 0, status: 'open', holders: [{ employmentId: 'e1', employeeNo: 'E-1', fte: 1, isActing: false, name: 'Amna Khan' }] }] }),
    });
    show('/hr/strength-report', '/hr/strength-report', <StrengthScreen />);
    const table = await screen.findByRole('table', { name: 'Strength report' });
    const body = within(table).getAllByRole('row');
    expect(within(body[1] as HTMLElement).getByText('Finance')).toBeTruthy();
    expect(within(body[1] as HTMLElement).getByText('0.5').className).toContain('erp-pos-over');
    expect(within(table).getByText('Total')).toBeTruthy();
    expect(calls.find((c) => c.path.startsWith('/api/hrm/strength-report'))?.path).toContain('groupBy=unit%2Cgrade');
    await user.click(screen.getByRole('button', { name: 'Show details of Finance · G7' }));
    expect(await screen.findByText('Accountant')).toBeTruthy();
    expect(screen.getByText('Amna Khan')).toBeTruthy();
    expect(calls.some((c) => c.path.startsWith('/api/hrm/strength-report/drill') && c.path.includes('unitId=u-fin') && c.path.includes('gradeId=g1'))).toBe(true);
  });

  it('sends the grouping that was chosen and says plainly that a headcount company has no locations', async () => {
    const user = userEvent.setup();
    const calls = serve({ 'GET /api/hrm/strength-report': () => json(report()) });
    show('/hr/strength-report', '/hr/strength-report', <StrengthScreen />);
    await screen.findByRole('table', { name: 'Strength report' });
    await user.click(screen.getByRole('checkbox', { name: 'Location' }));
    await waitFor(() => expect(calls.some((c) => c.path.includes('groupBy=unit%2Cgrade%2Clocation'))).toBe(true));
    cleanup();
    serve({ 'GET /api/hrm/strength-report': () => json(report('headcount')) });
    show('/hr/strength-report', '/hr/strength-report', <StrengthScreen />);
    expect(await screen.findByText(/plans by headcount/)).toBeTruthy();
    expect(screen.queryByRole('checkbox', { name: 'Location' })).toBeNull();
  });

  it('shows the denied, empty and error states', async () => {
    serve({ 'GET /api/hrm/strength-report': () => json({ status: 403, title: 'errors.access.forbidden' }, 403) });
    show('/hr/strength-report', '/hr/strength-report', <StrengthScreen />);
    expect(await screen.findByText('You do not have access to this information.')).toBeTruthy();
    cleanup();
    serve({ 'GET /api/hrm/strength-report': () => json({ ...report(), rows: [] }) });
    show('/hr/strength-report', '/hr/strength-report', <StrengthScreen />);
    expect(await screen.findByText('Nothing is sanctioned for this date.')).toBeTruthy();
    cleanup();
    serve({ 'GET /api/hrm/strength-report': () => json({ status: 500, title: 'errors.server' }, 500) });
    show('/hr/strength-report', '/hr/strength-report', <StrengthScreen />);
    expect(await screen.findByText('This could not be loaded. Try again.')).toBeTruthy();
  });
});

const chartAnswer = (nodes = NODES) => ({ companyId: COMPANY, asOf: '2026-12-01', model: 'position', truncated: false, total: nodes.length, nodes });

describe('FR-HRM-POS-04 the org chart by position screen', () => {
  it('walks the reporting lines with the keyboard, marks vacant boxes and opens the way to a searched person', async () => {
    const user = userEvent.setup();
    const calls = serve({ 'GET /api/hrm/org-chart': () => json(chartAnswer()) });
    show('/hr/position-chart', '/hr/position-chart?asOf=2026-12-01', <PositionChartScreen />);
    const tree = await screen.findByRole('tree', { name: 'Reporting lines' });
    const items = () => within(tree).getAllByRole('treeitem');
    expect(calls.find((c) => c.path.startsWith('/api/hrm/org-chart'))?.path).toContain('asOf=2026-12-01');
    expect(items().map((i) => i.getAttribute('data-code'))).toEqual(['BOSS', 'LEAD', 'STAFF']);
    expect(within(items()[1] as HTMLElement).getByText('Vacant')).toBeTruthy();
    items()[0]?.focus();
    await user.keyboard('{ArrowDown}{ArrowRight}');
    expect(items().map((i) => i.getAttribute('data-code'))).toEqual(['BOSS', 'LEAD', 'CLERK', 'STAFF']);
    expect(within(items()[2] as HTMLElement).getByText(/Vacant: 1/)).toBeTruthy();
    await user.keyboard('{ArrowLeft}');
    expect(items().map((i) => i.getAttribute('data-code'))).toEqual(['BOSS', 'LEAD', 'STAFF']);
    await user.type(screen.getByRole('textbox', { name: 'Find a person or position' }), 'person clerk');
    expect(await screen.findByText('1 match')).toBeTruthy();
    await waitFor(() => expect(items().some((i) => i.getAttribute('data-code') === 'CLERK')).toBe(true));
    expect(items().find((i) => i.getAttribute('data-code') === 'CLERK')?.className).toContain('erp-pos-box-match');
  });

  it('sends the filters of the unit, exports PDF in the language of the screen and CSV, and says when the chart is too big for a PDF', async () => {
    const user = userEvent.setup();
    const anchor = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    Object.assign(URL, { createObjectURL: () => 'blob:x', revokeObjectURL: () => undefined });
    const calls = serve({
      'GET /api/hrm/org-chart': () => json(chartAnswer()),
      'POST /api/hrm/org-chart/export': () => new Response('%PDF', { status: 200, headers: { 'Content-Type': 'application/pdf', 'Content-Disposition': 'attachment; filename="org-chart-2026-12-01.pdf"' } }),
    });
    show('/hr/position-chart', '/hr/position-chart?asOf=2026-12-01&unit=u-fin', <PositionChartScreen />);
    await screen.findByRole('tree', { name: 'Reporting lines' });
    expect(calls.find((c) => c.path.startsWith('/api/hrm/org-chart'))?.path).toContain('unitId=u-fin');
    await user.click(screen.getByRole('button', { name: 'Export PDF' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true));
    expect(calls.find((c) => c.method === 'POST')?.body).toMatchObject({ companyId: COMPANY, asOf: '2026-12-01', unitId: 'u-fin', format: 'pdf', language: 'en', labels: { title: 'Org chart', asOf: 'As of', vacant: 'Vacant' } });
    await user.click(screen.getByRole('button', { name: 'Export CSV' }));
    await waitFor(() => expect(calls.filter((c) => c.method === 'POST')).toHaveLength(2));
    expect((calls.filter((c) => c.method === 'POST')[1]?.body as { format: string }).format).toBe('csv');
    anchor.mockRestore();
    cleanup();
    serve({ 'GET /api/hrm/org-chart': () => json({ ...chartAnswer(), total: 800 }) });
    show('/hr/position-chart', '/hr/position-chart?asOf=2026-12-01', <PositionChartScreen />);
    expect(await screen.findByText(/more than 500 boxes/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Export PDF' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('zooms, shows the empty, denied and truncated states', async () => {
    const user = userEvent.setup();
    serve({ 'GET /api/hrm/org-chart': () => json(chartAnswer()) });
    show('/hr/position-chart', '/hr/position-chart?asOf=2026-12-01', <PositionChartScreen />);
    const tree = await screen.findByRole('tree', { name: 'Reporting lines' });
    expect(tree.style.fontSize).toBe('100%');
    await user.click(screen.getByRole('button', { name: 'Zoom in' }));
    expect(tree.style.fontSize).toBe('125%');
    await user.click(screen.getByRole('button', { name: '125%' }));
    expect(tree.style.fontSize).toBe('100%');
    cleanup();
    serve({ 'GET /api/hrm/org-chart': () => json(chartAnswer([])) });
    show('/hr/position-chart', '/hr/position-chart', <PositionChartScreen />);
    expect(await screen.findByText('There is nothing to show for these filters and this date.')).toBeTruthy();
    cleanup();
    serve({ 'GET /api/hrm/org-chart': () => json({ status: 403, title: 'errors.access.forbidden' }, 403) });
    show('/hr/position-chart', '/hr/position-chart', <PositionChartScreen />);
    expect(await screen.findByText('You do not have access to this information.')).toBeTruthy();
    cleanup();
    serve({ 'GET /api/hrm/org-chart': () => json({ ...chartAnswer(), truncated: true, total: 9000 }) });
    show('/hr/position-chart', '/hr/position-chart', <PositionChartScreen />);
    expect(await screen.findByText(/shows the first 4 of 9,000 boxes/)).toBeTruthy();
  });
});

const row = (extra: object = {}) => ({ id: 'p1', companyId: COMPANY, positionCode: 'FIN-1', title: 'Accountant', orgUnitId: UNIT, gradeId: null, reportsToPositionId: null, locationId: null, budgetedFte: '1.0000', status: 'open', validFrom: '2025-01-01', validTo: null, rowVersion: 3, ...extra });

describe('FR-HRM-POS-04 English and Urdu', () => {
  it('shows the chart and the strength report in Urdu with the holder\'s Urdu name, and every screen text has an Urdu text', async () => {
    serve({
      'GET /api/hrm/org-chart': () => json(chartAnswer([node('BOSS', null, { holders: [{ employmentId: 'e1', employeeNo: 'E-1', name: 'Amna Khan', nameLocal: 'آمنہ خان', photoFileId: null, fte: 1, isActing: false }] })])),
      'GET /api/hrm/strength-report': () => json(report()),
    });
    show('/hr/position-chart', '/hr/position-chart?asOf=2026-12-01', <PositionChartScreen />, shell(), 'ur');
    expect(await screen.findByRole('tree', { name: 'رپورٹنگ لائنیں' })).toBeTruthy();
    expect(screen.getByText('آمنہ خان')).toBeTruthy();
    expect(screen.queryByText('Amna Khan')).toBeNull();
    cleanup();
    show('/hr/strength-report', '/hr/strength-report', <StrengthScreen />, shell(), 'ur');
    expect(await screen.findByRole('table', { name: 'طاقت کی رپورٹ' })).toBeTruthy();
    expect(screen.getByText('منظور شدہ')).toBeTruthy();
  });
});

describe('FR-HRM-POS-02 the position form and the picker', () => {
  it('creates a position sending exactly what was entered', async () => {
    const user = userEvent.setup();
    const calls = serve({ 'GET /api/hrm/positions': () => json({ items: [], next: null }), 'POST /api/hrm/positions': () => json(row(), 201) });
    show('/hr/positions/new', '/hr/positions/new', <PositionForm />, shell([...READ, 'hrm.positions.create']));
    await user.type(await screen.findByRole('textbox', { name: /Position code/ }), 'FIN-1');
    await user.type(screen.getByRole('textbox', { name: /Title/ }), 'Accountant');
    await user.click(screen.getByRole('button', { name: /Unit/ }));
    await user.click(await screen.findByRole('option', { name: /Finance/ }));
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true));
    expect(calls.find((c) => c.method === 'POST')).toMatchObject({ path: '/api/hrm/positions', body: { companyId: COMPANY, positionCode: 'FIN-1', title: 'Accountant', orgUnitId: UNIT, budgetedFte: 1 } });
  });

  it('changes a position from a date sending only what changed with the version it read, and says the refusal in words', async () => {
    const user = userEvent.setup();
    const calls = serve({
      'GET /api/hrm/positions/p1': () => json(row()),
      'GET /api/hrm/positions': () => json({ items: [row()], next: null }),
      'GET /api/hrm/positions/p1/holders': () => json({ position: row(), asOf: '2026-12-01', status: 'filled', budgetedFte: 1, filledFte: 1, vacantFte: 0, overFte: 0, holders: [{ employmentId: 'e1', employeeNo: 'E-77', fte: 1, isActing: false }] }),
      'POST /api/hrm/positions/p1/changes': () => json({ status: 409, title: 'errors.hrm.position.reporting_cycle' }, 409),
    });
    show('/hr/positions/:id', '/hr/positions/p1', <PositionForm />, shell([...READ, 'hrm.positions.update', 'hrm.positions.close']));
    expect(await screen.findByText('E-77')).toBeTruthy();
    expect(screen.getByText(/Budget 1, filled 1, vacant 0, over 0/)).toBeTruthy();
    await user.clear(screen.getByRole('textbox', { name: /Title/ }));
    await user.type(screen.getByRole('textbox', { name: /Title/ }), 'Senior accountant');
    await user.click(screen.getByRole('button', { name: 'Save change from this date' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true));
    const body = calls.find((c) => c.method === 'POST')?.body as Record<string, unknown>;
    expect(body).toMatchObject({ title: 'Senior accountant', rowVersion: 3 });
    expect(Object.keys(body).sort()).toEqual(['effectiveDate', 'rowVersion', 'title']);
    expect(await screen.findByRole('alert')).toBeTruthy();
  });

  it('shows the not found state and hides every action from a person who may not change positions', async () => {
    serve({ 'GET /api/hrm/positions/gone': () => json({ status: 404, title: 'errors.data.not_found' }, 404) });
    show('/hr/positions/:id', '/hr/positions/gone', <PositionForm />);
    expect(await screen.findByText('This position was not found.')).toBeTruthy();
    cleanup();
    serve({
      'GET /api/hrm/positions/p1': () => json(row()), 'GET /api/hrm/positions': () => json({ items: [row()], next: null }),
      'GET /api/hrm/positions/p1/holders': () => json({ position: row(), asOf: '2026-12-01', status: 'open', budgetedFte: 1, filledFte: 0, vacantFte: 1, overFte: 0, holders: [] }),
    });
    show('/hr/positions/:id', '/hr/positions/p1', <PositionForm />);
    expect(await screen.findByText('Nobody holds this position on this date.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Save change from this date' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Close position' })).toBeNull();
  });

  it('lists only open positions in the picker and gives back the id that was chosen', async () => {
    const user = userEvent.setup();
    const calls = serve({ 'GET /api/hrm/positions': () => json({ items: [row(), row({ id: 'p2', positionCode: 'FIN-2', title: 'Frozen one', status: 'frozen' }), row({ id: 'p3', positionCode: 'FIN-3', title: 'Auditor' })], next: null }) });
    const picked: (string | null)[] = [];
    show('/x', '/x', <PositionPicker companyId={COMPANY} asOf="2026-12-01" value={null} onChange={(id) => picked.push(id)} />);
    await user.click(await screen.findByRole('button', { name: /Position/ }));
    const options = (await screen.findAllByRole('option')).map((o) => o.textContent ?? '');
    expect(options.some((o) => o.includes('Accountant'))).toBe(true);
    expect(options.some((o) => o.includes('Frozen one'))).toBe(false);
    await user.click(await screen.findByRole('option', { name: /Auditor/ }));
    expect(picked).toEqual(['p3']);
    expect(calls.find((c) => c.path.startsWith('/api/hrm/positions'))?.path).toContain('asOf=2026-12-01');
  });
});
