// The org chart and the unit lists (slice S1.14b; FR-KRN-ORG-03, FR-ADM-ORG-03, FR-ADM-ORG-04): the structure on a date as a tree
// that can be walked with the keyboard, the top-down chart, the move dialog that is the keyboard way to re-parent (the body it sends
// and the server's refusal said in plain words), the comparison of two dates and the 200-box limit. The server decides everything.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import type { OrgTreeNode } from '../../api/org';
import OrgChartScreen from './org-chart';
import UnitsScreen from './units';
import { buildModel, descendantIds, parentChoices, visibleRows } from './org-chart-model';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';
const OTHER = '00000000-0000-7000-8000-000000000002';
const HIERARCHY = '00000000-0000-7000-8000-0000000000a1';
const ALL = ['org.org_units.read', 'org.org_units.create', 'org.org_units.update', 'org.org_unit_types.read', 'org.org_unit_types.create', 'org.org_unit_types.update',
  'org.org_hierarchies.read', 'org.org_hierarchies.create', 'org.org_hierarchies.update', 'org.org_unit_links.read', 'org.org_unit_links.create', 'org.org_unit_links.update', 'org.locations.read'];

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

const node = (id: string, code: string, parent: string | null, extra: Partial<OrgTreeNode> = {}): OrgTreeNode => ({
  unitId: id, parentUnitId: parent, code, name: { en: `Unit ${code}` }, unitTypeId: 't1', companyId: COMPANY, locationId: null, isActive: true, linkId: `l-${id}`, validFrom: '2026-01-01', validTo: null, ...extra,
});

const CEO = node('u-ceo', 'CEO', null);
const DIVA = node('u-diva', 'DIVA', 'u-ceo');
const DIVB = node('u-divb', 'DIVB', 'u-ceo');
const DEPT = node('u-dept', 'DEPT', 'u-diva');
const TEAM = node('u-team', 'TEAM', 'u-dept');
const FOREIGN = node('u-foreign', 'FOREIGN', null, { companyId: OTHER });
const LOOSE = node('u-loose', 'LOOSE', null, { linkId: null, validFrom: null });
const NODES = [CEO, DIVA, DIVB, DEPT, TEAM, FOREIGN];

describe('FR-ADM-ORG-03 the model of the chart', () => {
  const model = buildModel(NODES);

  it('puts children under their parent, roots first, and treats an unseen parent as a root', () => {
    expect(model.roots.map((r) => r.code)).toEqual(['CEO', 'FOREIGN']);
    expect(model.children.get('u-ceo')?.map((c) => c.code)).toEqual(['DIVA', 'DIVB']);
    expect(buildModel([DEPT]).roots.map((r) => r.code)).toEqual(['DEPT']);
  });

  it('lists every unit below a unit and offers as parents only units of the same company that are not below it', () => {
    expect([...descendantIds(model, 'u-diva')].sort()).toEqual(['u-dept', 'u-team']);
    expect(parentChoices(model, DEPT, NODES).map((n) => n.code)).toEqual(['CEO', 'DIVA', 'DIVB']);
    expect(parentChoices(model, DIVA, NODES).map((n) => n.code)).toEqual(['CEO', 'DIVB']);
  });

  it('shows only the open part of the tree and stops at the limit while counting the rest', () => {
    expect(visibleRows(model, new Set(), 200).rows.map((r) => r.node.code)).toEqual(['CEO', 'FOREIGN']);
    const open = visibleRows(model, new Set(['u-ceo', 'u-diva']), 200);
    expect(open.rows.map((r) => `${r.level}:${r.node.code}`)).toEqual(['1:CEO', '2:DIVA', '3:DEPT', '2:DIVB', '1:FOREIGN']);
    const limited = visibleRows(model, new Set(['u-ceo', 'u-diva']), 3);
    expect(limited.rows).toHaveLength(3);
    expect(limited.total).toBe(5);
  });
});

interface Call { method: string; path: string; body: unknown }
interface Server { calls: Call[]; routes: Record<string, (call: Call) => Response> }

