// Notification texts and rules (slice S1.15d Build item 11; FR-KRN-NTF-02, -03): the catalogue, the active text of each language and channel, placeholders, saving a new
// version, the server's refusal shown on the field, preview with sample values, a test send to oneself, restoring the product text, rules (add, change, delete), the
// read-only person and the denied person.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import NotifyScreen from './notify';
import type { CatalogueEvent, NotificationText, Rule } from '../../api/templates';
import { json, shell, show } from './access-test-support';

const READ = ['ntf.catalogue.read', 'ntf.templates.read', 'ntf.subscriptions.read'];
const WRITE = [...READ, 'ntf.templates.create', 'ntf.templates.test_send', 'ntf.subscriptions.create', 'ntf.subscriptions.update', 'ntf.subscriptions.delete'];

const event: CatalogueEvent = { code: 'workflow.task_assigned', module: 'kernel', group: 'approvals', mandatory: true, audited: false, link: 'link',
  fields: [{ name: 'document_title', type: 'text', fieldClass: 'general' }, { name: 'id_card', type: 'text', fieldClass: 'personal' }, { name: 'salary', type: 'money', fieldClass: 'salary' }] };
const text = (over: Partial<NotificationText>): NotificationText => ({ id: 't1', eventCode: event.code, channel: 'in_app', language: 'en', subject: 'Waiting for you', body: 'Open {document_title} to decide.', version: 1, source: 'product', status: 'active', rowVersion: 1, ...over });
const rule: Rule = { id: 'r1', name: 'HR copy', eventCode: event.code, companyId: null, condition: null, audience: { users: null, roles: ['hr_admin'], groups: null, relationships: null }, channels: ['in_app'], mandatory: false, rowVersion: 2 };

interface Server { sent: { method: string; url: string; body: unknown }[]; texts?: NotificationText[]; rules?: Rule[]; answer?: (method: string, url: string, body: unknown) => Response | undefined }

function serve(s: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      const body = init?.body ? JSON.parse(String(init.body)) : null;
      s.sent.push({ method, url: path + (url.includes('?') ? `?${url.split('?')[1]}` : ''), body });
      const custom = s.answer?.(method, path, body);
      if (custom) return custom;
      if (path.endsWith('/preview')) return json({ subject: 'Waiting for you', body: 'Open Leave of Sara to decide.' });
      if (path.endsWith('/test-send')) return json({ id: 'm1' }, 202);
      if (method === 'DELETE') return new Response(null, { status: 204 });
      return json(text({ id: 't2', version: 2, source: 'tenant' }), 200);
    }
    if (path === '/api/ntf/catalogue') return json([event]);
    if (path === '/api/ntf/text-templates') return json(s.texts ?? [text({}), text({ id: 't3', language: 'ur', subject: 'آپ کے انتظار میں', body: 'فیصلے کے لیے {document_title} کھولیں۔' })]);
    if (path === '/api/ntf/rules') return json(s.rules ?? [rule]);
    if (path === '/api/iam/roles') return json({ items: [{ id: 'r1', code: 'hr_admin', nameI18n: { en: 'HR administrator' } }, { id: 'r2', code: 'tenant_admin', nameI18n: { en: 'Tenant administrator' } }] });
    if (path === '/api/iam/people-lookup') return json([{ id: 'u9', displayName: 'Imran Manager', username: 'imran' }]);
    return json({ title: 'errors.data.not_found' }, 404);
  }));
}

