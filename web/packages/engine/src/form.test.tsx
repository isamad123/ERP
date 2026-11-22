// The form engine on org.branches (slice S1.12c Build item 18; FR-ADM-UXA-02 to -05, FR-KRN-IAM-13, FR-KRN-ORG-02):
// checks on save and on leaving a field, server errors next to the field, the database rule as a clear message, the
// stale-version comparison, read-only and hidden fields, and the warning before leaving with unsaved changes.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import metadataJson from '@erp/generated/metadata/org.branches.json';
import schemaJson from '@erp/generated/validation/org.branches.json';
import { EngineProvider, type EngineApi, type Problem } from './data';
import { FormPage, type FormPageProps } from './FormPage';
import type { TableMetadata, ValidationSchema } from './metadata';

const metadata = metadataJson as TableMetadata;
const schema = schemaJson as ValidationSchema;
const COMPANY = '0190a1b2-0000-7000-8000-000000000001';
const saved = { id: 'b1', companyId: COMPANY, code: 'KHI', nameI18n: { en: 'Karachi', ur: 'کراچی' }, isActive: true, rowVersion: 2, updatedAt: '2026-11-20T10:00:00Z' };
const problem = (status: number, key: string, detail: string | null = null, fieldErrors: Record<string, string[]> = {}): Problem => ({ status, key, detail, fieldErrors, reference: 'ref-9' });

function setup(send: EngineApi['send'], props: Partial<FormPageProps> = {}) {
  const router = createMemoryRouter([
    {
      path: '/form',
      element: (
        <FormPage metadata={metadata} schema={schema} id={null} fields={['companyId', 'code', 'nameI18n', 'isActive']} defaults={{ companyId: COMPANY, isActive: true, nameI18n: {} }}
          companies={[{ id: COMPANY, label: 'Acme Pakistan' }]} canCreate canUpdate canDelete canHistory={false}
          recordTitle={(r) => String(r.code ?? '')} ruleTexts={{ ck_branches_one_per_company: 'A company must keep at least one branch.' }}
          onSaved={vi.fn()} onDeleted={vi.fn()} onCancel={vi.fn()} {...props} />
      ),
    },
    { path: '/elsewhere', element: <p>elsewhere</p> },
  ], { initialEntries: ['/form'] });
  render(
    <I18nRoot locale="en">
      <ToastProvider>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <EngineProvider api={{ send }}><RouterProvider router={router} /></EngineProvider>
        </QueryClientProvider>
      </ToastProvider>
    </I18nRoot>,
  );
  return router;
}

const field = (label: RegExp) => screen.getByRole('textbox', { name: label });

afterEach(() => cleanup());

