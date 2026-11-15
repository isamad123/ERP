# UX and Design System Principles (v0.1)

Document 13 of the planning set | Prepared for Ibrahim | 10 November 2026 | Companion to the Architecture (section 4.8), Platform Kernel, Admin Console and all module specifications

## 1. Introduction

### 1.1 Purpose and scope

This document defines how the product looks, feels and behaves for the people who use it every day: the principles, the design system (tokens, components, patterns), rules for data-heavy screens, internationalization and right-to-left design, accessibility, mobile and offline behavior, data visualization, content style, trust and security cues, and the way AI features appear. It also tells Claude Code how to build the front end so that hundreds of screens stay consistent.

It covers the web application, the supplier, customer and carrier portals, the admin consoles, and the mobile apps. Module-specific screens are described in module specifications; this document defines the rules every screen follows.

### 1.2 Why it matters for this product

An ERP is judged by daily use: accountants entering hundreds of journal lines, storekeepers scanning in a noisy warehouse, managers approving on a phone, administrators configuring workflows. Three facts shape the design:

1. Most screens are metadata-driven (Architecture section 4.8), so consistency must come from the design system and rendering rules, not from hand-built pages.
2. Customers work in English, Urdu and Arabic, so right-to-left layout, Hijri dates and Arabic-script typography are core requirements, not extras.
3. On-premise customers have no internet dependency, so every font, icon, script and map asset must ship with the product.

### 1.3 Conventions

- Requirement IDs: FR-UX-\<area>-\<number>. Priority (M), (S), (C); unmarked is (M). Wave A unless stated.
- Each area states Requirements and, where useful, Acceptance Criteria (AC).
- Numeric values (sizes, contrast ratios, time limits) are defaults to be validated in usability testing and recorded in the design tokens.
- Design decisions that need validation with real users (for example Urdu typeface choice) are listed in section 26.

### 1.4 How Claude Code uses this document

A short version of the rules in sections 3, 6, 9, 11, 12 and 25 goes into the front-end CLAUDE.md (document 12, section 4). Every UI slice includes the screen acceptance checklist of section 25 and the UX review prompt of document 12, section 8.5.

## 2. Users and contexts

### 2.1 Primary user groups

| Group | Typical tasks | Design implications |
| --- | --- | --- |
| Finance and back-office staff | Journals, invoices, reconciliation, payroll runs, high-volume data entry | Keyboard-first, dense grids, fast, predictable, undo, minimal mouse travel |
| Managers and approvers | Approve requests, review team and budgets, dashboards | Mobile-friendly, clear summaries, one-tap decisions with context |
| Employees and self-service users | Leave, attendance, payslips, requests | Simple, friendly, mobile first, language choice, low training |
| Warehouse and field staff | Receiving, picking, counting, delivery, maintenance, field sales | Handheld and phone, large touch targets, scanning, offline, bright or dim environments |
| Administrators and implementation consultants | Configuration, workflows, roles, imports | Guided designers, previews, safe change, searchable settings |
| Executives | Scorecards, exceptions, approvals | Glanceable, trustworthy numbers, narrative summaries |
| External users (suppliers, customers, carriers) | Quotes, orders, invoices, delivery updates | Focused portals, clear status, strict isolation, minimal navigation |
| Auditors | Evidence, history, drill-down | Traceability, filters, exports, no editing surprises |

### 2.2 Contexts to design for

- Languages: English, Urdu and Arabic, often within the same screen (Arabic-script text next to Latin identifiers and numbers).
- Devices: desktop and laptop (primary for office work), tablet, phone, rugged handhelds, shared kiosks.
- Networks: fast office networks and slow or intermittent mobile connections; offline capability for warehouse, field sales, attendance, delivery and maintenance.
- Environments: shared computers, printing and paper documents, glare and low light, gloves and one-handed use.
- Skills: users with limited digital experience and expert power users in the same tenant.

## 3. Design principles

1. **Speed of work first.** Every frequent task is optimized for few keystrokes, no unnecessary modals, and instant feedback.
2. **Predictable and consistent.** The same pattern looks and behaves the same in every module; users learn once.
3. **Show the next step.** Screens make the next action obvious; status and what is blocking progress are always visible.
4. **Safe by default.** Destructive or wide-impact actions state their impact, can be undone where possible, and are confirmed proportionately.
5. **Explain, do not just inform.** Numbers, statuses and denials come with reasons and traces (why this price, why this approver, why this field is hidden).
6. **Dense but calm.** Data screens are information-rich but visually quiet: strong alignment, restrained color, clear hierarchy.
7. **Language and direction are first-class.** Layouts mirror correctly; text, numbers and dates render properly in every supported language and calendar.
8. **Accessible to everyone.** Keyboard, screen reader and contrast requirements are met from the start, not retrofitted.
9. **Works anywhere.** Responsive layouts, touch targets and offline behavior for the contexts that need them.
10. **Configurable without chaos.** Tenants can adapt fields, layouts, terminology and branding inside guardrails that keep screens usable and consistent.
11. **Trustworthy AI.** AI is visible, labeled, reversible and always subordinate to the user's decision.
12. **No dependency on the outside.** Fonts, icons, scripts and maps ship with the product for on-premise operation.

## 4. Information architecture and navigation (IA)

- **FR-UX-IA-01** Navigation shall be organized by business area (module groups such as Finance, People, Purchasing, Inventory, Assets, Sales, Supply Chain, Analytics, Administration) with a second level of work areas and a third of screens, driven by the module manifests, role permissions and entitlements; items the user cannot use shall not appear.
- **FR-UX-IA-02** Each role shall have a home page with tasks (approvals, exceptions, assigned work), key figures and shortcuts, configurable by administrators and personalizable within limits.
- **FR-UX-IA-03** A global search and command palette (keyboard shortcut, available on every screen) shall find records (documents, customers, items, employees), screens, reports, settings and help in any supported language, and run actions (create invoice, go to ledger), respecting permissions.
- **FR-UX-IA-04** Users shall have recents, favorites and pinned views, and breadcrumbs shall show location and allow quick return from deep screens.
- **FR-UX-IA-05** Entity pages (customer, supplier, item, employee, asset) shall follow one structure: summary header with status and key facts, tabs for related information and documents, activity and history, and quick actions; related documents link through the document-flow view.
- **FR-UX-IA-06** A company switcher and, where used, an environment indicator (production, UAT, sandbox) shall be always visible and shall change context safely with clear confirmation of what changed.
- **FR-UX-IA-07** Deep links shall open any record or view in the right context and respect permissions, including links from emails, notifications and mobile pushes.
- **FR-UX-IA-08** Terminology shall follow the tenant's label overrides (kernel LOC and MET) consistently across menus, headings, columns and documents.
- **AC:** a user finds a specific purchase order by partial supplier name in Urdu through the command palette, opens it, sees the document flow from requisition to payment, and returns by breadcrumb in two clicks.

