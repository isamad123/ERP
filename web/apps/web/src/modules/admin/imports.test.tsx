// The import centre and the template designer (slice S1.16a Build items 4 and 5; FR-ADM-DAT-01, FR-ADM-INT-04, FR-KRN-INT-03, FR-KRN-INT-04):
// the upload sends the file and the template, nothing can be committed before a validation run found it good, problems are shown
// with an error file, a running batch is watched until it stops, a refused rollback is said in plain words, and the designer sends
// the mapping exactly as the contract says.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import type { ImportBatch, ImportTemplate, ImportType } from '../../api/imports';
import ImportsScreen from './imports';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';
const ADMIN = ['itg.imports.manage', 'itg.imports.design'];

function shell(permissions: string[] = ADMIN, writable = true): ShellValue {
  const grants = permissions.map((code) => ({ code, companies: null }));
  return {
    me: { userId: 'u-me', displayName: 'Me', language: 'en', timeZone: 'Asia/Karachi', defaultCompanyId: COMPANY, companies: [{ id: COMPANY, code: 'C1', name: { en: 'First company' } }],
      permissions: grants, mfa: { required: false, methods: [], enrolBy: null }, numbers: { digits: 'western', grouping: 'international' } },
    capabilities: { modules: [], features: [], flags: [], state: 'active', nextState: null, nextStateOn: null, warnings: [] },
    facts: accessFacts([], grants, 'active'),
    can: (code: string) => permissions.includes(code),
    writable,
    activeCompany: null,
    setActiveCompany: () => undefined,
  };
}



const TYPE: ImportType = {
  code: 'org.locations', module: 'kernel', name: { en: 'Locations' }, permission: 'org.locations.create', canUpdate: true, canRun: true,
  columns: [
    { name: 'Code', field: 'code', type: 'text', required: true, label: { en: 'Code' }, lookup: null, options: null, example: 'LHR-01' },
    { name: 'Name', field: 'name', type: 'text', required: true, label: { en: 'Name' }, lookup: null, options: null, example: 'Lahore office' },
    { name: 'Opened', field: 'openedOn', type: 'date', required: false, label: { en: 'Opened on' }, lookup: null, options: null, example: '2026-01-01' },
  ],
};
const TEMPLATE: ImportTemplate = {
  id: 't1', code: 'locations_standard', name: { en: 'Standard locations' }, entityType: 'org.locations', format: 'csv', mode: 'create', isDefault: true, version: 2, isActive: true, rowVersion: 5,
  mapping: { delimiter: null, sheet: null, headerRow: 1, columns: [
    { field: 'code', source: 'Code', transform: ['trim', 'upper'], default: null },
    { field: 'name', source: 'Name', transform: ['trim'], default: null },
    { field: 'openedOn', source: 'Opened on', transform: ['date:dd/MM/yyyy'], default: null },
  ] },
};
const batch = (extra: Partial<ImportBatch> = {}): ImportBatch => ({
  id: 'b1', templateId: 't1', templateName: { en: 'Standard locations' }, entityType: 'org.locations', fileName: 'locations.csv', status: 'validated', mode: 'create',
  totalRows: 50, okRows: 48, errorRows: 2, createdRows: null, updatedRows: null, progressPct: null, jobRunId: null, createdAt: '2026-11-23T08:00:00Z', createdBy: 'u1',
  validatedAt: '2026-11-23T08:01:00Z', committedAt: null, rolledBackAt: null, canCommit: false, canRollback: false, rollbackBlockedKey: null, ...extra,
});

interface Sent { method: string; path: string; body: unknown; form: Record<string, unknown> | null }
const sent: Sent[] = [];
const calls: string[] = [];

function serve(handler: (path: string, method: string, query: URLSearchParams) => Response | undefined) {
  sent.length = 0;
  calls.length = 0;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = '', q = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    calls.push(`${method} ${url}`);
    const form = init?.body instanceof FormData ? Object.fromEntries(init.body.entries()) : null;
    if (method !== 'GET') sent.push({ method, path, body: typeof init?.body === 'string' ? JSON.parse(init.body) : null, form });
    const answer = handler(path, method, new URLSearchParams(q));
    if (answer) return answer;
    if (path === '/api/itg/imports/types') return json([TYPE]);
    if (path === '/api/itg/imports/templates') return json([TEMPLATE]);
    if (path === '/api/itg/imports/batches') return json({ items: [batch()], next: null });
    return json({ status: 404, title: 'errors.data.not_found' }, 404);
  }));
}

