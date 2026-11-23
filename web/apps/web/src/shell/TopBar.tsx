// The top bar (slice S1.12b Build items 10, 12 and 13; spec 13 FR-UX-SHL-01, FR-UX-IA-06): product name, the company in
// hand and its switcher (FR-ADM-ORG-06, ruling Q10), the notification bell (FR-HRM-NTF-09, ruling Q16) and the user menu
// (language, theme, density, sign out).
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Bell, Building2, ChevronDown, Menu, UserRound } from 'lucide-react';
import { ActionMenu, Badge, Button, ErrorPanel, IconButton, Link, PopoverPanel, SelectField, Skeleton, useToast } from '@erp/ui';
import { formatDateTime, formatInteger, isolate, useLocaleSettings, useText } from '@erp/i18n';
import { ApiProblem } from '../api/client';
import { notifications, type NotificationItem } from '../api/kernel';
import { useBranding } from '../app/branding';
import { useDisplay } from '../app/display-context';
import type { Density, Theme } from '../app/preferences';
import { safeReturn, useSignOut } from '../app/session-context';
import { companyName, useShell } from './context';

export const unreadKey = ['ntf', 'unread'] as const;
export const latestKey = ['ntf', 'latest'] as const;

export function TopBar({ onOpenMenu, showMenuButton }: { onOpenMenu: () => void; showMenuButton: boolean }) {
  const { t } = useText();
  const { can } = useShell();
  const brand = useBranding();
  return (
    <header className="erp-topbar">
      {showMenuButton && (
        <span className="erp-topbar-menu">
          <IconButton label={t('shell.menu.open')} icon={<Menu size={20} aria-hidden="true" />} onPress={onOpenMenu} />
        </span>
      )}
      <span className="erp-topbar-product">{brand.productName}</span>
      <CompanySwitcher />
      <span className="erp-topbar-spacer" />
      {can('ntf.in_app_items.read') && <NotificationBell />}
      <UserMenu />
    </header>
  );
}

/** The company in hand, always shown; a switcher only for people with more than one company (ruling Q10). */
function CompanySwitcher() {
  const { t } = useText();
  const { me, activeCompany, setActiveCompany } = useShell();
  const { display } = useDisplay();
  const toast = useToast();
  const name = companyName(activeCompany, display.language);
  if (me.companies.length <= 1) {
    return name ? <span className="erp-topbar-company"><Building2 size={18} aria-hidden="true" /><span>{name}</span></span> : null;
  }
  return (
    <ActionMenu
      label={t('shell.company.change', { company: name })}
      trigger={(
        <Button variant="quiet" aria-label={t('shell.company.change', { company: name })} icon={<Building2 size={18} aria-hidden="true" />}>
          <span className="erp-topbar-company-name">{name}</span>
          <ChevronDown size={16} aria-hidden="true" />
        </Button>
      )}
      actions={me.companies.map((c) => ({ id: c.id, label: companyName(c, display.language) }))}
      onAction={(id) => {
        const chosen = me.companies.find((c) => c.id === id) ?? null;
        setActiveCompany(id);
        toast.info(t('shell.company.now', { company: companyName(chosen, display.language) }));
      }} />
  );
}

/** Unread count refreshed every 60 seconds while the tab is visible and on every page change (ruling Q16). */
export function NotificationBell() {
  const { t } = useText();
  const { numbers } = useLocaleSettings();
  const [open, setOpen] = useState(false);
  const unread = useQuery({ queryKey: unreadKey, queryFn: notifications.unreadCount, refetchInterval: 60_000, refetchIntervalInBackground: false });
  const count = unread.data?.unread ?? 0;
  const label = count > 0 ? t('shell.bell.unread', { count: formatInteger(count, numbers) }) : t('shell.bell.label');
  return (
    <PopoverPanel wide label={t('shell.bell.label')} isOpen={open} onOpenChange={setOpen}
      trigger={(
        <span className="erp-bell">
          <IconButton label={label} icon={<Bell size={20} aria-hidden="true" />} />
          <Badge decorative count={count} label={label} />
        </span>
      )}>
      {(close) => <LatestNotifications close={close} />}
    </PopoverPanel>
  );
}