## 5. Application shell and layout (SHL)

- **FR-UX-SHL-01** The web application shall use a persistent shell with a top bar (global search, company and environment switchers, notifications, assistant, user menu), a collapsible side navigation, and a content area with a consistent page header (title, status, primary actions, breadcrumbs).
- **FR-UX-SHL-02** Page layouts shall use a 12-column responsive grid with defined breakpoints (phone under 600 pixels, tablet 600 to 1023, laptop 1024 to 1439, wide 1440 and above) and content max widths for readability on forms (not for data grids).
- **FR-UX-SHL-03** Primary actions shall be placed consistently (top right in left-to-right, top left in right-to-left), with a sticky action bar on long forms and document pages, and keyboard shortcuts for save, submit, search and navigation.
- **FR-UX-SHL-04** Side panels (details, history, comments, assistant) shall open without leaving the page, be dismissible with the keyboard, and remember their state.
- **FR-UX-SHL-05** Modals shall be reserved for short, focused decisions; complex tasks use pages or wizards; modals shall trap focus, close with the escape key, and never hide validation errors.
- **FR-UX-SHL-06** Density modes (compact, standard, comfortable) shall be user-selectable and defaulted by role (compact for finance grids, comfortable for self-service and mobile).
- **FR-UX-SHL-07** The shell shall show system status (offline, syncing, maintenance, license warnings, read-only state) clearly and unobtrusively.
- **FR-UX-SHL-08** Session handling shall warn before timeout, save drafts, and return users to where they were after re-authentication.

## 6. Design tokens and theming (TOK)

All visual values come from design tokens implemented as CSS variables, so themes, density, branding and right-to-left changes need no component rewrites.

### 6.1 Token set

| Token group | Defaults (to validate) |
| --- | --- |
| Spacing | 4-pixel base scale (4, 8, 12, 16, 24, 32, 48, 64) |
| Radius | 2, 4, 8 pixels for controls, cards and dialogs |
| Elevation | Three levels (flat, raised, overlay) with subtle shadows; borders preferred over shadows in data screens |
| Typography scale | 12, 13, 14 (base), 16, 20, 24, 32 pixels; line heights 1.4 to 1.6; numeric text uses tabular figures |
| Control heights | Compact 28, standard 36, comfortable 44 pixels (touch controls at least 44) |
| Grid row heights | Compact 32, standard 40, comfortable 48 pixels |
| Motion | 100 to 200 milliseconds for feedback, ease-out; reduced-motion setting removes non-essential animation |
| Z-index layers | Defined layers for header, side panels, menus, modals, toasts |

### 6.2 Color

- **FR-UX-TOK-01** A neutral base palette (greys with slight warmth or coolness by theme), one primary brand color, and semantic colors (success, warning, danger, info) shall be defined as tokens with light and dark variants.
- **FR-UX-TOK-02** All text and essential icons shall meet a contrast ratio of at least 4.5 to 1 and large text and interface boundaries at least 3 to 1 in every theme; the build shall fail on token pairs below these limits.
- **FR-UX-TOK-03** Color shall never be the only carrier of meaning; statuses use icon, text or shape in addition to color.
- **FR-UX-TOK-04** Status colors shall be consistent across modules (for example draft grey, pending amber, approved green, rejected red, blocked red with lock icon, on hold amber with pause icon) and defined once in the status model tokens.

### 6.3 Typography and fonts

- **FR-UX-TOK-10** Fonts shall be bundled with the product (no external font services), licensed under open licenses verified under decision D-09, and loaded with fallbacks and subsetting for performance.
- **FR-UX-TOK-11** Latin script shall use a legible sans-serif UI font with tabular numerals; Arabic script shall use a Naskh-style or modern sans Arabic font designed for screens; Urdu shall offer a Naskh-style default for dense UI and an optional Nastaliq face for documents and long reading, selectable per tenant (decision to validate with Urdu-speaking users, section 26).
- **FR-UX-TOK-12** Line height, letter spacing and font size shall adapt per script (Arabic-script text typically needs more line height; Nastaliq needs substantially more), through script-specific tokens.
- **FR-UX-TOK-13** Monospaced fonts shall be used for codes and technical identifiers where alignment matters, with bidirectional isolation so identifiers read correctly inside right-to-left text.

### 6.4 Theming and branding

- **FR-UX-TOK-20** Light and dark themes shall be supported and follow the user's system setting by default, with a high-contrast theme for accessibility.
- **FR-UX-TOK-21** Tenants shall be able to brand logo, primary color, login page, email sender and document letterheads; the system shall check chosen brand colors against contrast rules and adjust or reject failing combinations, and shall keep semantic status colors fixed.
- **FR-UX-TOK-22** Theme, density, language and direction settings shall be stored per user and applied before first paint to avoid flashes.
- **AC:** switching a tenant's brand color keeps all text readable in light and dark themes, and a build with a token pair below the contrast limit fails the pipeline.

## 7. Component library (CMP)

### 7.1 Inventory

| Group | Components |
| --- | --- |
| Foundations | Button, icon button, link, badge and status chip, tag, tooltip, popover, menu, tabs, accordion, breadcrumb, pagination, stepper, avatar, card, divider, skeleton, spinner, progress, toast, banner, dialog, drawer and bottom sheet |
| Form controls | Text, number, money with currency, percentage, date (Gregorian and Hijri, range), time, date-time with time zone, select, combobox with asynchronous lookup, multi-select, checkbox, radio, switch, text area, limited rich text, file and image upload with camera, signature, barcode and QR scan input, phone with country, address, one-time code |
| Data display | Data grid, tree and hierarchy, pivot table, board (kanban), calendar and scheduler, timeline, organization chart, document and image viewer, map, comments and activity feed, approval timeline, diff viewer |
| Charts | Wrappers around the charting library for the chart types in document 9, section 6, with shared theming, accessibility and drill-down behavior |
| Layout | Shell, page header, split view, side panel, sticky action bar, responsive grid |
| ERP-specific | Journal and document line editor, dimension picker, account picker, partner picker, item picker with stock and price context, quantity with unit-of-measure input, tax summary and totals block, approval bar, document-flow view, history and audit panel, status stepper, attachments panel, assistant panel, AI proposal diff |

### 7.2 Requirements

