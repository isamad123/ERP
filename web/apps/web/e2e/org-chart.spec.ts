// The organization units and the org chart end to end (slice S1.14b Acceptance; FR-KRN-ORG-03, FR-ADM-ORG-03, FR-ADM-ORG-04,
// FR-KRN-LOC-01, NFR-11, NFR-12): real API host on the real access engine and real PostgreSQL. A five-level structure is moved
// from 1 January 2027: the old parent shows for 31 December 2026 and the new one from 1 January; compare lists the move; a unit
// dropped on one of its own sub-units is refused with a clear message; a move by drag and drop and the same by keyboard; every
// screen passes axe in English and Urdu at three widths.
import { expect, test, type Page } from '@playwright/test';
import { accessible, display, fitsWidth, menu, menuEntries, noProblems, person, server, shot } from './support';

test.describe.configure({ mode: 'serial' });

const run = Math.random().toString(36).slice(2, 6).toUpperCase();
const WIDTHS = [360, 768, 1280] as const;
const ADMIN = ['org.org_units.read', 'org.org_units.create', 'org.org_units.update', 'org.org_units.delete', 'org.org_unit_types.read', 'org.org_unit_types.create', 'org.org_unit_types.update',
  'org.org_hierarchies.read', 'org.org_hierarchies.create', 'org.org_hierarchies.update', 'org.org_unit_links.read', 'org.org_unit_links.create', 'org.org_unit_links.update',
  'org.locations.read', 'org.companies.read', 'org.branches.read', 'ref.calendars.read', 'aud.history.view', 'cfg.saved_views.share'];

async function chart(page: Page, hierarchy: string, asOf: string, view: 'tree' | 'chart' = 'tree'): Promise<void> {
  await page.goto(`/organization/org-chart?hierarchy=${hierarchy}&asOf=${asOf}&view=${view}`);
  await expect(page.getByRole('heading', { name: 'Org chart', level: 1 })).toBeVisible();
}

const unit = (page: Page, code: string) => page.locator(`[data-unit="${run}-${code}"]`);

async function chooseParent(page: Page, label: RegExp): Promise<void> {
  const dialog = page.getByRole('dialog');
  const opener = dialog.getByRole('button', { name: /New parent/ });
  await opener.click();
  if (!(await page.getByRole('option').first().isVisible().catch(() => false))) await opener.click();
  await page.getByRole('option', { name: label }).click();
}

test('FR-ADM-ORG-03 a department moves to another division from 1 January 2027: the old parent shows the day before, the new one from the day, and compare lists the move', async ({ browser }) => {
  const { hierarchy } = await server.orgStructure(run);
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Omar Organization' });
  const page = admin.page;
  await page.reload();
  await expect(menu(page).getByRole('link', { name: 'Org chart' })).toBeVisible();
  const entries = await menuEntries(page);
  const at = (name: string) => entries.findIndex((e) => e.includes(name));
  expect(at('Locations')).toBeGreaterThanOrEqual(0);
  expect(at('Locations')).toBeLessThan(at('Organization units'));
  expect(at('Organization units')).toBeLessThan(at('Org chart'));
  expect(at('Org chart')).toBeLessThan(at('Holiday calendars'));

  await menu(page).getByRole('link', { name: 'Org chart' }).click();
  await expect(page.getByRole('heading', { name: 'Org chart', level: 1 })).toBeVisible();
  await chart(page, hierarchy, '2027-01-01');
  await expect(unit(page, 'ROOT')).toBeVisible();
  await unit(page, 'DIVA').click();
  await page.keyboard.press('ArrowRight');
  await expect(unit(page, 'DEPT')).toBeVisible();
  await expect(unit(page, 'DEPT')).toHaveAttribute('aria-level', '3');
  await accessible(page, 'org chart tree');
  await shot(page, 'org-chart-tree-en-light-1280');

  await unit(page, 'DEPT').click();
  await page.getByRole('button', { name: 'Move to…' }).click();
  const dialog = page.getByRole('dialog', { name: /Move Unit DEPT/ });
  await expect(dialog).toBeVisible();
  await chooseParent(page, /^Unit DIVB$/);
  await dialog.getByRole('textbox', { name: 'Reason (optional)' }).fill('Reorganisation');
  await accessible(page, 'move dialog');
  await shot(page, 'org-chart-move-en-light-1280');
  await dialog.getByRole('button', { name: 'Move' }).click();
  await expect(page.getByText(/Unit DEPT moved from/)).toBeVisible();

  // From 1 January 2027 the department is under division B; on 31 December 2026 it is still under division A.
  await chart(page, hierarchy, '2027-01-01');
  await expect(unit(page, 'DIVB')).toBeVisible();
  const rows = async () => (await page.getByRole('treeitem').evaluateAll((items) => items.map((i) => `${i.getAttribute('aria-level')}:${i.getAttribute('data-unit')}`)));
  await unit(page, 'DIVB').click();
  await page.keyboard.press('ArrowRight');
  expect(await rows()).toContain(`3:${run}-DEPT`);
  await chart(page, hierarchy, '2026-12-31');
  await unit(page, 'DIVA').click();
  await page.keyboard.press('ArrowRight');
  expect(await rows()).toEqual([`1:${run}-ROOT`, `2:${run}-DIVA`, `3:${run}-DEPT`, `2:${run}-DIVB`]);

  await page.getByRole('button', { name: 'Compare two dates' }).click();
  const compare = page.getByRole('dialog', { name: 'Compare two dates' });
  await expect(compare.getByText(`from ${run}-DIVA to ${run}-DIVB`)).toBeVisible();
  await expect(compare.getByText('Added (0)')).toBeVisible();
  await accessible(page, 'compare dialog');
  await shot(page, 'org-chart-compare-en-light-1280');
  await compare.getByRole('button', { name: 'Close' }).last().click();
  noProblems(page);
});

