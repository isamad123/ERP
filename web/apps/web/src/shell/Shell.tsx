// The application shell (slice S1.12b Build item 10; spec 13 section 5 SHL, FR-UX-A11Y): skip link, top bar, collapsible
// side menu (a drawer on phones), licence banners and the content area with its own main landmark. While the licence is
// locked the menu is not offered: every address shows the locked page except the licence page and exports (ruling Q17).
import { useEffect, useRef, useState } from 'react';
import { Outlet, useLocation } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { PanelLeftClose, PanelLeftOpen } from 'lucide-react';
import { Dialog, IconButton, NavList, type NavSection } from '@erp/ui';
import { useText } from '@erp/i18n';
import { hrefOf, menuFor, type ScreenDeclaration } from '../navigation/screens';
import { LicenceBanners } from './Banners';
import { ShellDataProvider, useShell } from './context';
import { TopBar, unreadKey } from './TopBar';

export function ShellLayout({ screens }: { screens: readonly ScreenDeclaration[] }) {
  return (
    <ShellDataProvider>
      <ShellFrame screens={screens} />
    </ShellDataProvider>
  );
}

function ShellFrame({ screens }: { screens: readonly ScreenDeclaration[] }) {
  const { t } = useText();
  const { facts } = useShell();
  const location = useLocation();
  const client = useQueryClient();
  const main = useRef<HTMLElement>(null);
  const first = useRef(true);
  const [drawer, setDrawer] = useState(false);
  const [collapsed, setCollapsed] = useState(false);
  const locked = facts.state === 'locked';

  // Every page change refreshes the bell (ruling Q16) and moves focus to the new content for keyboard and screen readers.
  useEffect(() => {
    void client.invalidateQueries({ queryKey: unreadKey });
    if (first.current) {
      first.current = false;
      return;
    }
    main.current?.focus();
  }, [location.pathname, client]);

  const sections: NavSection[] = menuFor(screens, facts).map((section) => ({
    id: section.group,
    label: t(`nav.group.${section.group}`),
    items: section.entries.map((screen) => {
      const href = hrefOf(screen);
      return {
        id: screen.id,
        href,
        label: t(screen.labelKey),
        icon: screen.icon,
        current: href === '/' ? location.pathname === '/' : location.pathname === href || location.pathname.startsWith(`${href}/`),
      };
    }),
  }));

  return (
    <div className="erp-shell" data-collapsed={collapsed || undefined}>
      <a className="erp-skip-link" href="#main">{t('shell.skip')}</a>
      <TopBar onOpenMenu={() => setDrawer(true)} showMenuButton={!locked} />
      <div className="erp-shell-body">
        {!locked && (
          <aside className="erp-shell-side">
            <NavList label={t('shell.menu.label')} sections={sections} collapsed={collapsed} />
            <div className="erp-shell-collapse">
              <IconButton label={collapsed ? t('shell.menu.expand') : t('shell.menu.collapse')} aria-expanded={!collapsed}
                icon={collapsed ? <PanelLeftOpen className="erp-mirror" size={18} aria-hidden="true" /> : <PanelLeftClose className="erp-mirror" size={18} aria-hidden="true" />}
                onPress={() => setCollapsed((c) => !c)} />
            </div>
          </aside>
        )}
        {!locked && (
          <Dialog drawer side="start" title={t('shell.menu.label')} isOpen={drawer} onOpenChange={setDrawer}>
            <NavList label={t('shell.menu.label')} sections={sections} onNavigate={() => setDrawer(false)} />
          </Dialog>
        )}
        <main id="main" ref={main} tabIndex={-1} className="erp-shell-main">
          {!locked && <LicenceBanners />}
          <Outlet />
        </main>
      </div>
    </div>
  );
}
