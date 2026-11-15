# ERP Platform: Architecture and Foundation (v0.3)

Prepared for Ibrahim | 10 November 2026 | Supersedes Reference Architecture v0.1 (30 October 2026) | Product name TBD (placeholder namespace: Erp)

## 0. How to read this document

This is document 1 of the planning set. It defines the system architecture, application architecture and database architecture of the ERP, and the rules every module must follow. Functional specifications for the platform kernel and for each module follow as separate documents (section 17 lists the set and the order).

Every significant choice is numbered D-xx. Each becomes an Architecture Decision Record (ADR) in docs/adr before coding starts, so decisions stay traceable and reversible.

### 0.1 Decisions confirmed

| ID | Decision | Status |
| --- | --- | --- |
| D-01 | Modular monolith on .NET, PostgreSQL, React; Clean Architecture inside each module | Carried over from v0.1 |
| D-02 | Database-per-tenant is the SaaS default, behind a tenant-resolution abstraction; on-prem is one tenant, one database | New |
| D-03 | Industry-neutral horizontal core; industries handled by installable industry packs, not forks | New |
| D-04 | Country packs for Pakistan, United States and Saudi Arabia (KSA) | KSA added |
| D-05 | One entitlement service drives both the signed license file (on-prem) and subscription metering (SaaS) | New |
| D-06 | Analytics is built in (semantic layer, report and dashboard builder); Apache Superset is an optional adapter later | Changed |
| D-07 | AI is a kernel service (AI Gateway) with per-tenant controls, not per-module add-ons | New |
| D-08 | First build: platform foundation, Finance, HRMIS. All other modules are planned now so the foundation does not need rework | Confirmed |
| D-09 | Open-source dependencies only under permissive licenses, enforced by a CI license gate | Carried over |
| D-10 | Modules depend on each other softly: Finance is an optional integration for every other module, not a prerequisite | New (section 4.4) |
| D-11 | Item and service catalog, business partners and units of measure live in the kernel so any module can be sold alone | New (section 1.3) |

### Decisions added by the consistency review (document 14, approved 13 November 2026)

| ID | Decision |
| --- | --- |
| D-12 | Order-related supplier invoices are captured and matched in Procurement and handed to Finance AP; non-order invoices are keyed in Finance AP; duplicate detection spans both |
| D-13 | Inventory stores replenishment parameters and basic ABC class; Supply Chain proposes and writes approved values back through a contract |
| D-14 | Quality inspection is owned by Inventory |
| D-15 | Receiving, issuing and returns use synchronous commands; owners publish events after success; Finance accrues stock items from Inventory events and non-stock items and services from Procurement events |
| D-16 | Sales issues customer invoices when licensed and calls the e-invoicing connector; otherwise Finance AR |
| D-17 | Return authorizations are owned by Sales; Supply Chain extends them; Inventory receives goods |
| D-18 | DeliveryConfirmed is published by Supply Chain when licensed, otherwise by Sales |
| D-19 | Projects are a Finance dimension until a Projects module is specified |
| D-20 | The kernel adds external portals, offline sync, global search, collaboration and a print service |
| D-21 | Event conventions and the canonical event catalog of document 14, section 4 |
| D-22 | Canonical module names, codes and requirement prefixes |
| D-23 | The kernel provides a partner onboarding workflow template that modules configure |
| D-24 | The entitlement key catalog of document 14, section 8 is the starting point for packaging |
| D-25 | The analytics API and an external reporting database role are specified in the kernel and data documents |

### 0.2 What changed from v0.1

| Area | v0.1 | v0.2 |
| --- | --- | --- |
| Tenancy | One database, tenant\_id on every table, row-level security | Database per tenant by default, control-plane database, tenant-resolution layer; shared-schema mode kept as a later option |
| Markets | Pakistan, then USA | Pakistan, USA, KSA (Arabic, Hijri calendar, ZATCA e-invoicing, GOSI and similar, all to be verified with local advisers) |
| Industries | Not addressed | Industry packs next to country packs |
| Licensing | License file only | Entitlement model covering SaaS, on-prem, per-user, per-module, one-time, with or without source |
| Dependencies | Procurement depends on Inventory; HRMS on Finance | Hard dependencies on the kernel only; other modules are optional integrations |
| Analytics | Superset | Native semantic layer and builders; Superset optional |
| AI | Not addressed | AI Gateway, skills, guardrails, metering |
| Scope control | All kernel components first | Thin kernel first, proven by Finance, then deepened |

## 1. Product scope and module landscape

### 1.1 Modules and build waves

| Module | Scope summary | Wave |
| --- | --- | --- |
| Platform kernel | Tenancy, organization, identity, settings, audit, numbering, metadata, workflow, rules, notifications, documents, localization, reporting, integration, entitlements, AI Gateway, jobs | A (thin), deepened per wave |
| Finance | General ledger, accounts payable, accounts receivable, banking and cash, tax, fixed-asset accounting hooks, budgeting, multi-company, consolidation, period close | A |
| HRMIS | Core HR, attendance, leave, self-service, payroll, loans, compliance, then talent lifecycle | A (core HR and attendance and leave first; payroll next) |
| Procurement | Requisition, RFQ and tender, purchase order, receiving, supplier invoice matching, contracts, supplier portal | B |
| Inventory | Items stock, warehouses, movements, valuation, batch and serial, counts, reorder | B |
| Asset Management | Asset register, acquisition, transfer, maintenance, depreciation, disposal | B |
| Sales and Distribution | Customers, quotation, order, delivery, invoicing, pricing, returns | C |
| Supply Chain | Demand planning, replenishment, logistics and shipment tracking, warehouse execution, supplier collaboration | C |
| Analytics | Kernel capability from wave A; prebuilt dashboards and data models added per module | Continuous |
| Verticals and mobile apps | Industry packs, POS, education, healthcare extensions, native-feel mobile | D |

Wave B and C modules are specified in full now (documents 7, 8, 10 and 11) and built after Wave A is proven with a pilot customer. Wave legend: A is the first release (platform foundation, Finance and HRMIS, where HRMIS splits into A1 core HR, attendance and leave, and A2 payroll); B adds Procurement, Inventory, Asset Management, HRMIS talent areas and the Saudi pack; C adds Sales, Supply Chain, United States readiness and the Superset adapter; D covers verticals and the ecosystem.

### 1.2 Sellable units

| Unit | Meaning |
| --- | --- |
| Module | Any single module, subject to its hard dependencies (kernel only) |
| Bundle or edition | Named set of modules with limits, for example Starter, Business, Enterprise |
| Add-on | AI features, additional country pack, industry pack, advanced analytics, Superset adapter, additional companies or users |
| Deployment | SaaS, private cloud single-tenant, on-prem |
| Source access | Binary only, or source license with escrow (contract-protected, see section 7) |
| Pricing metric | Per user (named or concurrent), per module, per company, per active employee, one-time perpetual, annual subscription |

Because pricing metrics change commercially, the product records only usage facts (users, companies, active employees, transactions, storage, AI tokens) and the entitlement service decides what is allowed.

### 1.3 Shared master data ownership (D-11)

Selling modules separately fails if one module owns data another needs. Ownership rules:

| Master data | Owner | Consumers |
| --- | --- | --- |
| Tenant, company (legal entity), branch, location, department tree | Kernel: Organization | All |
| Users, roles, permissions, data scopes | Kernel: Identity | All |
| Business partner (supplier, customer, both), addresses, contacts, bank details | Kernel: Parties | Finance, Procurement, Sales, Supply Chain |
| Item and service catalog, categories, units of measure, conversions | Kernel: Catalog | Procurement, Inventory, Sales, Supply Chain, Assets |
| Currency, exchange rates, calendars (Gregorian, Hijri, fiscal), holidays | Kernel: Localization | All |
| Tax codes and rules | Kernel: Tax (content from country packs) | Finance, Procurement, Sales, HRMS |
| Chart of accounts, cost centers, profit centers, analytical dimensions | Finance | All (by reference) |
| Employee, position, grade, job | HRMS | Finance (payee), Workflow (approver resolution), Assets (custodian) |
| Stock attributes (valuation method, batch, serial, warehouse) | Inventory | Procurement, Sales, Supply Chain |

