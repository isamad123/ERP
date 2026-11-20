// Overlays and navigation pieces (spec 13 section 7; slice S1.12a Build item 3): tooltip, menu, dialog (focus trap,
// Escape closes, focus returns), drawer and side panel, tabs, page header and breadcrumbs. React Aria supplies the
// keyboard behaviour, focus management and right-to-left placement.
import type { ReactNode } from 'react';
import {
  Breadcrumb, Breadcrumbs as AriaBreadcrumbs, Dialog as AriaDialog, DialogTrigger, Heading, Menu as AriaMenu, MenuItem as AriaMenuItem,
  MenuTrigger, Modal, ModalOverlay, OverlayArrow, Popover, Tab, TabList, TabPanel, Tabs as AriaTabs, Tooltip as AriaTooltip, TooltipTrigger,
  Link as AriaLink, type Key,
} from 'react-aria-components';
import { ChevronRight, MoreHorizontal, X } from 'lucide-react';
import { useText } from '@erp/i18n';
import { IconButton } from './Button';

/** A tooltip that also opens on keyboard focus; never the only place a fact is shown (FR-UX-CMP-11). */
export function Tooltip({ text, children }: { text: string; children: ReactNode }) {
  return (
    <TooltipTrigger delay={500}>
      {children}
      <AriaTooltip className="erp-tooltip" offset={6}>
        <OverlayArrow />
        {text}
      </AriaTooltip>
    </TooltipTrigger>
  );
}

export interface MenuAction {
  id: string;
  label: string;
  icon?: ReactNode;
  isDisabled?: boolean;
}

/** An action menu behind a "more" button (or a given trigger). */
export function ActionMenu({ actions, onAction, label, trigger }: { actions: MenuAction[]; onAction: (id: string) => void; label?: string; trigger?: ReactNode }) {
  const { t } = useText();
  return (
    <MenuTrigger>
      {trigger ?? <IconButton label={label ?? t('ui.menu.more')} icon={<MoreHorizontal size={18} aria-hidden="true" />} />}
      <Popover className="erp-popover" placement="bottom end">
        <AriaMenu className="erp-menu" onAction={(key: Key) => onAction(String(key))} disabledKeys={actions.filter((a) => a.isDisabled).map((a) => a.id)}>
          {actions.map((action) => (
            <AriaMenuItem key={action.id} id={action.id} className="erp-option" textValue={action.label}>
              {action.icon}
              {action.label}
            </AriaMenuItem>
          ))}
        </AriaMenu>
      </Popover>
    </MenuTrigger>
  );
}

export interface DialogProps {
  title: string;
  /** The element that opens the dialog (usually a Button); omit when `isOpen` controls it. */
  trigger?: ReactNode;
  isOpen?: boolean;
  onOpenChange?: (open: boolean) => void;
  /** An alert dialog cannot be closed by clicking outside (decisions the person must answer). */
  role?: 'dialog' | 'alertdialog';
  children: ReactNode | ((close: () => void) => ReactNode);
  actions?: (close: () => void) => ReactNode;
  drawer?: boolean;
}

/** Dialog or drawer: focus is trapped inside, Escape closes (not for alert dialogs), focus returns to the trigger. */
export function Dialog({ title, trigger, isOpen, onOpenChange, role = 'dialog', children, actions, drawer }: DialogProps) {
  const { t } = useText();
  const content = (
    <ModalOverlay className={drawer ? 'erp-modal-overlay erp-drawer-overlay' : 'erp-modal-overlay'} isDismissable={role === 'dialog'}
      isKeyboardDismissDisabled={role === 'alertdialog'} {...(isOpen === undefined ? {} : { isOpen, onOpenChange })}>
      <Modal className={drawer ? 'erp-modal erp-drawer' : 'erp-modal'}>
        <AriaDialog className="erp-dialog" role={role}>
          {({ close }) => (
            <>
              <div className="erp-dialog-header">
                <Heading slot="title">{title}</Heading>
                {role === 'dialog' && <IconButton label={drawer ? t('ui.drawer.close') : t('ui.dialog.close')} icon={<X size={18} aria-hidden="true" />} onPress={close} />}
              </div>
              <div className="erp-stack">{typeof children === 'function' ? children(close) : children}</div>
              {actions && <div className="erp-dialog-actions">{actions(close)}</div>}
            </>
          )}
        </AriaDialog>
      </Modal>
    </ModalOverlay>
  );
  return trigger ? <DialogTrigger>{trigger}{content}</DialogTrigger> : content;
}

export function Tabs({ label, tabs, selectedKey, onSelectionChange }: { label: string; tabs: { id: string; title: string; content: ReactNode }[]; selectedKey?: string; onSelectionChange?: (id: string) => void }) {
  return (
    <AriaTabs className="erp-tabs" {...(selectedKey === undefined ? {} : { selectedKey })} onSelectionChange={(key) => onSelectionChange?.(String(key))}>
      <TabList className="erp-tab-list" aria-label={label}>
        {tabs.map((tab) => <Tab key={tab.id} id={tab.id} className="erp-tab">{tab.title}</Tab>)}
      </TabList>
      {tabs.map((tab) => <TabPanel key={tab.id} id={tab.id}>{tab.content}</TabPanel>)}
    </AriaTabs>
  );
}

export interface Crumb {
  label: string;
  href?: string;
}

/** Breadcrumbs; the separator mirrors in right-to-left layouts (a directional icon). */
export function Breadcrumbs({ items }: { items: Crumb[] }) {
  const { t } = useText();
  return (
    <nav aria-label={t('ui.breadcrumbs')}>
      <AriaBreadcrumbs className="erp-breadcrumbs">
        {items.map((item, index) => (
          <Breadcrumb key={`${index}-${item.label}`} className="erp-breadcrumb">
            {item.href && index < items.length - 1 ? <AriaLink href={item.href}>{item.label}</AriaLink> : <span>{item.label}</span>}
            {index < items.length - 1 && <ChevronRight className="erp-mirror" size={14} aria-hidden="true" />}
          </Breadcrumb>
        ))}
      </AriaBreadcrumbs>
    </nav>
  );
}

export function PageHeader({ title, breadcrumbs, actions, description }: { title: string; breadcrumbs?: Crumb[]; actions?: ReactNode; description?: ReactNode }) {
  return (
    <header className="erp-page-header">
      <div className="erp-stack">
        {breadcrumbs && breadcrumbs.length > 0 && <Breadcrumbs items={breadcrumbs} />}
        <h1>{title}</h1>
        {description && <p className="erp-muted">{description}</p>}
      </div>
      {actions && <div className="erp-row">{actions}</div>}
    </header>
  );
}