test('FR-ADM-ORG-03 a unit dropped on one of its own sub-units is refused in plain words, and a drop on a valid parent moves it', async ({ browser }) => {
  const { hierarchy } = await server.orgStructure(`${run}X`);
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Dina Drag' });
  const page = admin.page;
  const unitX = (code: string) => page.locator(`[data-unit="${run}X-${code}"]`);
  await page.reload();
  await chart(page, hierarchy, '2027-01-01', 'chart');
  const boxes = page.getByRole('group', { name: 'Org chart' });
  await expect(boxes).toBeVisible();
  for (const code of ['DIVA', 'DEPT']) await boxes.getByRole('button', { name: new RegExp(`Show the units below Unit ${code}`) }).click();
  await expect(unitX('TEAM')).toBeVisible();
  await accessible(page, 'org chart boxes');
  await shot(page, 'org-chart-boxes-en-light-1280');

  await unitX('DIVA').dragTo(unitX('TEAM'));
  const dialog = page.getByRole('dialog', { name: /Move Unit DIVA/ });
  await expect(dialog.getByText(/cannot be placed under itself or under one of its own sub-units/)).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Move' })).toBeDisabled();
  await dialog.getByRole('button', { name: 'Cancel' }).click();

  await unitX('TEAM').dragTo(unitX('DIVB'));
  const move = page.getByRole('dialog', { name: /Move Unit TEAM/ });
  await expect(move.getByRole('button', { name: /New parent/ })).toContainText('Unit DIVB');
  await move.getByRole('button', { name: 'Move' }).click();
  await expect(page.getByText(/Unit TEAM moved from/)).toBeVisible();
  await expect(boxes.getByRole('button', { name: /Show the units below Unit DIVB/ })).toBeVisible();
});

