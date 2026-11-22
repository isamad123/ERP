// Custom fields on forms and lists (slice S1.15b Build item 8; FR-KRN-MET-02, FR-KRN-MET-05, FR-KRN-IAM-13): the form's custom
// section from the definitions the server answers (typed controls, required marker, read-only and masked fields, retired
// fields), the body it sends (editable keys only), field errors beside the field in English and Urdu, and the list's custom
// columns (hidden until chosen).
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import metadataJson from '@erp/generated/metadata/org.org_units.json';
import schemaJson from '@erp/generated/validation/org.org_units.json';
import { EngineProvider, type EngineApi, type Problem } from './data';
import { FormPage, type FormPageProps } from './FormPage';
import { ListPage } from './ListPage';
import { customBody, customProblems, shownCustom, type CustomFormField } from './custom';
import type { TableMetadata, ValidationSchema } from './metadata';

const metadata = metadataJson as TableMetadata;
const schema = schemaJson as ValidationSchema;
const COMPANY = '0190a1b2-0000-7000-8000-000000000001';

const base: Omit<CustomFormField, 'fieldKey' | 'dataType' | 'label'> = { help: null, validation: {}, defaultValue: null, isRequired: false, isUnique: false, section: null, seq: 10, retired: false, access: 'editable', options: [] };
const blood: CustomFormField = { ...base, fieldKey: 'blood_group', dataType: 'choice', label: { en: 'Blood group', ur: 'بلڈ گروپ' }, isRequired: true, section: 'Health', seq: 10,
  options: [{ code: 'a_pos', label: { en: 'A positive', ur: 'اے پازیٹو' } }, { code: 'b_pos', label: { en: 'B positive', ur: 'بی پازیٹو' } }] };
const badge: CustomFormField = { ...base, fieldKey: 'badge_no', dataType: 'text', label: { en: 'Badge number', ur: 'بیج نمبر' }, help: { en: 'Printed on the badge.' }, validation: { maxLength: 12 }, seq: 20 };
const remote: CustomFormField = { ...base, fieldKey: 'works_remote', dataType: 'bool', label: { en: 'Works remotely' }, seq: 30 };
const skills: CustomFormField = { ...base, fieldKey: 'skills', dataType: 'multi_choice', label: { en: 'Skills' }, seq: 40, options: [{ code: 'sql', label: { en: 'SQL' } }, { code: 'js', label: { en: 'JavaScript' } }] };
const secret: CustomFormField = { ...base, fieldKey: 'clearance', dataType: 'text', label: { en: 'Clearance' }, access: 'masked', seq: 50 };
const fixed: CustomFormField = { ...base, fieldKey: 'cost_code', dataType: 'text', label: { en: 'Cost code' }, access: 'readonly', seq: 60 };
const oldField: CustomFormField = { ...base, fieldKey: 'old_tag', dataType: 'text', label: { en: 'Old tag' }, retired: true, seq: 70 };
const oldEmpty: CustomFormField = { ...base, fieldKey: 'gone', dataType: 'text', label: { en: 'Gone field' }, retired: true, seq: 80 };
const definitions = [blood, badge, remote, skills, secret, fixed, oldField, oldEmpty];

const problem = (status: number, key: string, fieldErrors: Record<string, string[]> = {}): Problem => ({ status, key, detail: null, fieldErrors, reference: 'ref-1' });
const saved = { id: 'u1', companyId: COMPANY, code: 'HQ', nameI18n: { en: 'Head office' }, isActive: true, rowVersion: 3, updatedAt: '2026-11-20T10:00:00Z',
  custom: { blood_group: 'a_pos', clearance: '****1234', cost_code: 'C-7', old_tag: 'legacy' } };

function serve(extra: (method: string, path: string, body?: string) => unknown | undefined, items: CustomFormField[] | 'denied' = definitions): { send: ReturnType<typeof vi.fn<EngineApi['send']>> } {
  const send = vi.fn<EngineApi['send']>(async (method, path, body) => {
    if (path.startsWith('/api/kernel/custom-fields/')) {
      if (items === 'denied') throw problem(403, 'errors.access.forbidden');
      return JSON.stringify({ items });
    }
    const answer = extra(method, path, body);
    if (answer instanceof Error || (typeof answer === 'object' && answer !== null && 'fieldErrors' in answer && 'status' in answer)) throw answer;
    if (answer !== undefined) return JSON.stringify(answer);
    throw problem(404, 'errors.data.not_found');
  });
  return { send };
}

