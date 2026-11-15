# ERP Platform Kernel: Functional Specification (v0.2)

Document 2 of the planning set | Prepared for Ibrahim | 10 November 2026 | Companion to Architecture and Foundation v0.2

## 1. Introduction

### 1.1 Purpose

This document defines what the platform kernel shall do in testable functional terms. The kernel is the shared foundation under every module (Finance, HRMIS, Procurement, Inventory, Assets, Sales, Supply Chain): tenancy, organization, identity, settings, audit, numbering, parties, catalog, tax, localization, metadata, workflow, rules, notifications, documents, integration, jobs, entitlements, packs and the AI gateway. It replaces the Phase 0 content of the earlier HR MIS specification, which should now refer here.

The admin screens that operate the kernel are specified in document 4. Analytics and reporting are specified in document 9. Table-level design is in document 3.

### 1.2 Conventions

- Requirement IDs follow FR-KRN-\<area>-\<number>, for example FR-KRN-WFL-03.
- Priority: (M) must, (S) should, (C) could. Unmarked requirements are (M).
- Wave: A means needed for the first release (Finance and HRMIS), B and C are later waves. Unmarked requirements are wave A. Areas that are thin in wave A say what is deferred.
- The word shall denotes a mandatory behavior.
- Each area lists Purpose, Actors, Functional Requirements (FR), Business Rules (BR) and Acceptance Criteria (AC).
- Every configurable behavior shall be reachable through both the admin UI and the public API, with the same permissions.

### 1.3 Actors

| Actor | Description |
| --- | --- |
| Platform Super Admin | Vendor staff operating the control plane; no access to tenant business data by default |
| Tenant Admin | Configures the tenant: organization, users, roles, settings, workflows, fields |
| Company Admin | Administers one legal entity inside a tenant |
| Business User | Uses modules within granted permissions |
| Approver | Acts on workflow tasks |
| Auditor | Read-only access to data and audit logs |
| Implementation Partner | Configures tenants on behalf of customers within delegated permissions |
| API Client | Integration authenticated by API key or OAuth client credentials |
| AI Agent | AI Gateway acting for a user, with that user's permissions only |

### 1.4 Definitions

| Term | Meaning |
| --- | --- |
| Tenant | A customer account with its own database, configuration, files and keys |
| Company | A legal entity inside a tenant, with its own tax IDs, currency, fiscal calendar and books |
| Environment | Production, UAT or sandbox copy of a tenant |
| Entitlement | Signed claims that define modules, limits and term a tenant may use |
| Pack | Installable, versioned bundle of configuration (country, industry, template, connector, language) |
| Configuration package | Exported set of configuration objects used to promote changes between environments |
| Maker-checker | A change by one user that takes effect only after approval by a different user |
| Data scope | A rule limiting which records a user can see, by company, branch, department or attribute |

## 2. Global requirements

| ID | Requirement |
| --- | --- |
| GR-01 | Standard pages and list views shall load in under 2 seconds at the 95th percentile with the target data volumes of Architecture section 3.7 |
| GR-02 | A tenant shall support at least 50,000 employees, 500 companies and 5 million ledger lines per year; the platform shall run at least 500 tenants per region on shared infrastructure |
| GR-03 | All long-running operations shall be asynchronous, resumable, and visible in the job center with progress and cancel |
| GR-04 | All data shall be encrypted in transit (TLS 1.2 or higher) and at rest; sensitive fields shall support field-level encryption and masking |
| GR-05 | Every create, update, delete, export and view of sensitive data shall be written to the immutable audit log |
| GR-06 | All lists shall support search, filter, sort, column selection, saved views and export, subject to permissions |
| GR-07 | English, Urdu and Arabic shall be supported in all end-user screens, documents and notifications, with right-to-left layouts and Hijri and Gregorian dates |
| GR-08 | The web client shall be responsive from 360 pixels width upward and meet WCAG 2.1 AA |
| GR-09 | Every API shall be versioned, documented in OpenAPI, and use RFC 9457 problem details |
| GR-10 | All configuration shall be exportable and importable as versioned packages |
| GR-11 | The system shall run fully offline for licensing, installation, updates and diagnostics when deployed on-premises |
| GR-12 | No module shall break when another module is not licensed; features that depend on an absent module shall hide or degrade gracefully |

## 3. Tenancy and provisioning (TEN)

**Purpose:** create, isolate, run and retire tenants from one codebase in SaaS, private cloud and on-premise modes. **Actors:** Platform Super Admin, Tenant Admin.

- **FR-KRN-TEN-01** The system shall keep a control-plane registry of tenants with name, status (provisioning, active, suspended, offboarding, deleted), region, cluster, environment type, and creation data.
- **FR-KRN-TEN-02** The system shall provision a tenant from a template: create its database, run kernel and licensed-module migrations, install selected country and industry packs, seed roles, default workflows and numbering, create the first administrator, apply entitlements and run smoke tests.
- **FR-KRN-TEN-03** Each tenant shall have its own database, file-store prefix, encryption keys, audit log and configuration. No query, job or API call shall cross tenant databases except explicit control-plane operations.
- **FR-KRN-TEN-04** The tenant shall be resolved server-side from the host name, token or API key, and never from request payload fields.
- **FR-KRN-TEN-05** The system shall support production, UAT and sandbox environments per tenant, with data masking rules applied when copying to non-production.
- **FR-KRN-TEN-06** A tenant shall be assignable to a hosting region and clusters; moving a tenant between clusters or regions shall be supported by backup, restore and a connection switch with verification.
- **FR-KRN-TEN-07** The on-premise package shall provision exactly one tenant with the same code, validating the license file instead of a subscription record.
- **FR-KRN-TEN-08** Tenants shall have custom domain, branding (logo, colors, login page) and email sender identity.
- **FR-KRN-TEN-09** The system shall suspend a tenant into read-only or locked state per the entitlement state machine without deleting data.
- **FR-KRN-TEN-10** The system shall export a complete portable tenant package (data and configuration) and shall support scheduled deletion with a retention window and a deletion certificate. (S)
- **FR-KRN-TEN-11** The system shall run fleet migrations across tenants in batches with canary tenants first, isolating a failed tenant in maintenance mode without stopping others.
- **FR-KRN-TEN-12** A vendor user may access tenant business data only through a time-boxed, logged, tenant-approved support grant.
- **BR:** a tenant cannot be deleted while a legal hold is set or inside the retention window.
- **AC:** provisioning from a template completes in under 10 minutes; automated tests prove a user in tenant A cannot retrieve any record of tenant B through any API or UI; a failed migration on one tenant leaves other tenants running.

## 4. Organization model (ORG)

**Purpose:** represent groups, legal entities and hierarchies in any shape. **Actors:** Tenant Admin, Company Admin.

