# Analytics, Reporting and AI: Functional Specification (v0.1)

Document 9 of the planning set | Prepared for Ibrahim | 10 November 2026 | Companion to the Architecture (sections 8 and 9), Platform Kernel (sections 18 and 22), Admin Console and all module specifications

## 1. Introduction

### 1.1 Purpose and scope

This document specifies two cross-cutting platform capabilities that every module relies on:

- **Analytics and reporting** (Part A): the semantic layer, reporting data layer, report designer, dashboards, scheduling and distribution, self-service analysis, external BI access and the optional Superset adapter, statutory reporting, governance, and the catalog of standard content that modules ship.
- **Artificial intelligence** (Part B): the AI Gateway, skills framework, retrieval, document intelligence, predictive services, natural-language analytics, the configuration and migration assistants, safety and governance, and commercial packaging.

It turns the principles of Architecture sections 8 and 9 and the AI requirements of the kernel specification (FR-KRN-AIG) into a testable specification. Module-specific reports, dashboards and AI skills stay in each module's specification; this document defines the engines and the rules they all follow.

### 1.2 Position in the platform

Analytics and the AI Gateway are kernel components (Architecture section 4.6). They hard-depend on the kernel only. Modules contribute semantic definitions, reporting views, standard content and AI skills through their manifests; a module that is not licensed contributes nothing and nothing breaks (document 2, GR-12).

### 1.3 Waves

| Wave | Analytics and reporting | AI |
| --- | --- | --- |
| A (with Finance and HRMIS) | Semantic layer core, reporting views, tabular and grouped report designer, KPI and chart dashboards, scheduling and export, statutory report templates, governance basics | AI Gateway, controls and metering, retrieval over policies and help, configuration assistant (drafts), natural-language query (basic), HR and finance assistants |
| B | Pivot and cross-tab analysis, cross-module models, advanced dashboards, embedding in screens, external BI access, summary tables | Document intelligence, anomaly detection, migration assistant, forecasting service, supplier and procurement skills |
| C | Analytical extract store for very large tenants, Superset adapter, scenario analytics | Advanced predictive models, supply chain and sales skills, conversational agents with multi-step plans |
| D | Marketplace for report packs, advanced visualization | Predictive HR analytics (with review), partner skills, voice |

### 1.4 Conventions

- Requirement IDs: FR-ANL-\<area>-\<number> for analytics and FR-AIX-\<area>-\<number> for AI. Priority (M), (S), (C); unmarked is (M). Wave A unless stated.
- Each area lists Functional Requirements, Business Rules (BR) and Acceptance Criteria (AC).
- Everything is configurable through the admin console and the API (documents 2 and 4).
- Requirements here extend, and do not replace, the kernel requirements FR-KRN-AIG (document 2, section 22) and the analytics statements of Architecture section 8.

### 1.5 Actors

| Actor | Role |
| --- | --- |
| Report Author | Builds and maintains reports |
| Dashboard Author | Builds dashboards |
| Data Steward | Owns semantic definitions, certification and data quality |
| Business User | Views reports and dashboards, uses self-service analysis |
| Executive | Consumes dashboards and scheduled summaries |
| Export Approver | Approves sensitive or large exports |
| AI Administrator | Configures AI controls, providers and skills for the tenant |
| Skill Reviewer | Evaluates and approves AI skills before activation |
| External Recipient | Receives scheduled reports by email or secure link |
| Auditor | Reviews report runs, exports and AI interactions |

### 1.6 Key terms

| Term | Meaning |
| --- | --- |
| Semantic layer | Business definitions (entities, measures, dimensions, joins) that reports and AI query instead of raw tables |
| Measure and dimension | A calculated number (for example total debit) and an attribute to slice by (for example cost center) |
| Certified metric | A measure approved by a data steward as the official definition |
| Reporting view | A read-only database view named v\_{module}\_{entity} that a module publishes for analytics |
| Skill | A named AI capability with defined inputs, tools, permissions and tests |
| Proposal | An AI-produced draft change that waits for human confirmation |
| Retrieval | Finding relevant documents and passages for the AI to use, filtered by the user's permissions |

## 2. Architecture recap

```
Modules publish:   reporting views (v_{module}_{entity})  |  semantic definitions  |  standard content  |  AI skills
                                   |
Data layer:        read replica or reporting views  |  summary tables  |  snapshots  |  optional analytical extract
                                   |
Semantic layer:    entities, measures, dimensions, joins, security bindings, calculated metrics, certification
                                   |
Query compiler:    permission scope (company, data scope, field security) + limits + cache  ->  SQL
                                   |
Consumers:         Report designer | Dashboards | Self-service explorer | Schedules | APIs | External BI | Superset adapter | AI (NLQ)

AI Gateway:        provider abstraction | prompt registry | skills and tools | retrieval | guardrails | metering | audit
                                   |
Uses:              module contracts (as the user) | semantic layer (NLQ) | retrieval index | document and prediction services
```

Two rules from the architecture are unchanged and enforced by tests: business logic never reads reporting views, and cross-module joins exist only in the semantic layer through published views.

# Part A: Analytics and reporting

## 3. Semantic layer (SEM)

**Purpose:** one governed set of business definitions that every report, dashboard and AI question uses. **Actors:** Data Steward, Report Author, Dashboard Author.

- **FR-ANL-SEM-01** The semantic model shall consist of entities (for example Journal Line, Stock Movement, Employee, Purchase Order, Sales Invoice Line), fields with data types and roles (dimension, measure, attribute), measures with aggregation rules, declared joins with cardinality, and labels and descriptions per language.
- **FR-ANL-SEM-02** Each module shall ship its semantic definitions and reporting views through its manifest; installing or upgrading a module shall add or update its definitions, and a module that is not licensed shall have none.
- **FR-ANL-SEM-03** Custom fields, custom dimensions and custom objects defined through the metadata engine shall appear automatically in the semantic model with labels, types and field-level permissions.
- **FR-ANL-SEM-04** Calculated measures shall be definable by data stewards and authorized authors with an expression editor (shared with the kernel rules engine), including ratios, conditional sums, weighted averages and distinct counts, with validation, tests against sample data, and version history.
- **FR-ANL-SEM-05** Time intelligence shall be built in: period to date (month, quarter, year, fiscal period), prior period and prior year, rolling windows, period-over-period change and fiscal calendar awareness (including Hijri display), using the kernel fiscal calendars.
- **FR-ANL-SEM-06** Currency handling shall be part of the model: amounts available in transaction, company and group currency, with conversion by configured rate types and dates and consistent handling of unrealized differences.
- **FR-ANL-SEM-07** Hierarchies shall be modelled and navigable: organization units, chart of accounts groups, item categories, geography, customer hierarchies, and time, with roll-up and drill-down.
- **FR-ANL-SEM-08** Joins between entities of different modules shall be allowed only where declared in a module's published reporting contract; the compiler shall refuse undeclared joins.
- **FR-ANL-SEM-09** Security shall be bound in the model: every entity declares how company scope, data scopes and field-level security apply, and the compiler shall inject them into every query so no consumer can bypass them.
- **FR-ANL-SEM-10** A metrics catalog shall list all measures with definition, formula, owner, source fields, certification status (draft, certified, deprecated), and usage count; certified metrics shall be marked wherever they appear.
- **FR-ANL-SEM-11** Lineage shall show for any measure the fields, views and source tables it depends on, and for any source table the reports and dashboards that use it, to support impact analysis before changes.
- **FR-ANL-SEM-12** The query compiler shall translate semantic queries to optimized SQL against reporting views (or summary tables when available), apply row and time limits, parameterize all inputs, and return explain information to support teams.
- **FR-ANL-SEM-13** Results shall be cacheable with keys that include query, parameters, permission scope and data freshness, with invalidation on relevant data changes or by time-to-live.
- **FR-ANL-SEM-14** Semantic definitions shall be versioned with backward compatibility rules: renaming or removing a field used by saved reports requires a deprecation period and migration of dependents.
- **BR:** semantic queries never expose fields the user cannot see; measures with sensitive classification (salary, cost, margin) are hidden or masked for users without the permission, in results, exports, API responses and AI answers.
- **AC:** a user without cost permission queries sales by item and sees revenue and quantity but no cost or margin fields; the same query by a finance user returns margin with the certified definition; a custom field added to purchase requests appears in the model and in the report designer without a release.