- **FR-UX-CMP-01** Every component shall be documented in a component catalog with purpose, anatomy, variants, states (default, hover, focus, pressed, disabled, read-only, loading, error), keyboard behavior, right-to-left behavior, accessibility notes, content rules, and usage dos and donts, and shall have visual and accessibility tests.
- **FR-UX-CMP-02** Components shall be built on open-source accessible primitives and the product's tokens; a component that duplicates an existing one shall not be added without a recorded decision.
- **FR-UX-CMP-03** The design system shall be a versioned package consumed by all front ends (web, portals, admin, mobile web views), with change notes and migration guidance for breaking changes.
- **FR-UX-CMP-04** Money inputs shall hold exact decimal values (no floating point), show the currency, apply the currency's decimals and rounding, accept pasted values in common formats, display negatives by tenant or pack convention (minus sign or parentheses), and never lose precision between entry, API and storage.
- **FR-UX-CMP-05** Quantity inputs shall show the unit of measure, convert between units where allowed, and respect rounding rules and decimal places by unit.
- **FR-UX-CMP-06** Lookup controls shall search by code or name in any supported language with typeahead and debounce, show secondary information (code, status, balance or stock where relevant), warn on inactive or blocked choices, support recent items, allow inline creation where permitted, and be fully operable by keyboard.
- **FR-UX-CMP-07** Date controls shall accept typed dates in multiple formats, offer a picker for Gregorian and Hijri calendars (selectable, with both shown when configured), support ranges and relative dates, and flag dates in locked periods or holidays where relevant.
- **FR-UX-CMP-08** The account, dimension and partner pickers shall support hierarchical browsing, favorites, validity dates, and permission-based filtering, and shall warn when a combination is invalid before submission.
- **FR-UX-CMP-09** Status chips shall come from the status models with consistent colors, icons and labels in all languages (section 6.2).
- **FR-UX-CMP-10** Components shall support the density modes and themes without per-component code, and shall render correctly in right-to-left layouts with logical properties.
- **FR-UX-CMP-11** Components shall never rely on hover alone: every hover action has a keyboard and touch equivalent.
- **FR-UX-CMP-12** Components that show sensitive data shall support masking, reveal with logging, and copy restrictions as configured by field security.

## 8. Data grid (GRD)

The grid is the most-used component in the product; it carries lists, document lines, reports and imports.

- **FR-UX-GRD-01** The grid shall virtualize rows and columns, load data by server-side cursor pagination with infinite scroll or page controls, and remain responsive with at least 100,000 rows available and 50 columns.
- **FR-UX-GRD-02** Columns shall be resizable, reorderable, pinnable, hideable and groupable, with saved column sets, and with type-appropriate alignment (numbers and money right-aligned in left-to-right and mirrored in right-to-left, using tabular figures).
- **FR-UX-GRD-03** Sorting shall support multi-column sort; filtering shall provide column filters by type (text, number range, date range with relative dates, select, status, boolean, custom field) and an advanced filter builder with grouped conditions, using the platform filter syntax.
- **FR-UX-GRD-04** Saved views (filters, sort, columns, grouping, density) shall be saveable per user and shareable by role or company, with a default view per list.
- **FR-UX-GRD-05** Inline editing shall be available where permitted: cell editing with validation, tab and enter navigation across cells, add and duplicate rows, fill down, paste from spreadsheets with validation preview, and undo for the last operations. (undo, S)
- **FR-UX-GRD-06** Keyboard navigation shall follow spreadsheet conventions (arrow keys, home and end, page keys, enter to edit, escape to cancel, shortcuts for copy and paste, select all, insert row, delete row), independent of keyboard layout.
- **FR-UX-GRD-07** Selection shall support single, multiple and range selection with a persistent bulk-action bar showing the count and applicable actions, and a clear way to select all matching records (not just the loaded page) with explicit confirmation of the number.
- **FR-UX-GRD-08** Totals, subtotals and aggregates shall be available in footers and group headers, computed server-side for large sets, with locale and currency formatting.
- **FR-UX-GRD-09** Row details shall be available through expandable rows, a side panel, or a navigation to the record, configurable per list.
- **FR-UX-GRD-10** Conditional formatting (row and cell highlights by rule), status chips, inline sparklines and progress bars shall be supported within accessibility rules.
- **FR-UX-GRD-11** Export shall offer the current view (visible columns and filters), selected rows, or all matching rows as an asynchronous job, honoring export controls and field security (document 9, section 10).
- **FR-UX-GRD-12** On phones the grid shall switch to a card or list layout with the most important columns and expandable details, preserving filter and sort.
- **FR-UX-GRD-13** The grid shall expose correct grid semantics to assistive technology, announce sorting, filtering and loading changes, support focus management in editable cells, and provide a non-grid fallback where a screen reader needs it.
- **FR-UX-GRD-14** Empty, loading and error states shall explain what is happening and offer actions (clear filters, retry, create).
- **FR-UX-GRD-15** Performance budget: initial render under 1 second for a page of 100 rows and 20 columns, scrolling at 60 frames per second on a mid-range laptop, filter response under 1 second for 1 million server-side rows with indexes.
- **FR-UX-GRD-16** The grid shall be chosen from open-source components under permissive licenses (decision D-09), wrapped by the product's own API so it can be replaced without changing screens.
- **AC:** an accountant opens a ledger list with 2 million rows, filters by account and period, groups by cost center, sees subtotals, saves the view for the team, edits a narration cell with the keyboard, and exports the view as a job.

## 9. Forms and data entry (FRM)

- **FR-UX-FRM-01** Forms shall use a consistent layout: labels above fields on narrow screens and beside or above on wide ones by density, required markers, units and currency next to values, helper text under fields, and logical grouping with section headings; placeholders shall never replace labels.
- **FR-UX-FRM-02** Validation shall run on field exit and on submit; messages shall be specific, in the user's language, shown next to the field and in a summary with links, announced to assistive technology, and mapped from server responses (problem details) to fields.
- **FR-UX-FRM-03** Forms shall be keyboard-first: logical tab order, enter moves to the next field where safe, shortcuts for save, submit, add line, delete line, search and help, and visible focus at all times.
- **FR-UX-FRM-04** Drafts shall autosave, indicate saved state, warn before navigation with unsaved changes, and recover after a lost connection or timeout.
- **FR-UX-FRM-05** Smart defaults shall prefill from context (company, currency, warehouse, last used, customer or supplier defaults) with a visible indication that a value was defaulted and an easy way to change it.
- **FR-UX-FRM-06** Document forms shall have a header, a lines grid, a totals block that updates live, an approval bar, a document-flow view and a history panel, as in section 10.2; lines shall support copy, paste, import and keyboard entry at spreadsheet speed.
- **FR-UX-FRM-07** Conditional fields and sections from configuration (kernel MET) shall appear without layout jumps, with focus preserved.
- **FR-UX-FRM-08** Concurrent edits shall be detected through the row version; the conflict view shall show the user's changes next to the current saved version with merge or overwrite options and the author and time of the other change.
- **FR-UX-FRM-09** Attachments shall support drag and drop, paste, camera capture on mobile, type and size validation with clear messages, previews, and progress with retry.
- **FR-UX-FRM-10** Fields with barcode or scan input shall keep focus for scan-after-scan entry, give audible and visual confirmation, and handle scan errors without losing the line.
- **FR-UX-FRM-11** Wizards shall show steps, allow going back without losing data, save progress, and summarize before final submission.
- **FR-UX-FRM-12** Read-only and locked states (posted, approved, closed period, no permission) shall be visually distinct, explain why a document is locked, and offer the correct next action (reverse, copy, request change).
- **FR-UX-FRM-13** Forms shall show who last changed the record and when, and offer quick access to history for users with permission.
- **FR-UX-FRM-14** Time-on-task targets for frequent tasks shall be defined and measured in usability tests (for example a 20-line journal in under 3 minutes by an experienced user, leave request on mobile in under 60 seconds).
- **AC:** a clerk enters a 30-line journal using only the keyboard with account lookup by code, receives an inline error on one dimension combination, fixes it, sees live totals, saves a draft after a dropped connection, and submits without using the mouse.