- **FR-KRN-ORG-01** The system shall support multiple companies per tenant, each with country, base currency, optional group currency, tax IDs, fiscal calendar, holiday calendar, bank accounts, letterhead and default language.
- **FR-KRN-ORG-02** The system shall support branches and locations under a company, with address, geo-coordinates and operating hours.
- **FR-KRN-ORG-03** The system shall support configurable organization unit types (division, department, wing, directorate, team) with unlimited depth and effective dating.
- **FR-KRN-ORG-04** The system shall support multiple hierarchy types on the same units (reporting, cost, geographic, project) (S).
- **FR-KRN-ORG-05** The system shall support fiscal calendars of any start month, 4-4-5 or 13-period patterns, and adjustment periods, with period statuses (future, open, soft-closed, closed).
- **FR-KRN-ORG-06** An organization unit shall optionally reference a Finance cost center; the reference shall remain empty when Finance is not licensed.
- **FR-KRN-ORG-07** The system shall support inter-company relationships (parent, subsidiary, ownership percentage) for consolidation (wave B).
- **FR-KRN-ORG-08** The tenant shall rename any standard label per language (for example Department to Wing).
- **BR:** a unit cannot be closed while it has active members or open positions unless a reassignment is given; a period cannot be closed with unposted documents unless the policy allows.
- **AC:** a three-company, five-level organization with matrix reporting is created through the admin UI without developer help.

## 5. Identity and access (IAM)

**Purpose:** authenticate users and authorize every action, down to field level. **Actors:** Tenant Admin, Company Admin, Business User, API Client.

**Authentication**

- **FR-KRN-IAM-01** The system shall support username and password with configurable policy (length, complexity, history, expiry, lockout), and password reset by verified email or SMS.
- **FR-KRN-IAM-02** The system shall support multi-factor authentication (authenticator app, SMS or email code, hardware key) with enforcement per tenant, role or user.
- **FR-KRN-IAM-03** The system shall support SSO through OIDC and SAML 2.0, and LDAP and Active Directory directory sync and authentication.
- **FR-KRN-IAM-04** The system shall provide session management: configurable timeout, concurrent session limits, forced sign-out, device and session list for each user.
- **FR-KRN-IAM-05** The system shall issue API keys and OAuth client credentials with scopes, expiry, IP allow lists and rate limits.
- **FR-KRN-IAM-06** The system shall support user invitations, bulk user import, and automatic deprovisioning through SCIM (S).

**Authorization**

- **FR-KRN-IAM-10** Access shall be permission-based. Each module and kernel component registers permissions (resource, action) through its manifest; roles are sets of permissions; users hold multiple roles.
- **FR-KRN-IAM-11** Permission sets shall be editable per tenant; baseline roles shipped by modules and packs are defaults only.
- **FR-KRN-IAM-12** Data scopes shall restrict records by company, branch, department, hierarchy position (own, team, subtree) and attribute conditions, and be applied on every query, report and export.
- **FR-KRN-IAM-13** Field-level security shall allow hidden, masked, read-only or editable per field and role, including custom fields.
- **FR-KRN-IAM-14** Maker-checker shall be configurable on any sensitive action (permission change, posting rule change, payroll finalization, bank detail change, bulk export).
- **FR-KRN-IAM-15** Segregation-of-duties rules shall be configurable (for example the creator of a payment cannot approve it) and checked at role assignment and at runtime; conflicts shall be reportable.
- **FR-KRN-IAM-16** Temporary access and delegation shall be supported with start, end, reason and audit.
- **FR-KRN-IAM-17** An access review report shall list users, roles, last login and sensitive permissions for periodic recertification. (S)
- **FR-KRN-IAM-18** User and company limits from the entitlement shall be enforced when creating or activating users and companies.
- **BR:** a user cannot grant a permission they do not hold; the last tenant admin cannot be removed or disabled; every permission decision uses the same engine for UI, API and AI.
- **AC:** a manager sees only their team's records and no salary fields while a payroll officer sees salary but only for assigned companies; an AI request cannot return data the user cannot see.

## 6. Settings and feature flags (SET)

**Purpose:** typed, layered configuration for the product and for tenants. **Actors:** Tenant Admin, Company Admin, Platform Super Admin.

- **FR-KRN-SET-01** Every setting shall declare key, type, default, validation, allowed layers (product, country pack, industry pack, tenant, company, role or user), visibility, and whether a change needs approval.
- **FR-KRN-SET-02** The system shall resolve effective values by layer with the most specific layer winning and shall show where each effective value came from.
- **FR-KRN-SET-03** Settings shall be searchable, grouped by module, with help text per language.
- **FR-KRN-SET-04** Changes shall be versioned, audited, exportable in configuration packages, and reversible to a prior version.
- **FR-KRN-SET-05** Feature flags shall switch capabilities on or off per tenant, company or user group, independent of entitlements (entitlements say what is allowed; flags say what is enabled).
- **FR-KRN-SET-06** Settings changes shall take effect without restart, with caching invalidated across hosts.
- **AC:** a company-level override of a tenant default applies only to that company and is shown with its source; reverting restores the inherited value.

## 7. Audit (AUD)

**Purpose:** a trustworthy record of who did what, when, and from where. **Actors:** Auditor, Tenant Admin.

- **FR-KRN-AUD-01** The system shall record, in the same transaction as the change, every create, update and delete with actor, timestamp, entity, record id, field-level before and after values, source (UI, API, import, job, AI) and correlation id.
- **FR-KRN-AUD-02** The system shall record views and exports of fields classified sensitive, and authentication and security events (login, failure, MFA, permission change, key change, license change).
- **FR-KRN-AUD-03** The audit log shall be append-only, tamper-evident through chained hashes, and not editable by any role including tenant admin.
- **FR-KRN-AUD-04** Audit entries shall be searchable by user, entity, record, date and action, and exportable with permission.
- **FR-KRN-AUD-05** Retention and archival of audit entries shall be configurable, with partitioned storage and verification of archived chains.
- **FR-KRN-AUD-06** Changes made through AI shall be marked as AI-assisted, with the confirming user recorded.
- **FR-KRN-AUD-07** A record's history view shall be available from every detail page to authorized users.
- **AC:** modifying an audited field produces an entry with both values within the same transaction; attempting to alter or delete an entry through any interface fails and is itself logged.

## 8. Numbering (NUM)

**Purpose:** configurable, legally compliant document numbers. **Actors:** Tenant Admin, Company Admin.

- **FR-KRN-NUM-01** Numbering schemes shall be defined per document type, company, branch and fiscal year with prefix, suffix, date parts, padding, reset rule (never, yearly, monthly) and starting value.
- **FR-KRN-NUM-02** A scheme shall be gap-allowed or gap-free; gap-free schemes allocate at posting time under lock and never skip on rollback.
- **FR-KRN-NUM-03** Numbers shall be assigned by the system on save or post according to the document type, and never editable afterwards.
- **FR-KRN-NUM-04** Manual numbering with uniqueness check shall be selectable where law or practice requires. (S)
- **FR-KRN-NUM-05** Voided numbers shall be retained with status and reason, and a void report shall exist for gap-free series.
- **FR-KRN-NUM-06** Numbering shall support concurrent users without duplicates under load.
- **AC:** 100 concurrent users posting documents in a gap-free series produce a continuous sequence with no duplicates.

## 9. Parties: business partners (PTY)

**Purpose:** one record for every external organization or person the business deals with, shared by Finance, Procurement, Sales and Supply Chain. **Actors:** Business User, Company Admin.

