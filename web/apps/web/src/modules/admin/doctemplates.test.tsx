// Document templates (slice S1.15d Build item 12; FR-KRN-DOC-05, FR-KRN-LOC-07): the list, versions (active read-only, new draft), the editor with the placeholders of the
// record type, saving, the server's refusal shown, preview as a PDF link, activation after confirming, the letterhead that applies, a language variant, creating a template,
// the read-only person and the denied person.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import DocumentTemplatesScreen from './doctemplates';
import type { TemplateRow, TemplateVersion } from '../../api/templates';
import { json, shell, show } from './access-test-support';

const READ = ['doc.templates.read', 'doc.template_versions.read'];
const WRITE = [...READ, 'doc.templates.create', 'doc.templates.update', 'doc.template_versions.create', 'doc.template_versions.update'];

const row = (over: Partial<TemplateRow>): TemplateRow => ({ id: 'tp1', code: 'offer-letter', nameI18n: { en: 'Offer letter', ur: 'پیشکش کا خط' }, kind: 'letter', entityType: 'hrm.employments', companyId: null, language: 'en', source: 'tenant', activeVersionId: 'v1', isActive: true, rowVersion: 4, ...over });
const letterhead = row({ id: 'tp9', code: 'letterhead', nameI18n: { en: 'Company letterhead' }, kind: 'part', entityType: '-', activeVersionId: 'vh' });
const version = (over: Partial<TemplateVersion>): TemplateVersion => ({ id: 'v1', templateId: 'tp1', version: 1, status: 'active', outputFormat: 'pdf', body: '<p>Dear {{ employee.name }}</p>', rowVersion: 3, ...over });
const fields = [{ entityType: 'hrm.employments', fields: [{ path: 'employee.name', type: 'text', fieldClass: 'general' }, { path: 'employee.salary', type: 'number', fieldClass: 'salary' }] }];

interface Server { sent: { method: string; url: string; body: unknown }[]; versions?: TemplateVersion[]; templates?: TemplateRow[]; answer?: (method: string, url: string, body: unknown) => Response | undefined }

function serve(s: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      const body = init?.body ? JSON.parse(String(init.body)) : null;
      s.sent.push({ method, url: path, body });
      const custom = s.answer?.(method, path, body);
      if (custom) return custom;
      if (path.endsWith('/preview')) return json({ html: '<p>x</p>', pdf: btoa('%PDF-1.4'), usedFields: ['employee.name'] });
      if (path === '/api/doc/templates') return json(row({ id: 'tp2', code: 'offer-letter-ur', language: 'ur' }), 201);
      return json(version({ id: 'v2', version: 2, status: 'draft' }), 200);
    }
    if (path === '/api/doc/templates') return json({ items: s.templates ?? [row({}), letterhead], next: null });
    if (path === '/api/doc/templates/entity-fields') return json(fields);
    if (path === '/api/doc/templates/tp1/versions') return json(s.versions ?? [version({})]);
    return json({ title: 'errors.data.not_found' }, 404);
  }));
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:preview', revokeObjectURL: () => undefined }));
}