## 4. Reporting data layer and performance (DAT)

**Purpose:** fast, fresh, safe data for analytics without affecting transactions. **Actors:** Data Steward, Tenant Admin, Platform Super Admin.

- **FR-ANL-DAT-01** Each module shall publish reporting views named v\_{module}\_{entity} exposing business-friendly columns, resolved reference names, currency-converted amounts and standard dimensions, with documentation, owned and versioned by the module.
- **FR-ANL-DAT-02** Reporting reads shall use read replicas or the primary with resource limits, as configured per tenant size, never competing unboundedly with transactions.
- **FR-ANL-DAT-03** Summary tables or materialized views shall be defined for heavy measures (trial balance by period and dimension, stock balances by day, headcount by month, sales by day) refreshed incrementally by jobs, with freshness timestamps shown to users.
- **FR-ANL-DAT-04** Snapshots shall preserve point-in-time facts that cannot be reconstructed reliably from history: headcount and positions by month end, inventory valuation by period end, receivables and payables ageing by period end, budget versions, and plan versions.
- **FR-ANL-DAT-05** As-of reporting shall be supported for effective-dated data (for example organization structure and employee assignments on any date), using validity ranges and snapshots.
- **FR-ANL-DAT-06** A query governor shall enforce per-tenant and per-user limits on concurrent queries, row counts, execution time and memory, with clear messages and administrator visibility.
- **FR-ANL-DAT-07** Large exports shall run asynchronously as jobs with progress, resumability and secure download, and shall respect export controls (section 10).
- **FR-ANL-DAT-08** For very large tenants an optional analytical extract store (columnar, such as DuckDB or ClickHouse, licenses verified under decision D-09) shall be loadable incrementally from reporting views, with identical semantic definitions and security applied. (wave C)
- **FR-ANL-DAT-09** Data freshness shall be visible on every report and dashboard, with alerts when refresh jobs lag beyond thresholds.
- **FR-ANL-DAT-10** Data quality rules (null checks, referential checks, reconciliation to ledgers) shall be defined on reporting views, run on a schedule, and publish results to data stewards.
- **FR-ANL-DAT-11** Tenant isolation shall apply to every analytics path: separate databases, no shared caches without tenant keys, and isolation tests for the compiler, caches, exports and APIs.
- **AC:** a trial balance for a year with 5 million ledger lines returns within 5 seconds at the 95th percentile from summary tables, drill-down to ledger lines within 3 seconds, and a stale summary refresh raises an alert and shows its freshness time.

## 5. Report designer (RPD)

**Purpose:** let authors create operational, management and statutory reports without developers. **Actors:** Report Author, Business User, Auditor.

- **FR-ANL-RPD-01** The report designer shall build tabular, grouped (with subtotals and grand totals), pivot and cross-tab reports from semantic entities (pivot in wave B), with drag-and-drop fields, filters, sorting, parameters, and calculated columns.
- **FR-ANL-RPD-02** Parameters shall support defaults, cascading selections, relative dates (this month, last fiscal year), multi-select and entity pickers, and be exposed as inputs in schedules and the API.
- **FR-ANL-RPD-03** Formatting shall include conditional formatting rules, number and date formats by locale, currency display, group headers, page breaks, repeated headers, and column visibility by role.
- **FR-ANL-RPD-04** Drill-down shall navigate hierarchies; drill-through shall open the source documents or detailed reports with context filters, subject to the user's permissions.
- **FR-ANL-RPD-05** A layout designer shall produce document-style and statutory reports (pixel-precise forms, multi-section pages, headers and footers, letterheads, barcodes, QR codes) using the kernel template engine, in English, Urdu and Arabic with right-to-left layout.
- **FR-ANL-RPD-06** Reports shall embed charts and sub-reports, and combine data from several entities through declared joins.
- **FR-ANL-RPD-07** Output shall be available on screen, as PDF, Excel (with formatting and formulas preserved where practical), CSV, Word and HTML, and for printing, with page setup and watermarks by status.
- **FR-ANL-RPD-08** Bursting shall run one report definition for many recipients or entities (for example a branch report per branch manager, a payslip per employee), applying each recipient's data scope.
- **FR-ANL-RPD-09** Standard reports shipped by modules shall be read-only originals that users can clone and edit; upgrades shall update originals without overwriting clones, with a comparison view of changes.
- **FR-ANL-RPD-10** Reports shall be versioned, with draft, published and retired states, a change history, and certification by data stewards (section 10).
- **FR-ANL-RPD-11** A report catalog shall list reports by module, owner, tags and usage with search in any language and favorites.
- **FR-ANL-RPD-12** Report definitions shall be exportable and importable through configuration packages and shareable by role, user group or company.
- **FR-ANL-RPD-13** The designer shall show a preview with sample or real data within the user's scope and warn about expensive queries before saving.
- **FR-ANL-RPD-14** Report run history shall record who ran what with which parameters and duration, for audit and performance tuning.
- **BR:** a report cannot show data the author's or viewer's permissions exclude; cloning never grants access the cloner lacks; published reports used in schedules cannot be deleted without replacing the schedules.
- **AC:** an author builds a grouped purchase report with a parameter for supplier category, adds a conditional highlight and a chart, publishes it to buyers, schedules it monthly, and a clone of a standard report keeps working after a module upgrade.

## 6. Dashboards (DSH)

**Purpose:** role-based, interactive views of performance that answer questions at a glance and lead to the underlying detail. **Actors:** Dashboard Author, Executive, Business User.

- **FR-ANL-DSH-01** The dashboard builder shall compose tiles on a responsive grid: KPI cards with trend and target, charts, tables, text and image tiles, maps, and embedded reports, each bound to semantic queries with its own filters.
- **FR-ANL-DSH-02** Chart types shall include bar, column, line, area, stacked, combination, scatter, bubble, heat map, treemap, funnel, gauge, waterfall, histogram and map visuals; pie and donut shall be offered with guidance to limit categories; charts use the product's open-source charting library (Apache ECharts, decision D-09).
- **FR-ANL-DSH-03** Dashboard-level filters (period, company, organization unit, currency, custom dimensions) shall apply across tiles with per-tile overrides, and selecting a chart element shall cross-filter other tiles.
- **FR-ANL-DSH-04** Drill-down and drill-through shall be available on every tile: navigate hierarchies, open detailed reports, or open source documents, always within the user's permissions.
- **FR-ANL-DSH-05** Targets, thresholds and comparisons (budget, prior period, plan, forecast) shall be configurable with color rules, and variances shall be shown as amounts and percentages.
- **FR-ANL-DSH-06** Threshold alerts shall notify users or roles through the notification component when a measure crosses a limit or changes unexpectedly, with frequency controls and acknowledgement.
- **FR-ANL-DSH-07** Home dashboards shall be assignable by role, with user personalization within allowed tiles, default landing pages, and mobile layouts that reorder and simplify tiles for narrow screens.
- **FR-ANL-DSH-08** Auto-refresh, full-screen and wall-display modes shall be available, with freshness timestamps and pause on inactivity.
- **FR-ANL-DSH-09** Contextual analytics shall let any entity page embed relevant tiles (for example a customer's sales trend on the customer screen, a supplier's performance on the supplier screen), defined through the metadata engine.
- **FR-ANL-DSH-10** Dashboards shall be versioned, shareable by role, user group or company, exportable as images, PDF and data, and included in configuration packages.
- **FR-ANL-DSH-11** Natural-language entry (section 17) shall create tiles from questions, always shown with their semantic definition for review before saving.
- **FR-ANL-DSH-12** Tile performance shall be monitored; slow tiles shall be flagged to authors with suggestions (summary tables, narrower filters), and progressive loading shall keep dashboards responsive.
- **FR-ANL-DSH-13** Annotations and commentary by authorized users shall be attachable to tiles and periods, visible to viewers, and included in scheduled packs. (S)
- **FR-ANL-DSH-14** Map tiles shall use bundled boundary data for Pakistan, Saudi Arabia and the United States and optional tile providers configured by the tenant; the product shall not depend on an external map service on premise.
- **BR:** tiles never show data outside the viewer's scope even when the dashboard author has wider access; shared dashboards re-evaluate security for each viewer.
- **AC:** a CFO dashboard shows cash, revenue against budget and receivables ageing; clicking a revenue bar filters other tiles by month, drilling opens the ledger lines, a threshold alert fires when ageing exceeds the limit, and the mobile layout shows the same KPIs in a single column.