- **FR-KRN-PTY-01** A business partner shall have one or more roles (supplier, customer, both, bank, tax authority, carrier, other) and role-specific data, so the same record serves purchasing and selling.
- **FR-KRN-PTY-02** A partner shall hold multiple addresses (billing, shipping, registered), contacts, bank accounts, tax registrations (per country), payment terms, currency, credit limit, and classification tags.
- **FR-KRN-PTY-03** Partners may be scoped to all companies in the tenant or to specific companies.
- **FR-KRN-PTY-04** The system shall detect probable duplicates on create (tax ID, name similarity, bank account) and offer merge with full audit and re-pointing of references through registered contracts.
- **FR-KRN-PTY-05** Bank account changes shall use maker-checker by default and notify designated approvers.
- **FR-KRN-PTY-06** Partner status (draft, active, on hold, blocked, archived) shall control whether new transactions are allowed, with reason codes.
- **FR-KRN-PTY-07** Partner onboarding shall be configurable as a workflow with required documents, approvals and expiry reminders (for example tax certificates). (S)
- **FR-KRN-PTY-08** Custom fields, layouts and validation shall apply to partners through the metadata engine.
- **FR-KRN-PTY-09** Tax ID formats shall be validated per country through packs (for example NTN and CNIC in Pakistan, VAT number in KSA, EIN in the United States).
- **BR:** a partner with open transactions cannot be deleted, only archived; a blocked partner cannot be selected on new documents.
- **AC:** the same record is selected as supplier on a purchase order and customer on a sales order; a duplicate tax ID triggers a warning with a merge option.

## 10. Catalog: items, services and units (CAT)

**Purpose:** a single catalog of things bought, stocked, sold, or consumed, so Procurement and Sales work without Inventory. **Actors:** Business User, Company Admin.

- **FR-KRN-CAT-01** The catalog shall hold items of configurable types (stock item, non-stock item, service, expense category, fixed-asset class, bundle) with code, multilingual name, description, category, status and images.
- **FR-KRN-CAT-02** Categories shall form a configurable hierarchy with inherited defaults (tax code, accounts, units).
- **FR-KRN-CAT-03** Units of measure and conversion tables shall support multiple units per item (base, purchase, sales, stock), with rounding rules, and global and item-specific conversions.
- **FR-KRN-CAT-04** Item attributes and variants (size, color, grade) shall be configurable per category, with variant generation. (S)
- **FR-KRN-CAT-05** Items shall support multiple identifiers (SKU, barcode, supplier part number, manufacturer part number) with uniqueness rules.
- **FR-KRN-CAT-06** Module-specific attributes (valuation method, batch and serial tracking, depreciation class, reorder rules) shall be added to items by the owning module without altering the kernel table, and shall appear only when that module is licensed.
- **FR-KRN-CAT-07** Items may be scoped to all companies or to specific companies.
- **FR-KRN-CAT-08** Price lists and effective-dated prices shall be supported by Procurement and Sales using catalog references; the catalog itself holds only standard cost and standard price defaults.
- **FR-KRN-CAT-09** Bulk import, export and mass update shall be supported with validation and preview.
- **BR:** an item used on any posted document cannot change its base unit; inactivating an item blocks new transactions but keeps history.
- **AC:** a tenant licensed for Procurement and Finance only can create service items and purchase them; adding Inventory later exposes stock attributes on the same items without data migration.

## 11. Tax (TAX)

**Purpose:** a country-neutral tax engine whose content comes from packs. **Actors:** Company Admin, Business User.

- **FR-KRN-TAX-01** The system shall hold tax codes, rates, effective dates, jurisdictions, tax accounts (by reference), and applicability (sales, purchase, both).
- **FR-KRN-TAX-02** Tax determination shall combine item or category, partner, company, jurisdiction, document type and date through configurable rules.
- **FR-KRN-TAX-03** The engine shall support inclusive and exclusive pricing, compound taxes, multiple taxes per line, withholding taxes at document or payment level, exemptions with certificates, and rounding policy.
- **FR-KRN-TAX-04** Tax rules shall be effective-dated so past documents recompute correctly and rate changes apply by document date.
- **FR-KRN-TAX-05** Country packs shall provide codes, rules, returns and statutory report layouts; tenants may extend them without overwriting the pack.
- **FR-KRN-TAX-06** An integration point shall let a connector call an external tax service (for example a US sales-tax rate service) for determination.
- **FR-KRN-TAX-07** Tax calculation shall return a trace explaining each tax line for audit and support.
- **AC:** a purchase with provincial sales tax and a withholding tax computes both, shows a trace, and re-computes correctly if the document date crosses a rate change.

## 12. Localization and calendars (LOC)

**Purpose:** languages, currencies, calendars and formats. **Actors:** Tenant Admin, Company Admin.

- **FR-KRN-LOC-01** The system shall support English, Urdu and Arabic at launch with right-to-left mirroring of layouts, and further languages through translation catalogs and language packs.
- **FR-KRN-LOC-02** Users shall choose language, time zone, date format, calendar (Gregorian or Hijri display), number format and first day of week; companies set defaults.
- **FR-KRN-LOC-03** Dates shall be stored in Gregorian and rendered in the user's calendar; Hijri conversion shall be configurable by method (for example Umm al-Qura) and support manual moon-sighting adjustments for religious-holiday calendars.
- **FR-KRN-LOC-04** Currencies shall be configurable with codes, symbols, decimals and rounding; exchange rates shall support multiple rate types (daily, average, budget), manual entry, import, and a connector for automatic feeds.
- **FR-KRN-LOC-05** Translation of master data names and labels shall be managed through the admin UI with export and import for translators, and missing translations shall fall back to a configured language.
- **FR-KRN-LOC-06** Holiday calendars shall be defined per company, branch or location, with packs providing national holidays.
- **FR-KRN-LOC-07** All generated documents and notifications shall render in the recipient's language where a template exists.
- **FR-KRN-LOC-08** Search shall normalize Arabic and Urdu text (diacritics, letter variants, digit systems).
- **AC:** a user switches to Arabic and sees mirrored layouts, Hijri dates and Arabic-Indic digits if chosen, while the stored data and reports remain Gregorian and consistent.

## 13. Metadata and customization (MET)

**Purpose:** make fields, forms, objects, statuses and menus data, not code. **Actors:** Tenant Admin, Implementation Partner.

**Custom fields**

- **FR-KRN-MET-01** Admins shall add custom fields to any extensible entity (types: text, long text, number, decimal, money, date, date-time, boolean, single and multi-select, lookup to another entity, user, file, formula) with label per language, help text, default, validation, uniqueness, and conditional visibility or requirement.
- **FR-KRN-MET-02** Custom fields shall be permission-controlled per role (hidden, read-only, editable) and carried through APIs, imports, exports, reports and workflows.
- **FR-KRN-MET-03** Option lists shall be reusable, effective-dated and translatable.
- **FR-KRN-MET-04** Frequently filtered fields shall be promotable to indexed storage by an admin action with impact warning. (S)
- **FR-KRN-MET-05** Deleting a custom field shall be a soft retire that hides the field and keeps its data until an explicit purge.

**Custom objects**

- **FR-KRN-MET-10** Admins shall define custom objects (entity name, fields, relationships to existing entities, list and form layouts, numbering, status model, permissions, workflow hooks) and manage records through generated screens and APIs. (wave B for full relationships; basic objects in wave A)
- **FR-KRN-MET-11** Custom objects shall be reportable through the semantic layer and available in workflows and rules.