Organization units belong to the kernel and carry an optional reference to a Finance cost center. Without Finance licensed, the reference is simply empty.

## 2. Architecture principles

1. Configuration over code: behavior that varies between customers is data (settings, metadata, rules, workflows, packs), versioned and auditable.
2. Contracts over coupling: modules talk through published contracts and events, never through each other's tables.
3. Financial integrity first: immutable ledger, decimal money, period locks, idempotent posting, full audit trail.
4. Isolation by construction: a tenant's data lives in its own database; code paths cannot reach another tenant without an explicit control-plane operation.
5. Works offline and air-gapped: licensing, updates, installation and diagnostics need no internet.
6. Everything reachable by API: every UI capability exists as a documented API endpoint with the same permissions.
7. AI is advisory: AI drafts, explains and proposes; humans confirm; all actions use normal permissions and are logged.
8. Verifiable by someone who does not read code: every module ships with specifications, acceptance tests, and automated checks that an outside reviewer can run.

## 3. System architecture

### 3.1 Logical view

```
Clients:  Web (React)  |  Mobile (React Native)  |  Partner systems (REST, webhooks, files)
                                   |
                        Reverse proxy / gateway (TLS, rate limits)
                                   |
   Erp.Host.Api  --  composition root: auth, tenant resolution, entitlement guard, module loader
                                   |
   Business modules:  Finance | HRMIS | Procurement | Inventory | Assets | Sales | Supply Chain
                      (contracts and events only between modules)
                                   |
   Platform kernel:   Tenancy | Organization | Identity | Settings | Audit | Numbering | Parties | Catalog
                      Metadata | Workflow | Rules | Notifications | Documents | Localization | Tax
                      Reporting and Analytics | Integration | Entitlements | AI Gateway | Jobs
                                   |
   Data plane:  Control-plane DB  |  Tenant DBs (one per tenant)  |  Valkey cache  |  File store
                Message transport: in-process outbox, RabbitMQ optional
   Workers:     Erp.Host.Worker (jobs, outbox dispatcher, report scheduler, AI jobs)
   Tools:       Erp.Host.Migrator, Installer, LicenseIssuer (never shipped), CodeGen
```

### 3.2 Control plane and data plane

| Plane | Holds | Notes |
| --- | --- | --- |
| Control plane | Tenant registry, connection strings (encrypted), entitlements and license state, module catalog and versions, schema version per tenant, pack catalog, usage meters, support grants, platform admins | One small database per installation. On-prem it exists but holds exactly one tenant |
| Data plane | Each tenant's business data, configuration, documents, audit log, encryption keys reference | One PostgreSQL database per tenant, with a schema per module |

Platform staff cannot read tenant business data by default. Access requires a time-boxed, logged, tenant-approved support grant recorded in the control plane.

### 3.3 Deployment topologies

| Topology | Typical customer | Shape |
| --- | --- | --- |
| Single-server on-prem | Most Pakistani mid-size customers | Docker Compose: API host, worker, PostgreSQL, Valkey, reverse proxy, optional RabbitMQ and local AI runtime |
| High-availability on-prem | Large enterprises | Kubernetes (Helm) or multiple VMs: replicated API and workers, PostgreSQL streaming replication, shared file storage, load balancer |
| Private cloud, single tenant | Large customers wanting vendor-hosted dedicated stack | Same as on-prem, run by you |
| Pooled SaaS by region | SMEs and mid-market | Shared API hosts and workers per region; one database per tenant on shared PostgreSQL clusters; regions: Pakistan, KSA, US as demand justifies |

The same build artifacts serve all four. Differences are configuration: entitlement source, hosting, number of tenants.

### 3.4 Environments per tenant

Each tenant can have production, UAT and sandbox databases. Copies to non-production mask personal and financial data by configurable rules. Configuration packages (section 6.3) promote tested configuration from sandbox or UAT to production.

### 3.5 Scaling model

- API hosts are stateless and scale horizontally; session state lives in tokens and Valkey.
- Workers scale by queue; heavy jobs (payroll calculation, period close, MRP-style planning, report generation) run as chunked, resumable jobs.
- Each tenant database scales vertically first. A very large tenant moves to a dedicated PostgreSQL server by changing its connection record, with no code change.
- Reporting reads come from read replicas or reporting views, never competing with transactions on large tenants.

### 3.6 Data residency

A tenant is assigned a hosting region at provisioning. Its database, files, backups and AI processing stay in that region. On-prem customers control residency themselves. AI features that call an external model provider are off by default for tenants with residency constraints unless a regional or local model is configured (section 9).

### 3.7 Targets (initial, to be validated by performance tests)

| Area | Target |
| --- | --- |
| Standard page or list | p95 under 2 seconds |
| Tenant size | At least 50,000 employees and 5 million ledger lines per year per tenant |
| Payroll calculation, 10,000 employees | Under 15 minutes |
| Bulk import, 50,000 rows | Asynchronous with progress, resumable |
| SaaS availability | 99.9 percent monthly, RPO 15 minutes, RTO 4 hours |
| Backups | Daily encrypted, per-tenant restore, 35-day retention default |

## 4. Application architecture

### 4.1 Style

A modular monolith: one deployable host, strictly separated modules, packaged per edition. Inside each module use Clean Architecture layers (Contracts, Domain, Application, Infrastructure, Api), with the Application layer organized as vertical feature slices (one folder per command or query holding handler, validator, DTOs and endpoint). CQRS-lite: commands go through aggregates; queries may use direct SQL for speed. Microservices are not used because on-prem installs need low operational burden; module boundaries allow extraction later if SaaS scale demands.

### 4.2 Module manifest

Every module implements IModule declaring: Id, Version, HardDependsOn (kernel components only), OptionalIntegrations, service registration, endpoint mapping, permissions, menu entries, configuration schema, migrations, seed data, event publications and subscriptions, entitlement keys, and AI skills. The host discovers modules, orders them, and loads only those the tenant's entitlement allows.

### 4.3 Layer and dependency rules

| From | May reference |
| --- | --- |
| Domain | BuildingBlocks.Domain only |
| Application | Own Domain, BuildingBlocks.Application, kernel Contracts, other modules' Contracts |
| Infrastructure | Own Domain and Application, BuildingBlocks.Infrastructure |
| Api | Own Application and Contracts, Host abstractions |
| Contracts | BuildingBlocks.Contracts only |
| Host | Every module (composition root only) |

Standards: nullable reference types on, warnings as errors, one aggregate per transaction, no business logic in endpoints or EF configurations, async everywhere, cancellation tokens everywhere, no static mutable state. Architecture tests (NetArchTest) fail the build on any violation.

### 4.4 Cross-module communication and soft dependencies (D-10)

- Business modules publish integration events through a transactional outbox. Subscribers are idempotent through an inbox table.
- Finance is an optional integration for every module. If Finance is licensed, it subscribes and creates journals through configurable posting rules. If it is not, the events are ignored or routed to an Accounting Export connector that writes files or calls an external accounting system. This lets you sell HRMIS or Procurement into a customer that keeps its existing accounting software.
- Synchronous calls between modules go through a Contracts interface only where an immediate answer is needed, and must degrade gracefully when the other module is not licensed.
- Posting failures land in a posting exception queue that users can correct and replay, so no module ever blocks on the ledger. \[Baseline 1.0 note: proposed decision D-31 makes atomic posting the default and this queue the exception; see section 18.2. Pending approval.\]

Initial event catalog:

| Event | Publisher | Typical subscriber |
| --- | --- | --- |
| PayrollRunFinalized, LoanDisbursed, ExpenseClaimApproved | HRMIS | Finance (journals), Notifications |
| SupplierInvoiceApproved, ReceiptPosted, GoodsReceiptPosted | Procurement, Inventory | Finance (AP, accruals) |
| GoodsReceiptPosted, GoodsIssuePosted, StockAdjustmentPosted | Inventory | Finance (valuation), Supply Chain |
| DepreciationCalculated, AssetDisposed | Assets | Finance |
| SalesInvoiceIssued, DeliveryPosted | Sales, Inventory | Finance (AR), Inventory |
| PeriodClosed, PeriodReopened | Finance | All modules (lock dates) |
| PurchaseRequisitionApproved, PurchaseOrderIssued | Procurement | Supply Chain, Budget control in Finance |

### 4.5 Request pipeline