function LatestNotifications({ close }: { close: () => void }) {
  const { t } = useText();
  const client = useQueryClient();
  const latest = useQuery({ queryKey: latestKey, queryFn: () => notifications.list({ limit: 10 }) });
  const open = useOpenNotification();
  const readAll = async () => {
    await notifications.readAll();
    await client.invalidateQueries({ queryKey: ['ntf'] });
  };
  return (
    <div className="erp-stack">
      <div className="erp-row erp-bell-head">
        <h2>{t('shell.bell.label')}</h2>
        {(latest.data?.unread ?? 0) > 0 && <Button variant="quiet" onPress={() => { void readAll(); }}>{t('ntf.readAll')}</Button>}
      </div>
      {latest.isPending && <Skeleton lines={3} />}
      {latest.isError && <ErrorPanel message={t('ntf.loadError')} reference={latest.error instanceof ApiProblem ? latest.error.reference : null} />}
      {latest.data && latest.data.items.length === 0 && <p className="erp-muted">{t('ntf.empty')}</p>}
      {latest.data && latest.data.items.length > 0 && (
        <ul className="erp-ntf-list">
          {latest.data.items.map((item) => (
            <li key={item.id}>
              <NotificationRow item={item} onOpen={() => { close(); void open(item); }} />
            </li>
          ))}
        </ul>
      )}
      <Link href="/notifications" onPress={close}>{t('ntf.seeAll')}</Link>
    </div>
  );
}

/** One notification: title, text and time; unread is said in words and shown in bold, never by colour alone. */
export function NotificationRow({ item, onOpen }: { item: NotificationItem; onOpen: () => void }) {
  const { t } = useText();
  const { locale, timeZone, numbers } = useLocaleSettings();
  const when = formatDateTime(item.createdAt, locale === 'ur' ? 'ur' : 'en', timeZone, numbers);
  return (
    <Button variant="quiet" block onPress={onOpen}>
      <span className="erp-ntf-row" data-unread={item.readAt ? undefined : true}>
        <span className="erp-ntf-title">{item.title}</span>
        <span className="erp-ntf-body">{item.body}</span>
        <span className="erp-ntf-meta">
          {!item.readAt && <span className="erp-ntf-unread">{t('ntf.unread')}</span>}
          <span>{isolate(when)}</span>
        </span>
      </span>
    </Button>
  );
}

/** Opening marks the item read, then follows its link on this site only; the screen's own gate checks the permission (FR-HRM-NTF-07). */
export function useOpenNotification() {
  const navigate = useNavigate();
  const client = useQueryClient();
  return async (item: NotificationItem) => {
    if (!item.readAt) {
      await notifications.markRead(item.id);
      await client.invalidateQueries({ queryKey: ['ntf'] });
    }
    if (item.link) void navigate(safeReturn(item.link));
  };
}

function UserMenu() {
  const { t } = useText();
  const { me } = useShell();
  const { display, setDisplay } = useDisplay();
  const signOut = useSignOut();
  return (
    <PopoverPanel label={t('shell.user.menu')}
      trigger={(
        <Button variant="quiet" aria-label={t('shell.user.open', { name: me.displayName })} icon={<UserRound size={20} aria-hidden="true" />}>
          <span className="erp-topbar-user-name">{me.displayName}</span>
        </Button>
      )}>
      <div className="erp-stack">
        <h2 className="erp-user-name">{me.displayName}</h2>
        <SelectField label={t('language.label')} value={display.language}
          onChange={(v) => setDisplay({ ...display, language: v === 'ur' ? 'ur' : 'en' })}
          options={[{ id: 'en', label: t('language.en') }, { id: 'ur', label: t('language.ur') }]} />
        <SelectField label={t('shell.theme.label')} value={display.theme} onChange={(v) => setDisplay({ ...display, theme: v as Theme })}
          options={(['system', 'light', 'dark', 'contrast'] as const).map((id) => ({ id, label: t(`shell.theme.${id}`) }))} />
        <SelectField label={t('shell.density.label')} value={display.density} onChange={(v) => setDisplay({ ...display, density: v as Density })}
          options={(['compact', 'standard', 'comfortable'] as const).map((id) => ({ id, label: t(`shell.density.${id}`) }))} />
        <Button variant="secondary" block onPress={() => { signOut(); }}>
          {t('home.signOut')}
        </Button>
      </div>
    </PopoverPanel>
  );
}