const open = (permissions = WRITE, at = '/settings/notification-texts?event=workflow.task_assigned') => show('/settings/notification-texts', at, <NotifyScreen />, shell(permissions));
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-KRN-NTF-02 notification texts', () => {
  it('lists the events and opens the active text of the first language and channel', async () => {
    serve({ sent: [] });
    open(WRITE, '/settings/notification-texts');
    const list = await screen.findByRole('list', { name: 'Notifications' });
    expect(within(list).getByText('workflow.task_assigned')).toBeTruthy();
    expect(within(list).getByText('Cannot be switched off')).toBeTruthy();
    await userEvent.click(within(list).getByRole('button', { name: 'Open the notification workflow.task_assigned' }));
    expect(await screen.findByDisplayValue('Waiting for you')).toBeTruthy();
    expect(screen.getByDisplayValue('Open {document_title} to decide.')).toBeTruthy();
    expect(screen.getByText("The product's text (version 1).")).toBeTruthy();
  });

  it('offers the placeholders of the event, marks the salary and personal ones, and adds one to the text', async () => {
    serve({ sent: [] });
    open();
    await screen.findByDisplayValue('Waiting for you');
    expect(screen.getByText('(never allowed in a notification)')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Add document_title to the text' }));
    expect(screen.getByDisplayValue('Open {document_title} to decide.{document_title}')).toBeTruthy();
  });

  it('saves an edited text as a new version, sending exactly what was typed', async () => {
    const s: Server = { sent: [] };
    serve(s);
    open();
    const body = await screen.findByDisplayValue('Open {document_title} to decide.');
    await userEvent.clear(body);
    await userEvent.type(body, 'Please decide');
    await userEvent.click(screen.getByRole('button', { name: 'Save as new version' }));
    await waitFor(() => expect(s.sent.find((x) => x.url === '/api/ntf/text-templates')).toBeTruthy());
    expect(s.sent.find((x) => x.url === '/api/ntf/text-templates')?.body).toEqual({ eventCode: event.code, channel: 'in_app', language: 'en', subject: 'Waiting for you', body: 'Please decide' });
    expect(await screen.findByText('The text is saved.')).toBeTruthy();
  });

  it('shows the server\'s refusal and keeps what was typed', async () => {
    const s: Server = { sent: [], answer: (method, url) => (method === 'POST' && url === '/api/ntf/text-templates' ? json({ title: 'errors.notification.template_salary_field', status: 400 }, 400) : undefined) };
    serve(s);
    open();
    const body = await screen.findByDisplayValue('Open {document_title} to decide.');
    await userEvent.type(body, ' {{salary}');
    await userEvent.click(screen.getByRole('button', { name: 'Save as new version' }));
    expect(await screen.findByText('The change could not be saved. Try again in a moment.')).toBeTruthy();
    expect(screen.getByDisplayValue('Open {document_title} to decide. {salary}')).toBeTruthy();
  });

  it('previews with the sample values given and sends nothing else', async () => {
    const s: Server = { sent: [] };
    serve(s);
    open();
    await screen.findByDisplayValue('Waiting for you');
    await userEvent.type(screen.getByRole('textbox', { name: 'document_title' }), 'Leave of Sara');
    await userEvent.click(screen.getByRole('button', { name: 'Preview' }));
    expect(await screen.findByText('Open Leave of Sara to decide.')).toBeTruthy();
    expect(s.sent).toHaveLength(1);
    expect(s.sent[0]?.url).toBe('/api/ntf/text-templates/preview');
    expect((s.sent[0]?.body as { sample: Record<string, string> }).sample).toEqual({ document_title: 'Leave of Sara' });
  });

  it('FR-KRN-NTF-02 sends a test to oneself', async () => {
    const s: Server = { sent: [] };
    serve(s);
    open();
    await screen.findByDisplayValue('Waiting for you');
    await userEvent.click(screen.getByRole('button', { name: 'Send a test to me' }));
    expect(await screen.findByText('A test e-mail is on its way to you.')).toBeTruthy();
    expect(s.sent[0]?.url).toBe('/api/ntf/text-templates/test-send');
  });

  it('restores the product text only for the person\'s own text, after confirming', async () => {
    const s: Server = { sent: [], texts: [text({ id: 't2', version: 2, source: 'tenant', body: 'Our own words' })] };
    serve(s);
    open();
    await screen.findByDisplayValue('Our own words');
    await userEvent.click(screen.getByRole('button', { name: 'Restore the product text' }));
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Restore' }));
    await waitFor(() => expect(s.sent.find((x) => x.url === '/api/ntf/text-templates/restore')).toBeTruthy());
    expect(s.sent[0]?.body).toEqual({ eventCode: event.code, channel: 'in_app', language: 'en', });
  });

  it('shows the Urdu text right to left', async () => {
    serve({ sent: [] });
    open();
    await screen.findByDisplayValue('Waiting for you');
    await userEvent.click(screen.getByRole('button', { name: /Language/ }));
    await userEvent.click(await screen.findByRole('option', { name: 'Urdu' }));
    const field = await screen.findByDisplayValue('آپ کے انتظار میں');
    expect(field.closest('[dir]')?.getAttribute('dir')).toBe('rtl');
  });

  it('is read only without the right to change texts', async () => {
    serve({ sent: [] });
    open(READ);
    const subject = await screen.findByDisplayValue('Waiting for you');
    expect(subject.hasAttribute('readonly')).toBe(true);
    expect(screen.queryByRole('button', { name: 'Save as new version' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Send a test to me' })).toBeNull();
  });

  it('says so when the person may not see the texts', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ title: 'errors.auth.forbidden' }, 403)));
    open(READ, '/settings/notification-texts');
    expect(await screen.findByText('You may not see the notification texts.')).toBeTruthy();
  });
});