1. Authenticate (OIDC token, API key).
2. Resolve tenant from host name, token claim or API key; fetch and cache its connection record.
3. Check entitlement for the module and feature requested.
4. Check permission (role, attribute conditions, data scope, field-level security).
5. Validate input.
6. Execute the handler inside one transaction against the tenant database, writing domain changes, outbox rows and audit rows together.
7. Return RFC 9457 problem details on failure; log with correlation id.

### 4.6 Kernel components

| Component | Responsibility | Wave A depth |
| --- | --- | --- |
| Tenancy | Tenant lifecycle, control plane, provisioning, connection resolution, environments | Full (foundation) |
| Organization | Companies, branches, locations, department tree, fiscal calendars | Full |
| Identity and Access | Users, roles, permission sets, data scopes, MFA, SSO (OIDC, SAML), LDAP and AD, API keys, sessions, maker-checker | Full |
| Settings and Feature Flags | Layered typed settings, flags per tenant and company | Full |
| Audit | Append-only change log, access log, export | Full |
| Numbering | Gap-aware and gap-free sequences per company, document type, year, branch | Full |
| Parties, Catalog, Tax | Business partners, items and services, UoM, tax codes | Basic |
| Metadata and Customization | Custom fields, layouts, labels, status models, menus | Thin: custom fields and layouts first |
| Workflow and Approvals | Approval definitions, routing, delegation, escalation, history | Thin: sequential and role-based approvals first |
| Rules and Expressions | Sandboxed expressions for eligibility, tax, posting, validation | Thin: needed by Finance posting and HR policies |
| Notifications | Email, SMS, WhatsApp, in-app, push; templates | Basic |
| Documents | Attachments, versioning, retention, storage abstraction | Basic |
| Localization | Languages, RTL, calendars incl. Hijri, formats, currencies, rates, pack loading | Full |
| Reporting and Analytics | Semantic layer, report and dashboard builders, scheduling, export | Thin first, deepened in wave B |
| Integration | Public API, webhooks, import and export, connector framework | Basic |
| Entitlements | Plans, limits, license verification, metering, expiry state machine | Full |
| AI Gateway | Provider abstraction, prompts, skills, retrieval, guardrails, metering | Thin: gateway and one skill |
| Jobs | Scheduler, retries, long-running jobs, job center | Full |

Kernel components added by decision D-20 (document 14): the External Portal framework (PRT, wave B), the Offline Sync framework (SYN, wave A), Global Search (SRC, wave A thin) and Collaboration (COL, wave A thin); the print service is part of Documents. Their requirements are in document 2, sections 27 to 30.

### 4.7 The extension ladder

Customers and partners extend the product at the lowest rung that works:

1. Settings and policies.
2. Custom fields, layouts, labels, status models.
3. Rules, expressions and workflows.
4. API, webhooks, import and export templates, connectors.
5. Signed plugin assemblies with a restricted API (later, needs its own ADR).
6. Source access (source edition only).

Higher rungs are costlier to support, so the product invests in making rungs 1 to 4 powerful.

### 4.8 Web application architecture

- React and TypeScript shell loads module screens on demand. A shared design system gives consistent look, accessibility (WCAG 2.1 AA) and keyboard-first data entry.
- Forms, grids, filters, detail pages and print layouts render from metadata, so a configuration change shows up without a frontend release. Hand-built screens are reserved for complex work: journal entry, bank reconciliation, stock count, timesheet grids, approval inbox, report and dashboard builders.
- Grids are virtualized, support saved views, column choice, grouping, export and inline edit where permitted.
- Internationalization from day one: translation catalogs, English, Urdu and Arabic, right-to-left mirroring, Gregorian and Hijri dates, locale number formats.
- White-labeling: logo, colors, login page, custom domain, sender identity per tenant.

### 4.9 Mobile

Cross-platform (React Native with Expo) for employee self-service, approvals, warehouse scanning and field procurement, working offline with a local database, a durable outbox of pending actions and explicit conflict rules. Same generated API client and permission model as web.

### 4.10 API standards

REST with OpenAPI per module under /api/v1/{module}; RFC 9457 errors; idempotency keys on writes; cursor pagination and a documented filter syntax; bulk endpoints for imports; generated TypeScript and mobile clients so frontend and backend cannot drift; signed webhooks with retries and delivery log; API keys and OAuth client credentials with rate limits.

## 5. Database architecture

This is the backbone. The rules here apply to every module and are enforced by integration tests and review checklists.

### 5.1 Tenant isolation models (D-02)

| Model | Isolation | Operating cost | Use |
| --- | --- | --- | --- |
| Database per tenant (default) | Strongest: own database, backup, restore, encryption key, tuning, extensions | Migrations must run across N databases; connection pooling needed | All SaaS tenants, private cloud, on-prem |
| Schema per tenant | Strong logical | Catalog bloat beyond a few hundred tenants | Not planned |
| Shared schema with tenant\_id and row-level security | Logical only | Lowest | Optional future micro tier for very small tenants, only by a separate ADR |

Rules:

- All data access goes through ITenantContext and ITenantConnectionProvider. No static connection strings. The tenant is resolved from the host name, token or API key, never from a request body or query string.
- Tenant databases do not carry a tenant\_id on business rows (redundant, and it invites mistakes). \[Baseline 1.0 note: proposed decision D-30 would reverse this sentence and the next; see section 18.1. Pending approval.\] If a pooled micro tier is ever built, it gets its own ADR and a generated tenant\_id with row-level security.
- Within a tenant, company\_id (legal entity) is on every company-scoped table and leads composite indexes where queries are company-scoped.
- Connection management: PgBouncer in transaction pooling mode, per-tenant pool limits, lazy connections, statement timeouts, and per-tenant job concurrency caps so one tenant cannot starve others.
- Scale-out: the control plane records which PostgreSQL cluster hosts each tenant. When a cluster fills, new tenants go to a new cluster, and a large tenant can be moved by changing its record after a restore.

### 5.2 Control-plane database

| Table group | Purpose |
| --- | --- |
| tenants, tenant\_environments | Tenant registry, environment (production, UAT, sandbox), status (provisioning, active, suspended, offboarding), region |
| db\_clusters, tenant\_databases | Cluster inventory, database name, secret reference for the connection, schema version |
| entitlements, license\_files, license\_events | Plan, modules, limits, expiry, state machine history, signed license payloads |
| usage\_events, usage\_meters | Active users, active employees, companies, storage, API calls, AI tokens, per month |
| module\_catalog, module\_versions, tenant\_module\_versions | Which module versions each tenant runs |
| pack\_catalog, tenant\_packs | Country, industry and template packs and installed versions |
| support\_grants | Time-boxed, tenant-approved vendor access with reason and expiry |
| platform\_users, platform\_audit | Vendor staff and their actions |
| migration\_runs, migration\_run\_items | Fleet migration progress and per-tenant results |

Connection secrets are stored encrypted or as references to a secrets store, never in plain text.

### 5.3 Tenant database layout

One PostgreSQL database per tenant, one schema per kernel component or module:

| Schema | Contents |
| --- | --- |
| iam | Users, roles, permission sets, data scopes, sessions, API keys, MFA, SSO links |
| org | Companies, branches, locations, department tree, fiscal calendars |
| ref | Business partners, item and service catalog, UoM, currencies, rates, tax codes, calendars, holidays |
| cfg | Settings, feature flags, custom field definitions, custom objects, layouts, labels, status models, numbering, installed packs |
| wf | Workflow definitions, instances, tasks, delegations; rules and expressions with versions |
| aud | Change log, access log, security events |
| doc | File metadata, versions, retention |
| ntf | Templates, outbound queue, delivery log, preferences |
| rpt | Semantic model, report and dashboard definitions, schedules, saved views, read views |
| itg | Webhooks, deliveries, import and export templates, connector configs |
| ai | Conversations, tool-call logs, prompts, retrieval index metadata, usage |
| job | Job definitions, runs, progress |
| fin, hrm, prc, inv, ast, sal, scm | Business modules, created only when licensed |
| meta | Schema history and installed module versions for this database |

Naming: snake\_case, plural table names (fin.journal\_entries), singular column names, id as primary key, foreign keys named table\_id.

