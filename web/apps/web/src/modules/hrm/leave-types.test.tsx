// The leave types screen (slice S1.33b Build item 6; FR-HRM-LVE-01): the list with names in both languages, paid and counting, the colour token, filters and search, create and change
// through the server's endpoints, the server's refusals beside the fields, the visible R-307 warning, retire and bring back (never delete), and the read-only and denied states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import LeaveTypesScreen from './leave-types';
import { COMPANY, json, sent, serve, shell, show } from './live-test-support';

const READ = ['hrm.leave_types.read'];
const EDIT = [...READ, 'hrm.leave_types.create', 'hrm.leave_types.update'];

const row = (id: string, code: string, en: string, ur: string, extra: Record<string, unknown> = {}) => ({
  id, companyId: COMPANY, code, nameI18n: { en, ur }, isPaid: true, counting: 'working_days', unit: 'day', requiresDocument: false, isCompOff: false, isUnpaidDeduction: false,
  source: 'tenant', colorToken: 'success', isActive: true, isDeleted: false, rowVersion: 4, ...extra,
});
const TYPES = [
  row('t1', 'AL', 'Annual leave', 'سالانہ رخصت'),
  row('t2', 'UL', 'Unpaid leave', 'بلا تنخواہ رخصت', { isPaid: false, isUnpaidDeduction: true, counting: 'calendar_days', colorToken: 'danger', requiresDocument: true }),
  row('t3', 'OLD', 'Old leave', 'پرانی رخصت', { isActive: false, colorToken: null }),
];

