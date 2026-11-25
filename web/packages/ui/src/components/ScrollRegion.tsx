// A region that scrolls sideways (a wide table): a keyboard user must be able to reach it and a screen reader must hear what it is, or its
// content cannot be scrolled without a pointer (WCAG 2.1.1; axe rule scrollable-region-focusable). R-318: one component for every screen's
// wide table instead of a hand-written wrapper per screen. The caller gives the name (the table's own name) from its text catalogue.
import type { ReactNode } from 'react';

export interface ScrollRegionProps {
  /** What the region holds, spoken to a screen-reader user: the table's name. */
  label: string;
  /** The screen's own class for spacing or cell styles; the scrolling itself comes from `erp-scroll-region`. */
  className?: string;
  children: ReactNode;
}

export function ScrollRegion({ label, className, children }: ScrollRegionProps) {
  // A scrolling region is a deliberate tab stop (WCAG 2.1.1), the one case where the rule against tabindex on a plain element does not apply.
  // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex
  return <div className={className ? `erp-scroll-region ${className}` : 'erp-scroll-region'} role="region" tabIndex={0} aria-label={label}>{children}</div>;
}