Cross-schema rules: a module may hold a foreign key to kernel schemas (kernel is always present). A module never holds a foreign key to another module's schema and never joins across module schemas. Cross-module data comes through contracts, events or read models. This is what lets an unlicensed module's schema simply not exist.

### 5.4 Standard columns and types

| Concern | Rule |
| --- | --- |
| Primary key | UUIDv7 (time-ordered, index-friendly), column id. Business numbers (document\_no) are separate columns |
| Audit columns | created\_at, created\_by, updated\_at, updated\_by on every table. Timestamps are timestamptz stored in UTC |
| Concurrency | row\_version column (bigint, incremented by the application) on every aggregate root; updates fail on mismatch |
| Dates | Business dates use date. Gregorian is canonical; Hijri is a presentation and calendar-service concern |
| Money | numeric(19,4) amount plus currency char(3). Company-currency amount stored beside the transaction amount, and optional group-currency amount. Never floating point |
| Rates and quantities | Exchange rates numeric(19,10); quantities numeric(19,6) with unit of measure; percentages numeric(9,6) |
| Status | Text with a check constraint or lookup; transitions enforced by the domain model and workflow engine |
| Deletion | No hard delete on transactional data. Master data uses is\_deleted with deleted\_at and deleted\_by. Legal erasure uses a documented anonymization procedure |
| Custom fields | custom jsonb column on extensible entities, validated against cfg definitions |
| Multilingual names | name\_i18n jsonb ({"en":"...","ur":"...","ar":"..."}) on master data; transaction text is stored as entered |
| Text search | Normalization for Arabic and Urdu (diacritics, letter variants); case-insensitive search via normalized columns, not collation tricks |

### 5.5 Key patterns

**Effective dating (mainly HRMIS).** Effective-dated tables carry valid\_from and valid\_to (date, valid\_to null means open). A PostgreSQL exclusion constraint on (key, daterange) prevents overlapping rows. As-of queries filter by date. Corrections edit the row with an audit entry; changes close the current row and open a new one. Transaction time comes from the audit log.

**General ledger.** Tables: fin.journal\_entries (header: company, ledger, entry number, posting date, period, source module and reference, status draft or posted or reversed, reverses\_id) and fin.journal\_lines (account, debit, credit, currency, rate, company-currency amounts, dimension columns such as cost center, project, department, partner, plus a jsonb for extra dimensions). Safeguards: a line has debit or credit, never both; a deferred constraint trigger enforces debits equal credits per entry; posted rows cannot be updated or deleted (trigger plus permissions); corrections are reversing entries; periods can be locked; a unique key on (source module, source reference, posting version) makes posting idempotent. A ledger\_id supports parallel ledgers (for example IFRS and local statutory). journal\_lines is range-partitioned by posting date. Running balances are kept in fin.account\_balances (per period, account, dimension set) updated in the posting transaction, so reports do not scan millions of lines.

**Subledgers.** AP, AR, bank, inventory valuation and fixed assets keep their own detail and reconcile to control accounts. An invariant test runs on every build: trial balance ties to the sum of subledgers.

**Stock ledger.** inv.stock\_movements is append-only (item, warehouse, batch, serial, quantity, value, source document). On-hand and reserved balances are kept in balance tables updated in the same transaction, with row locks only where availability must be guaranteed.

**Custom fields and custom objects.** No entity-attribute-value tables. Extensible entities carry a jsonb custom column with definitions in cfg.custom\_field\_defs (type, validation, visibility, permission per role). Frequently filtered or sorted custom fields can be promoted by an admin job to indexed generated columns. Customer-defined objects use cfg.custom\_objects (definition) and cfg.custom\_records (jsonb data with indexed key fields); heavy-use objects can be promoted to dedicated tables by a controlled process.

**Numbering.** Gap-allowed sequences use PostgreSQL sequences. Gap-free sequences (required by some tax rules) allocate from a locked counter row at posting time. Both are configurable per company, document type, fiscal year and branch.

**Audit.** aud.change\_log is partitioned by month and written in the same transaction as the change: occurred\_at, actor, entity, entity id, action, field-level before and after, correlation id, source (UI, API, import, AI). Database triggers are a backstop on critical tables. Daily hash chaining makes tampering detectable. A separate access log records views and exports of sensitive data.

**Outbox and inbox.** Each module schema has outbox\_messages and inbox\_messages. The dispatcher publishes after commit; consumers record processed message ids.

**Sensitive data.** National IDs, bank accounts and salaries use application-level envelope encryption: a per-tenant data key encrypted by a key-encryption key held in a KMS, HSM or protected key file on-prem. Exact-match search uses a keyed blind index (HMAC). Field-level permissions control display and masking.

**Files.** doc.files stores metadata, checksum and version; content lives in the file store under a tenant prefix (local, NFS or S3-compatible).

### 5.6 Kernel table inventory (first cut)

| Schema | Main tables |
| --- | --- |
| iam | users, user\_credentials, roles, permissions, role\_permissions, permission\_sets, user\_role\_assignments, data\_scopes, field\_policies, sessions, api\_keys, mfa\_devices, external\_identities, login\_attempts, maker\_checker\_requests |
| org | companies, branches, locations, org\_units, org\_unit\_types, org\_hierarchies, fiscal\_calendars, fiscal\_periods |
| ref | business\_partners, partner\_roles, partner\_addresses, partner\_contacts, partner\_bank\_accounts, items, item\_categories, item\_attributes, units\_of\_measure, uom\_conversions, currencies, exchange\_rates, tax\_codes, tax\_rules, calendars, holidays |
| cfg | settings, setting\_values, feature\_flags, custom\_field\_defs, custom\_objects, custom\_records, layouts, labels, status\_models, status\_transitions, numbering\_sequences, installed\_packs, config\_packages |
| wf | workflow\_definitions, workflow\_versions, workflow\_instances, workflow\_tasks, delegations, rule\_definitions, rule\_versions, rule\_tests |
| aud | change\_log, access\_log, security\_events |
| rpt | semantic\_entities, semantic\_measures, report\_definitions, dashboard\_definitions, schedules, saved\_views, export\_jobs |
| ntf, itg, ai, job, doc | templates, queue, delivery\_log, webhooks, deliveries, import\_templates, connectors, conversations, tool\_calls, job\_runs, files, file\_versions |

The module table inventories belong to the module specifications.

### 5.7 Migrations and versioning

- SQL-first migrations (DbUp or Flyway), versioned per module folder, reviewed as code. No auto-generated schema changes in production.
- Expand and contract: add new structures, backfill (as a job for large tables), switch reads and writes, remove the old structure one release later. Every release is backward compatible with the previous one, so rollback is safe.
- The Migrator reads the control plane, runs tenants in batches with a concurrency limit, canary tenants first, records results in migration\_runs and in each tenant's meta.schema\_history. A failing tenant is isolated (maintenance mode) and does not stop the fleet.
- On-prem uses the same Migrator inside a signed offline update bundle, with a pre-upgrade backup step and a rollback script.
- Pack and configuration changes are versioned separately from schema changes. Seed data is idempotent.
- Indexes are created concurrently; long backfills run as resumable jobs.

### 5.8 Performance, concurrency and scale rules

- Partition by date: journal\_lines, stock\_movements, change\_log, attendance punches, processed outbox rows, delivery logs. Define retention and archival from day one.
- Every foreign key is indexed. Composite indexes lead with company\_id where queries are company-scoped. Index review is part of every migration review.
- Lists use keyset pagination and bounded page sizes. Heavy reads use Dapper and reporting views; EF Core is for writes and simple reads. Integration tests check for N+1 queries on key screens.
- Default isolation is read committed. Optimistic concurrency covers editing. Pessimistic locks are limited to numbering, stock availability and period close (advisory locks).
- Long operations (payroll, period close, planning runs, imports, report generation) are chunked, resumable jobs with progress in the job center.
- Read replicas or reporting views serve reports; optional analytical extracts for very large tenants are covered in section 8.
- Performance budgets are tested with k6 at 1.5 times expected load before each release.

### 5.9 Backup, restore and keys

- PostgreSQL continuous archiving for point-in-time recovery per cluster, plus per-tenant logical backups so one tenant can be restored without touching others.
- Restore into a sandbox is a standard feature. A portable tenant export (data plus configuration) is required for data exit and for moving a tenant between regions or clusters.
- Key hierarchy: root key in KMS or HSM (or a protected key file on-prem) encrypts per-tenant data keys; data keys encrypt sensitive fields; backups are encrypted; rotation procedures are documented and tested.