## 7. Scheduling and distribution (SCH)

**Purpose:** deliver the right information to the right people at the right time, safely. **Actors:** Report Author, Business User, External Recipient, Export Approver.

- **FR-ANL-SCH-01** Schedules shall run reports and dashboard snapshots on calendars (daily, weekly, monthly, period close, custom cron), with business calendars, time zones, and event triggers (for example after payroll finalization or period close).
- **FR-ANL-SCH-02** Delivery channels shall include email attachments, secure links with expiry, in-app inbox, shared folders and SFTP, and webhooks, in the recipient's language where templates exist.
- **FR-ANL-SCH-03** Recipients may be users, roles, groups, dimension owners (for example each budget owner), and external addresses; delivery to external addresses shall be off by default and require approval by policy.
- **FR-ANL-SCH-04** Each delivery shall run with the recipient's data scope (bursting) so a manager receives only their team or branch.
- **FR-ANL-SCH-05** Conditional delivery shall send only when criteria are met (for example overdue amounts above a limit) and suppress empty reports.
- **FR-ANL-SCH-06** Secure links shall support expiry, one-time use, password, and watermarking, with access logging; attachments containing sensitive data shall be encrypted or sent as links under policy.
- **FR-ANL-SCH-07** Delivery logs shall show status, retries, bounces and opens where available, with resend and error alerts to the schedule owner.
- **FR-ANL-SCH-08** Report packs (several reports combined with a cover page and table of contents) shall be generated for periods, stored and versioned, for example monthly board packs and audit packs.
- **FR-ANL-SCH-09** Subscriptions shall let users subscribe to published reports and dashboards within their permissions, with frequency and format choices.
- **FR-ANL-SCH-10** Schedule administration shall show all schedules, owners, next runs, recent failures and load, with controls to pause, transfer ownership, and throttle.
- **AC:** a monthly pack runs after period close, each branch manager receives a branch report with only their scope, a missing recipient email raises an alert to the owner, and an external auditor link expires after seven days.

## 8. Self-service analysis and external access (ADH)

**Purpose:** let business users explore data and let other tools use it, without losing governance. **Actors:** Business User, Data Steward, Tenant Admin.

### 8.1 Explorer

- **FR-ANL-ADH-01** An explorer shall let users drag fields from the semantic model into rows, columns, values and filters, switch between table, pivot and chart views, sort, group, bin, calculate fields, and save views, with undo and sharing. (pivot in wave B)
- **FR-ANL-ADH-02** Users shall create sets and segments (for example top customers, at-risk items) from queries and reuse them as filters.
- **FR-ANL-ADH-03** Any explorer view shall be promotable to a report or dashboard tile by authorized users, retaining the semantic definitions used.
- **FR-ANL-ADH-04** The explorer shall show certified measures with indicators and warn when a user-created calculation duplicates a certified metric with a different result.
- **FR-ANL-ADH-05** Export of explorer results shall follow export controls (section 10).

### 8.2 External BI access

- **FR-ANL-ADH-10** A governed analytics API (REST and OData) shall expose semantic entities and measures to external tools (spreadsheet tools, BI products, partner applications), authenticated by API clients, applying the same scope, field security and limits as the UI. (wave B)
- **FR-ANL-ADH-11** For on-premise and private-cloud tenants, read-only database access to reporting views shall be available through a dedicated database role limited to the reporting schema and, where configured, to scoped views, with network allow lists and audit; direct SQL access is not offered in pooled SaaS. (wave B)
- **FR-ANL-ADH-12** Scheduled data extracts to files or storage (CSV, Parquet, JSON) shall be configurable with entity, columns, filters and destination, subject to approval and logging. (wave B)
- **FR-ANL-ADH-13** A spreadsheet connector shall refresh tables in Excel and similar tools from semantic queries with user authentication and scope. (S, wave B)

### 8.3 Superset adapter (optional add-on)

Apache Superset is not part of the base product (decision D-06). When a customer wants it:

- **FR-ANL-ADH-20** The adapter shall create a per-tenant connection to reporting views with a restricted read-only credential and publish semantic entities and certified measures as Superset datasets and metrics, kept in sync with the semantic model. (wave C)
- **FR-ANL-ADH-21** Single sign-on shall use the tenant's identity provider or the product's OIDC service, mapping roles to Superset roles.
- **FR-ANL-ADH-22** Security (company, data scope, field masking) shall be applied through scoped views or parameterized datasets so Superset users never see more than the product would show them.
- **FR-ANL-ADH-23** Dashboards from Superset shall be embeddable in product screens with the same session and theming where the licensing and technical conditions allow.
- **FR-ANL-ADH-24** The adapter shall be entitlement-gated, deployable as an optional container in on-premise installs, versioned independently, and removable without affecting the core product.
- **AC:** a customer enables the adapter, finance analysts build charts in Superset from certified datasets, a manager in Superset sees only their department's rows, and removing the adapter leaves reports and dashboards in the product unchanged.

## 9. Statutory and regulatory reporting (STA)

**Purpose:** produce official reports and filings correctly and on time. **Actors:** Finance Controller, Tax Officer, HR Compliance Officer, Auditor.

- **FR-ANL-STA-01** Statutory reports (financial statements in local formats, tax returns and worksheets, payroll and social-insurance returns, regulator submissions) shall be delivered by country and industry packs as report definitions and templates with effective dates and version history.
- **FR-ANL-STA-02** Each statutory report shall define its source (semantic entities or module views), validation rules (totals tie to ledgers, mandatory fields, cross-report consistency), and output formats (PDF, Excel, XML, text files in authority-specified layouts).
- **FR-ANL-STA-03** A filing calendar shall list due dates per entity and authority with owners, reminders, status (prepared, reviewed, filed) and evidence of filing.
- **FR-ANL-STA-04** Preparation shall follow a workflow: generate, validate, review and sign off by role, lock the period data, produce the file, record the submission reference, and archive the output permanently with its inputs snapshot.
- **FR-ANL-STA-05** Pack updates shall be staged with a preview of changes and a comparison against the prior version, applied only on an effective date and after approval.
- **FR-ANL-STA-06** Tenants may adapt layouts (headers, notes, local wording) without altering calculation logic of the pack; logic changes require a new pack version.
- **FR-ANL-STA-07** An audit file export (standard audit data of ledgers and subledgers for a period) shall be available as specified by packs.
- **FR-ANL-STA-08** Every figure in a statutory report shall drill down to documents, and the output shall record the data freshness, parameter values and user who produced it.
- **AC:** a pack-provided tax worksheet reproduces the period's ledger totals, fails validation when a mapping is missing, passes after correction, is signed off by the tax officer and archived with its input snapshot.