## 10. Page archetypes and patterns (PAT)

### 10.1 Archetypes

| Archetype | Use | Key elements |
| --- | --- | --- |
| List page | Browse and act on records | Title, saved views, filters, grid, bulk actions, create button, export |
| Entity page | Customers, suppliers, items, employees, assets | Summary header, tabs, activity and history, quick actions |
| Document page | Journals, invoices, orders, requests | Header, lines grid, totals, approval bar, document flow, attachments, history |
| Master-detail | Inbox, reconciliation, review queues | List on one side, detail on the other, keyboard stepping |
| Wizard | Setup, imports, runs | Steps, validation per step, summary, resume |
| Approvals inbox | Decisions across modules | Filters, grouped tasks, context, quick approve, mobile cards |
| Dashboard | Role scorecards | Tiles, filters, drill-down, freshness |
| Report viewer | Parameterized reports | Parameter bar, output, export, subscribe |
| Designer canvas | Workflow, forms, templates, rules | Palette, canvas, properties panel, validation, preview, versions |
| Settings page | Configuration | Search, grouped settings, inheritance, history |
| Board | Pipelines (recruitment, opportunities, work orders) | Columns, cards, drag with keyboard alternative |
| Calendar and timeline | Shifts, leave, maintenance, deliveries | Day, week, month views, conflicts, drag with keyboard alternative |
| Mobile task list | Receiving, counting, delivery, approvals | Large cards, scan bar, offline state, sync status |

### 10.2 Layout sketches

```
Document page (left-to-right; mirrored for right-to-left)
+-----------------------------------------------------------------------------------+
| Breadcrumb > Sales > Orders > SO-2026-00123            [Status chip]  [Save] [Submit] |
+-----------------------------------------------------------------------------------+
| Header fields (customer, dates, currency, terms ...)        | Side panel toggle:    |
|                                                              | History | Comments |  |
+--------------------------------------------------------------+ Documents | Flow   |
| Lines grid  (keyboard entry, paste, import)                   |                       |
|  item | description | qty | unit | price | disc | tax | total  |                       |
+--------------------------------------------------------------+                       |
| Totals block        Approval bar: [Reject] [Request change] [Approve]                |
+-----------------------------------------------------------------------------------+

List page
+-----------------------------------------------------------------------------------+
| Title            [Saved view v]  [Filters]  [Search]            [Export] [+ Create] |
+-----------------------------------------------------------------------------------+
| Bulk bar (when selected): 12 selected  [Approve] [Assign] [Export]                |
| Grid: sticky header, virtualized rows, totals footer                               |
+-----------------------------------------------------------------------------------+
```

### 10.3 Pattern rules

- **FR-UX-PAT-01** Action naming shall be consistent and verb-based (Save, Submit, Approve, Reject, Post, Reverse, Copy, Export), with the primary action visually emphasized once per view; destructive actions are secondary and confirmed.
- **FR-UX-PAT-02** Status shall be shown in the page header with the next allowed actions; the reason a transition is unavailable shall be explainable on demand.
- **FR-UX-PAT-03** Document-flow views shall show predecessor and successor documents (requisition to payment, quotation to receipt) with status and links, using the kernel document links.
- **FR-UX-PAT-04** The approvals inbox shall show the decision context (amounts, requester, history, budget or credit effects, attachments) without opening the document, support bulk approval where allowed, and be usable on phones with large buttons and no gesture-only actions.
- **FR-UX-PAT-05** Drag and drop (boards, calendars, designers) shall always have a keyboard and menu alternative.
- **FR-UX-PAT-06** Long-running operations shall show progress, allow leaving the page, appear in the job center, and notify on completion.
- **FR-UX-PAT-07** Bulk operations shall show a preview of the effect, the count of records, and a result summary with failures that can be downloaded and retried.
- **FR-UX-PAT-08** Master data screens shall offer duplicate warnings, merge access for permitted roles, and link to usage (where used) before deactivation.
- **FR-UX-PAT-09** Related lists inside entity pages shall support the same grid behavior as top-level lists.
- **FR-UX-PAT-10** Print and PDF views shall be reachable from every document and report and match the screen data exactly.

## 11. Internationalization, right-to-left and calendars (I18)

- **FR-UX-I18-01** The interface shall be fully translatable with message catalogs using an ICU-style message format (plurals, gender, selects), with no string concatenation, supporting English, Urdu and Arabic at launch and further languages by language packs.
- **FR-UX-I18-02** Direction shall follow the language, set on the document element, with layouts built on logical properties (inline-start and inline-end) so one stylesheet serves both directions; users can choose language and direction independently of the browser.
- **FR-UX-I18-03** Directional icons (arrows, chevrons, back and forward, progress, undo and redo, list and indentation) shall mirror in right-to-left; non-directional icons (logos, check marks, clocks, media symbols, charts) shall not; the icon set shall tag each icon accordingly.
- **FR-UX-I18-04** Bidirectional text shall be handled with Unicode isolation for identifiers, numbers, email addresses, URLs and mixed-language names so they never reorder incorrectly in a sentence or a table cell.
- **FR-UX-I18-05** Digits shall be configurable: Western digits, Arabic-Indic digits, and Eastern Arabic-Indic digits (commonly used for Urdu), as a tenant default and user preference, applied consistently to numbers, dates, money and documents, while stored and exported data remain unambiguous.
- **FR-UX-I18-06** Number grouping shall follow the locale and a configurable override, including the lakh and crore grouping used in Pakistan (for example 12,34,567) alongside international grouping.
- **FR-UX-I18-07** Dates shall be stored in Gregorian and displayed in the user's calendar (Gregorian or Hijri, or both), with first day of week and weekend days by company and country (for example Friday and Saturday weekends in some markets), and calendar pickers supporting both systems.
- **FR-UX-I18-08** Text expansion and contraction between languages shall never break layouts: no fixed-width labels, truncation with tooltips and accessible full text, and responsive wrapping in grids and buttons.
- **FR-UX-I18-09** Sorting and comparison shall use locale-aware collation for Arabic script and search shall normalize diacritics and letter variants (kernel LOC-08).
- **FR-UX-I18-10** Keyboard shortcuts shall use physical key positions where possible so they work across Latin, Arabic and Urdu keyboard layouts, and shall not conflict with input method editors.
- **FR-UX-I18-11** Generated documents (PDF, print, email) shall shape and order Arabic-script text correctly, embed required fonts, mirror layouts, and be tested in all supported languages.
- **FR-UX-I18-12** Charts and maps shall follow direction conventions: labels and legends mirror; time axes follow a tenant-configurable direction (validated with users); numbers keep their reading order.
- **FR-UX-I18-13** Pseudo-localization (expanded text, accented characters, mirrored layout) shall run in tests to detect hard-coded strings and layout breakage before real translations exist.
- **FR-UX-I18-14** Translation quality assurance shall include review by native speakers of Urdu and Arabic for terminology consistency (finance, HR, supply chain), with a glossary maintained per language and enforced in the translation workbench (document 4, LOC-02).
- **AC:** the same screen switched from English to Urdu mirrors layout and icons, shows Eastern Arabic-Indic digits with lakh and crore grouping when selected, keeps invoice numbers and emails readable inside Urdu sentences, and prints a PDF with correctly shaped text.