### 5.10 Tenant lifecycle

1. Provision: create the control-plane record, pick a cluster and region, create the database from a template, run kernel and licensed-module migrations, install the chosen country and industry packs, seed roles and default workflows, create the first admin, apply entitlements, run smoke tests. Target under 10 minutes.
2. Upgrade: fleet migration with canaries; per-tenant module versions.
3. Suspend: read-only or locked according to the entitlement state machine; data stays intact.
4. Export and offboard: full export, legal-hold check, scheduled deletion with a retention window and a certificate of deletion.
5. Move: backup, restore in target region or cluster, switch the connection record, verify.

## 6. Configurability architecture

### 6.1 Layered configuration

A setting resolves through layers, and the most specific layer wins: product default, country pack, industry pack, tenant, company, role or user. Each setting declares which layers may override it, its type, validation, and whether it is visible to the tenant admin. Resolved values are cached and invalidated on change.

### 6.2 Packs

A pack is a signed, versioned, installable unit. Types: country pack, industry pack, template pack (sample workflows, reports, dashboards), connector pack, language pack. A pack manifest lists id, version, compatible product versions, contents and merge rules. Installing or upgrading a pack never overwrites tenant changes silently: tenant overrides are preserved and conflicts are shown for review. Uninstalling is non-destructive to transactional data.

Pack contents can include tax codes and rules, charts-of-accounts templates, holidays, leave and labor statutes, payroll components, custom objects and fields, forms and layouts, workflows, print templates, dashboards, terminology and translations.

| Pack | Planned contents (all rules to be verified with qualified local advisers before release) |
| --- | --- |
| Pakistan | Federal and provincial sales tax, withholding taxes, salary income tax, EOBI, provincial social security, July to June fiscal year, Urdu, FBR and bank file formats, biometric devices |
| United States | GAAP-oriented charts of accounts, multi-state sales tax through a rate service or partner, payroll and benefits through partners, W-2 and 1099 style reporting, state labor rules, privacy requirements. A separate research and compliance project, not an adaptation of the Pakistan pack |
| Saudi Arabia | 15 percent VAT, ZATCA e-invoicing, GOSI contributions, wage protection (WPS) files, Saudization reporting, end-of-service calculation, Hijri calendar, Arabic and RTL, data-residency settings |
| Industry: Retail, Services, Education, Healthcare, Distribution | Starter terminology, custom objects, workflows, charts of accounts, dashboards and templates. Deep vertical features (point of sale, student lifecycle, clinical records) are separate modules in wave D, and healthcare brings regulatory work (for example HIPAA in the US) that must be scoped on its own |

### 6.3 Configuration packages and promotion

All configuration objects (custom fields, layouts, workflows, rules, templates, numbering, dashboards, settings) are versioned, validated and exportable as a configuration package with a checksum. A package can be diffed against a target, applied to sandbox or UAT, tested, and promoted to production, with maker-checker approval and an automatic snapshot for rollback.

### 6.4 Terminology and localization

A tenant can rename any standard label per language (for example Department to Wing, Employee to Staff Member). Languages at launch: English, Urdu, Arabic. Right-to-left layouts, Hijri and Gregorian dates, number and currency formats are handled by the Localization component, not by modules.

### 6.5 Rules and expressions

A sandboxed expression engine (Dynamic Expresso or NCalc, chosen by ADR) evaluates rules for approval thresholds, posting rules, eligibility, tax determination, pricing, validation and policy logic. Each rule context declares typed variables and an allowed function library (math, dates, lookups, string). Expressions cannot reach the file system, network or reflection and have time and memory limits. Rules are versioned, tested against sample data before activation, and reversible. Rule changes in sensitive areas (posting, payroll) require maker-checker.

### 6.6 Admin console scope

The tenant admin console covers: organization setup, users, roles and permissions, data scopes, field security, workflow designer, custom fields and objects, form and layout designer, numbering, templates, localization and terminology, integrations and API keys, webhooks, import and export, entitlements and usage, audit viewer, job center, AI controls, configuration packages, environment management, backup status and support grants. The platform console (vendor) covers tenant provisioning, entitlements, fleet migrations, pack catalog, usage and health. Both are specified in document 4.

## 7. Entitlements, licensing and commercial packaging (D-05)

### 7.1 One entitlement model

Every way of selling the product is expressed as an entitlement: a signed set of claims that the Entitlements component enforces.

| Claim | Examples |
| --- | --- |
| Customer and tenant | Customer id, tenant id, deployment type (SaaS, private cloud, on-prem) |
| Edition and modules | Starter, Business, Enterprise; list of licensed modules and feature keys |
| Packs and add-ons | Country packs, industry packs, AI features, advanced analytics, Superset adapter |
| Limits | Named users, concurrent users, companies, active employees, storage, API calls, AI tokens |
| Term | Start date, subscription expiry or perpetual, maintenance (update rights) expiry, support tier |
| Source access | Binary only, or source license |
| Binding | Optional server fingerprint, environment type (production or non-production) |

Sources of entitlements: in SaaS they come from the control-plane subscription record; on-prem they come from a signed license file verified fully offline, with optional online activation for customers who allow it.

### 7.2 Mapping sales models to metering

| Sales model | How it is enforced |
| --- | --- |
| Per named user | Count of active users in iam, with a limit claim |
| Per concurrent user | Session counter against a limit claim |
| Module-wise | Module list in the entitlement; unlicensed modules are not loaded and their schemas are not created |
| Per company | Count of companies in org |
| Per active employee | Monthly active employee count from HRMIS |
| Annual subscription | Expiry claim and the state machine below |
| One-time perpetual | No expiry claim for use; a separate maintenance expiry controls whether new versions can be installed |
| Bundle or edition | Named claim sets |

### 7.3 Expiry state machine

| State | Behavior |
| --- | --- |
| Active | Full function |
| Warning (about 30 days before expiry) | Full function, banners for admins, notification emails |
| Grace (short fixed period) | Full function, prominent warnings |
| Read-only | Users can log in, view and export their own data, no new transactions |
| Locked | Only the license page and administrator data export |

Read-only before locked is deliberate: customers keep access to their own data, which protects you from disputes. Confirm policy and wording with a Pakistani lawyer and place it in the contract.

### 7.4 Technical controls

- License checks at startup, at module load, on a timer, and at several independent points in the code path, not one if statement.
- Clock-rollback detection using the highest timestamp seen in several places (database, file, signed heartbeat).
- Optional server fingerprint binding tolerant of virtual machine changes, with a re-issue process.
- Per-edition build packages contain only licensed module assemblies. Migrations run only for licensed modules.
- Release builds are obfuscated and signed; the license verifier and a few critical routines can live in a small Native AOT library. No source, PDB files or server source maps are shipped. Frontend bundles are minified, and business-critical logic stays on the server.
- The Erp.LicenseIssuer tool signs licenses with a private key held offline (HSM or air-gapped machine) and is never shipped.

### 7.5 Honest limits and the source edition

These controls deter and detect; they are not an absolute lock. A customer who controls the server and has the skill can eventually defeat client-side protection, so the technical layer sits on top of a proprietary EULA that forbids copying, modification, reverse engineering, sublicensing and resale, defines expiry behavior and grants audit rights. The source edition cannot rely on technical protection at all, so it is sold under a source license with escrow where requested, per-customer identifiers in the delivered source for traceability, and contractual audit rights. Provide extension points so that customers rarely need source.

## 8. Analytics and reporting architecture (D-06)

### 8.1 Layers

| Layer | Purpose |
| --- | --- |
| Operational reports | Document-level and list reports inside each module (for example voucher, payslip, stock card) |
| Management reports | Financial statements, ageing, budget versus actual, headcount, stock valuation |
| Dashboards | Role-based tiles with KPIs, charts and drill-through |
| Ad-hoc analysis | Users build their own reports and pivots from the semantic layer |
| Statutory reports | Tax returns and regulatory formats delivered by country packs |
| Scheduled and embedded | Scheduled email delivery, export, embedding in other portals |

### 8.2 Semantic layer

