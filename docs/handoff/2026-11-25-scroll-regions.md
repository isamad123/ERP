# Scroll regions as one component (R-318)

**What.** A table wider than the screen sits in `ScrollRegion` (`web/packages/ui/src/components/ScrollRegion.tsx`): a `div` with `role="region"`, `tabIndex=0`, the table's own name as `aria-label`, class `erp-scroll-region` (sideways scroll, focus ring from the tokens) and the screen's own class for spacing. 39 hand-written wrappers in 26 screen files now use it (admin, HR policies, positions, payroll calendar, attendance, leave); the names are the table names the screens already had, so there are no new texts.

**Why.** A scrolling region that holds nothing focusable cannot be reached by keyboard (WCAG 2.1.1; axe `scrollable-region-focusable`). It showed only when the data was wide enough, so the browser tests failed now and then on attendance reports (R-318).

**Guards.** `apps/web/src/scroll-regions.test.ts` fails when a screen file has a `div` with a "scroll" class instead of the component (the leave team calendar grid is the one exception: its cells take focus themselves) or a `ScrollRegion` without a label; `ScrollRegion.test.tsx` checks the component; the component catalogue has a wide table and `e2e/sign-in.spec.ts` checks it scrolls, has the region markup and takes focus, then axe scans the catalogue in six display modes. Not done: an axe scan of every screen at a data size that makes it scroll (each needs its own seed data); the static guard replaces it.

**Writing screens.** Wrap a wide table in `<ScrollRegion label={t('…table name…')} className="erp-xxx">`; never a plain scrolling div. Shell stays at 265.7 KB (budget 285).