## 12. Accessibility (A11Y)

- **FR-UX-A11Y-01** All screens shall meet WCAG 2.1 level AA, plus the WCAG 2.2 criteria for focus appearance, target size and accessible authentication where feasible, and the product shall publish an accessibility conformance report for customers who require one (for example public-sector and enterprise buyers).
- **FR-UX-A11Y-02** Pages shall use semantic HTML (landmarks, headings in order, lists, tables) with ARIA only where native elements are insufficient, and shall provide skip links and a logical focus order.
- **FR-UX-A11Y-03** Every function shall be operable by keyboard alone; focus shall always be visible (at least 2 pixels with 3 to 1 contrast), never trapped (except in modals, which trap and restore focus), and never lost after dynamic updates.
- **FR-UX-A11Y-04** Forms shall associate labels, instructions and errors with controls; errors shall be announced and linked from a summary; required and invalid states shall not rely on color alone.
- **FR-UX-A11Y-05** Text and interface contrast shall meet the token rules of section 6.2; layouts shall support zoom to 200 percent and reflow at 320 pixels width without loss of content or function, and text spacing overrides.
- **FR-UX-A11Y-06** Content in another language (Arabic-script text inside English pages and the reverse, identifiers, names) shall carry correct language and direction attributes so screen readers pronounce and order it correctly.
- **FR-UX-A11Y-07** Dynamic changes (loading, saving, filtering, toasts, streaming AI responses) shall use appropriate live regions without flooding users with announcements, and allow stopping or pausing updates.
- **FR-UX-A11Y-08** Data grids, trees, menus, tabs, dialogs and comboboxes shall follow established accessibility interaction patterns for roles, states and keyboard behavior.
- **FR-UX-A11Y-09** Charts and maps shall provide text alternatives: a summary, a data table view, and keyboard-reachable data points; information shall not depend on color only (patterns, labels, shapes).
- **FR-UX-A11Y-10** Time limits (session timeout, approval links) shall warn in advance and be extendable; moving or auto-updating content shall be pausable; animations shall respect the reduced-motion setting.
- **FR-UX-A11Y-11** Authentication shall not require cognitive tests, shall allow password managers and paste, and shall offer accessible MFA methods.
- **FR-UX-A11Y-12** Minimum target size shall be 24 by 24 pixels on desktop and 44 by 44 on touch devices, with adequate spacing.
- **FR-UX-A11Y-13** Generated PDFs of statutory and customer-facing documents shall be tagged for accessibility where the document type is likely to be consumed by assistive technology. (S)
- **FR-UX-A11Y-14** Automated checks (for example axe) shall run in CI on every UI slice, with manual audits using screen readers on Windows, macOS, iOS and Android at each release, including tests with Arabic and Urdu content.
- **AC:** a keyboard-only user completes a leave request and a purchase approval; a screen-reader user hears correct language switching on a mixed English and Urdu page; the automated accessibility suite reports no serious violations.

## 13. Responsive design, mobile and offline (MOB)

- **FR-UX-MOB-01** Every screen shall adapt to phone, tablet and desktop breakpoints; screens that are not suitable for phones (heavy designers, large reconciliations) shall say so and offer the nearest alternative.
- **FR-UX-MOB-02** Mobile apps (document 1, section 4.9) shall use bottom navigation for primary tasks, large touch targets, one-handed reach for primary actions, biometric unlock, deep links, and push notifications with direct actions where safe.
- **FR-UX-MOB-03** Offline-capable tasks (attendance, leave, approvals review, receiving, counting, picking, delivery proof, maintenance, field sales, verification) shall work with a local encrypted database, show clear online, offline and syncing states, and queue actions in a visible outbox with retry.
- **FR-UX-MOB-04** Offline conflicts shall be resolved with explicit rules per task and a clear conflict screen when user input is needed; no silent data loss; financial postings and approvals shall be confirmed by the server before being shown as done.
- **FR-UX-MOB-05** Mobile data protection shall include encrypted local storage, automatic lock, remote wipe on device loss, wipe on logout or deprovisioning, and minimal sensitive data cached; rooted or jailbroken device detection shall be configurable. (S)
- **FR-UX-MOB-06** Scanning shall support camera, handheld scanner and Bluetooth devices with audible, visual and haptic confirmation, duplicate-scan handling, and manual entry fallback.
- **FR-UX-MOB-07** Low-bandwidth mode shall reduce image sizes, defer non-essential loads, compress uploads, and keep core tasks usable on poor connections.
- **FR-UX-MOB-08** Rugged handheld layouts shall use high-contrast, large-type task screens that work with gloves and in bright or dim light.
- **FR-UX-MOB-09** Mobile performance budgets: cold start under 3 seconds and screen transitions under 300 milliseconds on a mid-range Android phone; list scrolling at 60 frames per second.
- **FR-UX-MOB-10** App versions shall be managed with minimum supported version checks, update prompts, and backward-compatible APIs so field staff are not blocked mid-shift.
- **AC:** a storekeeper receives goods offline on a handheld scanner, sees pending actions in the outbox, reconnects, syncs without duplicates, and resolves one conflict where the order quantity changed meanwhile.

## 14. Data visualization (VIZ)