The rpt schema holds a semantic model: entities (for example Journal Line, Stock Movement, Employee, Purchase Order), measures (sum of debit, headcount, on-hand value), dimensions (company, period, cost center, department, item category, custom fields), and declared joins. Each module ships its semantic definitions. A query compiler turns a report or dashboard request into SQL against reporting views, applying company scope, data scope and field-level security automatically. Custom fields and dimensions appear in the semantic model once defined.

Cross-module reporting is the one permitted exception to the no-cross-module-join rule: it happens only in the reporting layer, read-only, through published reporting views named v\_{module}\_{entity} that each module exposes as part of its contract. Business logic never uses them.

### 8.3 Builders and rendering

- Report designer: tabular, grouped, pivot; parameters; formulas; conditional formatting; export to Excel, PDF and CSV; print-ready layouts through the template engine.
- Dashboard builder: KPI cards, charts, tables, filters, drill-through and cross-filtering, sharing by role, per-user personalization. Charts use Apache ECharts (Apache 2.0).
- Saved views, favorites, subscriptions and scheduled delivery with job-center visibility.

### 8.4 Performance

Pre-aggregated summary tables or materialized views refreshed by jobs for heavy measures; result caching in Valkey keyed by query and permission scope; a query governor (row limits, timeouts, concurrency caps per tenant); asynchronous large exports. For very large tenants an optional analytical extract (columnar store such as DuckDB or ClickHouse, license verified at adoption) can be added without changing report definitions.

### 8.5 Standard content

Each module ships role dashboards and reports: CFO and finance controller (cash position, P&L, ageing, budget variance), HR head (headcount, attrition, absenteeism, payroll cost), procurement head (spend, cycle time, supplier performance), warehouse manager (stock, aging, movements), asset manager (register, depreciation, maintenance), sales head (pipeline, orders, margins), supply chain planner (service level, replenishment). Details go in document 9.

### 8.6 Superset adapter (optional, later)

Superset is not part of the base product. When a customer wants it, the adapter exposes read-only reporting views with per-tenant credentials, single sign-on and embedding, sold as an add-on. Nothing in the core depends on it.

### 8.7 Natural-language analytics

The AI Gateway translates questions into semantic-layer queries, never raw SQL, so permissions, scope and limits always apply (section 9).

## 9. AI architecture (D-07)

### 9.1 Components

| Component | Role |
| --- | --- |
| Provider abstraction | Hosted model APIs, regional endpoints, and local or self-hosted models for air-gapped or residency-bound tenants; swap without touching skills |
| Prompt registry | Versioned prompts and templates, testable and reviewable |
| Skills | Named capabilities with tool definitions bound to module contracts (for example find leave balance, draft journal) |
| Retrieval | Tenant-scoped vector index (pgvector in the tenant database) over policies, documents and help content; honors document permissions |
| Guardrails | Permission-bound tool calls, confirmation before any write, redaction options, output validation, prompt-injection defenses for document content |
| Evaluation harness | Test sets per skill with accuracy and safety checks, run in CI and before each release |
| Metering and controls | Token and call metering per tenant and module; per-tenant, per-module and per-user switches; allowed data classes; provider and region selection |
| Audit | Every AI interaction and tool call stored in ai.tool\_calls; AI-initiated changes are marked in the audit log |

### 9.2 Action model

AI proposes and humans confirm. An action runs as the same command a user would run, with that user's permissions, workflows and validations. AI never posts journals, approves transactions, finalizes payroll, or makes hiring, disciplinary, termination or pay decisions; in those areas output is advisory and a human review is recorded.

### 9.3 Skill roadmap

| Skill | Module | Wave | Why it sells |
| --- | --- | --- | --- |
| Configuration assistant (describe a field, form, workflow, rule or report in plain language; receive a draft configuration package to review) | Kernel | A | Makes a deeply configurable product easy to configure, the main differentiator |
| Data migration assistant (map legacy spreadsheets to import templates, flag problems) | Kernel | A | Shortens implementations |
| Natural-language analytics | Analytics | A to B | Instant answers within the user's permissions |
| Employee and manager assistant (balances, policies, payslip questions, request drafting) | HRMIS | A | Visible daily value |
| HR drafting (letters, job descriptions, review summaries) | HRMIS | A to B | Saves HR time |
| Bank reconciliation suggestions, journal drafting, anomaly flags | Finance | A to B | Finance productivity and control |
| Invoice and document extraction into payables | Finance, Procurement | B | Removes data entry |
| Supplier comparison, contract clause summaries | Procurement | B | Faster sourcing decisions |
| Cash-flow, demand and reorder forecasting | Finance, Inventory, Supply Chain | B to C | Planning value |
| Predictive HR analytics (attrition, absenteeism) | HRMIS | D | Off by default, transparent, bias-reviewed |

### 9.4 Safety and trust

No training on customer data. Retrieval and tool access respect the user's permissions. Document content is treated as untrusted input. Red-team and regression tests for injection and data leakage run in CI. HR predictions are reviewed for bias and privacy before release and are off by default at the individual level. Local models are weaker than frontier hosted models; the product states clearly which features are available under each mode.

## 10. Security and compliance architecture

| Area | Design |
| --- | --- |
| Authentication | OIDC through OpenIddict, MFA, password and session policies, lockout, LDAP and Active Directory, SAML and OIDC SSO |
| Authorization | Permission-based RBAC plus attribute conditions, data scopes by company, branch and department, field-level security, maker-checker on sensitive changes |
| Tenant isolation | Separate databases, tenant resolved only server-side, isolation tests in every pipeline run (attempt cross-tenant access through every API) |
| Data protection | TLS everywhere, field-level envelope encryption, per-tenant keys, encrypted backups, secrets outside code and config files |
| Application security | OWASP ASVS, input validation, parameterized SQL only, strict CORS and CSP, dependency, container and secret scanning |
| Audit | Immutable change log, access log for sensitive data, tamper-evident chaining, security event alerts |
| Privacy | Retention and erasure rules, data export, consent where required; design for GDPR and CCPA style requirements, Saudi PDPL and Pakistan's evolving rules, each confirmed with local counsel |
| Secure development | Threat modeling per module, security review in the definition of done, independent penetration test before the first production customer and before each major release |
| Certification roadmap | SaaS buyers in the US and KSA often ask for SOC 2 or ISO 27001 and, in KSA, alignment with national cybersecurity controls. Plan for these before heavy SaaS selling; verify the current requirements |

## 11. Technology stack

Versions must be verified at project kickoff because they change. Every package passes the license gate; several popular .NET libraries moved to commercial licensing in recent years, so nothing is adopted out of habit.

| Concern | Choice | Notes |
| --- | --- | --- |
| Runtime | .NET 10 LTS, C# | .NET 8 leaves support in November 2026 |
| API | ASP.NET Core minimal APIs, OpenAPI per module |  |
| Data access | EF Core with Npgsql for writes, Dapper for heavy reads | SQL-first migrations (DbUp or Flyway) |
| Database | PostgreSQL, with btree\_gist and pgvector extensions | Database per tenant; PgBouncer for pooling |
| Cache | HybridCache with Valkey | In-memory only on small installs |
| Messaging and jobs | In-process dispatcher with transactional outbox, Quartz.NET; RabbitMQ optional | Single-server installs need no broker |
| Identity | ASP.NET Core Identity primitives, OpenIddict |  |
| Validation and mapping | FluentValidation, Mapperly | Confirm licenses at adoption |
| Logging and tracing | Serilog, OpenTelemetry; Prometheus, Grafana, Loki on the ops side |  |
| Documents | Scriban templates to PDF through headless Chromium; ClosedXML for Excel |  |
| Charts and analytics | Apache ECharts; native semantic layer; Superset optional |  |
| Web | React 19, TypeScript, Vite, pnpm workspace; schema-driven form engine | Open-source UI primitives, licensed permissively |
| Mobile | React Native with Expo and offline SQLite | Flutter if offline-first dominates |
| AI | Provider-agnostic AI Gateway; pgvector; hosted and local model adapters |  |
| Tests | xUnit, Shouldly, Testcontainers, NetArchTest, PactNet, Playwright, k6 |  |
| Packaging | Docker images, Compose, Helm, signed installer and update bundles |  |

## 12. Quality engineering and building with Claude Code