function show(path: string, value = shell(ADMIN)) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <I18nRoot locale="en">
        <ToastProvider>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <ShellValueProvider value={value}>
              <Routes><Route path="/admin/imports/*" element={<ImportsScreen />} /></Routes>
            </ShellValueProvider>
          </QueryClientProvider>
        </ToastProvider>
      </I18nRoot>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('FR-ADM-DAT-01 import centre', () => {
  it('lists the batches and uploads the chosen file with its template, then opens the batch', async () => {
    serve((path, method) => (path === '/api/itg/imports/batches' && method === 'POST' ? json(batch({ id: 'b9', status: 'uploaded', totalRows: null, okRows: null, errorRows: null }), 201)
      : path === '/api/itg/imports/batches/b9' ? json(batch({ id: 'b9', status: 'uploaded', totalRows: null, okRows: null, errorRows: null })) : undefined));
    show('/admin/imports');
    expect(await screen.findByText('locations.csv')).toBeTruthy();
    expect(screen.getAllByText('Checked').length).toBeGreaterThan(0);
    const file = new File(['Code,Name\nA,B'], 'new-locations.csv', { type: 'text/csv' });
    await userEvent.upload(await screen.findByLabelText('File to import'), file);
    await userEvent.click(screen.getByRole('button', { name: 'Upload and continue' }));
    await waitFor(() => expect(sent.some((s) => s.path === '/api/itg/imports/batches' && s.form?.templateId === 't1')).toBe(true));
    const upload = sent.find((s) => s.path === '/api/itg/imports/batches');
    expect((upload?.form?.file as File).name).toBe('new-locations.csv');
    expect(await screen.findByRole('heading', { name: 'locations.csv', level: 1 })).toBeTruthy();
  });

  it('says when the person may not import this type and offers no upload', async () => {
    serve((path) => (path === '/api/itg/imports/types' ? json([{ ...TYPE, canRun: false }]) : undefined));
    show('/admin/imports');
    expect(await screen.findByText('Your role does not allow importing this type. Ask an administrator for the permission.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Upload and continue' })).toBeNull();
  });

  it('shows a plain permission-denied message when the server refuses', async () => {
    serve((path) => (path === '/api/itg/imports/types' ? json({ status: 403, title: 'errors.access.forbidden' }, 403) : undefined));
    show('/admin/imports');
    expect(await screen.findByText('You do not have permission to use the import centre.')).toBeTruthy();
  });

  it('shows no screen at all without the permission to use the centre', async () => {
    serve(() => undefined);
    show('/admin/imports', shell([]));
    expect(await screen.findByText('You do not have permission to use the import centre.')).toBeTruthy();
    expect(calls).toEqual([]);
  });
});