describe('FR-ADM-UXA-02 the branch form', () => {
  it('checks required fields on save and a code that is too long when the field is left, before asking the server', async () => {
    const send = vi.fn<EngineApi['send']>();
    setup(send);
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));
    expect(await screen.findByText('Check the fields marked below.')).toBeTruthy();
    expect(screen.getAllByText('Fill in this field.').length).toBeGreaterThanOrEqual(2); // code and name
    expect(send).not.toHaveBeenCalled();

    fireEvent.change(field(/^Code/), { target: { value: 'K'.repeat(41) } });
    fireEvent.blur(field(/^Code/));
    expect(await screen.findAllByText('Use at most 40 characters.')).toBeTruthy();
  });

  it('creates with trimmed values and shows a duplicate code next to the code field (FR-ADM-UXA-04)', async () => {
    const send = vi.fn<EngineApi['send']>(async () => { throw problem(409, 'errors.data.duplicate', 'uq_branches_company_id_code'); });
    setup(send);
    fireEvent.change(field(/^Code/), { target: { value: 'KHI ' } });
    fireEvent.change(field(/^In English/), { target: { value: ' Karachi ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));
    await waitFor(() => expect(send).toHaveBeenCalled());
    const [method, path, body] = send.mock.calls[0]!;
    expect([method, path]).toEqual(['POST', '/api/org/branches']);
    expect(JSON.parse(body!)).toEqual({ companyId: COMPANY, code: 'KHI', nameI18n: { en: 'Karachi' }, isActive: true });
    expect(await screen.findAllByText('This is already used by another record. Choose another one.')).toBeTruthy();
    expect(screen.getByText('Reference: ref-9')).toBeTruthy();
  });
});

describe('FR-KRN-ORG-02 and FR-ADM-UXA-03 editing a branch', () => {
  it('says the rule plainly when the last branch of a company cannot be deleted', async () => {
    const send = vi.fn<EngineApi['send']>(async (method) => {
      if (method === 'GET') return JSON.stringify(saved);
      throw problem(400, 'errors.data.rule_violated', 'ck_branches_one_per_company');
    });
    setup(send, { id: 'b1' });
    expect(await screen.findByText(/Last changed/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    fireEvent.click(await screen.findByRole('button', { name: /^Delete branch$/i }));
    expect(await screen.findByText('A company must keep at least one branch.')).toBeTruthy();
    expect(send).toHaveBeenCalledWith('DELETE', '/api/org/branches/b1?rowVersion=2');
  });

  it('shows the saved version next to the person\'s when someone saved first, and keeps theirs on request', async () => {
    const newer = { ...saved, nameI18n: { en: 'Karachi Head Office' }, rowVersion: 3 };
    let gets = 0;
    const send = vi.fn<EngineApi['send']>(async (method, _path, body) => {
      if (method === 'GET') return JSON.stringify(gets++ === 0 ? saved : newer);
      const sent = JSON.parse(body!) as { rowVersion: number };
      if (sent.rowVersion === 2) throw problem(409, 'errors.data.concurrency_conflict');
      return JSON.stringify({ ...newer, nameI18n: { en: 'Karachi Main' }, rowVersion: 4 });
    });
    const onSaved = vi.fn();
    setup(send, { id: 'b1', onSaved });
    fireEvent.change(await screen.findByRole('textbox', { name: /^In English/ }), { target: { value: 'Karachi Main' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByRole('alertdialog', { name: 'Someone else saved this record' })).toBeTruthy();
    expect(screen.getByText('Karachi Head Office')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Keep my changes' }));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    const puts = send.mock.calls.filter(([method]) => method === 'PUT');
    expect(puts).toHaveLength(2);
    expect(JSON.parse(puts[1]![2]!)).toMatchObject({ rowVersion: 3, nameI18n: { en: 'Karachi Main', ur: 'کراچی' } });
  });

  it('is read-only without the update permission and shows a field the server left out as hidden (FR-KRN-IAM-13)', async () => {
    const withoutCode = Object.fromEntries(Object.entries(saved).filter(([key]) => key !== 'code'));
    const send = vi.fn<EngineApi['send']>(async () => JSON.stringify(withoutCode));
    setup(send, { id: 'b1', canUpdate: false, canDelete: false });
    expect(await screen.findByText('You can read this record')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Save changes' })).toBeNull();
    expect(screen.getByRole('group', { name: 'Code' }).textContent).toContain('Hidden');
  });

  it('leaves without asking once the changes are saved (FR-ADM-UXA-05)', async () => {
    const routerRef: { current: ReturnType<typeof setup> | null } = { current: null };
    const send = vi.fn<EngineApi['send']>(async (method) => JSON.stringify(method === 'GET' ? saved : { ...saved, code: 'KHI-2', rowVersion: 3 }));
    routerRef.current = setup(send, { id: 'b1', onSaved: () => { void routerRef.current?.navigate('/elsewhere'); } });
    fireEvent.change(await screen.findByRole('textbox', { name: /^Code/ }), { target: { value: 'KHI-2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByText('elsewhere')).toBeTruthy();
    expect(screen.queryByRole('alertdialog', { name: 'Leave without saving?' })).toBeNull();
  });

  it('asks before leaving with unsaved changes (FR-ADM-UXA-05)', async () => {
    const router = setup(vi.fn<EngineApi['send']>(async () => JSON.stringify(saved)), { id: 'b1' });
    fireEvent.change(await screen.findByRole('textbox', { name: /^Code/ }), { target: { value: 'KHI-2' } });
    await act(async () => { await router.navigate('/elsewhere'); });
    expect(await screen.findByRole('alertdialog', { name: 'Leave without saving?' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Stay on this page' }));
    expect(router.state.location.pathname).toBe('/form');
    await act(async () => { await router.navigate('/elsewhere'); });
    fireEvent.click(await screen.findByRole('button', { name: 'Leave without saving' }));
    expect(await screen.findByText('elsewhere')).toBeTruthy();
  });
});
