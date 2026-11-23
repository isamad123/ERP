// Option lists (slice S1.15b Build item 6; FR-KRN-MET-01, S1.15 ruling Q7): the dropdown lists that fields and reason codes use.
// A plain list-and-items screen: code, English and Urdu names, order and an active switch. A system list can be extended, never
// removed, and nothing is deleted: an item is deactivated (it stays on the records that hold it). Effective dates and import
// are Later (FR-ADM-STU-11).
import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { useDocumentTitle } from '@erp/engine';
import { ArrowDown, ArrowUp, Plus } from 'lucide-react';
import { Banner, Button, Dialog, EmptyState, ErrorPanel, IconButton, PageHeader, Skeleton, StatusChip, SwitchField, TextField, useToast } from '@erp/ui';
import { useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../../api/client';
import { optionListsApi, type OptionItem, type OptionListDetail, type OptionListSummary } from '../../api/config';
import { nameOf, type Names } from '../../api/workflow';
import { useShell } from '../../shell/context';
import { cleanNames, NamePair, useRefused } from './config-parts';
import './config-messages';
import './users.css';
import './configure.css';

const CODE = /^[a-z][a-z0-9_]{0,39}$/;

export default function OptionListsScreen() {
  const { t } = useText();
  useDocumentTitle(t('lists.title'));
  const [params, setParams] = useSearchParams();
  const open = params.get('list');
  const [creating, setCreating] = useState(false);
  const lists = useQuery({ queryKey: ['cfg', 'option-lists'], queryFn: optionListsApi.list, refetchOnMount: 'always' });
  const canCreate = useWritable('cfg.option_lists.create');
  const problem = lists.error instanceof ApiProblem ? lists.error : null;
  const select = (id: string | null) => setParams((current) => { const next = new URLSearchParams(current); if (id) next.set('list', id); else next.delete('list'); return next; }, { replace: true });
  return (
    <div className="erp-page">
      <PageHeader title={t('lists.title')} description={t('lists.description')}
        actions={canCreate ? <Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={() => setCreating(true)}>{t('lists.new')}</Button> : undefined} />
      {lists.isPending && <Skeleton lines={5} />}
      {lists.isError && (
        <ErrorPanel message={problem?.status === 403 ? t('lists.denied') : t('lists.loadError')} reference={problem?.reference ?? null}
          action={<Button variant="primary" onPress={() => { void lists.refetch(); }}>{t('common.retry')}</Button>} />
      )}
      {lists.isSuccess && lists.data.items.length === 0 && (
        <EmptyState title={t('lists.empty')} action={canCreate ? <Button variant="primary" onPress={() => setCreating(true)}>{t('lists.new')}</Button> : undefined}>{t('lists.empty.body')}</EmptyState>
      )}
      {lists.isSuccess && lists.data.items.length > 0 && (
        <div className="erp-config-split">
          <ListOfLists lists={lists.data.items} open={open} onOpen={select} />
          {open ? <ListDetail key={open} id={open} /> : <EmptyState title={t('lists.pick')}>{t('lists.pick.body')}</EmptyState>}
        </div>
      )}
      {creating && <NewList onClose={() => setCreating(false)} onCreated={(id) => { setCreating(false); select(id); }} />}
    </div>
  );
}

function useWritable(permission: string): boolean {
  const { can, writable } = useShell();
  return writable && can(permission);
}

function ListOfLists({ lists, open, onOpen }: { lists: OptionListSummary[]; open: string | null; onOpen: (id: string) => void }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  return (
    <ul className="erp-config-list" aria-label={t('lists.title')}>
      {lists.map((l) => (
        <li key={l.id}>
          <div className="erp-config-card">
            <span className="erp-stack">
              <span className="erp-settings-name"><bdi>{nameOf(l.name, locale)}</bdi></span>
              <span className="erp-small erp-muted erp-code"><bdi>{l.code}</bdi></span>
            </span>
            <span className="erp-row erp-wrap">
              <span className="erp-small">{t('lists.itemCount', { count: l.itemCount })}</span>
              {l.isSystem && <StatusChip status="draft" label={t('lists.system')} />}
            </span>
            <Button variant={open === l.id ? 'primary' : 'secondary'} aria-pressed={open === l.id} aria-label={t('lists.open.aria', { name: nameOf(l.name, locale) })} onPress={() => onOpen(l.id)}>{t('lists.open')}</Button>
          </div>
        </li>
      ))}
    </ul>
  );
}

function NewList({ onClose, onCreated }: { onClose: () => void; onCreated: (id: string) => void }) {
  const { t } = useText();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refused = useRefused();
  const [code, setCode] = useState('');
  const [name, setName] = useState<Names>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const submit = async () => {
    const found: Record<string, string> = {};
    if (!CODE.test(code)) found.code = t('lists.error.code');
    if (!(name.en ?? '').trim()) found['name.en'] = t('lists.error.name');
    setErrors(found);
    if (Object.keys(found).length > 0) { setSummary(t('config.error.fix')); return; }
    setBusy(true);
    setSummary(null);
    try {
      const created = await optionListsApi.create({ code, name: cleanNames(name) });
      await queryClient.invalidateQueries({ queryKey: ['cfg', 'option-lists'] });
      toast.success(t('lists.created'));
      onCreated(created.id);
    } catch (error) {
      const r = refused(error);
      setErrors(r.fields);
      setSummary(r.summary);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog title={t('lists.new')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" isPending={busy} onPress={() => { void submit(); }}>{t('lists.create')}</Button>
        </>
      )}>
      <div className="erp-stack">
        {summary && <Banner tone="danger">{summary}</Banner>}
        <TextField label={t('lists.code')} help={t('lists.code.help')} isCode isRequired value={code} onChange={setCode} maxLength={40} error={errors.code ?? null} />
        <NamePair label={t('lists.name')} value={name} onChange={setName} errors={{ en: errors['name.en'], ur: errors['name.ur'] }} />
      </div>
    </Dialog>
  );
}

function ListDetail({ id }: { id: string }) {
  const { t } = useText();
  const { locale } = useLocaleSettings();
  const toast = useToast();
  const queryClient = useQueryClient();
  const refused = useRefused();
  const canUpdate = useWritable('cfg.option_lists.update');
  const canAdd = useWritable('cfg.option_list_items.create');
  const canChange = useWritable('cfg.option_list_items.update');
  const [adding, setAdding] = useState(false);
  const [editing, setEditing] = useState<OptionItem | null>(null);
  const [renaming, setRenaming] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const detail = useQuery({ queryKey: ['cfg', 'option-list', id], queryFn: () => optionListsApi.get(id), refetchOnMount: 'always' });
  const problem = detail.error instanceof ApiProblem ? detail.error : null;

  const take = (answer: OptionListDetail) => {
    queryClient.setQueryData(['cfg', 'option-list', id], answer);
    void queryClient.invalidateQueries({ queryKey: ['cfg', 'option-lists'] });
  };
  const act = async (run: () => Promise<OptionListDetail>, done?: string) => {
    setBusy(true);
    setFailure(null);
    try {
      take(await run());
      if (done) toast.success(done);
    } catch (error) {
      setFailure(refused(error).summary);
    } finally {
      setBusy(false);
    }
  };

  if (detail.isPending) return <Skeleton lines={5} />;
  if (detail.isError) {
    return (
      <ErrorPanel message={problem?.status === 404 ? t('lists.notFound') : problem?.status === 403 ? t('lists.denied') : t('lists.loadError')} reference={problem?.reference ?? null}
        action={<Button variant="primary" onPress={() => { void detail.refetch(); }}>{t('common.retry')}</Button>} />
    );
  }
  const list = detail.data;
  const move = (index: number, by: number) => {
    const ids = list.items.map((i) => i.id);
    const [moved] = ids.splice(index, 1);
    if (!moved) return;
    ids.splice(index + by, 0, moved);
    void act(() => optionListsApi.order(id, ids));
  };
  return (
    <section className="erp-stack" aria-labelledby="list-detail-title">
      <div className="erp-row erp-wrap">
        <h2 id="list-detail-title" className="erp-h3"><bdi>{nameOf(list.name, locale)}</bdi></h2>
        <span className="erp-small erp-muted erp-code"><bdi>{list.code}</bdi></span>
        {list.isSystem && <StatusChip status="draft" label={t('lists.system')} />}
        {canUpdate && <Button variant="secondary" onPress={() => setRenaming(true)}>{t('lists.rename')}</Button>}
      </div>
      {list.isSystem && <Banner tone="info">{t('lists.system.note')}</Banner>}
      {failure && <Banner tone="danger">{failure}</Banner>}
      {canAdd && <div><Button variant="primary" icon={<Plus size={18} aria-hidden="true" />} onPress={() => setAdding(true)}>{t('lists.item.add')}</Button></div>}
      {list.items.length === 0
        ? <EmptyState title={t('lists.items.empty')}>{t('lists.items.empty.body')}</EmptyState>
        : (
          <div className="erp-table-scroll">
          <table className="erp-table" aria-label={t('lists.items.title', { name: nameOf(list.name, locale) })}>
            <thead>
              <tr>
                <th scope="col">{t('lists.col.code')}</th>
                <th scope="col">{t('lists.col.en')}</th>
                <th scope="col">{t('lists.col.ur')}</th>
                <th scope="col">{t('lists.col.active')}</th>
                <th scope="col"><span className="erp-visually-hidden">{t('lists.col.actions')}</span></th>
              </tr>
            </thead>
            <tbody>
              {list.items.map((item, index) => {
                const name = nameOf(item.label, locale);
                return (
                  <tr key={item.id}>
                    <th scope="row"><span className="erp-code"><bdi>{item.code}</bdi></span></th>
                    <td lang="en"><bdi>{item.label.en ?? ''}</bdi></td>
                    <td lang="ur" dir="rtl"><bdi>{item.label.ur ?? ''}</bdi></td>
                    <td><StatusChip status={item.isActive ? 'active' : 'inactive'} /></td>
                    <td>
                      <span className="erp-config-actions">
                        {canChange && (
                          <>
                            <IconButton label={t('lists.item.up', { name })} icon={<ArrowUp size={16} aria-hidden="true" />} isDisabled={busy || index === 0} onPress={() => move(index, -1)} />
                            <IconButton label={t('lists.item.down', { name })} icon={<ArrowDown size={16} aria-hidden="true" />} isDisabled={busy || index === list.items.length - 1} onPress={() => move(index, 1)} />
                            <Button variant="secondary" aria-label={t('lists.item.edit.aria', { name })} onPress={() => setEditing(item)}>{t('lists.item.edit')}</Button>
                            <Button variant="quiet" isDisabled={busy} aria-label={t(item.isActive ? 'lists.item.deactivate.aria' : 'lists.item.activate.aria', { name })}
                              onPress={() => { void act(() => optionListsApi.changeItem(id, item.id, { label: item.label, isActive: !item.isActive, rowVersion: item.rowVersion }), t(item.isActive ? 'lists.item.deactivated' : 'lists.item.activated')); }}>
                              {t(item.isActive ? 'lists.item.deactivate' : 'lists.item.activate')}
                            </Button>
                          </>
                        )}
                      </span>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          </div>
        )}
      <p className="erp-small erp-muted">{t('lists.note.noDelete')}</p>
      {adding && <ItemDialog listId={id} onClose={() => setAdding(false)} onDone={(answer) => { take(answer); toast.success(t('lists.item.added')); setAdding(false); }} />}
      {editing && <ItemDialog listId={id} item={editing} onClose={() => setEditing(null)} onDone={(answer) => { take(answer); toast.success(t('lists.item.saved')); setEditing(null); }} />}
      {renaming && <RenameDialog list={list} onClose={() => setRenaming(false)} onDone={(answer) => { take(answer); toast.success(t('lists.renamed')); setRenaming(false); }} />}
    </section>
  );
}

function ItemDialog({ listId, item, onClose, onDone }: { listId: string; item?: OptionItem; onClose: () => void; onDone: (answer: OptionListDetail) => void }) {
  const { t } = useText();
  const refused = useRefused();
  const [code, setCode] = useState(item?.code ?? '');
  const [label, setLabel] = useState<Names>(item?.label ?? {});
  const [isActive, setActive] = useState(item?.isActive ?? true);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const submit = async () => {
    const found: Record<string, string> = {};
    if (!item && !CODE.test(code)) found.code = t('lists.error.code');
    if (!(label.en ?? '').trim()) found['label.en'] = t('lists.error.name');
    setErrors(found);
    if (Object.keys(found).length > 0) { setSummary(t('config.error.fix')); return; }
    setBusy(true);
    setSummary(null);
    try {
      onDone(item
        ? await optionListsApi.changeItem(listId, item.id, { label: cleanNames(label), isActive, rowVersion: item.rowVersion })
        : await optionListsApi.addItem(listId, { code, label: cleanNames(label), isActive }));
    } catch (error) {
      const r = refused(error);
      setErrors(r.fields);
      setSummary(r.summary);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog title={item ? t('lists.item.edit.title') : t('lists.item.add')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" isPending={busy} onPress={() => { void submit(); }}>{item ? t('lists.item.save') : t('lists.item.create')}</Button>
        </>
      )}>
      <div className="erp-stack">
        {summary && <Banner tone="danger">{summary}</Banner>}
        <TextField label={t('lists.code')} help={item ? t('lists.code.fixed') : t('lists.code.help')} isCode isRequired={!item} isReadOnly={!!item} value={code} onChange={setCode} maxLength={40} error={errors.code ?? null} />
        <NamePair label={t('lists.item.label')} value={label} onChange={setLabel} errors={{ en: errors['label.en'], ur: errors['label.ur'] }} />
        <SwitchField label={t('lists.item.active')} isSelected={isActive} onChange={setActive} />
      </div>
    </Dialog>
  );
}

function RenameDialog({ list, onClose, onDone }: { list: OptionListDetail; onClose: () => void; onDone: (answer: OptionListDetail) => void }) {
  const { t } = useText();
  const refused = useRefused();
  const [name, setName] = useState<Names>(list.name);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const submit = async () => {
    if (!(name.en ?? '').trim()) { setErrors({ 'name.en': t('lists.error.name') }); setSummary(t('config.error.fix')); return; }
    setBusy(true);
    try {
      onDone(await optionListsApi.rename(list.id, cleanNames(name), list.rowVersion));
    } catch (error) {
      const r = refused(error);
      setErrors(r.fields);
      setSummary(r.summary);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog title={t('lists.rename')} isOpen onOpenChange={(open) => { if (!open) onClose(); }}
      actions={() => (
        <>
          <Button variant="secondary" onPress={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" isPending={busy} onPress={() => { void submit(); }}>{t('lists.item.save')}</Button>
        </>
      )}>
      <div className="erp-stack">
        {summary && <Banner tone="danger">{summary}</Banner>}
        <NamePair label={t('lists.name')} value={name} onChange={setName} errors={{ en: errors['name.en'], ur: errors['name.ur'] }} />
      </div>
    </Dialog>
  );
}