### 12.1 CI gates

| Layer | Gate |
| --- | --- |
| Unit and domain tests | Rules, calculations, aggregates covered on every commit |
| Architecture tests | Module boundaries, layering, no cycles, no cross-module foreign keys |
| Integration tests | Real PostgreSQL through Testcontainers: migrations on two tenant databases, outbox, repositories |
| Tenant isolation tests | Every endpoint attempted across tenants; must fail closed |
| Accounting invariants | Debits equal credits, trial balance ties to subledgers, posting idempotency, period locks |
| Contract tests | OpenAPI diff and consumer contracts; no breaking change without a version bump |
| End-to-end | Playwright on critical flows, including configured (not hand-built) screens |
| Performance | k6 budgets for key endpoints and batch jobs |
| Security and license | SAST, dependency, container and secret scans; license gate and SBOM block disallowed licenses |
| Static analysis | Nullable on, warnings as errors |

### 12.2 Spec-driven development

Every module spec carries requirement IDs (FR-module-area-number). Every requirement maps to at least one automated test, and a traceability matrix is generated per release. A feature is done only when its requirements pass, documentation exists, migrations are reversible, and the independent review pass is clean.

### 12.3 Working agreement for Claude Code

- CLAUDE.md at the repository root holds the rules in sections 2, 4.3, 5 and 12; architecture and module specs in docs/ are the source of truth, and Claude reads them at the start of each task.
- One vertical slice per session, planned before coding, committed in small steps, with tests written alongside.
- No new package without passing the license gate; no deviation from an ADR without a new ADR.
- A fresh review session audits each completed module against its spec and the checklists in this document, because the author session tends to confirm its own work.
- You verify outcomes, not code: pass or fail of the automated gates, demo walkthroughs against acceptance criteria, and milestone reviews by a senior human architect.

### 12.4 Model assignment

This is based on risk and blast radius, not measured benchmarks. Check available models in Claude Code with /model; Fable 5.2 is the current Fable version as far as I know.

| Use | Work |
| --- | --- |
| Fable 5.2 | Tenant resolution and isolation, migration orchestrator, entitlement and license engine, authentication and permission engine, ledger and posting engine, payroll calculation engine, rules sandbox, encryption design, whole-codebase audits against the spec, milestone architecture reviews, security reviews |
| Opus 5.5 | Module features, APIs, metadata-driven form engine and grids, workflow designer, report and dashboard builders, importers and integrations, admin console, test suites, documentation |
| Sonnet or Haiku | Boilerplate, scaffolding, translations, seed data, mechanical refactors |

If Fable declines or hedges on a security-related task, run that task on Opus and keep Fable for review.

## 13. Repository and solution structure (changes from v0.1)

One monorepo. Each business module has five projects (Contracts, Domain, Application, Infrastructure, Api). Kernel components start lean and split into five projects when they grow. Changes from v0.1: Parties, Catalog, Tax, Entitlements, AI and Analytics are kernel components; Inventory and Assets are separate modules because they are sold separately; Sales is added; a Saudi country pack and an industry-pack area are added.

```
erp-platform/
|-- CLAUDE.md  README.md  global.json  Directory.Build.props  Directory.Packages.props  .editorconfig  Erp.sln
|-- docs/
|   |-- architecture/   adr/   modules/   runbooks/   legal/   specs/ (the planning document set)
|-- src/
|   |-- Host/            Erp.Host.Api  Erp.Host.Worker  Erp.Host.Migrator (fleet migrations)
|   |-- BuildingBlocks/  Domain | Application | Infrastructure | Contracts
|   |-- Platform/
|   |   |-- Erp.Tenancy (control plane)  Erp.Organization  Erp.Identity  Erp.Settings  Erp.Audit  Erp.Numbering
|   |   |-- Erp.Parties  Erp.Catalog  Erp.Tax  Erp.Localization
|   |   |-- Erp.Metadata  Erp.Workflow  Erp.Rules  Erp.Notifications  Erp.Documents
|   |   |-- Erp.Analytics (semantic layer, builders)  Erp.Integration  Erp.Entitlements  Erp.Ai  Erp.Jobs  Erp.Portal  Erp.Sync  Erp.Search  Erp.Collaboration
|   |-- Modules/
|   |   |-- Finance/  Hrms/  Procurement/  Inventory/  Assets/  Sales/  SupplyChain/   (five projects each)
|   |-- Packs/
|       |-- Country/   Erp.Pack.Pk  Erp.Pack.Us  Erp.Pack.Sa
|       |-- Industry/  Retail  Services  Education  Healthcare  Distribution
|-- clients/
|   |-- web/ (apps/shell, packages/ui, form-engine, report-builder, api-client, modules/...)
|   |-- mobile/
|-- tools/   Erp.LicenseIssuer (never shipped)  Erp.Installer  Erp.CodeGen  Erp.PackBuilder
|-- deploy/  docker/  compose/  helm/  scripts/
|-- build/   pipelines, obfuscation, signing, SBOM, license scan
|-- tests/   Erp.ArchitectureTests  Erp.IntegrationTests  Erp.TenantIsolationTests  Erp.ContractTests  Erp.PerformanceTests  e2e/
```

## 14. Build roadmap

Durations are deliberately not stated. \[Baseline 1.0 note: proposed decision D-32 regroups these steps and waves into four phases; see section 18.4. Pending approval.\] Estimate them after the module specifications exist and a technical advisor has reviewed them.

| Step | Content | Exit criteria |
| --- | --- | --- |
| 0. Foundation setup | Repository, CI, standards, CLAUDE.md, first ADRs, license gate, architecture tests | Pipeline green; a violating change fails the build |
| 1. Walking skeleton | Control plane, tenant provisioning, tenant migrations, one thin sample module, one metadata-driven screen, entitlement toggle, container image, installer | Two tenants provisioned and isolated; sample module switches on and off by license; fresh install works on a clean machine |
| 2. Thin kernel | Organization, Identity, Settings, Audit, Numbering, Localization (English, Urdu, Arabic, Hijri), Parties, Catalog, Tax, then Metadata (custom fields, layouts), Workflow (sequential approvals), Rules, Notifications, Documents, Jobs, Integration (import and export), Entitlements (full), Analytics (thin), AI Gateway (thin) | Isolation, audit and permission tests green; a non-developer configures an organization, roles, custom field and approval flow |
| 3. Finance | Chart of accounts, journals, periods, multi-company and currency, AP, AR, banking and reconciliation, tax, budgets, financial statements, posting rules, accounting export connector | Pilot books close; trial balance ties to subledgers; all invariant tests green |
| 4. HRMIS core | Employee records, organization and positions, documents, attendance, leave, self-service portal and mobile | Pilot customer runs a month of attendance and leave |
| 5. Payroll | Payroll engine, loans, statutory compliance (Pakistan first), payroll journals into Finance | Two payroll cycles reconcile in parallel with the customer's existing process |
| 6. Hardening and release | Performance tests, penetration test, obfuscated signed builds, per-edition packages, offline update bundles, backup and restore drills | Penetration test clean, restore drill passed, first production customer live |
| Wave B | Procurement, then Inventory and Assets | Procure-to-pay flow runs end to end with Finance posting |
| Wave C | Sales, Supply Chain, deeper analytics, KSA and US packs | Order-to-cash and replenishment flows run end to end |
| Wave D | Industry packs, vertical modules, marketplace, extended AI | Driven by customer demand |

### 14.1 Hooks that wave A must contain so later modules fit without rework

- Ledger dimensions that already include item, project, warehouse, partner and custom dimensions.
- A document-flow link table (kernel) so that requisition to purchase order to receipt to invoice to payment, and quotation to order to delivery to invoice, can be traced across modules without foreign keys.
- A budget and commitment interface in Finance that Procurement can post commitments to later.
- Posting-rule engine supporting any source module through a published event contract, plus the accounting export connector.
- Item and service catalog with multiple units of measure and conversions, partners with multiple roles, multi-currency and tax determination in the kernel.
- Status models, numbering, approvals thresholds and attachments usable by any document type through metadata.
- Reporting views and semantic definitions as part of each module's definition of done.
- Custom objects, so verticals can add document types without schema changes to the core.

## 15. Risks and open decisions

