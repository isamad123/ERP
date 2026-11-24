// The export centre and the export dialog (slice S1.16b; FR-ADM-DAT-04, FR-ADM-NAV-02, FR-KRN-ENT-04): the centre lists one's own exports with plain
// statuses and a download only for a finished export of one's own, a held export says it waits for a second person, and the dialog asks the server what
// the export would be, asks for a reason only when the server says so and sends the list's search, sort, filters and columns exactly.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EngineProvider, LazyExportButton } from '@erp/engine';
import { I18nRoot } from '@erp/i18n';
import { ToastProvider } from '@erp/ui';
import { accessFacts } from '../../navigation/screens';
import { ShellValueProvider, type ShellValue } from '../../shell/context';
import type { ExportView } from '../../api/exports';
import ExportsScreen from './exports';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const COMPANY = '00000000-0000-7000-8000-000000000001';

function shell(permissions: string[]): ShellValue {
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

const view = (extra: Partial<ExportView> = {}): ExportView => ({
  id: 'e1', source: 'org.branches', format: 'csv', status: 'done', fileName: 'org-branches-20261124-1100.csv', rowCount: 12, sizeBytes: 900, reason: null, sensitiveClasses: [],
  companyId: COMPANY, createdBy: 'u-me', createdByName: 'Me', createdAt: '2026-11-24T06:00:00Z', finishedAt: '2026-11-24T06:00:05Z', expiresAt: '2026-12-01T06:00:05Z', errorKey: null,
  heldRequestId: null, mine: true, canDownload: true, ...extra,
});

interface Sent { method: string; path: string; body: Record<string, unknown> | null }
const sent: Sent[] = [];

function serve(items: ExportView[], handler?: (path: string, method: string) => Response | undefined) {
  sent.length = 0;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    sent.push({ method, path, body: typeof init?.body === 'string' ? JSON.parse(init.body) : null });
    return handler?.(path, method) ?? (path === '/api/itg/exports' ? json({ items, next: null }) : json({ status: 404, title: 'errors.data.not_found' }, 404));
  }));
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function client() { return new QueryClient({ defaultOptions: { queries: { retry: false } } }); }

describe('the export centre', () => {
  it('FR_ADM_DAT_04_lists_the_exports_with_plain_statuses_and_a_download_only_for_a_finished_export_of_ones_own', async () => {
    serve([
      view(),
      view({ id: 'e2', status: 'awaiting_approval', canDownload: false, fileName: null, rowCount: null, sensitiveClasses: ['salary'] }),
      view({ id: 'e3', status: 'failed', canDownload: false, errorKey: 'errors.export.too_many_rows', fileName: null }),
    ]);
    render(
      <MemoryRouter initialEntries={['/admin/exports']}><I18nRoot locale="en"><ToastProvider><QueryClientProvider client={client()}>
        <ShellValueProvider value={shell(['itg.exports.run'])}><ExportsScreen /></ShellValueProvider>
      </QueryClientProvider></ToastProvider></I18nRoot></MemoryRouter>,
    );
    const table = within(await screen.findByRole('table'));
    expect(table.getByText('Ready')).toBeTruthy();
    expect(table.getByText('Waiting for approval')).toBeTruthy();
    expect(table.getByText('Failed')).toBeTruthy();
    expect(table.getByText(/Too many rows|more rows than one export/)).toBeTruthy();
    expect(screen.getByText(/A second person must approve this export/)).toBeTruthy();
    expect(screen.getAllByRole('button', { name: /Download the export/ })).toHaveLength(1);
    expect(screen.queryByText('Show the exports of other people in my companies')).toBeNull();
  });

  it('FR_ADM_DAT_04_holders_of_the_log_right_can_ask_for_the_exports_of_their_companies', async () => {
    serve([view()]);
    render(
      <MemoryRouter><I18nRoot locale="en"><ToastProvider><QueryClientProvider client={client()}>
        <ShellValueProvider value={shell(['itg.exports.run', 'itg.export_jobs.read'])}><ExportsScreen /></ShellValueProvider>
      </QueryClientProvider></ToastProvider></I18nRoot></MemoryRouter>,
    );
    await userEvent.click(await screen.findByRole('checkbox', { name: 'Show the exports of other people in my companies' }));
    await waitFor(() => expect(sent.some((s) => s.path === '/api/itg/exports')).toBe(true));
  });

  it('FR_ADM_DAT_04_a_person_without_the_export_right_sees_the_denied_message_and_nothing_is_fetched', () => {
    serve([]);
    render(
      <MemoryRouter><I18nRoot locale="en"><ToastProvider><QueryClientProvider client={client()}>
        <ShellValueProvider value={shell([])}><ExportsScreen /></ShellValueProvider>
      </QueryClientProvider></ToastProvider></I18nRoot></MemoryRouter>,
    );
    expect(screen.getByText('You may not use the export centre.')).toBeTruthy();
    expect(sent).toHaveLength(0);
  });
});