describe('FR-KRN-NTF-03 notification rules', () => {
  const rules = async () => { await userEvent.click(await screen.findByRole('tab', { name: 'Who is told' })); };

  it('lists the rules of the event with their roles and channels', async () => {
    serve({ sent: [] });
    open();
    await rules();
    const list = await screen.findByRole('list', { name: 'Rules' });
    expect(within(list).getByText('HR copy')).toBeTruthy();
    expect(within(list).getByText(/Roles: HR administrator/)).toBeTruthy();
  });

  it('adds a rule for a role and sends it in the stored form', async () => {
    const s: Server = { sent: [] };
    serve(s);
    open();
    await rules();
    await userEvent.click(await screen.findByRole('button', { name: 'Add a rule' }));
    expect((screen.getByRole('button', { name: 'Save the rule' }) as HTMLButtonElement).disabled).toBe(true);
    await userEvent.click(await screen.findByRole('checkbox', { name: /Tenant administrator/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Save the rule' }));
    await waitFor(() => expect(s.sent.find((x) => x.method === 'POST' && x.url === '/api/ntf/rules')).toBeTruthy());
    expect(s.sent[0]?.body).toMatchObject({ eventCode: event.code, companyId: null, channels: ['in_app'], mandatory: false, audience: { roles: ['tenant_admin'], users: [] } });
  });

  it('changes a rule with the row version it read and keeps its condition', async () => {
    const s: Server = { sent: [], rules: [{ ...rule, condition: { all: [{ field: 'x', op: 'eq', value: '1' }] } }] };
    serve(s);
    open();
    await rules();
    await userEvent.click(await screen.findByRole('button', { name: 'Change the rule HR copy' }));
    await userEvent.click(await screen.findByRole('checkbox', { name: 'By e-mail' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save the rule' }));
    await waitFor(() => expect(s.sent.find((x) => x.method === 'PUT')).toBeTruthy());
    expect(s.sent[0]).toMatchObject({ url: '/api/ntf/rules/r1', body: { rowVersion: 2, channels: ['in_app', 'email'], condition: { all: [{ field: 'x', op: 'eq', value: '1' }] } } });
  });

  it('deletes a rule after confirming, with its row version', async () => {
    const s: Server = { sent: [] };
    serve(s);
    open();
    await rules();
    await userEvent.click(await screen.findByRole('button', { name: 'Delete the rule HR copy' }));
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(s.sent[0]).toMatchObject({ method: 'DELETE', url: '/api/ntf/rules/r1?rowVersion=2' }));
  });
});
