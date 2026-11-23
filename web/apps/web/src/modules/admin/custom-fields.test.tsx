// Custom field definitions (slice S1.15b Build item 7; FR-KRN-MET-01, FR-KRN-MET-05, S1.15 rulings Q5 and Q8): the fields of a
// table with their type, rules and state, creating a field with its type's validation, a choice field over an option list, the
// file type waiting for the documents screens, editing without changing key or type, retire and restore (values kept), and the
// read-only and denied states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import CustomFieldsScreen, { validationOf } from './custom-fields';
import type { CustomDef } from '../../api/config';
import { json, shell, show } from './access-test-support';

const READ = ['cfg.custom_field_defs.read', 'cfg.option_lists.read'];
const WRITE = [...READ, 'cfg.custom_field_defs.create', 'cfg.custom_field_defs.update'];

const def = (over: Partial<CustomDef>): CustomDef => ({ id: 'd1', entityType: 'hrm.employments', fieldKey: 'badge_no', dataType: 'text', label: { en: 'Badge number', ur: 'بیج نمبر' }, help: null, validation: { maxLength: 12 },
  defaultValue: null, isRequired: false, isUnique: true, optionListId: null, section: null, seq: 10, status: 'active', version: 1, rowVersion: 4, ...over });
const blood = def({ id: 'd2', fieldKey: 'blood_group', dataType: 'choice', label: { en: 'Blood group', ur: 'بلڈ گروپ' }, isRequired: true, isUnique: false, optionListId: 'l1', validation: {}, seq: 20, section: 'Health' });
const old = def({ id: 'd3', fieldKey: 'old_tag', label: { en: 'Old tag' }, status: 'retired', isUnique: false, seq: 30 });

interface Server { sent: { method: string; url: string; body: unknown }[]; defs: CustomDef[]; answer?: (method: string, url: string, body: unknown) => Response | undefined }

function serve(s: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = '', queryText = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      const body = init?.body ? JSON.parse(String(init.body)) : null;
      s.sent.push({ method, url: path, body });
      return s.answer?.(method, path, body) ?? json(def({}), method === 'POST' && path === '/api/cfg/custom-fields' ? 201 : 200);
    }
    if (path === '/api/cfg/custom-field-entities') return json({ items: [{ entityType: 'hrm.employments', labelKey: 'entities.hrm.employments', companyScoped: true }, { entityType: 'org.org_units', labelKey: 'entities.org.org_units', companyScoped: true }] });
    if (path === '/api/cfg/custom-fields') return json({ items: new URLSearchParams(queryText).get('entityType') === 'hrm.employments' ? s.defs : [] });
    if (path === '/api/cfg/option-lists') return json({ items: [{ id: 'l1', code: 'blood_group', name: { en: 'Blood groups' }, isSystem: false, itemCount: 2, rowVersion: 1 }] });
    if (path === '/api/cfg/option-lists/l1') return json({ id: 'l1', code: 'blood_group', name: { en: 'Blood groups' }, isSystem: false, rowVersion: 1, items: [{ id: 'i1', code: 'a_pos', label: { en: 'A positive' }, seq: 10, isActive: true, rowVersion: 1 }] });
    return json({ title: 'errors.data.not_found' }, 404);
  }));
}

const open = (permissions = WRITE, defs = [def({}), blood, old]) => { const s: Server = { sent: [], defs }; serve(s); show('/settings/custom-fields', '/settings/custom-fields', <CustomFieldsScreen />, shell(permissions)); return s; };

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-KRN-MET-01 the custom field list', () => {
  it('lists the fields of the first table in order with type, rules and state', async () => {
    open();
    const table = await screen.findByRole('table', { name: 'Custom fields of Employments' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(3);
    expect(within(rows[0]!).getByText('badge_no')).toBeTruthy();
    expect(within(rows[0]!).getByText('Text')).toBeTruthy();
    expect(within(rows[0]!).getByText('Unique')).toBeTruthy();
    expect(within(rows[1]!).getByText('Choice from a list')).toBeTruthy();
    expect(within(rows[1]!).getByText('Required')).toBeTruthy();
    expect(within(rows[2]!).getByText('Retired')).toBeTruthy();
    expect(screen.getByText(/Fields cannot be deleted in this release/)).toBeTruthy();
  });

  it('says a table has no custom fields yet and offers the first', async () => {
    open(WRITE, []);
    expect(await screen.findByText('No custom fields on Employments')).toBeTruthy();
  });
});

