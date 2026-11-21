// Navigation pieces of the application shell (slice S1.12b, spec 13 section 5 SHL and FR-UX-IA-01): the side menu list
// (groups with headings, the current page marked, icons that never carry meaning alone) and a popover panel that keeps
// focus inside while open, closes with Escape and returns focus to its button (the notification bell and the user menu).
import type { ReactNode } from 'react';
import { Dialog as AriaDialog, DialogTrigger, Link as AriaLink, Popover } from 'react-aria-components';
import type { LucideIcon } from 'lucide-react';

export interface NavItem {
  id: string;
  href: string;
  label: string;
  icon: LucideIcon;
  current?: boolean;
}

export interface NavSection {
  id: string;
  label: string;
  items: NavItem[];
}

/**
 * The side menu: one list per group under a heading. Collapsed, only the icons show; the label stays the link's
 * accessible name and appears as its tooltip text, so nothing is lost for screen readers.
 */
export function NavList({ label, sections, collapsed = false, onNavigate }: { label: string; sections: NavSection[]; collapsed?: boolean; onNavigate?: () => void }) {
  return (
    <nav className="erp-nav" aria-label={label} data-collapsed={collapsed || undefined}>
      {sections.map((section) => (
        <section key={section.id} className="erp-nav-section" aria-labelledby={`erp-nav-${section.id}`}>
          <h2 id={`erp-nav-${section.id}`} className={collapsed ? 'erp-visually-hidden' : 'erp-nav-heading'}>{section.label}</h2>
          <ul className="erp-nav-list">
            {section.items.map(({ id, href, label: text, icon: Icon, current }) => (
              <li key={id}>
                <AriaLink className="erp-nav-link" href={href} aria-current={current ? 'page' : undefined} aria-label={collapsed ? text : undefined}
                  {...(collapsed ? { title: text } : {})} onPress={onNavigate}>
                  <Icon size={18} aria-hidden="true" />
                  {!collapsed && <span>{text}</span>}
                </AriaLink>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </nav>
  );
}

/** A panel opened by a button (bell, user menu): a labelled dialog in a popover, placed at the inline end. */
export function PopoverPanel({ trigger, label, children, isOpen, onOpenChange, wide = false }: {
  trigger: ReactNode;
  label: string;
  children: ReactNode | ((close: () => void) => ReactNode);
  isOpen?: boolean;
  onOpenChange?: (open: boolean) => void;
  wide?: boolean;
}) {
  return (
    <DialogTrigger {...(isOpen === undefined ? {} : { isOpen, onOpenChange })}>
      {trigger}
      <Popover className={wide ? 'erp-popover erp-panel-popover erp-panel-popover-wide' : 'erp-popover erp-panel-popover'} placement="bottom end">
        <AriaDialog className="erp-popover-dialog" aria-label={label}>
          {({ close }) => (typeof children === 'function' ? children(close) : children)}
        </AriaDialog>
      </Popover>
    </DialogTrigger>
  );
}