## 10. Governance and security (GOV)

**Purpose:** trust in numbers, and protection of sensitive data. **Actors:** Data Steward, Tenant Admin, Export Approver, Auditor.

- **FR-ANL-GOV-01** Access to reports, dashboards, schedules and datasets shall be controlled by permissions (view, run, edit, share, certify, administer) assignable to users, roles and groups, organized in folders or spaces with inheritance.
- **FR-ANL-GOV-02** Data security shall be enforced by the semantic layer (company scope, data scopes, field-level security); content authors cannot widen access beyond their own and shared content never exposes more than the viewer may see.
- **FR-ANL-GOV-03** Fields and measures classified as sensitive (salary, cost, margin, bank details, personal identifiers) shall be masked or hidden by default in all outputs and require explicit permission.
- **FR-ANL-GOV-04** Export controls shall configure per role and sensitivity: allowed formats, row limits, approval requirements, reason capture, watermarking, and blocking of copy to external destinations.
- **FR-ANL-GOV-05** Certification workflow shall let data stewards review and certify measures, reports and dashboards, showing certification and last review date, and expiring certification after a set period.
- **FR-ANL-GOV-06** Ownership shall transfer automatically or by prompt when an owner is disabled, with orphan reports reassigned and schedules paused until a new owner is set.
- **FR-ANL-GOV-07** Audit shall record report runs, dashboard views of sensitive content, exports, shares, schedule changes and definition changes with user, time, parameters and result size.
- **FR-ANL-GOV-08** External sharing (links, external recipients) shall be disabled by default and enabled by policy with approvals, expiry and domain allow lists.
- **FR-ANL-GOV-09** Usage analytics shall show content use, slow content and unused content, with retirement workflows and retention rules for outputs and snapshots.
- **FR-ANL-GOV-10** Change impact analysis shall list dependent reports, dashboards, schedules and AI questions before a semantic definition or reporting view changes.
- **FR-ANL-GOV-11** A metric dictionary shall publish business definitions in the tenant's languages, available to users and to the AI Gateway.
- **FR-ANL-GOV-12** Tenant isolation, permission, masking and export-control tests shall run in every pipeline against the compiler, caches, exports, schedules and APIs.
- **AC:** a user lacking salary permission cannot see salary in a report, export, scheduled email, API response or AI answer; an export of 200,000 rows requires approval and is watermarked; every step is visible in the audit viewer.

## 11. Standard content catalog (STD)

### 11.1 Content shipped by modules

Each module ships standard reports, dashboards and semantic definitions as part of its definition of done. The detailed lists are in the module specifications:

| Module | Standard content defined in |
| --- | --- |
| Finance | Document 5, section 14 (RPT) and dashboards for CFO, controller, AP, AR, treasury, tax and budget owners |
| HRMIS | Document 6, section 13 (RPT) and compensation, attendance, leave and compliance reports |
| Procurement | Document 7, section 13 (ANL) |
| Inventory | Document 8, section 13 (IRP) |
| Asset Management | Document 8, section 24 (ARP) |
| Supply Chain | Document 10, section 15 (SCA) |
| Sales and Distribution | Document 11, section 13 (SAN) |

### 11.2 Cross-module executive content (wave B onward)

| Dashboard or report | Content |
| --- | --- |
| Executive scorecard | Revenue against budget, margin, cash, headcount and payroll cost, inventory value and turns, on-time delivery, exceptions requiring decision |
| Working capital and cash conversion | Days sales outstanding (Finance), days inventory (Inventory), days payables (Finance), cash conversion cycle, trends by company |
| Procure-to-pay performance | Requisition-to-payment cycle time, spend under contract, invoice match rate, payment timeliness |
| Order-to-cash performance | Quote-to-cash cycle time, on-time-in-full, invoice accuracy, collection effectiveness |
| People cost and productivity | Payroll cost by cost center against budget, revenue per employee, absenteeism cost |
| Asset and capital efficiency | Net book value, capital spend against budget, utilization, maintenance cost |
| Group view | Consolidated statements and key indicators across companies and currencies |
| Risk and exceptions | Open control exceptions, failed postings, overdue approvals, expiring licenses and contracts, anomalies |

### 11.3 Declared cross-module joins

The semantic layer allows only these declared joins (more are added by module specifications as published contracts):

| Join | Modules |
| --- | --- |
| Ledger line to cost center, project and organization unit | Finance, kernel |
| Payroll cost by cost center to budget | HRMIS, Finance |
| Requisition to order to receipt to invoice to payment | Procurement, Inventory, Finance |
| Sales order to delivery to invoice to receipt | Sales, Inventory, Finance |
| Stock movement to ledger posting | Inventory, Finance |
| Asset to depreciation to ledger posting | Assets, Finance |
| Employee to cost center, asset custody and approvals | HRMIS, Assets, Finance, Procurement |
| Item to every transactional entity | Kernel catalog with all modules |
| Business partner to payables, receivables, orders and contracts | Kernel parties with Finance, Procurement, Sales |
| Demand history and forecast to stock and supply | Sales, Supply Chain, Inventory |

The standard content rule applies to all catalogs: every report and dashboard shipped is cloneable, translatable, permission-aware and covered by tests against known data.

# Part B: Artificial intelligence

## 12. AI Gateway (GWY)

**Purpose:** one controlled entry point for every AI call, whatever the provider or deployment. These requirements extend FR-KRN-AIG-01, 07, 08 and 09. **Actors:** AI Administrator, Platform Super Admin, Auditor.

- **FR-AIX-GWY-01** All AI calls from any module shall pass through the gateway; modules shall never call a model provider directly.
- **FR-AIX-GWY-02** Provider adapters shall exist for hosted model APIs, regional endpoints, tenant-supplied provider keys, and self-hosted open-weight models served through standard inference interfaces; adding a provider shall not change skills.
- **FR-AIX-GWY-03** A model catalog shall record for each model its capabilities (text, vision, tool calling, structured output, embeddings, languages including English, Urdu and Arabic), context size, latency and cost class, region, retention terms, and license (for self-hosted models, verified under decision D-09).
- **FR-AIX-GWY-04** Routing policies shall choose the model by skill, data classification, tenant region and residency, required capability, cost limits and availability, with explicit fallback order and circuit breakers; routing decisions shall be logged.
- **FR-AIX-GWY-05** Data classes (general, personal, sensitive, restricted) shall determine which providers may receive data; sensitive and restricted classes shall be blocked from external providers unless the tenant explicitly allows them for a named skill.
- **FR-AIX-GWY-06** Redaction and pseudonymization shall mask configured identifiers (national IDs, bank accounts, phone numbers, names) before external calls and restore them in responses where the skill needs them, with tests proving no leakage in prompts and logs.
- **FR-AIX-GWY-07** Provider data-retention and training-use terms shall be recorded; calls shall request zero retention where the provider supports it; customer data shall never be used for model training by the product.
- **FR-AIX-GWY-08** Structured outputs shall be validated against schemas, with bounded automatic retries and repair, and failures returned as clear errors rather than partial data.
- **FR-AIX-GWY-09** Streaming, batch (asynchronous bulk) and embedding endpoints shall be available, with timeouts, cancellation, rate limits per tenant and user, and fair scheduling across tenants.
- **FR-AIX-GWY-10** Caching shall be allowed for non-sensitive, permission-independent prompts and disabled for sensitive classes and permission-scoped content; cache keys shall include tenant and permission scope.
- **FR-AIX-GWY-11** Observability shall record per call: tenant, user, skill, model, tokens, latency, cost estimate, outcome and redaction counts, without storing prompt content unless the tenant enables content logging with retention limits.
- **FR-AIX-GWY-12** Metering shall count tokens and calls per tenant, module, skill and user group, enforce quotas from entitlements (kernel ENT), warn at thresholds, and produce usage statements for billing.
- **FR-AIX-GWY-13** Prompts shall live in a versioned registry with owners, change history, tests and staged rollout; skills reference prompt versions, and changes follow the evaluation gate (section 20).
- **FR-AIX-GWY-14** In air-gapped mode the gateway shall operate with local models only; the product shall publish a capability matrix stating which skills are available, degraded or unavailable under hosted, regional and local modes.
- **FR-AIX-GWY-15** Test mode shall replay recorded fixtures so CI never calls live providers, and evaluation runs shall use isolated test tenants and budgets.
- **FR-AIX-GWY-16** A kill switch shall disable a provider, model, skill or the whole gateway per tenant or platform-wide immediately, with graceful messages to users.
- **BR:** an AI call that would violate a data-class policy is refused and logged; no module may bypass the gateway's metering or controls.
- **AC:** a tenant with residency constraints routes all calls to a regional or local model; a prompt containing a national ID shows the identifier pseudonymized in the provider request; exceeding the monthly quota blocks new calls with a clear message and alerts the administrator.