describe('the export dialog', () => {
  function mount(check: object) {
    serve([], (path, method) => {
      if (path === '/api/itg/exports/check') return json(check);
      if (path === '/api/itg/exports' && method === 'POST') return json(view({ status: 'queued' }), 202);
      return undefined;
    });
    render(
      <MemoryRouter><I18nRoot locale="en"><ToastProvider><QueryClientProvider client={client()}>
        <EngineProvider api={{ send: async (method, path, body) => { const r = await fetch(path, { method, ...(body ? { body } : {}) }); return r.text(); } }}>
          <LazyExportButton source="org.branches" title="Branches" companyId={COMPANY} search="lahore" sort="-code" query={{ isActive: true, companyId: COMPANY }}
            columns={[{ field: 'code', label: 'Code' }, { field: 'nameI18n', label: 'Name' }]} />
        </EngineProvider>
      </QueryClientProvider></ToastProvider></I18nRoot></MemoryRouter>,
    );
  }

  it('FR_ADM_DAT_04_sends_the_search_sort_filters_and_columns_of_the_view_and_needs_no_reason_when_the_server_does_not_ask', async () => {
    mount({ sensitiveClasses: [], reasonRequired: false, needsApproval: false, approvalOn: false, rows: 0, rowsCapped: false, approvalRows: 5000, hidden: [] });
    await userEvent.click(await screen.findByRole('button', { name: 'Export' }));
    const dialog = await screen.findByRole('dialog');
    const start = await within(dialog).findByRole('button', { name: 'Make the file' });
    await waitFor(() => expect((start as HTMLButtonElement).disabled).toBe(false));
    expect(within(dialog).queryByLabelText(/Reason/)).toBeNull();
    await userEvent.click(start);
    await waitFor(() => expect(sent.some((s) => s.method === 'POST' && s.path === '/api/itg/exports')).toBe(true));
    const body = sent.find((s) => s.method === 'POST' && s.path === '/api/itg/exports')?.body;
    expect(body).toMatchObject({ source: 'org.branches', search: 'lahore', sort: '-code', format: 'csv', companyId: COMPANY, query: { isActive: true }, columns: [{ field: 'code', label: 'Code' }, { field: 'nameI18n', label: 'Name' }] });
  });

  it('FR_KRN_AUD_02_asks_for_a_reason_when_the_server_finds_sensitive_fields_and_says_when_a_second_person_must_approve', async () => {
    mount({ sensitiveClasses: ['salary'], reasonRequired: true, needsApproval: true, approvalOn: true, rows: 9000, rowsCapped: true, approvalRows: 5000, hidden: ['approvalThreshold'] });
    await userEvent.click(await screen.findByRole('button', { name: 'Export' }));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText('This export contains sensitive data')).toBeTruthy();
    expect(within(dialog).getByText('A second person must approve this export')).toBeTruthy();
    expect(within(dialog).getByText(/hidden from you/)).toBeTruthy();
    const start = within(dialog).getByRole('button', { name: 'Make the file' });
    expect((start as HTMLButtonElement).disabled).toBe(true);
    await userEvent.type(within(dialog).getByLabelText(/Reason/), 'Quarterly review');
    await waitFor(() => expect((start as HTMLButtonElement).disabled).toBe(false));
    await userEvent.click(start);
    await waitFor(() => expect(sent.find((s) => s.method === 'POST' && s.path === '/api/itg/exports')?.body).toMatchObject({ reason: 'Quarterly review' }));
  });
});