function serve(extra: Server['routes'] = {}, nodes: OrgTreeNode[] = NODES, unplaced: OrgTreeNode[] = [LOOSE]): Server {
  const server: Server = { calls: [], routes: {
    'GET /api/org/org-hierarchies': () => json({ items: [{ id: HIERARCHY, code: 'reporting', nameI18n: { en: 'Reporting lines' }, kind: 'reporting', isActive: true, rowVersion: 1 }], next: null }),
    [`GET /api/org/hierarchies/${HIERARCHY}/tree`]: (call) => json({ hierarchyId: HIERARCHY, asOf: call.path.includes('2027') ? '2027-01-01' : '2026-12-31', nodes, unplaced }),
    'GET /api/org/org-unit-types': () => json({ items: [{ id: 't1', code: 'DEPARTMENT', nameI18n: { en: 'Department' }, isActive: true, rowVersion: 1 }], next: null }),
    ...extra,
  } };
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = '', query = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    const call = { method, path: path + (query ? `?${query}` : ''), body: init?.body && typeof init.body === 'string' ? JSON.parse(init.body) as unknown : null };
    server.calls.push(call);
    const handler = server.routes[`${method} ${path}`];
    return handler ? handler(call) : json({ status: 404, title: 'errors.data.not_found' }, 404);
  }));
  return server;
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