describe('FR-KRN-MET-05 defining a field', () => {
  it('creates a required choice field over an option list with both languages and a section', async () => {
    const s = open(WRITE, []);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'New field' }));
    const drawer = await screen.findByRole('dialog', { name: 'New custom field of Employments' });
    await user.type(within(drawer).getByRole('textbox', { name: /^Key/ }), 'blood_group');
    await user.click(within(drawer).getByRole('button', { name: /Type/ }));
    await user.click(await screen.findByRole('option', { name: 'Choice from a list' }));
    await user.click(within(drawer).getByRole('button', { name: /Option list/ }));
    await user.click(await screen.findByRole('option', { name: /Blood groups/ }));
    await user.type(within(drawer).getByRole('textbox', { name: /^Name in English/ }), 'Blood group');
    await user.type(within(drawer).getByRole('textbox', { name: /^Name in Urdu/ }), 'بلڈ گروپ');
    await user.type(within(drawer).getByRole('textbox', { name: 'Section' }), 'Health');
    await user.click(within(drawer).getByRole('switch', { name: 'Required' }));
    await user.click(within(drawer).getByRole('button', { name: 'Create field' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ method: 'POST', url: '/api/cfg/custom-fields', body: {
      entityType: 'hrm.employments', fieldKey: 'blood_group', dataType: 'choice', optionListId: 'l1', label: { en: 'Blood group', ur: 'بلڈ گروپ' }, help: null, validation: {}, defaultValue: null,
      isRequired: true, isUnique: false, section: 'Health', seq: 10,
    } });
  });

  it('offers the validation that fits each type, and no unique switch where it makes no sense', async () => {
    open(WRITE, []);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'New field' }));
    const drawer = await screen.findByRole('dialog', { name: 'New custom field of Employments' });
    const choose = async (type: string) => { await user.click(within(drawer).getByRole('button', { name: /Type/ })); await user.click(await screen.findByRole('option', { name: type })); };
    expect(within(drawer).getByRole('textbox', { name: 'Shortest length' })).toBeTruthy();
    expect(within(drawer).getByRole('textbox', { name: 'Longest length' })).toBeTruthy();
    expect(within(drawer).getByRole('textbox', { name: /^Pattern/ })).toBeTruthy();
    expect(within(drawer).getByRole('switch', { name: /^Unique/ })).toBeTruthy();
    await choose('Whole number');
    expect(within(drawer).getByRole('textbox', { name: 'Smallest value' })).toBeTruthy();
    expect(within(drawer).queryByRole('textbox', { name: /^Pattern/ })).toBeNull();
    await choose('Date');
    expect(within(drawer).getByText('Earliest date')).toBeTruthy();
    expect(within(drawer).getByText('Latest date')).toBeTruthy();
    await choose('Several choices from a list');
    expect(within(drawer).getByRole('textbox', { name: 'Most items that may be chosen' })).toBeTruthy();
    expect(within(drawer).queryByRole('switch', { name: /^Unique/ })).toBeNull();
    await choose('Yes or no');
    expect(within(drawer).queryByRole('group', { name: 'Validation' })).toBeNull();
  });

  it('lists the file type but does not let it be chosen (it comes with the documents screens)', async () => {
    open(WRITE, []);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'New field' }));
    const drawer = await screen.findByRole('dialog', { name: 'New custom field of Employments' });
    await user.click(within(drawer).getByRole('button', { name: /Type/ }));
    const file = await screen.findByRole('option', { name: 'File (comes with the documents screens)' });
    expect(file.getAttribute('aria-disabled')).toBe('true');
  });

  it('checks the key, the name, the list and the rules before asking the server', async () => {
    const s = open(WRITE, []);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'New field' }));
    const drawer = await screen.findByRole('dialog', { name: 'New custom field of Employments' });
    await user.type(within(drawer).getByRole('textbox', { name: /^Key/ }), 'Bad Key');
    await user.type(within(drawer).getByRole('textbox', { name: 'Shortest length' }), '9');
    await user.type(within(drawer).getByRole('textbox', { name: 'Longest length' }), '3');
    await user.click(within(drawer).getByRole('button', { name: 'Create field' }));
    expect(await within(drawer).findByText(/Use lower-case letters, digits and underscore/)).toBeTruthy();
    expect(within(drawer).getByText('Fill in the English name.')).toBeTruthy();
    expect(within(drawer).getByText('The smallest value cannot be larger than the largest.')).toBeTruthy();
    expect(s.sent).toHaveLength(0);
  });

  it('builds the validation of a type from its text boxes', () => {
    expect(validationOf('text', { minLength: '2', maxLength: '12', pattern: '[A-Z]+', min: '5' })).toEqual({ ok: true, value: { minLength: 2, maxLength: 12, pattern: '[A-Z]+' } });
    expect(validationOf('decimal', { min: '0.50', max: '99.9' })).toEqual({ ok: true, value: { min: '0.50', max: '99.9' } });
    expect(validationOf('number', { min: '1.5' })).toEqual({ ok: false, field: 'min', problem: 'whole' });
    expect(validationOf('decimal', { max: 'x' })).toEqual({ ok: false, field: 'max', problem: 'decimal' });
    expect(validationOf('date', { minDate: '2026-01-01', maxDate: '2025-01-01' })).toEqual({ ok: false, field: 'minDate', problem: 'order' });
    expect(validationOf('bool', { min: '1' })).toEqual({ ok: true, value: {} });
  });

  it('shows the server\'s refusal beside the key (a key already used)', async () => {
    const s = open(WRITE, []);
    s.answer = () => json({ title: 'errors.data.invalid_request', errors: { fieldKey: ['errors.data.duplicate'] } }, 400);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'New field' }));
    const drawer = await screen.findByRole('dialog', { name: 'New custom field of Employments' });
    await user.type(within(drawer).getByRole('textbox', { name: /^Key/ }), 'badge_no');
    await user.type(within(drawer).getByRole('textbox', { name: /^Name in English/ }), 'Badge');
    await user.click(within(drawer).getByRole('button', { name: 'Create field' }));
    expect(await within(drawer).findByText('Check the fields marked below.')).toBeTruthy();
  });
});