**Layouts, labels and menus**

- **FR-KRN-MET-20** Form layouts shall be configurable per entity, role and company: sections, tabs, field order, columns, read-only rules, conditional sections, and mobile variants.
- **FR-KRN-MET-21** List layouts shall support column sets, default filters, grouping, totals, row actions and saved views shared by user, role or company.
- **FR-KRN-MET-22** Labels and terminology shall be overridable per language and layer.
- **FR-KRN-MET-23** Navigation menus shall be configurable per role, with ordering, grouping, icons, links to reports and external URLs.

**Status models**

- **FR-KRN-MET-30** Each document type shall have a status model (states, allowed transitions, who may transition, required fields per state, automatic actions) defined as data; modules ship defaults and tenants may adjust within module-defined safe limits.
- **FR-KRN-MET-31** Statuses that carry accounting or legal meaning (posted, finalized, closed) shall be locked against removal.
- **BR:** configuration that would orphan existing data (removing a used option, state or field type change) is blocked or requires a migration plan; every change is versioned and testable in a sandbox before production.
- **AC:** an admin adds a mandatory custom field to purchase requests, hides it from one role, and sees it in the list, API and report within a minute and without a release.

## 14. Workflow and approvals (WFL)

**Purpose:** a deterministic, auditable approval and routing engine. **Actors:** Tenant Admin, Approver, Business User. **Wave:** sequential and role-based routing in wave A; parallel, conditional and escalation depth completed in wave B.