## 13. Skills framework and assistants (SKL)

**Purpose:** a safe, uniform way to build AI capabilities that read and propose changes through the product's own permissions. These requirements extend FR-KRN-AIG-02 to 05. **Actors:** Business User, AI Administrator, Skill Reviewer.

- **FR-AIX-SKL-01** A skill shall be declared in a manifest: name, description, owning module, input and output schemas, tools it may call, required permissions, data classes touched, prompt versions, evaluation set, risk tier, and supported languages.
- **FR-AIX-SKL-02** Tools shall be thin wrappers over module contracts (queries and commands); they execute with the requesting user's identity and pass through the normal authorization, data scope, field security, validation and workflow, and are never given direct database access.
- **FR-AIX-SKL-03** Read tools shall return only data the user may see, with sensitive fields masked as in the UI.
- **FR-AIX-SKL-04** Write actions shall follow the proposal pattern: the skill produces a proposal object (target, change, rationale, sources), the user reviews a readable diff, and only an explicit confirmation executes the normal command; proposals expire and are logged with the confirming user.
- **FR-AIX-SKL-05** Multi-step plans shall display all steps in advance, execute read steps freely, and require confirmation for each write step or batch, with the ability to stop and undo where a reversing command exists.
- **FR-AIX-SKL-06** Skills shall be idempotent where possible and use idempotency keys for commands so repeated confirmations never duplicate documents.
- **FR-AIX-SKL-07** The assistant shall be available as a side panel, contextual Ask-AI actions on screens (explain, summarize, draft), a command palette entry, and on mobile; context from the current screen shall be passed only within the user's scope.
- **FR-AIX-SKL-08** Explanations shall use product traces where they exist (pricing trace, tax trace, posting rule trace, policy resolution, calculation trace) so answers to why-questions cite the deterministic source, not model guesses.
- **FR-AIX-SKL-09** Conversation context shall be per user and session, with configurable retention, deletion on request, no cross-user sharing, and an option to disable history for sensitive modules.
- **FR-AIX-SKL-10** Responses that use retrieved documents or data shall include citations to those sources; unverifiable statements shall be labeled as such.
- **FR-AIX-SKL-11** Skill lifecycle shall be draft, evaluated, active, deprecated and retired, with version pinning, canary rollout to a subset of tenants or users, and rollback.
- **FR-AIX-SKL-12** Users shall rate responses and flag problems; flagged items shall route to the AI administrator and evaluation backlog.
- **FR-AIX-SKL-13** Skills shall support English, Urdu and Arabic input and output, with right-to-left rendering and correct number, date and calendar formats; language coverage shall be declared and tested per skill.
- **FR-AIX-SKL-14** AI-generated content shall be labeled as such in the interface and in documents where the tenant requires it.
- **FR-AIX-SKL-15** Partner-authored skills shall run in a restricted sandbox with declared tools and review before activation. (wave D)
- **BR:** skills cannot acquire permissions beyond the user's; a proposal executes only with explicit confirmation by a user who holds the required permission; blocked actions in the safety policy (section 20) can never be proposed for execution.
- **AC:** a manager asks the assistant to approve leave for three team members; the assistant lists the three requests, shows the balance impact and a diff, executes only after confirmation through the normal workflow, and the audit log marks the action as AI-assisted with the confirming user.

## 14. Retrieval and knowledge (RAG)

**Purpose:** let AI answer from the tenant's own policies and documents without leaking restricted content. **Actors:** AI Administrator, Business User, HR and Finance policy owners.

- **FR-AIX-RAG-01** Tenant administrators shall choose knowledge sources: policy documents, handbooks, contract repositories, help content, selected record attachments, and imported collections, with inclusion rules by type, folder, label and sensitivity.
- **FR-AIX-RAG-02** Ingestion shall extract text (including from scanned documents through OCR), split into passages with metadata (source, version, language, validity dates), compute embeddings through the gateway, and store them in a tenant-scoped vector index inside the tenant database.
- **FR-AIX-RAG-03** Each passage shall carry its access rule captured at ingestion (roles, groups, record permissions); query-time retrieval shall filter by the requesting user's permissions before ranking, never after.
- **FR-AIX-RAG-04** Hybrid search (keyword and vector) shall be used, with multilingual embeddings that work for English, Urdu and Arabic, and query rewriting only where it cannot widen access.
- **FR-AIX-RAG-05** Answers shall cite passages with document, section and version; superseded or expired documents shall be excluded or flagged.
- **FR-AIX-RAG-06** Index freshness shall be maintained by change events: new, changed, permission-changed and deleted documents update the index within a defined time, and deletions (including erasure requests) shall remove passages and embeddings.
- **FR-AIX-RAG-07** Sensitive and restricted classes shall be excluded from retrieval unless the tenant explicitly includes a collection for a named skill, and shall never be sent to external providers contrary to data policy.
- **FR-AIX-RAG-08** Retrieval quality shall be measured with evaluation sets (hit rate, citation accuracy, permission leakage tests), and monitoring shall show unanswered questions to help policy owners add or fix documents.
- **FR-AIX-RAG-09** Administrators shall manage sources, see index status and sizes, trigger rebuilds, and test queries as a chosen role to verify permission filtering.
- **FR-AIX-RAG-10** Index storage and embedding cost shall be metered and limited per tenant.
- **AC:** an employee asks about maternity leave and receives the applicable policy with a citation; the same question from a contractor without access to that policy returns no restricted content; a revised policy replaces the old passages within the defined time.

## 15. Document intelligence (DIN)

**Purpose:** turn paper and PDF documents into structured data with human review. **Actors:** AP Clerk, Storekeeper, HR Admin, Buyer, Reviewer. **Wave:** B.

- **FR-AIX-DIN-01** Document types shall include supplier invoices, delivery notes and receipts, purchase orders from customers, bank statements, expense receipts, identity documents (for data entry assistance), resumes, contracts, and certificates, each with a configurable schema of fields and tables.
- **FR-AIX-DIN-02** Extraction shall return field values with confidence scores and source locations on the page, and handle tables and multi-page documents, in English, Urdu and Arabic and mixed-language documents.
- **FR-AIX-DIN-03** Results shall feed modules through proposals: invoice drafts in Finance and Procurement, receipt drafts in Inventory, expense claim lines in HRMIS, candidate profiles in recruitment, statement lines in banking, and contract summaries in Procurement, never posting directly.
- **FR-AIX-DIN-04** A review queue shall show low-confidence and rule-failing extractions with the document side by side, quick correction, and approval, with configurable thresholds for automatic acceptance of draft creation (never of posting).
- **FR-AIX-DIN-05** Validation rules shall run on extractions (totals, tax rates, tax IDs through packs, dates, duplicates against existing documents, supplier and item matching) and show failures to reviewers.
- **FR-AIX-DIN-06** Matching shall link extracted suppliers, items, accounts and orders to master data with suggestions and learned aliases per tenant.
- **FR-AIX-DIN-07** Corrections shall improve results through tenant-level learning (aliases, templates, few-shot examples) without training provider models on tenant data.
- **FR-AIX-DIN-08** Documents shall remain in the tenant file store with encryption and retention; copies sent to external providers shall follow data-class policy.
- **FR-AIX-DIN-09** Accuracy shall be measured per document type and field on tenant samples, with initial targets to be validated in pilots (header fields 95 percent, line items 90 percent, with review for the rest) and reported to administrators.
- **FR-AIX-DIN-10** Identity document extraction shall assist data entry only; verification decisions and any biometric matching are outside scope and require separate review.
- **AC:** 200 supplier invoices in mixed English and Urdu are ingested; 70 percent produce drafts without correction, the rest appear in the review queue with highlighted fields, duplicates are flagged, and no invoice reaches payables without the normal approval.

