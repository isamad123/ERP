// Buttons and links (spec 13 FR-UX-CMP-01, -02, -11; slice S1.12a Build item 3). Built on React Aria, so press,
// keyboard, focus-visible and pending states work the same everywhere.
import type { ReactNode } from 'react';
import { Button as AriaButton, Link as AriaLink, type ButtonProps as AriaButtonProps, type LinkProps as AriaLinkProps } from 'react-aria-components';
import { useText } from '@erp/i18n';
import { Spinner } from './Feedback';

export type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'quiet';

export interface ButtonProps extends Omit<AriaButtonProps, 'className' | 'style' | 'children'> {
  variant?: ButtonVariant;
  /** Takes the full width of its container (forms on phones). */
  block?: boolean;
  icon?: ReactNode;
  children: ReactNode;
}

export function Button({ variant = 'secondary', block, icon, children, isPending, ...props }: ButtonProps) {
  return (
    <AriaButton {...props} isPending={isPending} className="erp-button" data-variant={variant} data-block={block || undefined}>
      {isPending ? <Spinner size="small" immediate /> : icon}
      <span>{children}</span>
    </AriaButton>
  );
}

export interface IconButtonProps extends Omit<AriaButtonProps, 'className' | 'style' | 'children' | 'aria-label'> {
  /** The accessible name (never empty: an icon alone says nothing to a screen reader). */
  label: string;
  icon: ReactNode;
  variant?: ButtonVariant;
}

export function IconButton({ label, icon, variant = 'quiet', ...props }: IconButtonProps) {
  return (
    <AriaButton {...props} aria-label={label} className="erp-button erp-icon-button" data-variant={variant}>
      {icon}
    </AriaButton>
  );
}

export interface LinkProps extends Omit<AriaLinkProps, 'className' | 'style'> {
  children: ReactNode;
}

/** A link; inside the application the router handles `href` (RouterProvider in the app). */
export function Link({ children, ...props }: LinkProps) {
  return (
    <AriaLink {...props} className="erp-link">
      {children}
    </AriaLink>
  );
}

/** A copy-to-clipboard button that says "Copied" for a moment (recovery codes, keys). */
export function CopyButton({ text, label }: { text: string; label?: string }) {
  const { t } = useText();
  return (
    <Button variant="secondary" onPress={() => { void navigator.clipboard?.writeText(text); }}>
      {label ?? t('common.copy')}
    </Button>
  );
}