describe('FR-KRN-MET-05 changing, retiring and restoring', () => {
  it('opens a field with its key, type and list fixed, and saves the changes with the row version', async () => {
    const s = open();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Edit the field Blood group' }));
    const drawer = await screen.findByRole('dialog', { name: 'Custom field Blood group' });
    expect((within(drawer).getByRole('textbox', { name: /^Key/ }) as HTMLInputElement).readOnly).toBe(true);
    expect(within(drawer).getByRole('button', { name: /Type/ }).hasAttribute('disabled')).toBe(true);
    expect(within(drawer).getByRole('button', { name: /Option list/ }).hasAttribute('disabled')).toBe(true);
    await user.click(within(drawer).getByRole('switch', { name: 'Required' }));
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ method: 'PUT', url: '/api/cfg/custom-fields/d2', body: { isRequired: false, rowVersion: 4, section: 'Health', seq: 20, label: { en: 'Blood group', ur: 'بلڈ گروپ' } } });
    expect(s.sent[0]!.body).not.toHaveProperty('fieldKey');
    expect(s.sent[0]!.body).not.toHaveProperty('dataType');
  });

  it('retires a field after a confirmation that says the values are kept, and restores a retired one', async () => {
    const s = open();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Retire the field Badge number' }));
    const confirm = await screen.findByRole('alertdialog', { name: 'Retire the field Badge number?' });
    expect(within(confirm).getByText(/The values already stored are kept/)).toBeTruthy();
    await user.click(within(confirm).getByRole('button', { name: 'Retire field' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ method: 'POST', url: '/api/cfg/custom-fields/d1/retire', body: { rowVersion: 4 } });
    await user.click(await screen.findByRole('button', { name: 'Restore the field Old tag' }));
    await waitFor(() => expect(s.sent).toHaveLength(2));
    expect(s.sent[1]).toMatchObject({ url: '/api/cfg/custom-fields/d3/restore', body: { rowVersion: 4 } });
  });

  it('shows a reader the fields without new, retire or restore', async () => {
    open(READ);
    await screen.findByRole('table', { name: 'Custom fields of Employments' });
    expect(screen.queryByRole('button', { name: 'New field' })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Retire the field/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Restore the field/ })).toBeNull();
    expect(screen.getByRole('button', { name: 'Open the field Badge number' })).toBeTruthy();
  });

  it('says so when the person may not see the custom fields', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ title: 'errors.access.forbidden' }, 403)));
    show('/settings/custom-fields', '/settings/custom-fields', <CustomFieldsScreen />, shell(READ));
    expect(await screen.findByText('You cannot see the custom fields')).toBeTruthy();
  });
});