- **FR-UX-VIZ-01** Chart selection shall follow guidance built into the builders: comparison uses bars, trends use lines, composition uses stacked bars (pie and donut limited to few categories), distribution uses histograms, relationships use scatter plots, single indicators use number with trend and target.
- **FR-UX-VIZ-02** Palettes shall be color-blind safe, defined as tokens (categorical up to eight colors, sequential, diverging), with the same color meaning for the same entity or status across charts and dashboards.
- **FR-UX-VIZ-03** Bar charts shall start at zero; axes, units, currency and periods shall be labeled; direct labels shall be preferred over legends where space allows; dual axes shall be clearly labeled and discouraged.
- **FR-UX-VIZ-04** Tooltips and interactions shall work by keyboard and touch; selections shall drive cross-filtering as in document 9, section 6.
- **FR-UX-VIZ-05** Large data shall be downsampled or aggregated with an indication to the user and an option to see detail; charts shall remain legible at tile sizes on phones.
- **FR-UX-VIZ-06** Numbers in charts shall follow locale rules (digits, grouping, currency, direction), and right-to-left behavior as in FR-UX-I18-12.
- **FR-UX-VIZ-07** Dashboards shall prioritize the most important tiles at the top and start, limit tiles (about twelve per dashboard), show freshness and targets, and use short narrative headings that state the finding.
- **FR-UX-VIZ-08** Charts shall render statically for PDF and print with the same data and theme-independent colors.
- **FR-UX-VIZ-09** Three-dimensional effects, decorative gradients and unlabeled abbreviations shall be avoided.

## 15. Content and microcopy (CNT)

- **FR-UX-CNT-01** Voice shall be clear, respectful and plain: short sentences, active voice, the user's vocabulary, no internal codes or developer terms in user messages.
- **FR-UX-CNT-02** Buttons and links shall use verbs that describe the result (Submit for approval, Post journal, Send reminder); labels shall be nouns or noun phrases; capitalization shall follow one convention per language.
- **FR-UX-CNT-03** Error messages shall say what happened, why, and how to fix it, with a reference code for support; they shall never blame the user or reveal sensitive details.
- **FR-UX-CNT-04** Confirmation messages for wide-impact actions shall state the impact in numbers (for example how many users or records are affected) and the consequences.
- **FR-UX-CNT-05** Empty states shall explain what the area is for and offer the next action or import option; success messages shall be brief and specific.
- **FR-UX-CNT-06** A terminology glossary per language (finance, HR, supply chain, sales, asset terms) shall be maintained and enforced in translation and in configuration labels; tenant label overrides shall be respected throughout.
- **FR-UX-CNT-07** Copy shall be translation-friendly: no idioms or humor that does not travel, no sentence fragments assembled in code, variables placed where word order can change, and room for text expansion.
- **FR-UX-CNT-08** Language shall be inclusive and neutral about gender where the language allows, and sensitive notices (HR, privacy, disciplinary, AI) shall be reviewed by the owning function.
- **FR-UX-CNT-09** AI-generated text shall follow the same style and shall be labeled; the assistant shall reply in the user's language.
- **FR-UX-CNT-10** Content shall go through review (style, terminology, translation) as part of the definition of done for each UI slice.

## 16. Feedback states: loading, empty, error, conflict (STA)

- **FR-UX-STA-01** Loading shall use skeletons for page structure and spinners only after 300 milliseconds; operations longer than 2 seconds shall show progress, and operations longer than 10 seconds shall move to the job center with notification.
- **FR-UX-STA-02** Error states shall be categorized and handled consistently: validation (field-level), permission (explain and offer request access), not found, conflict (concurrency), server error (retry with reference code), and offline (queue or wait).
- **FR-UX-STA-03** Concurrency conflicts shall use the comparison view of FR-UX-FRM-08; offline sync conflicts shall use the explicit rules of FR-UX-MOB-04.
- **FR-UX-STA-04** Partial failures in bulk or imported operations shall show counts of success and failure, a downloadable error file, and a retry for failures only.
- **FR-UX-STA-05** Toasts shall be non-blocking, stay long enough to read (at least 6 seconds, longer for longer text), offer undo where possible, announce to assistive technology, and never be the only place an actionable error appears.
- **FR-UX-STA-06** Optimistic updates shall be used only for low-risk actions; financial postings, approvals, payments and stock movements shall show confirmed state from the server.
- **FR-UX-STA-07** Maintenance, read-only (license or period lock), and degraded-service states shall be shown by banners with explanation and expected resolution, without hiding data users may still read.
- **FR-UX-STA-08** Permission-denied and not-found pages shall offer useful navigation and, where appropriate, a request-access action.

## 17. Security and trust cues (SEC)

- **FR-UX-SEC-01** Sensitive fields (salary, bank account, national ID, cost, margin) shall display masked by default, reveal on explicit action with a reason where policy requires, re-mask automatically after a short period, and log the reveal.
- **FR-UX-SEC-02** The interface shall follow permission-aware rules: items a user can never use are hidden; items the user could request or that are blocked by state are shown disabled with an explanation and, where possible, a way to request.
- **FR-UX-SEC-03** Step-up authentication shall be required for high-risk actions (payroll finalization, changing bank details, exporting sensitive data, changing roles) with accessible flows and clear reasons.
- **FR-UX-SEC-04** Session timeout warnings shall appear before expiry, preserve drafts, and avoid data loss; shared-device mode shall shorten sessions and clear local data on logout.
- **FR-UX-SEC-05** Environment banners (production, UAT, sandbox) shall be unmistakable, with distinct color and label, and sandbox data shall be visibly marked, to prevent actions in the wrong environment.
- **FR-UX-SEC-06** Export, sharing and download actions shall show what will be included, warn about sensitivity, and apply watermarks as configured; secure links shall show expiry and recipients.
- **FR-UX-SEC-07** Login and recovery screens shall not reveal whether an account exists, shall support password managers, and shall show clear status of MFA and recovery options.
- **FR-UX-SEC-08** Audit visibility shall be offered where it builds trust: last changed by, history panels, and reasons for locked or blocked states.
- **FR-UX-SEC-09** Destructive actions (delete, void, reverse, terminate) shall explain irreversibility or the reversal path, and require typing or an extra confirmation proportionate to impact.

## 18. AI experience patterns (AIU)