describe('FR-KRN-INT-03 validation summary, problems and commit', () => {
  it('shows the summary, the first problems and the error file link, and keeps commit off while rows have problems', async () => {
    URL.createObjectURL = vi.fn(() => 'blob:x');
    URL.revokeObjectURL = vi.fn();
    serve((path) => (path === '/api/itg/imports/batches/b1' ? json(batch())
      : path === '/api/itg/imports/batches/b1/errors' ? json({ items: [{ rowNo: 7, field: 'code', message: 'Duplicate code', phase: 'validate' }, { rowNo: 9, field: null, message: 'Row is empty', phase: 'validate' }], next: null })
        : path === '/api/itg/imports/batches/b1/errors.csv' ? new Response('row,message', { status: 200 }) : undefined));
    show('/admin/imports/batches/b1');
    expect(await screen.findByText('Duplicate code')).toBeTruthy();
    expect(screen.getByText('Row is empty')).toBeTruthy();
    expect(screen.getByText('2 rows have problems')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Save the records' }) as HTMLButtonElement).disabled).toBe(true);
    await userEvent.click(screen.getByRole('button', { name: 'Download the error file' }));
    await waitFor(() => expect(calls.some((c) => c.endsWith('/errors.csv'))).toBe(true));
  });

  it('keeps commit off until the file is checked, then commits only after a good validation', async () => {
    serve((path, method) => {
      if (path === '/api/itg/imports/batches/b1/commit' && method === 'POST') return json(batch({ status: 'committing', progressPct: 0, canCommit: false }), 202);
      if (path === '/api/itg/imports/batches/b1') return json(batch({ errorRows: 0, okRows: 50, canCommit: true }));
      return undefined;
    });
    show('/admin/imports/batches/b1');
    expect(await screen.findByText('The file is ready')).toBeTruthy();
    const commit = screen.getByRole('button', { name: 'Save the records' }) as HTMLButtonElement;
    expect(commit.disabled).toBe(false);
    await userEvent.click(commit);
    await waitFor(() => expect(sent.some((s) => s.path === '/api/itg/imports/batches/b1/commit')).toBe(true));
    expect(await screen.findByText('Saving the records: 0%')).toBeTruthy();
  });

  it('does not offer commit for an uploaded file that was never checked', async () => {
    serve((path) => (path === '/api/itg/imports/batches/b1' ? json(batch({ status: 'uploaded', totalRows: null, okRows: null, errorRows: null })) : undefined));
    show('/admin/imports/batches/b1');
    expect(await screen.findByRole('button', { name: 'Check the file' })).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Save the records' }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('FR-KRN-INT-04 progress of a background import', () => {
  it('watches a running batch and stops asking once it is saved', async () => {
    let reads = 0;
    serve((path) => {
      if (path !== '/api/itg/imports/batches/b1') return undefined;
      reads += 1;
      return json(reads < 2 ? batch({ status: 'committing', progressPct: 40, errorRows: 0 }) : batch({ status: 'committed', progressPct: 100, errorRows: 0, createdRows: 50, committedAt: '2026-11-23T08:05:00Z' }));
    });
    show('/admin/imports/batches/b1');
    expect(await screen.findByText('Saving the records: 40%')).toBeTruthy();
    await waitFor(() => expect(screen.getByText('Saved', { selector: 'span span' })).toBeTruthy(), { timeout: 6000 });
    const seen = reads;
    await new Promise((resolve) => setTimeout(resolve, 2600));
    expect(reads).toBe(seen);
  }, 15000);
});

describe('FR-KRN-INT-03 rollback', () => {
  it('rolls back a saved import with a reason', async () => {
    serve((path, method) => {
      if (path === '/api/itg/imports/batches/b1/rollback' && method === 'POST') return json(batch({ status: 'rolled_back', rolledBackAt: '2026-11-23T09:00:00Z' }));
      if (path === '/api/itg/imports/batches/b1') return json(batch({ status: 'committed', errorRows: 0, createdRows: 50, canRollback: true }));
      return undefined;
    });
    show('/admin/imports/batches/b1');
    await userEvent.click(await screen.findByRole('button', { name: 'Roll back' }));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.type(within(dialog).getByLabelText(/Reason/), 'Wrong file');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Roll back' }));
    await waitFor(() => expect(sent.find((s) => s.path.endsWith('/rollback'))?.body).toEqual({ reason: 'Wrong file' }));
  });

  it('says in plain words why a rollback was refused', async () => {
    serve((path, method) => {
      if (path === '/api/itg/imports/batches/b1/rollback' && method === 'POST') return json({ status: 409, title: 'errors.import.rollback_changed' }, 409);
      if (path === '/api/itg/imports/batches/b1') return json(batch({ status: 'committed', errorRows: 0, createdRows: 50, canRollback: true }));
      return undefined;
    });
    show('/admin/imports/batches/b1');
    await userEvent.click(await screen.findByRole('button', { name: 'Roll back' }));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.type(within(dialog).getByLabelText(/Reason/), 'Mistake');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Roll back' }));
    expect(await screen.findByText('This import cannot be rolled back because some of its records have changed since.')).toBeTruthy();
  });

  it('explains a rollback that is not available from the batch itself', async () => {
    serve((path) => (path === '/api/itg/imports/batches/b1' ? json(batch({ status: 'committed', errorRows: 0, canRollback: false, rollbackBlockedKey: 'errors.import.rollback_updated' })) : undefined));
    show('/admin/imports/batches/b1');
    expect(await screen.findByText('This import cannot be rolled back because it updated existing records.')).toBeTruthy();
  });

  it('says in plain words that a file was refused at upload', async () => {
    serve((path, method) => (path === '/api/itg/imports/batches' && method === 'POST' ? json({ status: 400, title: 'errors.import.infected' }, 400) : undefined));
    show('/admin/imports');
    await userEvent.upload(await screen.findByLabelText('File to import'), new File(['x'], 'bad.csv', { type: 'text/csv' }));
    await userEvent.click(screen.getByRole('button', { name: 'Upload and continue' }));
    expect(await screen.findByText('The file was refused because the virus scan found a problem in it.')).toBeTruthy();
  });
});

describe('FR-ADM-INT-04 template designer', () => {
  it('creates a template and sends the mapping exactly as the contract says', async () => {
    serve((path, method) => (path === '/api/itg/imports/templates' && method === 'POST' ? json({ ...TEMPLATE, id: 't2', code: 'mine', rowVersion: 1 }, 201) : undefined));
    show('/admin/imports/templates');
    await userEvent.click(await screen.findByRole('button', { name: 'New template' }));
    await userEvent.type(await screen.findByLabelText(/^Code/), 'mine');
    await userEvent.type(screen.getByLabelText(/^Name in English/), 'My locations');
    // The first column: tick "Trim spaces" and "Read as a date" is for the third; set a default on the second.
    const groups = screen.getAllByRole('group', { name: /Clean-up of/ });
    await userEvent.click(within(groups[0] as HTMLElement).getByRole('checkbox', { name: 'Trim spaces' }));
    await userEvent.click(within(groups[0] as HTMLElement).getByRole('checkbox', { name: 'Capitals' }));
    await userEvent.click(within(groups[2] as HTMLElement).getByRole('checkbox', { name: 'Read as a date' }));
    const dateFormat = await screen.findByLabelText('Date format');
    await userEvent.clear(dateFormat);
    await userEvent.type(dateFormat, 'dd/MM/yyyy');
    await userEvent.click(screen.getByRole('button', { name: 'Create the template' }));
    await waitFor(() => expect(sent.some((s) => s.path === '/api/itg/imports/templates' && s.method === 'POST')).toBe(true));
    expect(sent.find((s) => s.method === 'POST')?.body).toEqual({
      code: 'mine', name: { en: 'My locations' }, entityType: 'org.locations', format: 'csv', mode: 'create',
      mapping: { delimiter: null, sheet: null, headerRow: 1, columns: [
        { field: 'code', source: 'Code', transform: ['trim', 'upper'], default: null },
        { field: 'name', source: 'Name', transform: [], default: null },
        { field: 'openedOn', source: 'Opened on', transform: ['date:dd/MM/yyyy'], default: null },
      ] },
    });
  });

  it('saves a change as a new version with the row version it read, and tests with a sample file', async () => {
    serve((path, method) => {
      if (path === '/api/itg/imports/templates/t1' && method === 'PUT') return json({ ...TEMPLATE, version: 3, rowVersion: 6 });
      if (path === '/api/itg/imports/templates/t1/test') return json({ totalRows: 20, errorRows: 1, errors: [{ rowNo: 4, field: 'code', message: 'Code is required' }], sample: [{ rowNo: 2, values: { code: 'LHR-01', name: 'Lahore office' } }] });
      return undefined;
    });
    show('/admin/imports/templates');
    await userEvent.click(await screen.findByRole('button', { name: 'Open template Standard locations' }));
    expect(await screen.findByRole('heading', { name: 'Template Standard locations' })).toBeTruthy();
    const source = screen.getAllByLabelText(/^Column heading in the file/)[1] as HTMLInputElement;
    await userEvent.clear(source);
    await userEvent.type(source, 'Site name');
    await userEvent.click(screen.getByRole('button', { name: 'Save as a new version' }));
    await waitFor(() => expect(sent.some((s) => s.method === 'PUT')).toBe(true));
    const put = sent.find((s) => s.method === 'PUT')?.body as { rowVersion: number; mode: string; mapping: { columns: { field: string; source: string }[] } };
    expect(put.rowVersion).toBe(5);
    expect(put.mode).toBe('create');
    expect(put.mapping.columns.find((c) => c.field === 'name')?.source).toBe('Site name');

    await userEvent.upload(await screen.findByLabelText('Sample file'), new File(['Code\nA'], 'sample.csv', { type: 'text/csv' }));
    await userEvent.click(screen.getByRole('button', { name: 'Test the template' }));
    expect(await screen.findByText('20 rows read, 1 with problems')).toBeTruthy();
    expect(screen.getByText('Code is required')).toBeTruthy();
    expect(screen.getByText('Lahore office')).toBeTruthy();
  });

  it('keeps people without the design permission out of the designer', async () => {
    serve(() => undefined);
    show('/admin/imports/templates', shell(['itg.imports.manage']));
    expect(await screen.findByText('You do not have permission to design import templates.')).toBeTruthy();
  });

  it('shows a plain permission-denied message when the server refuses the templates', async () => {
    serve((path) => (path === '/api/itg/imports/templates' ? json({ status: 403, title: 'errors.access.forbidden' }, 403) : undefined));
    show('/admin/imports/templates');
    expect(await screen.findByText('You do not have permission to use the import centre.')).toBeTruthy();
  });
});
