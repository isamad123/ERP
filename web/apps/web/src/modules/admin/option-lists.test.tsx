// Option lists (slice S1.15b Build item 6; FR-KRN-MET-01, S1.15 ruling Q7): the lists and their items, adding, ordering and
// switching items off (never deleting), the system-list note, the server's refusals and the read-only and denied states.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import OptionListsScreen from './option-lists';
import type { OptionListDetail, OptionListSummary } from '../../api/config';
import { json, shell, show } from './access-test-support';

const READ = ['cfg.option_lists.read', 'cfg.option_list_items.read'];
const WRITE = [...READ, 'cfg.option_lists.create', 'cfg.option_lists.update', 'cfg.option_list_items.create', 'cfg.option_list_items.update'];

const item = (id: string, code: string, en: string, ur: string, seq: number, isActive = true) => ({ id, code, label: { en, ur }, seq, isActive, rowVersion: 1 });
const reasons: OptionListDetail = { id: 'l1', code: 'hire_reason', name: { en: 'Hiring reasons', ur: 'بھرتی کی وجوہات' }, isSystem: true, rowVersion: 1,
  items: [item('i1', 'new_post', 'New post', 'نئی آسامی', 10), item('i2', 'replacement', 'Replacement', 'متبادل', 20), item('i3', 'old', 'Old reason', 'پرانی وجہ', 30, false)] };
const blood: OptionListDetail = { id: 'l2', code: 'blood_group', name: { en: 'Blood groups' }, isSystem: false, rowVersion: 1, items: [] };
const summaries = (): OptionListSummary[] => [reasons, blood].map((l) => ({ id: l.id, code: l.code, name: l.name, isSystem: l.isSystem, itemCount: l.items.length, rowVersion: l.rowVersion }));

interface Server { sent: { method: string; url: string; body: unknown }[]; answer?: (method: string, url: string, body: unknown) => Response | undefined }

function serve(s: Server) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const [path = ''] = url.split('?');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') {
      const body = init?.body ? JSON.parse(String(init.body)) : null;
      s.sent.push({ method, url: path, body });
      return s.answer?.(method, path, body) ?? json(reasons);
    }
    if (path === '/api/cfg/option-lists') return json({ items: summaries() });
    if (path === '/api/cfg/option-lists/l1') return json(reasons);
    if (path === '/api/cfg/option-lists/l2') return json(blood);
    return json({ title: 'errors.data.not_found' }, 404);
  }));
}