- **FR-UX-AIU-01** The assistant shall be reachable from a consistent entry (panel, command palette, contextual actions), explain what it can do on the current screen, and offer relevant starter prompts without pushing users who do not want AI; tenants that disable AI shall see no AI entry points.
- **FR-UX-AIU-02** AI-generated content shall be labeled as AI-generated wherever it appears (text, drafts, summaries, proposals), and the label shall be preserved in documents when the tenant requires it.
- **FR-UX-AIU-03** Proposals shall appear in a standard component: what will change, a readable diff, rationale, sources, qualitative confidence, and actions to accept, edit or reject; accepted proposals execute through the normal screens' commands and show the same results and errors as manual work.
- **FR-UX-AIU-04** Answers using data or documents shall cite sources and show the scope used (for example the user's permissions, the period, the company), and shall show the interpreted query for analytical questions (document 9, section 17).
- **FR-UX-AIU-05** Streaming responses shall have a stop control, handle failure and fallback gracefully (clear message, retry, manual path), and announce progress to assistive technology without flooding.
- **FR-UX-AIU-06** Users shall be able to give feedback (helpful or not, with reason), see and delete their conversation history where policy allows, and see when an external provider processed their request if the tenant chooses to show this.
- **FR-UX-AIU-07** Quota and limit messages shall explain what is unavailable and why, and never block non-AI work.
- **FR-UX-AIU-08** The configuration assistant shall present drafts as diffs with sandbox previews and a clear path to promotion (document 9, section 18), not as direct changes.
- **FR-UX-AIU-09** The assistant shall respond in the user's language and script, respect right-to-left layout, and avoid claims about abilities it does not have; limits and uncertainty shall be stated plainly.
- **FR-UX-AIU-10** AI features shall not use dark patterns: no forced use, no hidden defaults that submit AI content, and no misleading anthropomorphism.
- **AC:** a user opens the assistant on a purchase order, asks for a summary of price changes, receives an answer with citations and scope, accepts a drafted message to the supplier after editing, and sees the AI label on the draft until sent.

## 19. Configurable UI and branding rules (CUI)

Because most screens are rendered from metadata (forms, lists, layouts, status models), consistency has to be guaranteed by the renderer and the designers.

- **FR-UX-CUI-01** The metadata renderer shall compose screens only from design-system components; every custom field type maps to a standard component, and tenants cannot inject arbitrary markup or styles into screens.
- **FR-UX-CUI-02** The form and list designers (document 4, STU) shall validate layouts against UX rules before publishing: a label in every enabled language for each field, required system fields not hideable, primary actions and status always present, sensible limits on visible fields per section and columns per default view, and a mobile variant for forms enabled on mobile.
- **FR-UX-CUI-03** Conditional visibility shall never hide fields that the platform requires for a valid document; hidden-but-required combinations shall be rejected at design time.
- **FR-UX-CUI-04** Custom labels shall respect length limits and warn when translations are missing or likely to overflow in other languages.
- **FR-UX-CUI-05** Designers shall themselves follow this specification: keyboard alternatives to drag and drop, accessible property panels, previews in each language, direction and theme, and undo.
- **FR-UX-CUI-06** Performance guardrails shall apply to configured screens: default column sets within budget, warnings for lookups and calculated fields in large grids, and measurement of render time for custom layouts.
- **FR-UX-CUI-07** Tenant branding shall be limited to logo, primary color, login page imagery and text, app name, sender identity, letterheads and document templates; semantic status colors, focus styles, error styles and the layout grid cannot be overridden.
- **FR-UX-CUI-08** The branding editor shall preview all key screens in light, dark and high-contrast themes, flag contrast failures, and offer compliant alternatives automatically.
- **FR-UX-CUI-09** Portals (supplier, customer, carrier) shall use the same design system with portal-specific shells, branding per tenant, and a reduced navigation set.
- **AC:** an administrator adds a custom field and a conditional section without a label in Arabic; the designer warns before publishing and refuses to hide a required system field; the published form renders correctly in all three languages and both directions.

## 20. Documents, print and notification design (DOC)

- **FR-UX-DOC-01** Print and PDF templates shall use standard page sizes (A4 and Letter), consistent margins, headers and footers with company details and page numbers, embedded fonts, and repeating table headers with rows that do not split across pages.
- **FR-UX-DOC-02** Financial and legal documents (invoices, credit notes, purchase orders, delivery notes, payslips, vouchers, cheques, certificates) shall follow one layout system per document family with right-aligned amounts, tabular figures, clear totals, amounts in words in English, Urdu and Arabic where required, and legal elements placed per pack rules (tax IDs, sequence numbers, QR codes).
- **FR-UX-DOC-03** Bilingual documents shall support side-by-side or stacked layouts, with mirrored variants for right-to-left, correct shaping and bidirectional ordering, and tested rendering of mixed content.
- **FR-UX-DOC-04** Documents shall carry status watermarks (draft, copy, cancelled) from document status, and shall print legibly in black and white without relying on color.
- **FR-UX-DOC-05** Email notifications shall be responsive, include a plain-text alternative, follow the recipient's language and direction, show who, what, why and the action with a deep link, work in light and dark clients, and avoid sensitive content in subject lines.
- **FR-UX-DOC-06** SMS and WhatsApp messages shall be short, in the recipient's language, use approved templates, identify the sender, and honor opt-out preferences.
- **FR-UX-DOC-07** Push notifications shall avoid sensitive content on lock screens by default, with the detail visible after authentication.
- **FR-UX-DOC-08** Templates shall be previewable against real sample records in every language, with a test send and version history (document 2, DOC and NTF).

## 21. Onboarding and help (HLP)

- **FR-UX-HLP-01** First-run experiences shall guide new users by role with short tours, checklists and sample tasks; administrators follow the setup wizard and readiness checklists (document 4, ONB).
- **FR-UX-HLP-02** Contextual help shall be available on screens and fields (short text, links to detailed guides, examples), in the user's language, editable by tenants for their own policies and terminology.
- **FR-UX-HLP-03** A searchable help center shall be bundled with the product so it works offline for on-premise customers, with role-based guides, task walkthroughs and a glossary.
- **FR-UX-HLP-04** A training mode or training tenant with sample data shall let new users practice without affecting real data, clearly marked as such.
- **FR-UX-HLP-05** A what-is-new view shall explain changes for each release in plain language by role, with links to affected screens.
- **FR-UX-HLP-06** A keyboard shortcut reference shall be available from any screen.
- **FR-UX-HLP-07** Users shall be able to report a problem or give feedback from any screen with automatic context (screen, correlation id, role, language) and optional masked screenshot, creating a support case.
- **FR-UX-HLP-08** Where AI is enabled, the assistant shall answer how-to questions from the help content with citations (document 9, section 14).

## 22. Performance budgets (PRF)

| Area | Budget (defaults to validate) |
| --- | --- |
| Initial load | Largest content visible within 2.5 seconds at the 75th percentile on a typical office network; shell script under 300 kilobytes compressed |
| Route chunks | Each module route under 200 kilobytes compressed, loaded on demand; shared design system code cached |
| Interaction | Response to input under 100 milliseconds for common actions; interaction to next paint under 200 milliseconds at the 75th percentile |
| Layout stability | Cumulative layout shift under 0.1 |
| Data | Standard list or document pages meet document 2, GR-01 (95th percentile under 2 seconds) |
| Long sessions | No memory growth that degrades performance over an 8-hour session |
| Slow networks | Core tasks remain usable on 3G-class connections with low-bandwidth mode |
| Mobile | As in FR-UX-MOB-09 |

- **FR-UX-PRF-01** Budgets shall be enforced in CI by bundle size checks and automated performance tests with throttled network and CPU profiles; regressions block merging.
- **FR-UX-PRF-02** Assets shall be cached with versioned names so on-premise installations work behind slow or offline networks, and shall never be fetched from external hosts.
- **FR-UX-PRF-03** Screens shall use code splitting by module, virtualization for long lists, memoization where measured, and image optimization.

## 23. Design governance and quality (GOV)

- **FR-UX-GOV-01** The design system shall have named ownership (you as product owner with a UX adviser), a contribution process, and a request-for-comment step for new components or token changes.
- **FR-UX-GOV-02** A component catalog (for example in a component workshop tool) shall document every component and run its visual, accessibility and interaction tests.
- **FR-UX-GOV-03** Visual regression tests shall capture components and key screens in English, Urdu and Arabic, in both directions, light and dark themes and compact and comfortable densities, with approval of intended changes.
- **FR-UX-GOV-04** Every UI slice shall pass the screen acceptance checklist (section 25) and the UX review prompt (document 12, section 8.5) before merge.
- **FR-UX-GOV-05** Clickable prototypes shall be produced and validated with representative users before complex screens are built (journal entry, document pages, approvals inbox, roster and attendance, payroll run, stock count on mobile, workflow designer).
- **FR-UX-GOV-06** Usability testing shall cover each primary persona with defined tasks, measuring task success, time on task, errors and satisfaction (target for the System Usability Scale to be at least 75 in pilots), including sessions in Urdu and Arabic with native users and sessions on handhelds with warehouse staff.
- **FR-UX-GOV-07** UX debt shall be tracked like technical debt, prioritized each release, with severity levels; no severity-1 or severity-2 UX or accessibility defect shall ship.
- **FR-UX-GOV-08** Usage analytics for UX improvement (task paths, errors, time on task) shall be privacy-respecting, off by default, enabled per tenant with disclosure, and free of personal content.
- **FR-UX-GOV-09** Design tokens shall be the single source of truth, generated into code and documentation, with a lint that rejects hard-coded colors, sizes and fonts in components.

## 24. Implementation guidance for the front end (IMP)

Choices are confirmed by ADRs at Step 0 and their licenses and versions verified at the time (decision D-09).

| Concern | Direction |
| --- | --- |
| Framework | React with TypeScript and a modern build tool; pnpm workspace as in Architecture section 13 |
| Data fetching and caching | A query and cache library for server state; generated API clients from OpenAPI (Architecture section 4.10) |
| Forms and validation | A form library with schema validation generated from API schemas so client and server rules cannot drift |
| Metadata renderer | Reads layout and entity metadata and composes design-system components; field renderers registered by type; no per-module hand-built forms except the archetypes that need them |
| Accessibility primitives | Open-source accessible primitives for menus, dialogs, comboboxes, tabs and similar widgets |
| Styling | CSS variables from tokens, logical properties, no runtime dependency on external CDNs; lint rules for tokens |
| Data grid | Selected through a short technical spike against the requirements of section 8 (candidate open-source grids with permissive licenses), wrapped by the product's own grid API |
| Charts | Apache ECharts through shared wrappers (document 9) |
| Dates and calendars | Locale-aware formatting with a Hijri-capable calendar library and tested conversions |
| Internationalization | ICU message format library, extraction tooling, pseudo-localization and RTL tests |
| Icons and fonts | Open-source icon set with directional tags; bundled open-license fonts for Latin, Arabic and Urdu |
| Testing | Unit and component tests, end-to-end and visual tests in a browser automation tool, accessibility checks in CI, component workshop for catalog |
| Lint rules | Accessibility rules, no hard-coded strings, no hard-coded colors or sizes, logical CSS properties, forbidden imports of non-approved UI libraries |

### 24.1 Front-end addendum for CLAUDE.md

```markdown
## Front-end rules
- Use only components from the design system package. Do not add UI libraries without an ADR and the license gate.
- Use design tokens (CSS variables). No hard-coded colors, spacing, fonts or z-index values.
- Use logical CSS properties (inline-start/end). Never left/right for layout. Test every screen in LTR and RTL.
- No hard-coded user-facing strings. Use message catalogs with ICU formats. Never concatenate translated fragments.
- Numbers, money, dates: use the shared formatting utilities (digits, grouping incl. lakh/crore, Hijri/Gregorian). Money is a decimal, never a float.
- Every interactive element works with keyboard and has an accessible name. Focus is visible. Run the accessibility checks.
- Every screen has loading, empty, error and permission-denied states.
- Sensitive fields are masked by default and follow field security. Respect hide-versus-disable rules.
- Tests: component tests, one end-to-end flow per screen archetype, visual baselines for en/ur/ar in both directions.
- Performance: stay within the budgets; lazy-load module routes; virtualize long lists.
- Never fetch assets from external hosts; everything ships with the product.
```

## 25. Screen acceptance checklist

Every UI slice attaches this checklist with evidence (document 12, section 5).

| No. | Check |
| --- | --- |
| 1 | Built only from design-system components and tokens |
| 2 | Fully operable by keyboard; visible focus; logical tab order |
| 3 | Labels, help text and errors present, specific and translated (English, Urdu, Arabic) |
| 4 | Renders correctly in left-to-right and right-to-left, with mirrored icons as tagged |
| 5 | Digits, grouping (including lakh and crore), currency, Gregorian and Hijri dates correct |
| 6 | Works at 360, 768 and 1280 pixel widths and in compact and comfortable density |
| 7 | Loading, empty, error, conflict and permission-denied states implemented |
| 8 | Permissions respected: hidden versus disabled rules, data scopes, field security and masking |
| 9 | Status, next actions and reasons for locked states shown |
| 10 | History and audit access where the entity warrants it |
| 11 | Large data handled by server-side paging and virtualization |
| 12 | Performance budgets met (measured) |
| 13 | Automated accessibility checks pass; screen reader smoke test done |
| 14 | Visual baselines captured for en, ur and ar in both directions and both themes |
| 15 | Content reviewed against the glossary and microcopy rules |
| 16 | Help text and, where needed, help center entries added |
| 17 | Tests reference the requirement IDs they cover |
| 18 | AI features (if any) follow section 18 |

## 26. Open items and next steps

**Decisions to validate with real users:**

- Default Urdu typeface for dense UI (Naskh-style) versus Nastaliq for long reading and documents.
- Default digits per market (Western, Arabic-Indic, Eastern Arabic-Indic) and default grouping (international versus lakh and crore) for Pakistani tenants.
- Time-axis direction in charts for right-to-left locales.
- Default density by role, and the compact grid row height for accountants.
- Whether weekends and first day of week need per-company defaults beyond country packs.

**Technical choices to settle by spike or ADR at Step 0 and Step 1:** data grid library, accessible primitive library, form and state libraries, icon set, Hijri calendar library, map boundary data and optional tile providers, component workshop tooling.

**Process items:** recruit pilot users for usability testing in Pakistan and Saudi Arabia (including Arabic and Urdu speakers and warehouse staff); plan the accessibility conformance report; decide how clickable prototypes are produced and tested; define the UX adviser role.

**Planning set status:** documents 1 to 13 are now drafted. Before coding beyond Step 1, I recommend a consistency review across all documents (terminology, requirement IDs, event names, the alignment items in document 11 section 22, and decisions D-01 to D-11), producing a list of corrections and a single decision log. Say the word and I will start it.