function open(permissions = EDIT, locale: 'en' | 'ur' = 'en', answer?: (method: string, url: string, body: unknown) => Response | undefined, writable = true) {
  serve((method, url, body) => {
    const custom = answer?.(method, url, body);
    if (custom) return custom;
    if (method === 'GET' && url.startsWith('/api/hrm/leave-types')) return json({ items: TYPES, next: null });
    return json(TYPES[0]);
  });
  show(<LeaveTypesScreen />, shell(permissions, writable), locale);
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-HRM-LVE-01 the list of leave types', () => {
  it('lists the active types with pay, counting, unit, document, colour and state, in a named keyboard region', async () => {
    open();
    const region = await screen.findByRole('region', { name: 'Leave types of the company' });
    expect(region.getAttribute('tabindex')).toBe('0');
    const rows = within(region).getAllByRole('row');
    expect(rows).toHaveLength(3);
    const annual = within(rows[1] as HTMLElement);
    expect(annual.getByText('AL')).toBeTruthy();
    expect(annual.getByText('Annual leave')).toBeTruthy();
    expect(annual.getByText('Paid')).toBeTruthy();
    expect(annual.getByText('Working days')).toBeTruthy();
    expect(annual.getByText('Whole days')).toBeTruthy();
    const chip = annual.getByText('Green (success)');
    expect(chip.getAttribute('data-colour')).toBe('success');
    expect(within(rows[2] as HTMLElement).getByText('Red (danger)').getAttribute('data-colour')).toBe('danger');
    expect(within(rows[2] as HTMLElement).getByText('Calendar days')).toBeTruthy();
    expect(screen.queryByText('Old leave')).toBeNull();
  });

  it('shows the names in Urdu when the language is Urdu', async () => {
    open(EDIT, 'ur');
    expect(await screen.findByText('سالانہ رخصت')).toBeTruthy();
    expect(screen.getByText('بلا تنخواہ رخصت')).toBeTruthy();
    expect(screen.queryByText('Annual leave')).toBeNull();
  });

  it('filters by state and searches by code or name', async () => {
    open();
    const user = userEvent.setup();
    await screen.findByText('Annual leave');
    await user.click(screen.getByRole('button', { name: /Show/ }));
    await user.click(await screen.findByRole('option', { name: 'Retired types' }));
    expect(await screen.findByText('Old leave')).toBeTruthy();
    expect(screen.queryByText('Annual leave')).toBeNull();
    expect(screen.getByText('No colour')).toBeTruthy();
    await user.click(screen.getByRole('button', { name: /Show/ }));
    await user.click(await screen.findByRole('option', { name: 'All types' }));
    await user.type(screen.getByRole('searchbox', { name: 'Search by code or name' }), 'unpaid');
    expect(screen.getByText('Unpaid leave')).toBeTruthy();
    expect(screen.queryByText('Annual leave')).toBeNull();
    expect(screen.queryByText('Old leave')).toBeNull();
  });

  it('says so when the company has no leave type yet', async () => {
    open(EDIT, 'en', (m, url) => (m === 'GET' && url.startsWith('/api/hrm/leave-types') ? json({ items: [], next: null }) : undefined));
    expect(await screen.findByText('No leave types yet')).toBeTruthy();
  });

  it('shows the denied state without the permission and when the server refuses the list', async () => {
    open([]);
    expect(await screen.findByText('You may not see leave types.')).toBeTruthy();
    expect(sent).toHaveLength(0);
    cleanup();
    open(READ, 'en', (m, url) => (m === 'GET' && url.startsWith('/api/hrm/leave-types') ? json({ title: 'errors.access.forbidden' }, 403) : undefined));
    expect(await screen.findByText('You may not see leave types.')).toBeTruthy();
  });

  it('shows an error with a retry when the list cannot be loaded', async () => {
    open(READ, 'en', (m, url) => (m === 'GET' && url.startsWith('/api/hrm/leave-types') ? json({ title: 'errors.http.500' }, 500) : undefined));
    expect(await screen.findByText('The leave types could not be loaded')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });

  it('offers no change to a reader or while the system is read-only', async () => {
    open(READ);
    await screen.findByText('Annual leave');
    expect(screen.queryByRole('button', { name: 'New leave type' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Edit leave type/ })).toBeNull();
    cleanup();
    open(EDIT, 'en', undefined, false);
    await screen.findByText('Annual leave');
    expect(screen.getByText('The system is read-only now, so nothing can be added or changed.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'New leave type' })).toBeNull();
  });
});

describe('FR-HRM-LVE-01 creating and changing a leave type', () => {
  it('sends the whole type to the server when a new one is saved', async () => {
    open();
    const user = userEvent.setup();
    await screen.findByText('Annual leave');
    await user.click(screen.getByRole('button', { name: 'New leave type' }));
    const dialog = await screen.findByRole('dialog', { name: 'New leave type' });
    await user.type(within(dialog).getByRole('textbox', { name: /^Code/ }), 'CL');
    await user.type(within(dialog).getByRole('textbox', { name: /^Name in English/ }), 'Casual leave');
    await user.type(within(dialog).getByRole('textbox', { name: /^Name in Urdu/ }), 'اتفاقی رخصت');
    await user.click(within(dialog).getByRole('button', { name: /Pay/ }));
    await user.click(await screen.findByRole('option', { name: 'Paid' }));
    await user.click(within(dialog).getByRole('button', { name: /Counted in/ }));
    await user.click(await screen.findByRole('option', { name: 'Working days' }));
    await user.click(within(dialog).getByRole('button', { name: /Taken in/ }));
    await user.click(await screen.findByRole('option', { name: 'Half days' }));
    await user.click(within(dialog).getByRole('button', { name: /Colour in calendars/ }));
    await user.click(await screen.findByRole('option', { name: 'Blue (information)' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sent.filter((s) => s.method === 'POST')).toHaveLength(1));
    expect(sent.find((s) => s.method === 'POST')).toEqual({
      method: 'POST', url: '/api/hrm/leave-types',
      body: { companyId: COMPANY, code: 'CL', nameI18n: { en: 'Casual leave', ur: 'اتفاقی رخصت' }, isPaid: true, isUnpaidDeduction: false, requiresDocument: false, isCompOff: false, colorToken: 'info', counting: 'working_days', unit: 'half_day', isActive: true },
    });
  });

  it('shows the server\'s refusals in words beside the fields', async () => {
    open(EDIT, 'en', (m) => (m === 'POST' ? json({ title: 'errors.data.invalid_request', errors: { code: ['errors.hrm.leave.code_taken'], isUnpaidDeduction: ['errors.hrm.leave.paid_deduction'], unit: ['errors.hrm.leave.hours_unit'] } }, 400) : undefined));
    const user = userEvent.setup();
    await screen.findByText('Annual leave');
    await user.click(screen.getByRole('button', { name: 'New leave type' }));
    const dialog = await screen.findByRole('dialog', { name: 'New leave type' });
    await user.type(within(dialog).getByRole('textbox', { name: /^Code/ }), 'AL');
    await user.click(within(dialog).getByRole('button', { name: /Pay/ }));
    await user.click(await screen.findByRole('option', { name: 'Paid' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await within(dialog).findByText('Another leave type of this company already has this code.')).toBeTruthy();
    expect(within(dialog).getByText('A paid leave type cannot be deducted from pay.')).toBeTruthy();
    expect(within(dialog).getByText('A leave type counted in hours is taken in hours, and only such a type is.')).toBeTruthy();
    expect(within(dialog).getByText('Check the fields marked below.')).toBeTruthy();
  });

  it('asks for the pay choice before sending anything', async () => {
    open();
    const user = userEvent.setup();
    await screen.findByText('Annual leave');
    await user.click(screen.getByRole('button', { name: 'New leave type' }));
    const dialog = await screen.findByRole('dialog', { name: 'New leave type' });
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await within(dialog).findByText('Choose whether the leave is paid or unpaid.')).toBeTruthy();
    expect(sent.filter((s) => s.method !== 'GET')).toHaveLength(0);
  });

  it('always shows the warnings: pay flags are not refused after first use (R-307); code, counting and unit are fixed once used', async () => {
    open();
    const user = userEvent.setup();
    await screen.findByText('Annual leave');
    await user.click(screen.getByRole('button', { name: 'Edit leave type AL' }));
    const dialog = await screen.findByRole('dialog', { name: 'Change leave type' });
    expect(within(dialog).getByText('Changing whether a type is paid or an unpaid deduction is not refused by the system even after the type has been used; payroll reads past periods with the new flag. Change it only before first use.')).toBeTruthy();
    expect(within(dialog).getByText('The code, how the type is counted and the unit it is taken in are fixed once the type is used; the server refuses the change.')).toBeTruthy();
  });

  it('changes a type with its row version and shows the server\'s "fixed once used" refusal', async () => {
    open(EDIT, 'en', (m) => (m === 'PUT' ? json({ title: 'errors.data.invalid_request', errors: { counting: ['errors.hrm.leave.type_fixed'] } }, 400) : undefined));
    const user = userEvent.setup();
    await screen.findByText('Annual leave');
    await user.click(screen.getByRole('button', { name: 'Edit leave type AL' }));
    const dialog = await screen.findByRole('dialog', { name: 'Change leave type' });
    await user.click(within(dialog).getByRole('button', { name: /Counted in/ }));
    await user.click(await screen.findByRole('option', { name: 'Calendar days' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sent.filter((s) => s.method === 'PUT')).toHaveLength(1));
    const put = sent.find((s) => s.method === 'PUT');
    expect(put?.url).toBe('/api/hrm/leave-types/t1');
    expect(put?.body).toMatchObject({ code: 'AL', counting: 'calendar_days', unit: 'day', isPaid: true, rowVersion: 4, colorToken: 'success' });
    expect(await within(dialog).findByText('The code, counting and unit cannot change once the leave type has ledger entries.')).toBeTruthy();
  });

  it('shows a plain message when someone else changed the type meanwhile', async () => {
    open(EDIT, 'en', (m) => (m === 'PUT' ? json({ title: 'errors.data.concurrency_conflict' }, 409) : undefined));
    const user = userEvent.setup();
    await screen.findByText('Annual leave');
    await user.click(screen.getByRole('button', { name: 'Edit leave type AL' }));
    const dialog = await screen.findByRole('dialog', { name: 'Change leave type' });
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await within(dialog).findByText('Someone else changed this at the same time. Reload the page and try again.')).toBeTruthy();
  });
});

describe('FR-HRM-LVE-01 retiring and bringing back (never deleted)', () => {
  it('retires an active type after confirmation, sending only the state and the row version', async () => {
    open();
    const user = userEvent.setup();
    await screen.findByText('Annual leave');
    await user.click(screen.getByRole('button', { name: 'Retire leave type AL' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(/A leave type is never deleted/)).toBeTruthy();
    await user.click(within(dialog).getByRole('button', { name: 'Retire the type' }));
    await waitFor(() => expect(sent.filter((s) => s.method === 'PUT')).toHaveLength(1));
    expect(sent.find((s) => s.method === 'PUT')).toEqual({ method: 'PUT', url: '/api/hrm/leave-types/t1', body: { isActive: false, rowVersion: 4 } });
    expect(sent.some((s) => s.method === 'DELETE')).toBe(false);
  });

  it('brings a retired type back', async () => {
    open();
    const user = userEvent.setup();
    await screen.findByText('Annual leave');
    await user.click(screen.getByRole('button', { name: /Show/ }));
    await user.click(await screen.findByRole('option', { name: 'Retired types' }));
    await user.click(await screen.findByRole('button', { name: 'Bring back leave type OLD' }));
    await waitFor(() => expect(sent.filter((s) => s.method === 'PUT')).toHaveLength(1));
    expect(sent.find((s) => s.method === 'PUT')).toEqual({ method: 'PUT', url: '/api/hrm/leave-types/t3', body: { isActive: true, rowVersion: 4 } });
  });
});