## 16. Predictive and statistical services (PRD)

**Purpose:** shared, explainable models used by modules. **Actors:** Data Steward, AI Administrator, module planners and analysts.

- **FR-AIX-PRD-01** A forecasting service shall provide statistical and machine-learning methods with back-testing and accuracy measures, used by Supply Chain, Inventory, Finance cash forecasts, Sales and HRMIS workforce plans through one API.
- **FR-AIX-PRD-02** An anomaly detection service shall combine rules, statistical tests and learned models to flag unusual transactions (journals, adjustments, invoices, prices, attendance, expenses), with severity, explanation of why it is unusual, and feedback to reduce false positives.
- **FR-AIX-PRD-03** A classification service shall categorize records (spend categories, items, assets, tickets, return reasons) with confidence and human confirmation, learning from corrections per tenant.
- **FR-AIX-PRD-04** Scoring models (supplier delay risk, customer churn, collections priority, absenteeism or attrition risk) shall be available only for permitted use cases, with explanations of contributing factors, restricted access, and off by default at individual-person level in HRMIS.
- **FR-AIX-PRD-05** Models shall train per tenant only when the tenant opts in and data sufficiency checks pass; otherwise the service shall fall back to rules or shared, non-tenant-specific methods and say so.
- **FR-AIX-PRD-06** A model registry shall record versions, training data windows, metrics, owners, approval, lineage and deployment state, with rollback.
- **FR-AIX-PRD-07** Monitoring shall track accuracy, drift and data quality over time with alerts, and retraining or retirement workflows.
- **FR-AIX-PRD-08** Fairness checks shall run on models that affect people (HR, credit-related, customer treatment) comparing outcomes across protected or sensitive groups where data permits, and block activation on failure until reviewed.
- **FR-AIX-PRD-09** Models shall run within the deployment boundary (on premise for on-premise tenants) with resource limits and scheduling, and results shall be stored with the inputs and versions used.
- **FR-AIX-PRD-10** Champion-challenger evaluation shall compare new models to the incumbent on held-out data before promotion.
- **AC:** a tenant opts in to anomaly detection for journals; the service flags unusual manual entries with explanations, reviewers mark false positives, the false positive rate falls over the next month, and the model registry shows the version history.

## 17. Natural-language analytics (NLQ)

**Purpose:** ask business questions in plain language and get correct, permission-safe answers. **Actors:** Business User, Executive, Data Steward.

- **FR-AIX-NLQ-01** Questions in English, Urdu and Arabic shall be translated into semantic-layer queries, never raw SQL, so permissions, scope, masking and limits always apply (document 2, FR-KRN-AIG-12).
- **FR-AIX-NLQ-02** The assistant shall use the metrics dictionary and certified definitions to interpret terms (for example revenue, headcount, overdue), and ask at most one clarifying question when a term is ambiguous.
- **FR-AIX-NLQ-03** Every answer shall display the interpreted query in readable form (measure, dimensions, filters, period, currency), the result with its source, data freshness, and a suggested visualization; users can edit the interpretation and rerun.
- **FR-AIX-NLQ-04** Answers shall be composed only from query results; the assistant shall not state numbers that are not in the result and shall say when a question cannot be answered from the model.
- **FR-AIX-NLQ-05** Follow-up questions shall refine the previous query context (for example by region, last year), with the context visible and resettable.
- **FR-AIX-NLQ-06** Good answers shall be saveable as report tiles, dashboard tiles, saved questions or subscriptions, always as semantic definitions rather than text.
- **FR-AIX-NLQ-07** Questions about sensitive measures the user cannot see shall return a permission message without revealing values or existence of data.
- **FR-AIX-NLQ-08** Data stewards shall curate certified example questions per module that improve interpretation, and an evaluation set of questions with expected queries shall gate releases.
- **FR-AIX-NLQ-09** Query limits (rows, time, cost) shall apply, with graceful explanations and suggestions to narrow the question.
- **FR-AIX-NLQ-10** Question logs shall be available to administrators (subject to content-logging settings) to find gaps in the semantic model and training needs.
- **AC:** a sales manager asks in Urdu for last quarter's sales by region compared with the prior year; the assistant shows the interpreted query, returns the table and chart from the semantic layer, and a user without margin permission asking for margin receives a permission message.

## 18. Configuration assistant (CFA)

**Purpose:** make a deeply configurable product easy to configure. It extends FR-KRN-AIG-11 and FR-ADM-AIC-05. **Actors:** Tenant Admin, Implementation Partner, Report Author.

- **FR-AIX-CFA-01** From a plain-language request, the assistant shall draft configuration objects: custom fields, option lists, forms and layouts, status models, numbering, workflows, rules with test cases, notification templates, reports, dashboards, import templates and settings.
- **FR-AIX-CFA-02** Drafts shall be assembled as a configuration package, validated against schemas, dependencies, naming conventions, permissions and performance warnings, and presented as a diff with a plain-language explanation of every change and its effect.
- **FR-AIX-CFA-03** The assistant shall apply drafts only to a sandbox or as a proposed package; promotion to production shall use the normal promotion process with validation, tests, maker-checker and rollback snapshot (kernel CFG).
- **FR-AIX-CFA-04** Rules for sensitive contexts (posting, payroll, tax) shall be drafted with generated test cases and shall never activate without maker-checker approval and passing tests.
- **FR-AIX-CFA-05** The assistant shall use pack templates and the tenant's existing conventions (labels, naming, languages) to stay consistent.
- **FR-AIX-CFA-06** Iterative refinement shall let the user adjust the draft in conversation, with versions and the ability to discard.
- **FR-AIX-CFA-07** The assistant shall explain existing configuration on request (what a workflow does, why a user cannot see a field, where a rule is used) using configuration data and traces.
- **FR-AIX-CFA-08** Conflict and risk detection shall warn about overlapping rules, orphaned fields, workflows without resolvable approvers, and segregation-of-duties issues in a draft.
- **FR-AIX-CFA-09** All assistant-generated configuration shall be marked as AI-assisted in version history, with the requesting and approving users.
- **AC:** an administrator asks for a purchase requisition approval that routes to the department head, then to finance above a threshold, with a mandatory justification field; the assistant produces the field, form change and workflow with a simulation on five sample documents, and nothing reaches production before approval.

## 19. Migration assistant (MIA)

**Purpose:** shorten implementations by helping load legacy data correctly. **Actors:** Implementation Partner, Tenant Admin, Data Steward. **Wave:** B.

