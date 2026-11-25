import type { ReactNode } from 'react';

/** A table that may scroll sideways: a named region reachable by keyboard, so its rows can be scrolled without a pointer (WCAG 2.1.1, axe scrollable-region-focusable). */
export function ScrollRegion({ label, children }: { label: string; children: ReactNode }) {
  return (
    // A scrolling region must be reachable by keyboard even when nothing inside it is focusable.
    // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex
    <div className="erp-live-scroll" role="region" aria-label={label} tabIndex={0}>{children}</div>
  );
}
