// The notification centre page (slice S1.12b Build item 13; FR-HRM-NTF-09, FR-HRM-NTF-07; S1.12 ruling Q16): the person's
// notifications with search, a group filter, unread only, paging, read and unread, mark all read, and links that open
// their screen after the screen's own permission check. Preferences and quiet hours come with self-service (S1.26).
import { useState, type FormEvent } from 'react';
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, EmptyState, ErrorPanel, PageHeader, SelectField, Skeleton, SwitchField, TextField } from '@erp/ui';
import { formatInteger, useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../api/client';
import { notifications, type NotificationItem } from '../api/kernel';
import { NotificationRow, useOpenNotification } from '../shell/TopBar';
import { usePageTitle } from './errors';

const PAGE = 20;
// Groups of the notification catalogue (S1.11b1, notification-events.json).
const GROUPS = ['approvals', 'my_requests', 'documents', 'system'] as const;

export default function NotificationsPage() {
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  usePageTitle(t('ntf.title'));
  const client = useQueryClient();
  const open = useOpenNotification();
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const [group, setGroup] = useState('all');
  const [unreadOnly, setUnreadOnly] = useState(false);
  const [offset, setOffset] = useState(0);
  const filters = { search, group: group === 'all' ? undefined : group, unread: unreadOnly, limit: PAGE, offset };
  const page = useQuery({ queryKey: ['ntf', 'page', filters], queryFn: () => notifications.list(filters), placeholderData: keepPreviousData });

  const refresh = () => client.invalidateQueries({ queryKey: ['ntf'] });
  const submit = (event: FormEvent) => {
    event.preventDefault();
    setSearch(draft.trim());
    setOffset(0);
  };
  const toggle = async (item: NotificationItem) => {
    await (item.readAt ? notifications.markUnread(item.id) : notifications.markRead(item.id));
    await refresh();
  };

  const data = page.data;
  const from = data && data.total > 0 ? offset + 1 : 0;
  const to = data ? offset + data.items.length : 0;
  return (
    <div className="erp-page">
      <PageHeader title={t('ntf.title')}
        actions={data && data.unread > 0 ? <Button variant="secondary" onPress={() => { void notifications.readAll().then(refresh); }}>{t('ntf.readAll')}</Button> : undefined} />
      <form className="erp-filters" role="search" aria-label={t('ntf.filters')} onSubmit={submit}>
        <TextField type="search" label={t('ntf.search')} value={draft} onChange={setDraft} />
        <Button type="submit" variant="secondary">{t('ntf.searchButton')}</Button>
        <SelectField label={t('ntf.group')} value={group} onChange={(v) => { setGroup(v); setOffset(0); }}
          options={[{ id: 'all', label: t('ntf.group.all') }, ...GROUPS.map((g) => ({ id: g, label: t(`ntf.group.${g}`) }))]} />
        <SwitchField label={t('ntf.unreadOnly')} isSelected={unreadOnly} onChange={(v) => { setUnreadOnly(v); setOffset(0); }} />
      </form>
      {page.isPending && <Skeleton lines={5} />}
      {page.isError && <ErrorPanel message={t('ntf.loadError')} reference={page.error instanceof ApiProblem ? page.error.reference : null}
        action={<Button variant="primary" onPress={() => { void page.refetch(); }}>{t('common.retry')}</Button>} />}
      {data && data.items.length === 0 && (
        <EmptyState title={search || group !== 'all' || unreadOnly ? t('ntf.noMatches') : t('ntf.empty')}>
          {t('ntf.empty.body')}
        </EmptyState>
      )}
      {data && data.items.length > 0 && (
        <>
          <ul className="erp-ntf-list erp-ntf-page">
            {data.items.map((item) => (
              <li key={item.id} className="erp-ntf-entry">
                <NotificationRow item={item} onOpen={() => { void open(item); }} />
                <Button variant="quiet" onPress={() => { void toggle(item); }}>{item.readAt ? t('ntf.markUnread') : t('ntf.markRead')}</Button>
              </li>
            ))}
          </ul>
          <nav className="erp-row erp-pager" aria-label={t('ntf.paging')}>
            <span className="erp-muted" aria-live="polite">
              {t('ntf.showing', { from: formatInteger(from, numbers), to: formatInteger(to, numbers), total: formatInteger(data.total, numbers) })}
            </span>
            <Button variant="secondary" isDisabled={offset === 0} onPress={() => setOffset(Math.max(0, offset - PAGE))}>{t('ntf.previous')}</Button>
            <Button variant="secondary" isDisabled={to >= data.total} onPress={() => setOffset(offset + PAGE)}>{t('ntf.next')}</Button>
          </nav>
        </>
      )}
    </div>
  );
}