const open = (permissions = WRITE, at = '/settings/option-lists') => show('/settings/option-lists', at, <OptionListsScreen />, shell(permissions));

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('FR-KRN-MET-01 the option lists screen', () => {
  it('lists the lists with their item counts and marks the system list', async () => {
    serve({ sent: [] });
    open();
    const lists = await screen.findByRole('list', { name: 'Option lists' });
    expect(within(lists).getByText('Hiring reasons')).toBeTruthy();
    expect(within(lists).getByText('3 items')).toBeTruthy();
    expect(within(lists).getAllByText('System list')).toHaveLength(1);
  });

  it('opens a list with English and Urdu names side by side, in order, and says a system list can be extended, not removed', async () => {
    serve({ sent: [] });
    open(WRITE, '/settings/option-lists?list=l1');
    const table = await screen.findByRole('table', { name: 'Items of Hiring reasons' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows.map((r) => within(r).getAllByRole('cell')[0]!.textContent)).toEqual(['New post', 'Replacement', 'Old reason']);
    expect(within(rows[0]!).getByText('نئی آسامی')).toBeTruthy();
    expect(screen.getByText('System list: you can add items, not remove them.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Delete/ })).toBeNull();
  });

  it('creates a list from a code and a name in both languages', async () => {
    const s: Server = { sent: [], answer: () => json({ ...blood, id: 'l3', code: 'exit_reason', name: { en: 'Exit reasons', ur: 'رخصتی کی وجوہات' } }, 201) };
    serve(s);
    open();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'New list' }));
    const dialog = await screen.findByRole('dialog', { name: 'New list' });
    await user.type(within(dialog).getByRole('textbox', { name: /^Code/ }), 'exit_reason');
    await user.type(within(dialog).getByRole('textbox', { name: /^List name in English/ }), 'Exit reasons');
    await user.type(within(dialog).getByRole('textbox', { name: /^List name in Urdu/ }), 'رخصتی کی وجوہات');
    await user.click(within(dialog).getByRole('button', { name: 'Create list' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ method: 'POST', url: '/api/cfg/option-lists', body: { code: 'exit_reason', name: { en: 'Exit reasons', ur: 'رخصتی کی وجوہات' } } });
  });

  it('refuses a code with capital letters before asking the server', async () => {
    const s: Server = { sent: [] };
    serve(s);
    open();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'New list' }));
    const dialog = await screen.findByRole('dialog', { name: 'New list' });
    await user.type(within(dialog).getByRole('textbox', { name: /^Code/ }), 'Bad Code');
    await user.type(within(dialog).getByRole('textbox', { name: /^List name in English/ }), 'X');
    await user.click(within(dialog).getByRole('button', { name: 'Create list' }));
    expect(await within(dialog).findByText(/Use lower-case letters, digits and underscore/)).toBeTruthy();
    expect(s.sent).toHaveLength(0);
  });

  it('adds an item with a code and two names', async () => {
    const s: Server = { sent: [], answer: () => json(reasons, 201) };
    serve(s);
    open(WRITE, '/settings/option-lists?list=l1');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Add item' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add item' });
    await user.type(within(dialog).getByRole('textbox', { name: /^Code/ }), 'transfer_in');
    await user.type(within(dialog).getByRole('textbox', { name: /^Item name in English/ }), 'Transfer in');
    await user.type(within(dialog).getByRole('textbox', { name: /^Item name in Urdu/ }), 'تبادلہ');
    await user.click(within(dialog).getByRole('button', { name: 'Add item' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ method: 'POST', url: '/api/cfg/option-lists/l1/items', body: { code: 'transfer_in', label: { en: 'Transfer in', ur: 'تبادلہ' }, isActive: true } });
  });

  it('moves an item down: sends the new order of all item ids', async () => {
    const s: Server = { sent: [] };
    serve(s);
    open(WRITE, '/settings/option-lists?list=l1');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Move New post down' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ method: 'POST', url: '/api/cfg/option-lists/l1/items/order', body: { itemIds: ['i2', 'i1', 'i3'] } });
  });

  it('switches an item off instead of deleting it, and an off item back on', async () => {
    const s: Server = { sent: [] };
    serve(s);
    open(WRITE, '/settings/option-lists?list=l1');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Switch off the item New post' }));
    await waitFor(() => expect(s.sent).toHaveLength(1));
    expect(s.sent[0]).toMatchObject({ method: 'PUT', url: '/api/cfg/option-lists/l1/items/i1', body: { isActive: false, rowVersion: 1, label: { en: 'New post', ur: 'نئی آسامی' } } });
    await user.click(await screen.findByRole('button', { name: 'Switch on the item Old reason' }));
    await waitFor(() => expect(s.sent).toHaveLength(2));
    expect(s.sent[1]).toMatchObject({ url: '/api/cfg/option-lists/l1/items/i3', body: { isActive: true } });
  });

  it('shows the server\'s refusal of a duplicate code beside the code', async () => {
    const s: Server = { sent: [], answer: () => json({ title: 'errors.data.invalid_request', errors: { code: ['errors.data.duplicate'] } }, 400) };
    serve(s);
    open(WRITE, '/settings/option-lists?list=l1');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Add item' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add item' });
    await user.type(within(dialog).getByRole('textbox', { name: /^Code/ }), 'new_post');
    await user.type(within(dialog).getByRole('textbox', { name: /^Item name in English/ }), 'New post');
    await user.click(within(dialog).getByRole('button', { name: 'Add item' }));
    expect(await within(dialog).findByText('Check the fields marked below.')).toBeTruthy();
  });

  it('shows a reader the items without any change buttons', async () => {
    serve({ sent: [] });
    open(READ, '/settings/option-lists?list=l1');
    await screen.findByRole('table', { name: 'Items of Hiring reasons' });
    expect(screen.queryByRole('button', { name: 'Add item' })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Edit the item/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'New list' })).toBeNull();
  });

  it('says so when the person may not see the lists', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ title: 'errors.access.forbidden' }, 403)));
    open(READ);
    expect(await screen.findByText('You cannot see the option lists')).toBeTruthy();
  });
});