function renderForm(send: EngineApi['send'], props: Partial<FormPageProps> = {}, locale: 'en' | 'ur' = 'en') {
  const router = createMemoryRouter([{
    path: '/form',
    element: (
      <FormPage metadata={metadata} schema={schema} id={null} fields={['companyId', 'code', 'nameI18n', 'isActive']} defaults={{ companyId: COMPANY, isActive: true, nameI18n: {} }}
        companies={[{ id: COMPANY, label: 'Acme Pakistan' }]} canCreate canUpdate canDelete={false} canHistory={false} recordTitle={(r) => String(r.code ?? '')}
        onSaved={vi.fn()} onDeleted={vi.fn()} onCancel={vi.fn()} {...props} />
    ),
  }], { initialEntries: ['/form'] });
  render(
    <I18nRoot locale={locale}>
      <ToastProvider>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <EngineProvider api={{ send }}><RouterProvider router={router} /></EngineProvider>
        </QueryClientProvider>
      </ToastProvider>
    </I18nRoot>,
  );
}

afterEach(() => cleanup());

describe('FR-KRN-MET-02 the custom section of a form', () => {
  it('shows typed controls for the active fields, grouped by section, with the required marker and help', async () => {
    renderForm(serve(() => undefined).send);
    const section = await screen.findByRole('region', { name: 'Additional fields' });
    const health = within(section).getByRole('group', { name: 'Health' });
    expect(within(health).getByRole('button', { name: /Blood group/ })).toBeTruthy(); // a choice: the select button
    expect(within(health).getByText('*')).toBeTruthy(); // required marker
    expect(within(section).getByRole('textbox', { name: /^Badge number/ })).toBeTruthy();
    expect(within(section).getByText('Printed on the badge.')).toBeTruthy();
    expect(within(section).getByRole('switch', { name: 'Works remotely' })).toBeTruthy();
    expect(within(section).getByRole('group', { name: /^Skills/ })).toBeTruthy();
    expect(within(section).getByRole('checkbox', { name: 'JavaScript' })).toBeTruthy();
  });

  it('keeps retired fields without a value out, and shows retired fields with a value, masked and read-only fields as text', async () => {
    renderForm(serve((method, path) => (method === 'GET' && path === '/api/org/org-units/u1' ? saved : undefined)).send, { id: 'u1' });
    const section = await screen.findByRole('region', { name: 'Additional fields' });
    expect(within(section).queryByText(/Gone field/)).toBeNull();
    const retired = within(section).getByRole('group', { name: 'Old tag' });
    expect(within(retired).getByText('legacy')).toBeTruthy();
    expect(within(retired).getByText(/retired/)).toBeTruthy();
    expect(within(within(section).getByRole('group', { name: 'Clearance' })).getByText('****1234')).toBeTruthy();
    expect(within(within(section).getByRole('group', { name: 'Cost code' })).getByText('C-7')).toBeTruthy();
    expect(within(section).queryByRole('textbox', { name: /Clearance|Cost code|Old tag/ })).toBeNull();
  });

  it('refuses a missing required custom field before asking the server, with the text beside the field (FR-KRN-MET-02)', async () => {
    const { send } = serve(() => undefined);
    renderForm(send);
    await screen.findByRole('region', { name: 'Additional fields' });
    fireEvent.change(screen.getByRole('textbox', { name: /^Code/ }), { target: { value: 'HQ' } });
    fireEvent.change(screen.getByRole('textbox', { name: /^In English/ }), { target: { value: 'Head office' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));
    expect(await screen.findByText('Check the fields marked below.')).toBeTruthy();
    const field = document.getElementById('field-custom.blood_group')!;
    expect(within(field).getByText('Fill in this field.')).toBeTruthy();
    expect(send.mock.calls.filter(([m]) => m === 'POST')).toHaveLength(0);
  });

  it('sends custom with the editable keys only: the choice made, the text typed, never the masked, read-only or retired values', async () => {
    const user = userEvent.setup();
    const { send } = serve((method, path) => {
      if (method === 'GET' && path === '/api/org/org-units/u1') return saved;
      if (method === 'PUT') return { ...saved, rowVersion: 4 };
      return undefined;
    });
    renderForm(send, { id: 'u1' });
    await screen.findByRole('region', { name: 'Additional fields' });
    await user.click(screen.getByRole('button', { name: /Blood group/ }));
    await user.click(await screen.findByRole('option', { name: 'B positive' }));
    await user.type(screen.getByRole('textbox', { name: /^Badge number/ }), 'B-100');
    await user.click(screen.getByRole('checkbox', { name: 'SQL' }));
    await user.click(screen.getByRole('switch', { name: 'Works remotely' }));
    await user.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(send.mock.calls.some(([m]) => m === 'PUT')).toBe(true));
    const [, path, body] = send.mock.calls.find(([m]) => m === 'PUT')!;
    expect(path).toBe('/api/org/org-units/u1');
    expect(JSON.parse(body!).custom).toEqual({ blood_group: 'b_pos', badge_no: 'B-100', skills: ['sql'], works_remote: true });
  });

  it('shows the server\'s custom field error next to the field in English and in Urdu (FR-KRN-MET-02)', async () => {
    const refuse = (method: string) => (method === 'POST' ? problem(400, 'errors.data.invalid_request', { 'custom.badge_no': ['errors.custom.duplicate'] }) : undefined);
    for (const [locale, text] of [['en', 'Another record already has this value. Choose another one.'], ['ur', 'کسی اور ریکارڈ میں یہ قدر پہلے سے ہے۔ کوئی اور منتخب کریں۔']] as const) {
      cleanup();
      const { send } = serve((method) => { const a = refuse(method); return a; });
      renderForm(send, { defaults: { companyId: COMPANY, isActive: true, nameI18n: { en: 'HQ' }, code: 'HQ' } }, locale);
      const user = userEvent.setup();
      await screen.findByRole('region', { name: locale === 'en' ? 'Additional fields' : 'اضافی خانے' });
      await user.click(screen.getByRole('button', { name: new RegExp(locale === 'en' ? 'Blood group' : 'بلڈ گروپ') }));
      await user.click(await screen.findByRole('option', { name: locale === 'en' ? 'A positive' : 'اے پازیٹو' }));
      await user.type(screen.getByRole('textbox', { name: new RegExp(`^${locale === 'en' ? 'Badge number' : 'بیج نمبر'}`) }), 'B-1');
      fireEvent.click(document.querySelector('button[type="submit"]')!);
      await waitFor(() => expect(send.mock.calls.some(([m]) => m === 'POST')).toBe(true));
      const field = document.getElementById('field-custom.badge_no')!;
      expect(await within(field).findByText(text)).toBeTruthy();
    }
  });

  it('shows no custom section when the person may not read the definitions (the form still works)', async () => {
    const { send } = serve(() => undefined, 'denied');
    renderForm(send);
    expect(await screen.findByRole('textbox', { name: /^Code/ })).toBeTruthy();
    await waitFor(() => expect(send).toHaveBeenCalled());
    expect(screen.queryByRole('region', { name: 'Additional fields' })).toBeNull();
  });
});