| Risk | Mitigation |
| --- | --- |
| Scope too large for a first release | Wave A is limited to the thin kernel, Finance and HRMIS; everything else is specified but not built |
| Metadata engine becomes over-engineered | Build two hand-coded screens first, extract the form engine from them, and generalize only what repeats |
| You cannot read the code | Automated gates, a separate review pass, milestone reviews by a senior human architect, penetration test before launch |
| Statutory accuracy (tax, payroll, e-invoicing) | Country packs validated against independent calculations by qualified practitioners in each country before release |
| Database-per-tenant operations | Migrator with canaries, fleet dashboards, tested restore and move procedures |
| JSONB custom fields slow on large tables | Promotion to indexed columns, performance tests with realistic data volumes |
| Licensing can be bypassed by a skilled customer | Layered technical controls plus contract, audit rights and escrow |
| AI dependence on external providers | Provider-agnostic gateway, local-model option, per-tenant controls |
| Competing with entrenched ERP vendors | Win on configurability, implementation speed (AI configuration and migration assistants), price and local compliance |

Open decisions to settle before or during step 0: product name; pilot customer for Finance and HRMIS; first hosting provider and regions for SaaS; expression engine (Dynamic Expresso or NCalc); obfuscator vendor; SaaS billing system; pricing and metering definitions; support model; whether Sales is promoted to wave B.

## 16. Next decisions I will assume unless you object

1. Database-per-tenant with a control plane (D-02).
2. Finance as the first business module, with HRMIS core following (D-08).
3. Industry-neutral core with packs (D-03); no single target industry.
4. Superset deferred (D-06).

## 17. Planning document set

| No. | Document | Status |
| --- | --- | --- |
| 1 | Architecture and Foundation (this document) | v0.2 |
| 2 | Platform Kernel Functional Specification (tenancy, organization, identity, settings, numbering, audit, metadata, workflow, rules, notifications, documents, localization, integration, jobs, entitlements) | Next |
| 3 | Data Architecture Standards and Kernel Data Model (table-level design) | Planned |
| 4 | Admin Console, User Management and Settings Specification | Planned |
| 5 | Finance Functional Specification | Planned |
| 6 | HRMIS Functional Specification (revised and aligned to this foundation) | Planned |
| 7 | Procurement Functional Specification | Planned |
| 8 | Inventory and Asset Management Functional Specification | Planned |
| 9 | Analytics, Reporting and AI Functional Specification (including standard dashboards) | Planned |
| 10 | Supply Chain Functional Specification | Planned |
| 11 | Sales and Distribution Functional Specification | Planned |
| 12 | Claude Code Operating Manual (CLAUDE.md, build prompts per step, review checklists, model assignment) | Planned, produced right after document 6 |
| 13 | UX and Design System Principles | Planned |

Status on 13 November 2026: documents 1 to 15 are drafted (document 15 covers editions and the Starter edition); document 14 is the consistency review and decision log, and its approved decisions D-12 to D-25 are applied to the documents. Module specifications are documents 5 to 8, 10 and 11. Documents 2 to 6 and 12 to 14 are enough to start coding Step 0 to Step 4; documents 7 to 11 are used from Step 7 on.

## 18. Baseline 1.0 addendum (this document is now version 0.3)

*Added 14 November 2026 by the comprehensive review (Doc 16). The text of sections 0 to 17 is unchanged except for three marked notes in sections 4.4, 5.1 and 14. The previous state (version 0.2) is revision 12 in this document's version history. Items marked "proposed" take effect only on approval; until then the original text stands.*

### 18.1 Tenancy: hybrid-ready (proposed D-30; replaces the last two sentences of section 5.1)

- Every tenant-scoped table in every schema shall carry `tenant_id` (UUID, not null), included as the leading column of unique constraints and of the main access indexes.
- **Dedicated mode** (default; the only mode for Business, Enterprise and on-premises): one database per tenant as described in section 5. `tenant_id` holds one value. Isolation is by database.
- **Pooled mode** (SaaS Starter only): many small tenants share one database. Row-level security policies on every tenant-scoped table restrict access to the tenant set on the connection for the current request. The application role cannot bypass the policies.
- The same migrations, the same code and the same isolation test suite run in both modes. Mode is a property of the tenant's placement in the control plane, not of the application.
- A tenant moves from pooled to dedicated by export and import of its rows; no schema change.
- Cross-tenant reporting never queries tenant databases directly.
- Pooled mode shall not be sold before it passes an independent security review.

Rationale and alternatives: Doc 16 section 3, F-02.

### 18.2 Ledger posting: atomic by default (proposed D-31; replaces the last sentence of section 4.4)

- A module whose document has financial effect shall call the Finance posting contract synchronously inside its own database transaction. Document and ledger entry commit together or not at all.
- The call is to a published contract in the Finance Contracts project; no module reads or writes Finance tables. Module isolation (D-10) is unchanged.
- If Finance is not licensed, a kernel stub satisfies the contract and records an accounting event.
- Deferred posting through the outbox remains available per source by policy; the posting exception queue then applies, with ageing alerts, and blocks period close.
- Domain events are still published after commit for analytics, notifications and other listeners.

Rationale: Doc 16 section 3, F-03. Binding cross-module rules: Doc 16 section 6 (FR-XM-01 to FR-XM-32).

### 18.3 Minimum Viable Kernel (proposed D-33)

The kernel is built in two parts. Phase 1 builds only: Tenancy, Organization, Identity and access, Settings, Audit, Numbering, Parties, Catalog (basic), Tax (basic), Localization, Workflow (linear and conditional), Notifications (in-app, email), Documents (attachments), Jobs, Entitlements, Integration basics. All other kernel services are scheduled by phase in Doc 2 section 32.

### 18.4 Roadmap: four phases (proposed D-32; supersedes section 14 as the planning unit)

| Phase | Content | Release |
| --- | --- | --- |
| 1 Platform foundation | Minimum Viable Kernel, admin essentials, both tenancy modes | Internal 0.5 |
| 2 First usable ERP | Finance core, HRMIS core, Pakistan pack, mobile M1 | 1.0 |
| 3 Operations suite | Procurement, Inventory, Assets, Sales core, portals, geo-attendance and tasks, KSA pack, on-premises | 2.0 |
| 4 Enterprise and planning | Supply Chain, consolidation, cost accounting, HR talent, US readiness, iOS | 3.0 |

Waves A to D map as: A1 → Phase 1; A2 → Phase 2; B and the Sales part of C → Phase 3; the rest of C and D → Phase 4. Detail: Doc 16 section 8.

### 18.5 Other changes

- **Scalability levers.** Edition (what is licensed), size profile (how complex it feels) and deployment tier (where it runs) are separate. Doc 16 section 5; Doc 15 section on size profiles.
- **Geospatial.** No database geospatial extension; application library with a permissive licence (proposed D-36).
- **Module data design gate** before each module build (proposed D-35). Doc 3 addendum.
- **Document set (section 17)** gains Doc 16, *ERP Baseline 1.0*, which is the entry point to the set.
- **Security architecture and threat model** (gap G-01) is a completion condition of Phase 1.

### 18.6 Corrections applied without a decision

The review found six defects in the data standards that follow from decisions already made, so they are corrected rather than proposed (Doc 14 section 16.2, C-37 to C-42; text in Doc 3 section 14.2): the ledger is part of the posting idempotency key; journal and document lines carry profit center, item, location and branch dimensions, which section 14.1 of this document already required; `branch_id` is a standard column on transactional tables so that the branch data scopes of section 10 can work; balances are maintained by appended deltas and roll-up, not by locking one row per account; kernel item cost and price are informational defaults only; association tables may use composite keys and offline-created records get server ids.

### 18.7 Where each part of the architecture is now defined

| Topic | Authoritative text |
| --- | --- |
| Decisions and their status | Doc 14 sections 6 and 16.1 |
| Cross-module transaction rules | Doc 16 section 6 |
| Kernel services and their phase | Doc 2 section 32.1 |
| Data standards | Doc 3 sections 3 to 10 as amended by section 14 |
| Scalability levers and size profiles | Doc 16 section 5; Doc 15 section 17 |
| Phases, milestones and completion conditions | Doc 16 section 8 |
| Model allocation, governance, change control | Doc 16 sections 9 and 11; Doc 12 section 16 |