const open = (permissions = WRITE, at = '/settings/document-templates?template=tp1') => show('/settings/document-templates', at, <DocumentTemplatesScreen />, shell(permissions));
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-KRN-DOC-05 document templates', () => {
  it('lists the templates and opens one with its versions', async () => {
    serve({ sent: [] });
    open(WRITE, '/settings/document-templates');
    const list = await screen.findByRole('list', { name: 'Templates' });
    expect(within(list).getByText('Offer letter')).toBeTruthy();
    await userEvent.click(within(list).getByRole('button', { name: 'Open the template Offer letter' }));
    expect(await screen.findByRole('list', { name: 'Versions' })).toBeTruthy();
  });

  it('keeps the active version read-only and starts a draft from it', async () => {
    const s: Server = { sent: [] };
    serve(s);
    open();
    expect(await screen.findByText('This version is active and cannot be edited. Start a new draft from it to change the text.')).toBeTruthy();
    expect(screen.getByDisplayValue('<p>Dear {{ employee.name }}</p>').hasAttribute('readonly')).toBe(true);
    await userEvent.click(screen.getByRole('button', { name: 'New draft from version 1' }));
    await waitFor(() => expect(s.sent[0]).toMatchObject({ method: 'POST', url: '/api/doc/templates/tp1/versions', body: { body: '<p>Dear {{ employee.name }}</p>', outputFormat: 'pdf' } }));
  });

  it('edits a draft with the placeholders of the record, marks salary, and saves with the row version', async () => {
    const s: Server = { sent: [], versions: [version({ id: 'v2', version: 2, status: 'draft', rowVersion: 7 })] };
    serve(s);
    open();
    await screen.findByDisplayValue('<p>Dear {{ employee.name }}</p>');
    expect(screen.getByText('(salary: needs a special right to print)')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Add employee.name to the text' }));
    expect(screen.getByDisplayValue('<p>Dear {{ employee.name }}</p>{{ employee.name }}')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Save the draft' }));
    await waitFor(() => expect(s.sent[0]).toMatchObject({ method: 'PUT', url: '/api/doc/templates/versions/v2', body: { body: '<p>Dear {{ employee.name }}</p>{{ employee.name }}', outputFormat: 'pdf', rowVersion: 7 } }));
    expect(await screen.findByText('The draft is saved.')).toBeTruthy();
  });

  it('shows the server\'s refusal of a body and keeps the text', async () => {
    const s: Server = { sent: [], versions: [version({ id: 'v2', version: 2, status: 'draft' })], answer: (m) => (m === 'PUT' ? json({ title: 'errors.template.syntax', status: 400 }, 400) : undefined) };
    serve(s);
    open();
    const body = await screen.findByDisplayValue('<p>Dear {{ employee.name }}</p>');
    await userEvent.type(body, 'x');
    await userEvent.click(screen.getByRole('button', { name: 'Save the draft' }));
    expect(await screen.findByText('The template text has a mistake and cannot be read.')).toBeTruthy();
    expect(screen.getByDisplayValue('<p>Dear {{ employee.name }}</p>x')).toBeTruthy();
  });

  it('previews the text as a PDF link with the names it used, storing nothing', async () => {
    const s: Server = { sent: [] };
    serve(s);
    open();
    await screen.findByDisplayValue('<p>Dear {{ employee.name }}</p>');
    await userEvent.click(screen.getByRole('button', { name: 'Preview' }));
    const link = await screen.findByRole('link', { name: 'Open the PDF preview in a new tab' });
    expect(link.getAttribute('href')).toBe('blob:preview');
    expect(s.sent).toHaveLength(1);
    expect(s.sent[0]).toMatchObject({ url: '/api/doc/templates/preview', body: { language: 'en', entityType: 'hrm.employments', outputFormat: 'pdf' } });
  });

  it('activates a draft only after confirming what happens', async () => {
    const s: Server = { sent: [], versions: [version({ id: 'v2', version: 2, status: 'draft', rowVersion: 5 }), version({})] };
    serve(s);
    open();
    await screen.findByDisplayValue('<p>Dear {{ employee.name }}</p>');
    await userEvent.click(screen.getByRole('button', { name: 'Activate' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(/The version that was active becomes retired/)).toBeTruthy();
    expect(s.sent).toHaveLength(0);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Activate version 2' }));
    await waitFor(() => expect(s.sent[0]).toMatchObject({ url: '/api/doc/templates/versions/v2/activate', body: { rowVersion: 5 } }));
  });

  it('FR-KRN-LOC-07 says which letterhead applies, and starts a variant in the other language', async () => {
    serve({ sent: [] });
    open();
    expect(await screen.findByText('Letterhead used: Company letterhead.')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Start a version in the other language' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByDisplayValue('offer-letter-ur')).toBeTruthy();
    expect(within(dialog).getByDisplayValue('<p>Dear {{ employee.name }}</p>')).toBeTruthy();
  });

  it('creates a template with its first draft', async () => {
    const s: Server = { sent: [] };
    serve(s);
    open(WRITE, '/settings/document-templates');
    await userEvent.click(await screen.findByRole('button', { name: 'New template' }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.type(within(dialog).getByRole('textbox', { name: /Code/ }), 'warning-letter');
    await userEvent.type(within(dialog).getByRole('textbox', { name: /Name in English/ }), 'Warning letter');
    await userEvent.type(within(dialog).getByRole('textbox', { name: /Text/ }), 'Hello');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create the template' }));
    await waitFor(() => expect(s.sent[0]).toMatchObject({ method: 'POST', url: '/api/doc/templates', body: { code: 'warning-letter', name: { en: 'Warning letter' }, kind: 'letter', entityType: 'hrm.employments', companyId: null, language: 'en', outputFormat: 'pdf', body: 'Hello' } }));
    // The new template is listed and opened (the list is read again after the create).
    await waitFor(() => expect(s.sent).toHaveLength(1));
  });

  it('is read only without the rights to change, and denied without the right to read', async () => {
    serve({ sent: [] });
    open(READ);
    await screen.findByDisplayValue('<p>Dear {{ employee.name }}</p>');
    expect(screen.queryByRole('button', { name: 'New draft from version 1' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'New template' })).toBeNull();
    cleanup();
    vi.stubGlobal('fetch', vi.fn(async () => json({ title: 'errors.auth.forbidden' }, 403)));
    open(READ, '/settings/document-templates');
    expect(await screen.findByText('You may not see the document templates.')).toBeTruthy();
  });
});