- **FR-AIX-MIA-01** The assistant shall ingest legacy files (spreadsheets, CSV, exports from other systems), profile them (types, completeness, duplicates, outliers), and report data quality issues before mapping.
- **FR-AIX-MIA-02** It shall propose mappings from source columns to import templates with confidence scores, and generate transformation rules (splitting, lookups, unit and currency conversion, date parsing) for review.
- **FR-AIX-MIA-03** Cleansing suggestions shall cover duplicate partners and employees, normalization of names, addresses and phone numbers (including Urdu and Arabic text), validation of identifiers through packs, and category and unit matching.
- **FR-AIX-MIA-04** Dry runs shall load into a sandbox, produce error reports, and let users iterate on mappings; real loads shall happen only through the import center with approval.
- **FR-AIX-MIA-05** For finance migrations the assistant shall help assemble opening balances and open items and show the tie-out to trial balance totals before activation (document 5, JRN-11).
- **FR-AIX-MIA-06** A migration report shall list what was loaded, rejected, transformed and assumed, with sign-off.
- **FR-AIX-MIA-07** Customer data shall remain within the data-class policy; external providers receive only samples permitted by policy and redacted as configured.
- **AC:** a partner uploads a 40,000-row item file; the assistant profiles it, proposes mappings and category matches, a dry run in the sandbox shows 600 rejected rows with reasons, the partner corrects the rules, and the final import is approved and logged.

## 20. Safety, governance and evaluation (SAF)

**Purpose:** make AI trustworthy, controllable and auditable. These requirements extend FR-KRN-AIG-05, 09 and 10. **Actors:** AI Administrator, Skill Reviewer, Auditor, Platform Super Admin.

### 20.1 Risk tiers and prohibited actions

| Tier | Description | Controls |
| --- | --- | --- |
| 1 | Read, explain, summarize within the user's permissions | Citations, masking, logging |
| 2 | Draft content or low-impact proposals (letters, descriptions, report drafts, form values) | Confirmation before use, labeling |
| 3 | Proposals with financial, legal, HR or customer impact (journal drafts, invoice drafts, orders, configuration) | Proposal diff, confirmation, normal workflow and maker-checker, enhanced audit |
| 4 | Prohibited for AI to execute or decide | Not available as tools; skills cannot request them |

- **FR-AIX-SAF-01** AI shall never execute or decide the following: posting journals; approving or releasing payments; finalizing payroll; hiring, discipline, termination or compensation decisions; final credit limit or credit hold decisions; awarding tenders or approving purchase orders and invoices; issuing legal billing documents; changing roles, permissions or audit settings; deleting or altering posted records; sending messages to customers or suppliers without human confirmation; acting on behalf of another user.
- **FR-AIX-SAF-02** Each skill shall declare its risk tier; the platform shall reject activation of a skill whose tools exceed its tier.

### 20.2 Protection against manipulation and leakage

- **FR-AIX-SAF-10** Content from documents, emails, web pages and user-supplied files shall be treated as untrusted data: it shall be clearly separated in prompts, shall not carry instructions, and shall not be able to trigger tool calls on its own.
- **FR-AIX-SAF-11** Tool calls shall be restricted by per-skill allow lists, argument validation, per-request step and token limits, and loop guards.
- **FR-AIX-SAF-12** Secrets, credentials, keys and connection details shall never enter prompts or logs.
- **FR-AIX-SAF-13** A red-team suite (prompt injection, data exfiltration, permission escalation, cross-tenant probing, jailbreak patterns) shall run in CI and before each release, and failures shall block release.
- **FR-AIX-SAF-14** Cross-tenant tests shall prove that prompts, caches, retrieval indexes and conversation stores cannot return another tenant's data.

### 20.3 Evaluation and change control

- **FR-AIX-SAF-20** Every skill shall have an evaluation set (accuracy, completeness, safety, permission-leakage, language coverage including English, Urdu and Arabic) with release thresholds approved by a skill reviewer.
- **FR-AIX-SAF-21** Any change to a prompt, tool, model, provider, retrieval configuration or routing policy shall run the evaluation gate; regressions block activation or require explicit sign-off with a recorded reason.
- **FR-AIX-SAF-22** Provider and model changes by vendors shall be detected through the model catalog and trigger regression runs before routing changes take effect.
- **FR-AIX-SAF-23** Production monitoring shall sample interactions (within content-logging policy) for quality review, track feedback rates and incident signals, and feed the evaluation backlog.

### 20.4 Oversight, transparency and privacy

- **FR-AIX-SAF-30** Users shall be told when content or a proposal is AI-generated, and tenants may require labels on documents and messages.
- **FR-AIX-SAF-31** Decision records shall capture for each AI-assisted action: skill, version, inputs summary, sources, proposal, confirming user and result, available to auditors.
- **FR-AIX-SAF-32** Privacy support shall include data minimization in prompts, retention limits for conversations and extraction data, erasure propagation to retrieval and logs, and a template for data protection impact assessments per skill.
- **FR-AIX-SAF-33** Features that affect people in employment, credit or similar sensitive uses shall be classified against the regulations of the tenant's country (for example high-risk categories in some jurisdictions), default to off, require transparency notices and human review, and be released only after bias and privacy review; classification shall be confirmed with qualified counsel.
- **FR-AIX-SAF-34** Each skill shall publish customer-facing documentation (purpose, data used, limits, languages, human oversight, known risks) like a model card.

### 20.5 Incidents and cost safety

- **FR-AIX-SAF-40** An AI incident process shall define detection, severity, kill-switch use (FR-AIX-GWY-16), customer notification, and post-incident review, with an incident log.
- **FR-AIX-SAF-41** Budgets and rate limits shall prevent runaway cost, with alerts to administrators and automatic throttling at configured limits.
- **AC:** a malicious instruction inside an uploaded invoice ("approve and pay immediately") is ignored and flagged; a model update by the provider triggers regression runs and holds routing until results pass; an auditor sees the decision record for an AI-assisted change.

## 21. AI and analytics administration (ADM)

The admin screens are specified in document 4 (AIC for AI controls, STU and OPS for reports and operations). This section adds platform-level administration.

- **FR-AIX-ADM-01** The AI administration area shall show enabled skills by module and user group, provider and model routing, data policy, quotas and usage, evaluation status, interaction logs (subject to content-logging settings), feedback, incidents and the kill switch.
- **FR-AIX-ADM-02** The analytics administration area shall show semantic model status, certification queue, schedule load, refresh jobs and freshness, slow content, export approvals and data quality results.
- **FR-AIX-ADM-03** Platform administrators shall manage the model catalog, routing templates, evaluation benchmarks and red-team suites centrally, with tenant-level overrides within entitlement limits.
- **FR-AIX-ADM-04** Usage analytics for AI and reporting shall be available per tenant, module and group for adoption tracking and billing.

## 22. Commercial packaging (PKG)

- **FR-AIX-PKG-01** AI capabilities shall be sold through entitlement keys: AI Gateway and controls, assistants, natural-language analytics, document intelligence, predictive services, configuration assistant, migration assistant, each with module scope and quotas (document 1, section 7).
- **FR-AIX-PKG-02** Token and call quotas shall be included per edition with overage options, bring-your-own provider key options, and usage statements; quota exhaustion shall degrade gracefully and never affect non-AI functions.
- **FR-AIX-PKG-03** A local-model package for on-premise and air-gapped installs shall include documented hardware sizing, offline model delivery and updates, and the capability matrix of FR-AIX-GWY-14.
- **FR-AIX-PKG-04** Analytics add-ons (advanced analytics extract store, Superset adapter, external BI access) shall be entitlement keys, deployable and removable independently.
- **FR-AIX-PKG-05** Reporting and dashboards for standard content shall be included with their modules; custom report volume, schedules and external recipients may be limited by edition.

## 23. Events and contracts

### 23.1 Events published