- **FR-KRN-WFL-01** Workflow definitions shall be created per document type with steps, conditions, approver resolution and outcomes, stored as versioned data and edited in a visual designer.
- **FR-KRN-WFL-02** Approver resolution shall support named user, role, organization hierarchy (manager, manager's manager, department head), position, partner or document field, expression, and queue with claim.
- **FR-KRN-WFL-03** Conditions shall use the rules engine (for example amount thresholds, department, item category, risk flags) to choose routes and skip steps.
- **FR-KRN-WFL-04** Steps shall support sequential, parallel (all or any, quorum), and conditional branching. (parallel and conditional in wave B)
- **FR-KRN-WFL-05** Delegation and out-of-office substitution shall be configurable by date range, with audit of who acted for whom.
- **FR-KRN-WFL-06** SLAs shall define reminders and escalations by elapsed time, working calendar and priority. (S in wave A, M in wave B)
- **FR-KRN-WFL-07** Approvers shall be able to approve, reject with reason, request changes, reassign, or ask for information, with comments and attachments; all decisions shall be kept in a decision history.
- **FR-KRN-WFL-08** The approval inbox shall list pending tasks across modules with filters, bulk approval where allowed, mobile access and email or push deep links.
- **FR-KRN-WFL-09** The engine shall prevent self-approval and apply segregation-of-duties rules from IAM.
- **FR-KRN-WFL-10** Running instances shall stay on the version they started with; new versions apply to new instances unless migrated explicitly.
- **FR-KRN-WFL-11** Workflows shall support automated steps (send notification, call webhook, set field, create document, run job) with retry and failure handling.
- **FR-KRN-WFL-12** A simulator shall run a definition against sample documents and show the route before activation.
- **FR-KRN-WFL-13** Long-running orchestration (multi-day, event-driven) may use a separate engine behind the same task model; approvals stay in the native engine. (C)
- **BR:** a workflow cannot be activated if a step has no resolvable approver for its conditions in test; changes to workflows for posting, payments or payroll require maker-checker.
- **AC:** a purchase requisition above a threshold routes to the department head then finance, skips a step when the approver is the requester, escalates after two working days, and shows the full decision history.

## 15. Rules and expressions (RUL)

**Purpose:** business logic stored as data: eligibility, thresholds, tax and posting determination, validation, pricing, policy calculations. **Actors:** Tenant Admin, Implementation Partner. **Wave:** A (needed by posting rules and HR policies).

- **FR-KRN-RUL-01** The engine shall evaluate expressions in a sandbox with typed variables supplied by each rule context, an allowed function library (math, date, string, lookup, aggregation over provided collections), and no access to files, network or reflection.
- **FR-KRN-RUL-02** Each evaluation shall have time and memory limits and fail safely with a clear error.
- **FR-KRN-RUL-03** Rule types shall include validation (block or warn), default value, calculation, eligibility, routing condition, determination (for example account, tax code, price), and notification condition.
- **FR-KRN-RUL-04** Rules shall be authored through a guided editor with autocomplete of available variables and functions, syntax check, and plain-language rendering, and through a table-style decision table for common cases.
- **FR-KRN-RUL-05** Every rule shall be versioned, effective-dated, testable against sample data and saved test cases, and reversible to a prior version.
- **FR-KRN-RUL-06** Activation of rules in sensitive contexts (posting, payroll, tax) shall require maker-checker and a passing test suite.
- **FR-KRN-RUL-07** Rule evaluation shall produce a trace (inputs, branches taken, output) available to support and auditors.
- **FR-KRN-RUL-08** A dependency view shall show which workflows, templates and policies use a rule before it is changed.
- **FR-KRN-RUL-09** Heavy repeated evaluation (payroll for thousands of employees) shall run through compiled or cached expressions meeting the performance targets of the consuming module.
- **AC:** an admin writes a rule granting an allowance to grade 5 and above with 2 years of service, tests it on sample employees, activates it through maker-checker, and the trace explains each result.

## 16. Notifications (NTF)

**Purpose:** reach people through the right channel with the right content. **Actors:** Tenant Admin, Business User.

- **FR-KRN-NTF-01** Channels shall include in-app, email, SMS, WhatsApp Business (through a gateway), and mobile push, each with a pluggable provider and delivery log.
- **FR-KRN-NTF-02** Templates shall be configurable per event, channel and language, with placeholders from the event context, preview, and test send.
- **FR-KRN-NTF-03** Subscriptions and triggers shall be rule-based (for example notify the line manager when leave is requested, notify finance when a payment run fails) and configurable per tenant and company.
- **FR-KRN-NTF-04** Users shall set channel preferences and quiet hours, except for notifications marked mandatory.
- **FR-KRN-NTF-05** Notifications shall be queued, retried with back-off, deduplicated, rate limited, and visible in a delivery log with status and failure reasons.
- **FR-KRN-NTF-06** Digest and batching shall be supported for noisy events. (S)
- **FR-KRN-NTF-07** Email sending shall support tenant-specific sender identity, SPF and DKIM guidance, and bounce handling.
- **AC:** a leave request triggers an email in the manager's language and an in-app item; a failed SMS is retried and then shown as failed in the log.

## 17. Documents and templates (DOC)

**Purpose:** attachments, generated documents and print templates. **Actors:** Business User, Tenant Admin.

- **FR-KRN-DOC-01** Any record may carry attachments with type, description, version, expiry date and permissions inherited from the record.
- **FR-KRN-DOC-02** Storage shall be abstracted (local disk, network share, S3-compatible) with checksum, size and type validation, a configurable virus-scan hook, and per-tenant encryption.
- **FR-KRN-DOC-03** Versioning shall keep prior versions with who and when, and allow restore.
- **FR-KRN-DOC-04** Retention rules shall define how long document types are kept and what happens afterwards (archive, delete, legal hold).
- **FR-KRN-DOC-05** A template engine shall generate letters, vouchers, invoices, payslips, labels and statutory forms from data and templates with conditional blocks, tables, images, barcodes and QR codes, in PDF, Word and HTML.
- **FR-KRN-DOC-06** A visual template designer shall edit headers, footers, letterheads, fonts and layouts per company and language, with versioning and preview against sample records.
- **FR-KRN-DOC-07** Generated documents may be stored against the record, signed (digital signature, e-signature connector), emailed, and watermarked by status (draft, approved).
- **FR-KRN-DOC-08** Expiry tracking shall raise reminders for documents that expire (licenses, contracts, certificates).
- **AC:** an invoice template with a QR code renders in Arabic and English with the company letterhead, is stored against the invoice, and a prior version of an attachment can be restored.

## 18. Integration (INT)

**Purpose:** open, safe, and observable ways to connect other systems. **Actors:** Tenant Admin, API Client, Implementation Partner.

- **FR-KRN-INT-01** Every module capability shall be exposed through a versioned REST API with OpenAPI, consistent filtering, sorting, cursor pagination, idempotency keys, and bulk endpoints.
- **FR-KRN-INT-02** Webhooks shall deliver configured events with signed payloads, retries with back-off, a dead-letter queue, replay, and a delivery log.
- **FR-KRN-INT-03** Import and export templates shall be configurable: file formats (CSV, Excel, JSON, XML), column mapping, transformations, validation preview, error report, and rollback of an import batch where feasible.
- **FR-KRN-INT-04** A bulk data loader shall process large files asynchronously with progress, restartability and duplicate handling; target 50,000 rows per file.
- **FR-KRN-INT-05** A connector framework shall define a standard adapter contract for banks, tax portals, e-invoicing services, biometric devices, SMS and email gateways, and accounting export, with configuration, credentials in secret storage, health checks and logs.
- **FR-KRN-INT-06** A scheduled file-exchange facility (SFTP, shared folder) shall support inbound and outbound jobs with archiving.
- **FR-KRN-INT-07** API clients shall be rate limited, throttled per tenant, and observable (usage, errors, latency) with alerts.
- **FR-KRN-INT-08** Sandbox endpoints and test data shall be available to partners per tenant environment.
- **FR-KRN-INT-09** Accounting Export connector: when Finance is not licensed, subledger events shall be exportable as journals to files or to external accounting systems with configurable account mapping.
- **AC:** an external system creates a purchase requisition through the API with an idempotency key; a retry creates no duplicate; a webhook to a failing endpoint retries then lands in the dead-letter queue and can be replayed.

## 19. Jobs and scheduling (JOB)

**Purpose:** reliable background execution. **Actors:** Tenant Admin, Platform Super Admin.

- **FR-KRN-JOB-01** Jobs shall be defined with schedule (cron-like, calendar-aware), parameters, owner, retry policy, concurrency limit, timeout and priority.
- **FR-KRN-JOB-02** Long-running jobs shall run in chunks with checkpoints so they resume after restart, and expose progress, logs, and cancel.
- **FR-KRN-JOB-03** The job center shall show running, queued, failed and completed jobs for the tenant with filters, rerun, and download of results.
- **FR-KRN-JOB-04** Per-tenant concurrency caps and fair scheduling shall stop one tenant's jobs from starving others.
- **FR-KRN-JOB-05** Jobs shall be cluster-safe: a job runs once even with multiple workers.
- **FR-KRN-JOB-06** Operational alerts shall fire for failed, stuck or overdue jobs, outbox lag, and failed postings.
- **AC:** a 50,000-row import interrupted by a worker restart resumes from its last checkpoint without duplicating rows.

## 20. Entitlements and licensing (ENT)

**Purpose:** enforce what each tenant has bought. **Actors:** Platform Super Admin, Tenant Admin, license issuer (vendor).

- **FR-KRN-ENT-01** An entitlement shall define deployment type, edition, licensed modules and feature keys, packs and add-ons, limits (named users, concurrent users, companies, active employees, storage, API calls, AI tokens), term, maintenance expiry, support tier, source flag, and optional server binding.
- **FR-KRN-ENT-02** On-premise entitlements shall come from a signed license file verified offline against an embedded public key; SaaS entitlements from the control-plane subscription.
- **FR-KRN-ENT-03** The module loader shall load only licensed modules; unlicensed modules' schemas, menus, APIs and screens shall not exist for the tenant.
- **FR-KRN-ENT-04** The state machine (active, warning, grace, read-only, locked) shall drive behavior with banners, admin emails and a license page, and shall allow exporting data in read-only and locked states.
- **FR-KRN-ENT-05** Limits shall be enforced at the point of use with soft warnings (default 90 percent) and hard stops, with clear messages and an administrator view of consumption.
- **FR-KRN-ENT-06** Usage metering shall record active users, active employees, companies, storage, API calls and AI tokens monthly and produce a usage statement per tenant.
- **FR-KRN-ENT-07** Checks shall occur at startup, module load, periodically and at multiple independent points; clock rollback shall be detected from several stores.
- **FR-KRN-ENT-08** License renewal, upgrade (more modules or users), and re-issue after hardware change shall be possible by uploading a new signed file, without reinstalling.
- **FR-KRN-ENT-09** A maintenance expiry shall block installing releases newer than the maintenance date while leaving the installed version usable (perpetual licenses).
- **FR-KRN-ENT-10** The vendor-side issuer tool shall produce licenses with unique ids, support revocation lists for online-connected installs, and keep its private key offline.
- **FR-KRN-ENT-11** Optional online activation and usage reporting shall be off by default and enabled only with the customer's consent.
- **BR:** read-only precedes locked; expiry never deletes or hides customer data; wording and periods follow the signed contract.
- **AC:** with an expired license, after the grace period the system enters read-only, users can still log in and export, and a new signed file restores full function within minutes without downtime.

## 21. Packs and configuration lifecycle (CFG)

**Purpose:** install, upgrade and promote configuration safely. **Actors:** Tenant Admin, Implementation Partner, Platform Super Admin.

- **FR-KRN-CFG-01** Packs (country, industry, template, connector, language) shall be signed, versioned bundles with a manifest of contents, compatible product versions, dependencies and merge rules.
- **FR-KRN-CFG-02** Installing or upgrading a pack shall preserve tenant overrides, report conflicts for review, and never alter posted transactional data.
- **FR-KRN-CFG-03** The system shall show pack contents, versions, available updates, and release notes; upgrades shall be previewed with a change report and applied through maker-checker.
- **FR-KRN-CFG-04** All configuration objects shall be exportable as a configuration package with checksum, diffable against another environment, and importable with validation.
- **FR-KRN-CFG-05** Promotion from sandbox or UAT to production shall run validation, dependency checks and automated tests where defined, take a rollback snapshot, and require approval.
- **FR-KRN-CFG-06** Configuration history shall show who changed what and when, with the ability to roll back a package.
- **FR-KRN-CFG-07** Pack authoring tools (vendor side) shall validate manifests and test packs against a clean tenant before release.
- **AC:** a tenant promotes a tested workflow and custom-field set from sandbox to production; a rollback snapshot is created; rolling back restores the earlier configuration without touching documents.

## 22. AI gateway (AIG)

**Purpose:** safe, controllable AI for every module. **Actors:** Business User, Tenant Admin, AI Agent. **Wave:** gateway, controls, metering and two skills (configuration assistant, natural-language query) in wave A.

- **FR-KRN-AIG-01** AI requests shall pass through a gateway that selects provider and model by tenant policy and region (hosted, regional or local), and hides provider details from skills.
- **FR-KRN-AIG-02** Skills shall be registered by modules with name, description, input schema, tools they may call (module contracts), permission requirements, and evaluation tests.
- **FR-KRN-AIG-03** The AI shall act only with the permissions of the requesting user; tool calls shall go through the same authorization, data scope and field security as the UI.
- **FR-KRN-AIG-04** Any action that changes data shall be proposed first and executed only after explicit user confirmation through the same commands, validations and workflows as the UI.
- **FR-KRN-AIG-05** The AI shall not post journals, approve transactions, finalize payroll, or make hiring, disciplinary, termination or compensation decisions; outputs in these areas are advisory with human review recorded.
- **FR-KRN-AIG-06** Retrieval over tenant documents (policies, contracts, help) shall use a tenant-scoped index that honors document permissions and cites sources.
- **FR-KRN-AIG-07** Tenants shall switch AI on or off per module and per user group, restrict data classes sent to external providers, choose provider and region, and review all AI interaction logs.
- **FR-KRN-AIG-08** Usage shall be metered by tokens and calls per tenant and module, with limits and alerts that tie to entitlements.
- **FR-KRN-AIG-09** Prompts shall be versioned in a registry; skills shall have evaluation sets run before each release; safety tests for prompt injection and data leakage shall run in CI.
- **FR-KRN-AIG-10** Content from documents and external sources shall be treated as untrusted input and shall not be able to trigger tool calls on its own.
- **FR-KRN-AIG-11** Configuration assistant: given a plain-language request, the AI shall produce a draft configuration package (fields, layouts, workflow, rules, report) for review, diff and sandbox test, and never apply it to production directly.
- **FR-KRN-AIG-12** Natural-language query: questions shall be translated into semantic-layer queries, shown to the user in readable form, and executed within the user's scope.
- **AC:** a manager asks for the team's leave balances and receives only their team's data; asking for a colleague outside the team returns a permission message; a configuration request produces a package that can be previewed and tested in sandbox but not applied without approval.

## 23. Baseline roles and permissions

Baseline roles ship with the kernel and each module; all are editable through permission sets (FR-KRN-IAM-11).

| Capability | Tenant Admin | Company Admin | Business User | Approver | Auditor |
| --- | --- | --- | --- | --- | --- |
| Manage tenant settings, modules and packs | Yes | No | No | No | No |
| Manage company settings, numbering, templates | Yes | Yes | No | No | No |
| Create and manage users and roles | Yes | Scope | No | No | No |
| Configure workflows, fields, rules | Yes | Limited | No | No | No |
| Work in module screens | By role | By role | By role | By role | Read-only |
| Approve tasks | By workflow | By workflow | By workflow | Yes | No |
| View audit logs | Yes | Limited | No | No | Yes |
| Bulk export | Approval | Approval | Approval | No | Approval |
| Manage API keys and integrations | Yes | Limited | No | No | No |
| Use AI features | By setting | By setting | By setting | By setting | No |

## 24. Key kernel entities

| Entity | Key attributes | Effective-dated |
| --- | --- | --- |
| Tenant, Environment | Name, region, cluster, status, entitlement | Status history |
| Company | Country, currency, tax IDs, calendars, bank accounts | Yes |
| Branch, Location | Company, address, hours | Yes |
| Organization Unit | Type, parent, head, cost center reference | Yes |
| Fiscal Calendar, Period | Pattern, dates, status | Status history |
| User, Role, Permission Set, Data Scope | Credentials reference, roles, scopes, field policies | Assignments yes |
| Setting, Feature Flag | Key, value, layer, version | Versioned |
| Audit Event | Actor, time, entity, before and after | Immutable |
| Numbering Scheme | Document type, pattern, reset rule, counter | Versioned |
| Business Partner, Role, Address, Bank Account | Roles, tax IDs, terms, status | Partly |
| Item, Category, UoM, Conversion | Type, units, identifiers, status | Partly |
| Tax Code, Rule | Rate, jurisdiction, applicability | Yes |
| Currency, Exchange Rate, Calendar, Holiday | Code, rate type, date | Yes |
| Custom Field, Custom Object, Layout, Status Model | Definitions, validation, transitions | Versioned |
| Workflow Definition, Instance, Task | Steps, versions, state, assignees | Versioned |
| Rule and Rule Version | Expression, context, tests | Versioned |
| Notification Template, Delivery | Event, channel, language, status | Versioned |
| File, Template | Type, version, retention | Versioned |
| Webhook, Import Template, Connector | Events, mapping, credentials reference | Versioned |
| Job, Job Run | Schedule, progress, result | Status history |
| Entitlement, Usage Meter | Claims, limits, period consumption | Versioned |
| Pack, Configuration Package | Manifest, version, checksum | Versioned |
| AI Skill, Conversation, Tool Call | Skill, user, inputs, outputs, confirmation | Immutable |

## 25. Acceptance and testing approach

- **Traceability:** every FR maps to at least one automated test; a traceability matrix is generated for each release.
- **Tenant isolation:** an automated suite calls every endpoint across tenants and must fail closed; it runs in every pipeline.
- **Permission testing:** matrix tests for roles, data scopes and field security across UI, API, reports, exports and AI.
- **Configuration testing:** each configurable capability is exercised through the admin UI by a non-developer persona script.
- **Migration testing:** every release migrates two test tenants from the previous version and rolls back.
- **Entitlement testing:** valid, expired, tampered, rolled-back-clock and over-limit licenses for each state.
- **Performance testing:** targets in section 2 verified at 1.5 times expected load.
- **Security testing:** penetration test before the first production customer and before each major release.
- **AI evaluation:** skill evaluation sets and injection tests gate each release.
- **UAT:** the first Finance and HRMIS pilot exercises the kernel end to end with exit criteria of zero open severity-1 and severity-2 defects.

## 26. Assumptions and open items

**Assumptions:** PostgreSQL per tenant; modular monolith; country-specific rules arrive as packs and are verified by qualified local practitioners; connectors for banks, tax portals and devices are added by demand; English, Urdu and Arabic are launch languages.

**Open decisions:** expression engine choice (Dynamic Expresso or NCalc); whether a separate orchestration engine is needed beyond native approvals; SMS and WhatsApp gateway providers per country; e-signature provider; default retention periods per document type; scope of AI configuration assistant in wave A.

**Next documents:** 3 Data Architecture Standards and Kernel Data Model, then 4 Admin Console, User Management and Settings.

## 27. External portal framework (PRT), wave B

Sections 27 to 31 were added by the consistency review (document 14, decision D-20) and form part of this specification.

**Purpose:** secure, branded access for suppliers, customers, carriers and partners, with identities fully separate from internal users. **Actors:** Tenant Admin, External User, Partner Administrator.

- **FR-KRN-PRT-01** Each portal type (supplier, customer, carrier, partner) shall have its own authentication realm; external identities shall never share accounts, sessions or credentials with internal users.
- **FR-KRN-PRT-02** External users shall be created by invitation or by approved self-registration, with email verification, multi-factor authentication, optional single sign-on, and account recovery that never reveals whether an account exists.
- **FR-KRN-PRT-03** Every external account shall link to a business partner and see only that partner's data, through portal-specific permissions and data scopes defined by the owning module.
- **FR-KRN-PRT-04** Portals shall support tenant branding, custom domains, languages (English, Urdu, Arabic with right-to-left layout), and a reduced navigation set.
- **FR-KRN-PRT-05** Partner administrators shall manage their own users within limits set by the tenant, with audit.
- **FR-KRN-PRT-06** Portal traffic shall be rate limited and protected against automated abuse; uploads shall be scanned and size limited; all actions shall be audited and anomalies alerted.
- **FR-KRN-PRT-07** A message center shall let internal and external users exchange messages and attachments on a record, using the collaboration component with an external visibility scope.
- **FR-KRN-PRT-08** Portal APIs shall be separate from internal APIs, versioned, and unable to reach internal endpoints or other partners' data; isolation tests shall run in every pipeline.
- **FR-KRN-PRT-09** The number of external users shall be metered against the entitlement (portal.external\_users); blocking a partner shall disable its external accounts automatically.
- **FR-KRN-PRT-10** Access reviews and inactivity expiry shall be available for external accounts.
- **AC:** a supplier user logs in through the supplier realm, sees only their orders and invoices, exchanges a message with the buyer, and cannot reach any internal route or another supplier's record by any request.

## 28. Offline sync framework (SYN), wave A

**Purpose:** a single, tested way for mobile and handheld applications to work offline and synchronize safely. **Actors:** Mobile User, Tenant Admin, Platform Super Admin.

- **FR-KRN-SYN-01** A device registry shall record each registered device (user, platform, app version, status, last sync), with per-user device limits, remote lock and remote wipe.
- **FR-KRN-SYN-02** The sync protocol shall pull changes by scope (what a user or role may hold offline, for example a warehouse or a route), push queued actions, use delta tokens, be resumable and compressed, and be versioned.
- **FR-KRN-SYN-03** Each device shall keep an outbox of pending actions with idempotency keys, retry and visible status; the server shall record received actions so repeats never duplicate documents.
- **FR-KRN-SYN-04** Modules shall declare, in their manifests, conflict rules per entity type (server wins, merge fields, last write wins for low-risk fields, manual resolution); conflicts shall be recorded and presented to users with clear choices.
- **FR-KRN-SYN-05** Local storage shall be encrypted with keys bound to the user session; data shall be wiped on logout, deprovisioning, wipe command or after a maximum offline period; field security shall be applied before data is synced, and masked fields shall not be synced unless allowed.
- **FR-KRN-SYN-06** Modules shall declare which commands may be completed offline and which require server confirmation (financial postings and approvals are confirmed by the server before being shown as done).
- **FR-KRN-SYN-07** Attachments (photos, signatures, scans) shall sync in chunks with resume.
- **FR-KRN-SYN-08** Sync health (failures, backlog, conflicts, devices not seen) shall be monitored per tenant and device and shown to administrators.
- **FR-KRN-SYN-09** A minimum app version shall be enforceable, and the API shall remain compatible with the previous app version so field staff are not blocked mid-shift.
- **FR-KRN-SYN-10** Device clock differences shall be detected and recorded; server time shall be authoritative for audit; location tampering checks shall be available. (S)
- **FR-KRN-SYN-11** Selective sync limits (item counts, data age, size) shall be configurable per module, with refresh policies for large reference data such as catalogs and price lists.
- **AC:** a storekeeper works offline for a shift on a handheld, resolves one conflict on a changed order quantity, syncs without duplicates, and a lost device is wiped remotely with no residual data.

## 29. Global search (SRC), wave A thin

**Purpose:** find anything quickly and safely from one place. **Actors:** Business User, Tenant Admin.

- **FR-KRN-SRC-01** Modules shall declare searchable entities (documents, partners, items, employees, assets) with searchable fields and display templates; indexing shall follow change events within seconds.
- **FR-KRN-SRC-02** Results shall be filtered by the requesting user's permissions, data scopes and field security at query time, and shall never reveal the existence of records the user cannot see.
- **FR-KRN-SRC-03** Search shall normalize English, Urdu and Arabic text (diacritics, letter variants, digit systems), tolerate typing errors, support prefix and code lookups, and optionally transliterate between scripts. (transliteration, S)
- **FR-KRN-SRC-04** A command palette shall combine search results with screens, reports, settings and actions, showing recents and favorites.
- **FR-KRN-SRC-05** Ranking shall consider exact code matches, recency and usage, with facets by entity type and company.
- **FR-KRN-SRC-06** Results shall return in under 300 milliseconds at the 95th percentile on 10 million indexed records per tenant; the default index shall use the tenant database's text search facilities and be replaceable behind the same interface.
- **FR-KRN-SRC-07** Sensitive fields shall be excluded from the index by classification; administrators shall see index status, rebuild it, and manage exclusions.
- **FR-KRN-SRC-08** AI retrieval over documents (document 9, section 14) shall use its own permission-aware index and shall not share storage with this search index.
- **AC:** typing part of a supplier name in Urdu returns matching suppliers, purchase orders and invoices the user may see, ranked by relevance, in under 300 milliseconds; a record outside the user's scope does not appear.

## 30. Collaboration (COL), wave A thin

**Purpose:** conversation and follow-up around records without leaving the product. **Actors:** Business User, Approver, External User (portals).

- **FR-KRN-COL-01** Any record or document type that enables it shall support comment threads with mentions, attachments, edit and delete rules, and audit; visibility shall be internal or, for portals, shared with the external partner.
- **FR-KRN-COL-02** Tasks and reminders shall be creatable on records, assignable to users or groups, with due dates, recurrence and status, appearing on the user's home page and integrating with workflow tasks.
- **FR-KRN-COL-03** A per-record activity feed shall combine changes, comments, workflow events and documents, with filters.
- **FR-KRN-COL-04** Users shall follow records and choose notification preferences for comments, mentions, assignments and status changes.
- **FR-KRN-COL-05** Mentions and assignments shall notify through the notification component; reply by email shall add a comment where the tenant enables it. (S)
- **FR-KRN-COL-06** Comment visibility shall inherit record permissions; sensitive record types may disable comments; retention and legal hold shall apply.
- **FR-KRN-COL-07** Compliance export and deletion of comments shall be available to authorized roles with audit.
- **AC:** an approver mentions a colleague in a comment on a purchase order, the colleague receives a notification and a task, and the thread shows in the record's activity feed and in the portal message center only if marked shared.

## 31. Additions to documents and integration

- **FR-KRN-DOC-09** A print service shall manage printers by location and type (network document printers, label printers using standard label languages, cheque printers), with print agents for on-premise and desktop environments, queues, retries and routing by document type and location.
- **FR-KRN-DOC-10** Label and barcode printing shall use templates with barcodes and QR codes (including GS1 formats), support batch printing and reprint control with audit.
- **FR-KRN-DOC-11** Print jobs shall be logged with status and errors, restricted by permission, and available for reprint within retention rules.
- **FR-KRN-INT-10** The analytics API (REST and OData) of document 9 (ADH-10) shall be part of the public API, subject to the same authentication, scope, field security and rate limits, and metered with other API usage.

Key entities added by this section: Portal Realm, Portal User and Invitation, Device and Sync Scope, Sync Conflict, Search Document and Exclusion, Comment, Mention, Task and Activity Event, Printer and Print Job. Their tables are listed in document 3, section 13.

## 32. Baseline 1.0 addendum (this document is now version 0.2)

*Added 14 November 2026 by the comprehensive review (Doc 16). Sections 1 to 31 are unchanged. The previous state (version 0.1) is kept in this document's version history and in the frozen originals archive. Items marked "proposed" take effect only on approval of the named decision; until then the original text stands.*

### 32.1 Minimum Viable Kernel and service schedule (proposed D-33)

The kernel is not built as one block. Each service is built in the phase whose modules first need it (phases: Doc 16 section 8).

| Kernel service | Sections | Phase | Scope in that phase |
| --- | --- | --- | --- |
| Tenancy and provisioning (TEN) | 3 | 1 | Full, in both tenancy modes (32.3) |
| Organization (ORG) | 4 | 1 | Full |
| Identity and access (IAM) | 5 | 1 | Full except single sign-on federation and SCIM, which follow in Phase 3 |
| Settings and feature flags (SET) | 6 | 1 | Full |
| Audit (AUD) | 7 | 1 | Full |
| Numbering (NUM) | 8 | 1 | Full |
| Parties (PTY) | 9 | 1 | Full |
| Catalog (CAT) | 10 | 1 basic, 3 full | Phase 1: items, services, units and conversions. Phase 3: variants, identifiers, attributes |
| Tax (TAX) | 11 | 1 basic, 2 full | Phase 1: codes and rates. Phase 2: rules, exemptions, withholding |
| Localization (LOC) | 12 | 1 | English, Urdu, Arabic, right-to-left, Hijri display |
| Metadata (MET) | 13 | 1 custom fields, 3 the rest | Phase 1: custom fields, labels, status models. Phase 3: layout designer, custom objects |
| Workflow (WFL) | 14 | 1 | Linear and conditional approvals, delegation, escalation. Parallel and matrix routing in Phase 3 |
| Rules (RUL) | 15 | 2 | Only the expressions workflow conditions need exist in Phase 1 |
| Notifications (NTF) | 16 | 1 in-app and email, 2 push, 3 SMS and messaging |  |
| Documents (DOC) | 17 | 1 attachments, 2 templates and print | Large media pipeline in Phase 3 (KER-08) |
| Integration (INT) | 18 | 1 basics | API keys, outbox, import and export. Webhooks and connectors in Phase 2 and 3 |
| Jobs (JOB) | 19 | 1 | Full |
| Entitlements (ENT) | 20 | 1 | Full, including licence file and expiry states |
| Packs and configuration packages (CFG) | 21 | 2 packs, 3 packages | Pakistan pack needs pack install in Phase 2 |
| AI gateway (AIG) | 22 | 2 thin, 4 full |  |
| External portal (PRT) | 27 | 3 |  |
| Offline sync (SYN) | 28 | 2 | Moved from wave A1 to Phase 2 with mobile release M1 |
| Global search (SRC) | 29 | 3 | Phase 1 and 2 use per-list search only |
| Collaboration (COL) | 30 | 3 |  |
| Analytics API, report and dashboard builders | 31 and Doc 9 | 2 fixed reports, 3 builders |  |

### 32.2 Kernel extensions accepted from HRMIS v0.3 (proposed D-37)

HRMIS v0.3 section 3.3 requested eight kernel extensions. All eight are accepted. Detailed requirements are written at the entry of the phase shown, as part of that phase's kernel data design gate.

| Request | Accepted as | New requirement | Phase |
| --- | --- | --- | --- |
| KER-01 Geospatial service | New kernel service GEO | **FR-KRN-GEO-01** The kernel shall store geo-fences (circle and polygon) as versioned data and shall compute distance and point-in-boundary results with accuracy awareness in the application, using a permissively licensed library and no database geospatial extension (proposed D-36). | 2 circle, 3 polygon |
| KER-02 Map component | Design system component (Doc 13), not a kernel service | **FR-KRN-GEO-02** A shared map component shall show markers, clusters, fence overlays and accuracy circles, with a tile provider chosen by configuration and an offline boundary fallback. | 3 |
| KER-03 Device trust registry | Extension of SYN device registry | **FR-KRN-SYN-12** The device registry shall record trust state and risk signals, enforce per-user device limits and support remote revoke. | 2 basic, 3 risk signals |
| KER-04 Push tokens and providers | Extension of NTF | **FR-KRN-NTF-08** Notifications shall manage push device tokens and deliver through provider adapters chosen by configuration; no provider is hard-coded. | 2 |
| KER-05 Classification and retention of location data | Extension of DOC retention and AUD | **FR-KRN-DOC-12** Retention rules shall apply to data classes (location evidence, media evidence) as well as files, with audited purge jobs that honor legal holds. | 3 |
| KER-06 Mobile API conventions | Extension of INT | **FR-KRN-INT-11** The API shall publish versioning, idempotency-key, sync-endpoint and error conventions for mobile clients, and shall enforce a minimum supported client version. | 2 |
| KER-07 Mobile session management | Extension of IAM | **FR-KRN-IAM-19** Mobile sessions shall be device-bound with refresh-token rotation, remote logout and step-up authentication; device biometrics unlock an existing session and are never stored by the server. | 2 |
| KER-08 Large media pipeline | Extension of DOC | **FR-KRN-DOC-13** Uploads shall support chunked, resumable transfer with scanning, image compression and thumbnails. | 3 |

### 32.3 Tenancy modes (proposed D-30)

- **FR-KRN-TEN-13** Every tenant-scoped table shall carry `tenant_id`, set by the platform and never accepted from a client.
- **FR-KRN-TEN-14** A tenant shall be placed in dedicated mode (own database) or pooled mode (shared database with row-level security). Placement is a control-plane property; application code shall not branch on it.
- **FR-KRN-TEN-15** In pooled mode the database session shall be bound to one tenant for the life of a request, and the application role shall be unable to bypass the isolation policies.
- **FR-KRN-TEN-16** A tenant shall be movable from pooled to dedicated mode by export and import with verification, without schema change.
- **FR-KRN-TEN-17** The isolation test suite shall run in both modes on every build. Pooled mode shall not be offered commercially before an independent security review passes.

### 32.4 Posting contract stub (proposed D-31)

- **FR-KRN-INT-12** Where Finance is not licensed, the kernel shall satisfy the Finance posting contract with a stub that records an accounting event for later export (the Accounting Export connector). Modules shall not test whether Finance is present (Doc 16, FR-XM-02).

### 32.5 Cross-references

Cross-module transaction rules FR-XM-01 to FR-XM-32 (Doc 16 section 6) bind the kernel workflow, numbering and audit services where they are named. Data standards changes are in Doc 3 section 14.