const chart = (value = shell(), query = '?asOf=2026-12-31') => show(`/organization/org-chart${query}`, '/organization/org-chart', <OrgChartScreen />, value);

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-ADM-ORG-03 the org chart screen', () => {
  it('opens the top of every company first and walks the tree with the arrow keys', async () => {
    const user = userEvent.setup();
    serve();
    chart();
    const tree = await screen.findByRole('tree', { name: 'Units' });
    const items = () => within(tree).getAllByRole('treeitem');
    expect(items().map((i) => i.getAttribute('data-unit'))).toEqual(['CEO', 'DIVA', 'DIVB', 'FOREIGN']);
    expect(items()[0]?.getAttribute('aria-expanded')).toBe('true');
    items()[0]?.focus();
    await user.keyboard('{ArrowDown}');
    expect(document.activeElement?.getAttribute('data-unit')).toBe('DIVA');
    await user.keyboard('{ArrowRight}');
    expect(items().map((i) => i.getAttribute('data-unit'))).toEqual(['CEO', 'DIVA', 'DEPT', 'DIVB', 'FOREIGN']);
    expect(items()[2]?.getAttribute('aria-level')).toBe('3');
    await user.keyboard('{ArrowLeft}');
    expect(items().map((i) => i.getAttribute('data-unit'))).toEqual(['CEO', 'DIVA', 'DIVB', 'FOREIGN']);
    await user.keyboard('{ArrowLeft}');
    expect(document.activeElement?.getAttribute('data-unit')).toBe('CEO');
    await user.keyboard('{End}');
    expect(document.activeElement?.getAttribute('data-unit')).toBe('FOREIGN');
  });

  it('moves a unit from a date with the dialog and sends exactly what was chosen', async () => {
    const user = userEvent.setup();
    const server = serve({ 'POST /api/org/org-units/u-diva/move': () => json({ outcome: 'done', id: 'l-new' }, 201) });
    chart();
    await user.click(await screen.findByRole('treeitem', { name: /DIVA/ }));
    await user.click(screen.getByRole('button', { name: 'Move to…' }));
    const dialog = await screen.findByRole('dialog', { name: 'Move Unit DIVA' });
    await user.click(within(dialog).getByRole('button', { name: /New parent/ }));
    await user.click(await screen.findByRole('option', { name: /DIVB/ }));
    await user.type(within(dialog).getByRole('textbox', { name: 'Reason (optional)' }), 'Reorganisation');
    await user.click(within(dialog).getByRole('button', { name: 'Move' }));
    await waitFor(() => expect(server.calls.some((c) => c.method === 'POST')).toBe(true));
    expect(server.calls.find((c) => c.method === 'POST')).toMatchObject({ path: '/api/org/org-units/u-diva/move', body: { hierarchyId: HIERARCHY, newParentId: 'u-divb', effectiveFrom: '2026-12-31', reason: 'Reorganisation' } });
  });

  it('offers no parent below the unit itself and none of another company', async () => {
    const user = userEvent.setup();
    serve();
    chart();
    await user.click(await screen.findByRole('treeitem', { name: /DIVA/ }));
    await user.click(screen.getByRole('button', { name: 'Move to…' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: /New parent/ }));
    const options = (await screen.findAllByRole('option')).map((o) => o.textContent ?? '');
    expect(options.some((o) => o.includes('DEPT'))).toBe(false);
    expect(options.some((o) => o.includes('TEAM'))).toBe(false);
    expect(options.some((o) => o.includes('FOREIGN'))).toBe(false);
    expect(options.some((o) => o.includes('DIVB'))).toBe(true);
  });

  it('says the server refusal of a cycle in plain words and keeps the dialog open', async () => {
    const user = userEvent.setup();
    serve({ 'POST /api/org/org-units/u-diva/move': () => json({ status: 400, title: 'errors.org.move_cycle' }, 400) });
    chart();
    await user.click(await screen.findByRole('treeitem', { name: /DIVA/ }));
    await user.click(screen.getByRole('button', { name: 'Move to…' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: /New parent/ }));
    await user.click(await screen.findByRole('option', { name: /DIVB/ }));
    await user.click(within(dialog).getByRole('button', { name: 'Move' }));
    expect(await within(dialog).findByText(/cannot be placed under itself or under one of its own sub-units/)).toBeTruthy();
  });

  it('opens the move dialog with the new parent already chosen when a unit is dropped on it', async () => {
    serve();
    chart();
    const dragged = await screen.findByRole('treeitem', { name: /DIVA/ });
    const target = screen.getByRole('treeitem', { name: /DIVB/ });
    const data = new Map<string, string>();
    const transfer = { setData: (k: string, v: string) => data.set(k, v), getData: (k: string) => data.get(k) ?? '', effectAllowed: '' };
    const { fireEvent } = await import('@testing-library/react');
    fireEvent.dragStart(dragged, { dataTransfer: transfer });
    fireEvent.dragOver(target, { dataTransfer: transfer });
    fireEvent.drop(target, { dataTransfer: transfer });
    const dialog = await screen.findByRole('dialog', { name: 'Move Unit DIVA' });
    expect(within(dialog).getByRole('button', { name: /New parent/ }).textContent).toContain('DIVB');
  });

  it('does not allow dropping a unit on a unit of another company', async () => {
    serve();
    chart();
    const dragged = await screen.findByRole('treeitem', { name: /DIVA/ });
    const target = screen.getByRole('treeitem', { name: /FOREIGN/ });
    const { fireEvent } = await import('@testing-library/react');
    const transfer = { setData: () => undefined, getData: () => '', effectAllowed: '' };
    fireEvent.dragStart(dragged, { dataTransfer: transfer });
    const allowed = fireEvent.dragOver(target, { dataTransfer: transfer });
    expect(allowed).toBe(true); // not cancelled: the browser shows "not allowed"
  });

  it('lists units without a place and places one with a first move', async () => {
    const user = userEvent.setup();
    const server = serve({ 'POST /api/org/org-units/u-loose/move': () => json({ outcome: 'done', id: 'l-new' }, 201) });
    chart();
    await user.click(await screen.findByRole('button', { name: 'Place Unit LOOSE…' }));
    const dialog = await screen.findByRole('dialog', { name: 'Place Unit LOOSE' });
    await user.click(within(dialog).getByRole('button', { name: /New parent/ }));
    await user.click(await screen.findByRole('option', { name: /Top level/ }));
    await user.click(within(dialog).getByRole('button', { name: 'Place' }));
    await waitFor(() => expect(server.calls.some((c) => c.method === 'POST')).toBe(true));
    expect(server.calls.find((c) => c.method === 'POST')?.body).toMatchObject({ hierarchyId: HIERARCHY, newParentId: null, effectiveFrom: '2026-12-31' });
  });

  it('compares two dates and lists what was added, removed and moved', async () => {
    const user = userEvent.setup();
    const server = serve({
      [`GET /api/org/hierarchies/${HIERARCHY}/compare`]: () => json({
        hierarchyId: HIERARCHY, from: '2026-12-31', to: '2027-12-31', added: [{ unitId: 'u-new', code: 'NEWU', name: { en: 'New unit' }, fromParentId: null, fromParentCode: null, toParentId: 'u-ceo', toParentCode: 'CEO' }],
        removed: [], moved: [{ unitId: 'u-dept', code: 'DEPT', name: { en: 'Unit DEPT' }, fromParentId: 'u-diva', fromParentCode: 'DIVA', toParentId: 'u-divb', toParentCode: 'DIVB' }],
      }),
    });
    chart();
    await user.click(await screen.findByRole('button', { name: 'Compare two dates' }));
    const dialog = await screen.findByRole('dialog', { name: 'Compare two dates' });
    expect(await within(dialog).findByText('from DIVA to DIVB')).toBeTruthy();
    expect(within(dialog).getByText('now under CEO')).toBeTruthy();
    expect(within(dialog).getByText('Removed (0)')).toBeTruthy();
    expect(server.calls.find((c) => c.path.includes('/compare'))?.path).toContain('from=2026-12-31&to=2027-12-31');
  });

  it('shows the chart with one box per unit and the same select and move steps', async () => {
    const user = userEvent.setup();
    serve();
    chart(shell(), '?asOf=2026-12-31&view=chart');
    const group = await screen.findByRole('group', { name: 'Org chart' });
    const boxes = within(group).getAllByRole('button', { pressed: false }).filter((b) => b.getAttribute('data-unit'));
    expect(boxes.map((b) => b.getAttribute('data-unit'))).toEqual(['CEO', 'DIVA', 'DIVB', 'FOREIGN']);
    await user.click(within(group).getByRole('button', { name: /Show the units below Unit DIVA/ }));
    expect(within(group).getByText('DEPT')).toBeTruthy();
  });

  it('keeps people without the update permission from moving: no button, no dragging', async () => {
    const user = userEvent.setup();
    serve();
    chart(shell(['org.org_unit_links.read', 'org.org_units.read', 'org.org_hierarchies.read']));
    const item = await screen.findByRole('treeitem', { name: /DIVA/ });
    expect(item.getAttribute('draggable')).toBe('false');
    await user.click(item);
    expect(screen.queryByRole('button', { name: 'Move to…' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Place Unit LOOSE…' })).toBeNull();
  });

  it('shows at most 200 boxes with "show more"', async () => {
    const user = userEvent.setup();
    const many = [CEO, ...Array.from({ length: 249 }, (_, i) => node(`u-${i}`, `U${String(i).padStart(3, '0')}`, 'u-ceo'))];
    serve({}, many, []);
    chart();
    const tree = await screen.findByRole('tree', { name: 'Units' });
    expect(within(tree).getAllByRole('treeitem')).toHaveLength(200);
    await user.click(screen.getByRole('button', { name: 'Show more (200 of 250 shown)' }));
    expect(within(tree).getAllByRole('treeitem')).toHaveLength(250);
  });

  it('says plainly when there is no hierarchy, when nothing is placed and when access is denied', async () => {
    serve({ 'GET /api/org/org-hierarchies': () => json({ items: [], next: null }) });
    chart();
    expect(await screen.findByText('No hierarchy yet')).toBeTruthy();
    cleanup();
    serve({ [`GET /api/org/hierarchies/${HIERARCHY}/tree`]: () => json({ status: 403, title: 'errors.access.forbidden' }, 403) });
    chart();
    expect(await screen.findByText('You are not allowed to see this.')).toBeTruthy();
  });
});

describe('FR-ADM-ORG-04 unit types, hierarchies and units', () => {
  const unit = { id: 'u1', companyId: COMPANY, code: 'FIN', nameI18n: { en: 'Finance' }, unitTypeId: 't1', locationId: null, costCenterRef: null, headPositionRef: null, isActive: true, custom: {}, createdAt: '2026-01-01T00:00:00Z', createdBy: 'u', updatedAt: '2026-01-01T00:00:00Z', updatedBy: 'u', rowVersion: 1 };

  it('lists units with their type, and links the unit sections and the chart', async () => {
    serve({ 'GET /api/org/org-units': () => json({ items: [unit], next: null }), 'GET /api/org/locations': () => json({ items: [], next: null }) });
    show('/organization/org-units', '/organization/org-units/*', <UnitsScreen />);
    expect(await screen.findByText('FIN')).toBeTruthy();
    expect(screen.getByText('Finance')).toBeTruthy();
    expect(await screen.findByText('Department')).toBeTruthy();
    const nav = screen.getByRole('navigation', { name: 'Organization structure' });
    expect(within(nav).getByRole('link', { name: 'Unit types' }).getAttribute('href')).toBe('/organization/org-units/types');
    expect(within(nav).getByRole('link', { name: 'Org chart' }).getAttribute('href')).toBe('/organization/org-chart');
  });

  it('links the unit types list to the labels editor for renaming standard terms', async () => {
    serve({ 'GET /api/org/org-unit-types': () => json({ items: [{ id: 't1', code: 'DEPARTMENT', nameI18n: { en: 'Department' }, levelHint: 2, isActive: true, rowVersion: 1 }], next: null }) });
    show('/organization/org-units/types', '/organization/org-units/*', <UnitsScreen />);
    const link = await screen.findByRole('link', { name: 'Rename a standard term' });
    expect(link.getAttribute('href')).toBe('/settings/labels?table=org.org_units');
  });
});