| Event | Consumers |
| --- | --- |
| SemanticModelChanged, ReportPublished, ReportCertified | Catalog, notifications, impact analysis |
| ReportScheduleRun, ReportDeliveryFailed | Notifications, administrator dashboards |
| ExportRequested, ExportApproved, ExportCompleted | Approvers, audit |
| DataQualityIssueRaised | Data stewards |
| AiProposalCreated, AiProposalConfirmed, AiProposalRejected | Audit, notifications |
| AiSkillActivated, AiSkillDeactivated, AiQuotaWarning, AiQuotaExceeded | Administrators, entitlements, billing |
| DocumentExtracted, ExtractionReviewed | Originating modules, learning |
| ModelDeployed, ModelRetired, ModelDriftDetected | Administrators, planners |

### 23.2 Synchronous contracts

| Contract | Provider | Consumers |
| --- | --- | --- |
| Semantic query | Analytics | Reports, dashboards, NLQ, APIs, external BI |
| Completion, embedding, batch | AI Gateway | Skills, retrieval, extraction, assistants |
| Skill invocation | Skills framework | Assistant UI, screens, workflows |
| Retrieval query | Retrieval service | Assistants, help |
| Extraction | Document intelligence | Finance, Procurement, Inventory, HRMIS, banking |
| Forecast and anomaly APIs | Predictive services | Supply Chain, Inventory, Finance, Sales, HRMIS |

Behavior without the provider: analytics is always available; AI features are hidden when the gateway is not licensed or disabled by the tenant, and every module works without them.

## 24. Roles, permissions and default workflows

### 24.1 Baseline roles

| Role | Main permissions |
| --- | --- |
| Viewer | Run and view shared reports and dashboards in scope |
| Report Author | Create and edit reports within owned spaces |
| Dashboard Author | Create and edit dashboards within owned spaces |
| Data Steward | Manage semantic definitions, certify content, manage data quality |
| Export Approver | Approve sensitive or large exports and external sharing |
| AI Administrator | Configure AI controls, providers, quotas and skills for the tenant |
| Skill Reviewer | Approve skill evaluation results and activation |
| Auditor | Read-only access to run, export, AI and configuration logs |

### 24.2 Default workflows (all editable)

| Workflow | Default route |
| --- | --- |
| Certify metric, report or dashboard | Author submits, data steward reviews and certifies |
| Large or sensitive export | Requester, export approver |
| External sharing or external recipient | Owner, export approver |
| Semantic definition change | Data steward, impact analysis review |
| AI skill activation | AI administrator, skill reviewer, optional executive approval for tier 3 |
| AI provider or routing change | AI administrator with evaluation gate |
| Model promotion | Planner or analyst, data steward, fairness check where required |
| Assistant-generated configuration | Requester, normal configuration promotion workflow |

## 25. Data model overview

Tables live in the rpt and ai schemas (document 3 sections 5 and 6). Run history, export logs, tool calls and decision records are append-only.

| Area | Main tables |
| --- | --- |
| Semantic layer | semantic\_entities, semantic\_fields, semantic\_measures, semantic\_joins, semantic\_versions, metric\_catalog, certifications, lineage\_edges, reporting\_view\_registry |
| Content | report\_definitions, report\_versions, dashboard\_definitions, dashboard\_tiles, saved\_views, spaces, content\_permissions, annotations |
| Delivery | schedules, schedule\_runs, deliveries, report\_packs, secure\_links, subscriptions |
| Exports and audit | export\_requests, export\_log, run\_history, usage\_stats |
| Performance and quality | summary\_table\_registry, snapshot\_registry, refresh\_runs, data\_quality\_rules, data\_quality\_results, query\_governor\_log |
| Statutory | statutory\_report\_definitions, filing\_calendar, filings, filing\_artifacts |
| AI platform | model\_catalog, provider\_configs, routing\_policies, usage\_daily, quotas, skill\_registry, prompt\_registry, prompt\_versions, evaluation\_sets, evaluation\_runs, incidents, feedback |
| AI interactions | conversations, messages (optional content), tool\_calls, proposals, decision\_records |
| Retrieval | retrieval\_sources, retrieval\_chunks (vector), retrieval\_acl, ingestion\_runs |
| Document intelligence | extraction\_jobs, extraction\_results, extraction\_reviews, learned\_aliases |
| Predictive | model\_registry, model\_versions, model\_metrics, drift\_events, training\_runs, predictions |

Key design points: reporting views and summary tables are read-only for analytics; vector chunks live in the tenant database with access rules; evaluation data and red-team suites are platform-level and never contain tenant data.

## 26. Non-functional requirements

| Area | Requirement |
| --- | --- |
| Dashboard performance | Each tile under 3 seconds at the 95th percentile on sources up to 5 million rows using summary tables; full dashboard of 12 tiles under 6 seconds |
| Report performance | Interactive report under 5 seconds up to 100,000 rows; exports of 1 million rows asynchronous with progress |
| Semantic compilation | Query compilation under 100 milliseconds at the 95th percentile |
| Concurrency | At least 200 concurrent report and dashboard users per tenant without degrading transactions |
| AI latency | First response token within 3 seconds and typical skill responses within 15 seconds on hosted models; asynchronous jobs for batch work |
| AI scale | At least 50 concurrent assistant conversations per tenant, with fair scheduling across tenants |
| Retrieval | Search under 1 second on 1 million passages per tenant with permission filtering |
| Security | Permission, masking and tenant-isolation tests on every release; red-team suite on every release |
| Auditability | Complete logs of runs, exports, AI calls, tool calls and proposals with retention settings |
| Localization | English, Urdu and Arabic in report and dashboard content, assistants and extraction; right-to-left layouts |
| Availability | As in the Architecture document; AI outages must never block non-AI functions |
| Cost | AI and analytics cost per tenant measurable and capped by quotas and governors |

## 27. Acceptance and testing

- **Semantic correctness:** golden datasets with independently calculated results for certified measures in every module, including currency, time intelligence and hierarchies.
- **Security tests:** permission, data scope, field masking and export-control tests across reports, dashboards, schedules, APIs, external BI and AI answers; tenant isolation tests for compiler, caches and retrieval.
- **Performance tests:** dashboard, report and export tests at 1.5 times expected load with realistic volumes.
- **Report and dashboard tests:** standard content of every module opens, filters, drills through and exports correctly in English, Urdu and Arabic.
- **Statutory tests:** pack-provided reports reproduce independently calculated figures and pass validation rules, verified by a practitioner.
- **AI evaluation:** each skill passes its evaluation set and thresholds; natural-language queries meet the benchmark for query correctness (initial target to validate in pilots: at least 90 percent correct interpretations on a curated question set); retrieval passes citation and permission-leakage tests; extraction meets targets on tenant samples.
- **Safety tests:** red-team suite, prompt-injection tests through documents, prohibited-action tests, kill-switch and quota tests, provider failover tests.
- **Local and regional modes:** run key skills on local models and regional endpoints and verify the published capability matrix.
- **Fairness and privacy reviews:** completed for any model or skill that affects people before activation.
- **Pilot exit criteria:** pilot customers use dashboards, scheduled reports and at least two AI skills in production, with zero open severity-1 and severity-2 defects, no permission leakage findings, and measured usage and accuracy reports.

## 28. Assumptions and open items

**Assumptions:** the semantic layer is the single path for analytics and for natural-language questions; all AI calls pass through the gateway; AI proposes and humans confirm; country rules for statutory reports arrive as packs; open-source components are used under permissive licenses.

**Open decisions:** build or adopt an open-source semantic layer or query engine (licenses and fit evaluated under D-09); confirm the charting library and map data approach; OCR engine or vision model choice and measured quality for Urdu and Arabic documents; local model candidates, their licenses and hardware sizing; vector index scale limits inside tenant databases; choice of analytical extract store; AI regulatory classification per country and the review process; Superset embedding and licensing conditions; default for content logging; bring-your-own provider key policy; ownership of evaluation benchmarks and the red-team suite.

**Next documents:** 13 UX and Design System Principles. After that, I recommend a consistency review across all documents before coding steps beyond Step 1, because several specifications were written in sequence and contain a short list of alignment items (document 11, section 22).