describe('FR-KRN-MET-02 helpers', () => {
  it('sends only editable active fields that hold a value and trims text', () => {
    expect(customBody(definitions, { blood_group: 'a_pos', badge_no: '  X1 ', works_remote: false, skills: [], clearance: '****1', old_tag: 'z' })).toEqual({ blood_group: 'a_pos', badge_no: 'X1', works_remote: false });
  });
  it('lists required editable fields without a value and ignores read-only ones', () => {
    expect(customProblems(definitions, {})).toEqual({ blood_group: 'errors.custom.required' });
    expect(customProblems([{ ...fixed, isRequired: true }], {})).toEqual({});
  });
  it('shows a retired field only while a value is stored', () => {
    expect(shownCustom(definitions, { old_tag: 'x' }).map((f) => f.fieldKey)).toEqual(['blood_group', 'badge_no', 'works_remote', 'skills', 'clearance', 'cost_code', 'old_tag']);
  });
});

describe('FR-KRN-MET-05 custom columns of a list', () => {
  it('offers a custom field as a column that is hidden until the person chooses it', async () => {
    const user = userEvent.setup();
    const rows = [{ id: 'u1', companyId: COMPANY, code: 'HQ', nameI18n: { en: 'Head office' }, isActive: true, rowVersion: 1, custom: { blood_group: 'a_pos', badge_no: 'B-9' } }];
    const { send } = serve((method, path) => (method === 'GET' && path.startsWith('/api/org/org-units') && !path.includes('saved-views') ? { items: rows, next: null } : (path.startsWith('/api/cfg/saved-views') ? [] : undefined)));
    const router = createMemoryRouter([{ path: '/', element: <ListPage metadata={metadata} columns={['code', 'nameI18n']} canCreate={false} canShare={false} onOpen={vi.fn()} rowLabel={(r) => String(r.code)} /> }]);
    render(
      <I18nRoot locale="en"><ToastProvider><QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <EngineProvider api={{ send }}><RouterProvider router={router} /></EngineProvider>
      </QueryClientProvider></ToastProvider></I18nRoot>,
    );
    expect(await screen.findByText('Head office')).toBeTruthy();
    expect(screen.queryByRole('columnheader', { name: /Blood group/ })).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Columns' }));
    await user.click(await screen.findByRole('checkbox', { name: 'Blood group' }));
    await user.click(screen.getByRole('button', { name: 'Done' }));
    expect(await screen.findByRole('columnheader', { name: /Blood group/ })).toBeTruthy();
    expect(screen.getAllByText('A positive').length).toBeGreaterThan(0);
  });
});
