// The home page "Waiting for you" (slice S1.12b Build item 11, S1.12 ruling Q8): approvals waiting for the person and
// unread notifications, and nothing else until the role home pages (FR-HRM-SSP-06, release 1.1).
import { useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Bell, ClipboardCheck, ListChecks } from 'lucide-react';
import { ErrorPanel, Link, PageHeader, Skeleton } from '@erp/ui';
import { formatInteger, useLocaleSettings, useText, type MessageKey } from '@erp/i18n';
import { ApiProblem } from '../api/client';
import { kernel, notifications } from '../api/kernel';
import { setupApi, stepsOpen, untouched } from '../api/setup';
import { useNavigate } from 'react-router';
import { useShell } from '../shell/context';
import { unreadKey } from '../shell/TopBar';
import { usePageTitle } from './errors';

/** The guide is offered once per page load (a person who leaves it is not sent back at every visit to the home page). */
const offered = { done: false };

export function HomePage() {
  const { t } = useText();
  const { can, me } = useShell();
  usePageTitle(t('home.title'));
  const approvals = can('wf.workflow_tasks.read');
  const notices = can('ntf.in_app_items.read');
  const counts = useQuery({ queryKey: ['wf', 'counts'], queryFn: kernel.inboxCounts, enabled: approvals });
  const unread = useQuery({ queryKey: unreadKey, queryFn: notifications.unreadCount, enabled: notices });
  // S1.18b (ruling Q1): a tenant administrator sees the set-up guide as a card until every step is done or skipped, and is taken to it once
  // when nothing has been touched yet (the first sign-in). Other people never see it: the permission decides.
  const setup = can('cfg.setup_tasks.update');
  const guide = useQuery({ queryKey: ['cfg', 'readiness', me.defaultCompanyId ?? ''], queryFn: () => setupApi.overview(me.defaultCompanyId ?? me.companies[0]?.id ?? null), enabled: setup, retry: false });
  const navigate = useNavigate();
  const waiting = guide.data ? stepsOpen(guide.data.steps) : 0;
  useEffect(() => {
    if (guide.data && untouched(guide.data.steps) && !offered.done) { offered.done = true; void navigate('/setup'); }
  }, [guide.data, navigate]);
  const pending = (approvals && counts.isPending) || (notices && unread.isPending);
  const failed = (approvals && counts.isError ? counts.error : null) ?? (notices && unread.isError ? unread.error : null);

  return (
    <div className="erp-page">
      <PageHeader title={t('home.title')} description={t('home.greeting', { name: me.displayName })} />
      {pending && <Skeleton lines={3} />}
      {failed && <ErrorPanel message={t('home.loadError')} reference={failed instanceof ApiProblem ? failed.reference : null} />}
      {!pending && !failed && (
        <ul className="erp-tiles">
          {approvals && counts.data && (
            <Tile icon={<ClipboardCheck size={24} aria-hidden="true" />} count={counts.data.total + counts.data.changeApprovals}
              labelKey="home.approvals" emptyKey="home.approvals.none" href="/approvals" linkKey="home.approvals.open" />
          )}
          {notices && unread.data && (
            <Tile icon={<Bell size={24} aria-hidden="true" />} count={unread.data.unread}
              labelKey="home.unread" emptyKey="home.unread.none" href="/notifications" linkKey="home.unread.open" />
          )}
          {setup && waiting > 0 && (
            <Tile icon={<ListChecks size={24} aria-hidden="true" />} count={waiting} labelKey="home.setup.body" emptyKey="home.setup.title" href="/setup" linkKey="home.setup.open" />
          )}
          {!approvals && !notices && !(setup && waiting > 0) && <li className="erp-tile erp-muted">{t('home.nothing')}</li>}
        </ul>
      )}
    </div>
  );
}

function Tile({ icon, count, labelKey, emptyKey, href, linkKey }: { icon: React.ReactNode; count: number; labelKey: MessageKey; emptyKey: MessageKey; href: string; linkKey: MessageKey }) {
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  return (
    <li className="erp-tile">
      {icon}
      <h2>{count > 0 ? t(labelKey, { count: formatInteger(count, numbers), n: count }) : t(emptyKey)}</h2>
      <Link href={href}>{t(linkKey)}</Link>
    </li>
  );
}