test('FR-ADM-ORG-03 the same move works by keyboard, and a unit that is in no place is placed', async ({ browser }) => {
  const { hierarchy } = await server.orgStructure(`${run}K`);
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Kamran Keyboard' });
  const page = admin.page;
  const unitK = (code: string) => page.locator(`[data-unit="${run}K-${code}"]`);
  await page.reload();
  await chart(page, hierarchy, '2027-01-01');
  await unitK('DIVA').focus();
  await page.keyboard.press('ArrowDown');
  await expect(unitK('DIVB')).toBeFocused();
  await page.keyboard.press('Enter');
  await page.getByRole('button', { name: 'Move to…' }).focus();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: /Move Unit DIVB/ });
  await expect(dialog).toBeVisible();
  const opener = dialog.getByRole('button', { name: /New parent/ });
  await opener.focus();
  await page.keyboard.press('Enter');
  if (!(await page.getByRole('option').first().isVisible().catch(() => false))) await page.keyboard.press('Enter');
  await page.getByRole('option', { name: /^Unit DIVA$/ }).press('Enter');
  await dialog.getByRole('button', { name: 'Move' }).press('Enter');
  await expect(page.getByText(/Unit DIVB moved from/)).toBeVisible();

  await page.getByRole('button', { name: 'Place Unit LOOSE…' }).first().click();
  const place = page.getByRole('dialog', { name: /Place Unit LOOSE/ });
  await chooseParent(page, /^Unit ROOT$/);
  await place.getByRole('button', { name: 'Place' }).click();
  await expect(page.getByText(/Unit LOOSE moved from/)).toBeVisible();
  await expect(unitK('LOOSE')).toBeVisible();
  await expect(page.getByRole('heading', { name: /Units not in this hierarchy/ })).toHaveCount(0);
});

test('FR-ADM-ORG-04 units, unit types and hierarchies are listed and edited, and the unit sections link to each other and to the chart', async ({ browser }) => {
  await server.orgStructure(`${run}U`);
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Uma Units' });
  const page = admin.page;
  await page.reload();
  await menu(page).getByRole('link', { name: 'Organization units' }).click();
  await expect(page.getByRole('heading', { name: 'Org units', level: 1 })).toBeVisible();
  await expect(page.getByRole('gridcell', { name: `${run}U-DEPT` })).toBeVisible();
  await accessible(page, 'unit list');
  await shot(page, 'org-units-en-light-1280');

  await page.getByRole('button', { name: 'New Org unit' }).first().click();
  await page.getByLabel(/^Code/).fill(`${run}-NEW`);
  await page.getByLabel(/^In English/).fill('A new unit');
  await page.getByRole('button', { name: /Unit type$/ }).click();
  await page.getByRole('option', { name: /^Department/ }).first().click();
  await accessible(page, 'unit form');
  await page.getByRole('button', { name: 'Create' }).click();
  await expect(page.getByText(/The unit .* was created/)).toBeVisible();

  const sections = page.getByRole('navigation', { name: 'Organization structure' });
  await page.goto('/organization/org-units');
  await sections.getByRole('link', { name: 'Unit types' }).click();
  await expect(page.getByRole('heading', { name: 'Org unit types', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Rename a standard term' })).toBeVisible();
  await accessible(page, 'unit type list');
  await sections.getByRole('link', { name: 'Hierarchies' }).click();
  await expect(page.getByRole('heading', { name: 'Org hierarchies', level: 1 })).toBeVisible();
  await accessible(page, 'hierarchy list');
  await sections.getByRole('link', { name: 'Org chart' }).click();
  await expect(page.getByRole('heading', { name: 'Org chart', level: 1 })).toBeVisible();
});

test('FR-KRN-LOC-01 NFR-11 NFR-12 the units screens and the org chart pass axe in Urdu and fit three widths', async ({ browser }) => {
  const { hierarchy } = await server.orgStructure(`${run}R`);
  const admin = await person(browser, { permissions: ADMIN, displayName: 'Urdu Org Admin' });
  const page = admin.page;
  await display(admin.context, { language: 'ur' });
  const paths: [string, string][] = [['/organization/org-units', 'units'], ['/organization/org-units/types', 'types'], ['/organization/org-units/hierarchies', 'hierarchies'],
    [`/organization/org-chart?hierarchy=${hierarchy}&asOf=2027-01-01`, 'chart tree'], [`/organization/org-chart?hierarchy=${hierarchy}&asOf=2027-01-01&view=chart`, 'chart boxes']];
  for (const [path, label] of paths) {
    for (const width of WIDTHS) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(path);
      await expect(page.locator('main h1')).toBeVisible();
      await accessible(page, `${label} ${width} ur`);
      await fitsWidth(page);
    }
  }

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`/organization/org-chart?hierarchy=${hierarchy}&asOf=2027-01-01`);
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await shot(page, 'org-chart-tree-ur-light-1280');
  noProblems(page);
});
