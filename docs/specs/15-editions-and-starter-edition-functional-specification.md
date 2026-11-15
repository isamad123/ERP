# Editions and Starter Edition: Functional Specification (v0.2)

Document 15 of the planning set | Prepared for Ibrahim | 13 November 2026 | Companion to Architecture section 7, the Platform Kernel Specification and the consistency review (document 14, section 8)

## 1. Purpose and scope

The product has to serve a trading company with 10 to 15 employees and a multi-company enterprise from the same code, the same data model and the same entitlement system. The architecture already allows this: every module is optional, behavior is configuration, and limits are entitlements. What was missing is the packaging and experience layer that makes the small end comfortable. This document adds it.

It defines customer segments, a draft edition model (Starter, Business, Enterprise), the Starter edition requirements, a simple mode and business-profile setup for small companies, self-service onboarding, a trading starter pack, limits and upgrade rules, cost-to-serve and operations for many small tenants, support and accountant channels, billing flow, country starters and success metrics. It also lists the build slices and decisions that follow.

Prices, edition names and numeric limits in this document are placeholders for your commercial decision (document 14, gap G-03). Statutory rules in country starters are planning inputs to be verified with local advisers.

### 1.1 Conventions

- Requirement IDs: FR-STR-\<area>-\<number>. Priority (M), (S), (C); unmarked is (M).
- Waves follow document 1; this document states which wave first allows each offering.
- All behavior is delivered through configuration (settings, flags, layouts, labels, packs, entitlements), not through a separate code branch for small customers.

## 2. Customer segments

| Segment | Typical profile | Needs | Tolerance for complexity | Deployment |
| --- | --- | --- | --- | --- |
| Micro (1 to 15 employees) | Startup, trading shop, small service firm, owner involved daily | Invoices, purchases, stock, cash, tax compliance, simple payroll | Very low; no consultant | Hosted SaaS, self-service |
| Small (16 to 150) | Growing trader, distributor, clinic, school, contractor | Full order-to-cash and procure-to-pay, approvals, a few reports, some customization | Low to medium; light partner help | SaaS or hosted single tenant; some on-premise |
| Mid-market (150 to 1,000) | Manufacturer, retailer chain, service group | Multi-branch, workflows, payroll, analytics, integrations | Medium; implementation partner | SaaS, private cloud or on-premise |
| Enterprise (1,000 and above, or many companies) | Group of companies, public-sector bodies, large distributors | Consolidation, strict controls, SSO, residency, high availability, source options | High; project teams | On-premise, private cloud or dedicated SaaS |

The first three Starter-relevant rows share one product; what changes is which keys are enabled, how many things are shown by default, and how the customer is onboarded and supported.

## 3. Edition model (draft)

An edition is a named entitlement template: a set of keys and limits from the catalog of document 14, section 8. Customers can add or remove modules and add-ons around an edition. Editions are a commercial decision; the structure below is a starting point.

| Dimension | Starter | Business | Enterprise |
| --- | --- | --- | --- |
| Target | Segment micro | Segments small and mid-market | Segments mid-market and enterprise |
| Deployment | Hosted SaaS on pooled infrastructure | Hosted SaaS, hosted single tenant, on-premise on request | On-premise, private cloud, dedicated SaaS, regional hosting |
| Companies | 1 | Up to 5 | Many, with consolidation |
| Full users (placeholder) | 3, more by add-on | Per agreement | Per agreement or unlimited |
| Self-service and portal users | Included up to a modest number | Priced low per user | Per agreement |
| Module bundle | Finance, Inventory, Procurement, Sales (basic); People Lite as an add-on | Any modules; full feature depth | Any modules; all features and add-ons |
| Configuration depth | Presets; limited custom fields (placeholder 20 per entity); two approval templates; no custom objects | Full workflows, rules, custom objects | Everything, including packs authored by the customer |
| Analytics | Standard dashboards and five saved custom reports | Report designer, scheduling | Full analytics add-ons, external BI, extract store |
| AI | Small included quota of the assistant | Standard quota, more skills | Full skills, local-model option, bring-your-own provider key |
| Integrations | Basic API, three webhooks | Full API, connectors | EDI, premium connectors, SCIM, SSO |
| Environments | Production only | Production and one sandbox | Production, UAT and sandbox |
| Support | Self-service, in-app assistant, email | Business hours with response targets | Premium with named contacts and service levels |
| Source code option | No | No | Source license available |

Upgrade is a change of entitlement: no migration, no new installation, effective immediately (section 9).

## 4. Principles for small-business use

1. **Time to value in hours, not weeks.** A new customer issues a first invoice within an hour and finishes basic setup within a day.
2. **Progressive disclosure.** Hide what a small company does not need; reveal it when asked or when the business grows.
3. **Good defaults.** Charts of accounts, taxes, numbering, roles, templates and workflows are preset for the country and business type.
4. **Compliance without effort.** Local tax and invoicing rules work out of the box through the country pack.
5. **Low cost to serve.** Provisioning, upgrades, backups and support are automated so a micro tenant is profitable.
6. **No lock-in, no dead end.** Data can be exported at any time, and growth never requires a rebuild.
7. **One product.** Starter users see the same screens, data model and engines as enterprise users, only with fewer options switched on.

## 5. Starter composition and wave alignment

### 5.1 What a trading company needs

Finance (invoicing, payments, bank and cash, tax, reports), Inventory (items, stock, simple valuation), Procurement (purchase orders, supplier bills) and a sales flow (quotation, order, delivery, invoice). Payroll and attendance are often needed too once the company reaches ten employees.

### 5.2 Wave dependencies

Procurement and Inventory arrive in wave B and Sales in wave C (document 1). A trading company cannot be fully served before wave B ends. Options:

| Option | Description | Earliest availability | Trade-off |
| --- | --- | --- | --- |
| 1. Starter without the Sales module (recommended) | Customer invoices are entered directly in Finance AR; stock is issued by Inventory against the invoice or a simple stock issue; Procurement and Inventory provide the purchase and stock side | End of wave B | Fewer sales features (no quotations, contracts, commissions) but a complete trading cycle |
| 2. Promote Sales into wave B | Sales is built before Supply Chain, as noted in document 11 section 22 | Wave B | More work in wave B; full order-to-cash for Starter |
| 3. Service Starter first | Finance plus People Lite for small service firms and startups | End of wave A | Does not cover trading; opens an early small-business pilot |

Recommendation: offer the Service Starter at the end of wave A for a first small pilot, the Trading Starter (option 1) at the end of wave B, and add the Sales module to Starter when it is available. Document 11 notes that the architecture supports promoting Sales if a pilot requires it.

### 5.3 People Lite

A Starter add-on of HRMIS for up to a small number of employees (placeholder 25): employee records, leave, attendance by simple check-in, and payroll with the country statutory pack, using the same engines with the simple profile.

## 6. Simple mode and business profile (SIM)

**Purpose:** present a small, friendly product without forking it. **Actors:** Owner, Accountant, Salesperson, Storekeeper, Tenant Admin.

- **FR-STR-SIM-01** At signup, a business-profile questionnaire (country, business type, number of people, warehouses, whether stock is tracked by batch or serial, whether approvals are needed, registered for sales tax) shall select a profile, for example Trading, Services or Retail, and apply the matching preset configuration.
- **FR-STR-SIM-02** A profile shall be a configuration package (document 2, CFG) that sets feature flags, settings, labels, menus, layouts, status models, numbering, roles, workflows, templates and dashboards; no profile shall require code changes.
- **FR-STR-SIM-03** Simple mode shall hide advanced menus, fields and options by default (for example multiple ledgers, cost centers, batch and serial tracking, location-level stock, multi-level approvals) and show them when an administrator or user selects Show advanced for a module.
- **FR-STR-SIM-04** Hidden features shall remain discoverable: an Add this feature entry shall explain what a feature does, enable it with sensible defaults, and, if it needs a higher edition, show the upgrade path.
- **FR-STR-SIM-05** Quick actions shall cover the daily tasks in the fewest steps: quick invoice, quick supplier bill, record a payment received, record a payment made, stock adjustment, expense entry; each as a single screen using the standard components and engines underneath.
- **FR-STR-SIM-06** Role presets (Owner, Accountant, Salesperson, Storekeeper, Staff) shall be created automatically with permissions, home pages and menus suited to a small team.
- **FR-STR-SIM-07** Simple terminology shall be offered as a label pack (for example Bill for supplier invoice, Sale for customer invoice) in English, Urdu and Arabic, applied through label overrides and reversible.
- **FR-STR-SIM-08** Default dashboards for the Owner shall show cash and bank balances, receivables and payables due, sales this month, stock value and low stock, and top customers and items.
- **FR-STR-SIM-09** Switching a tenant from simple to advanced mode, or from Starter to Business, shall change what is shown and enabled but never require data migration or lose data.
- **FR-STR-SIM-10** Demo data shall be loadable at signup for exploration, clearly marked, and removable in one step before real data entry.
- **FR-STR-SIM-11** Contextual guidance (short tours, checklists, empty-state help) shall be tuned for first-time business owners, in the user's language.
- **AC:** a new trading company answers eight questions, receives a preconfigured tenant with the owner dashboard, a quick invoice screen and no advanced menus, enables location-level stock later through Add this feature, and loses no data when it does.

## 7. Self-service onboarding (ONB)

**Purpose:** a customer signs up, sets up and starts working without a consultant. **Actors:** New customer, Owner, Accountant (invited), Platform operations.

- **FR-STR-ONB-01** Self-service signup shall collect email or phone verification, country, business type and plan, create the tenant automatically through the provisioning pipeline (document 2, TEN-02) in under 10 minutes, and sign the owner in.
- **FR-STR-ONB-02** A trial period (placeholder 14 to 30 days) shall run with full Starter features, with reminders, a clear conversion path, and read-only behavior after expiry that keeps data safe and exportable (document 2, ENT-04).
- **FR-STR-ONB-03** The guided setup shall walk through company profile and logo, sales tax or VAT registration, fiscal year and currency, chart of accounts from the profile, bank and cash accounts, taxes and numbering, invoice template preview, and users.
- **FR-STR-ONB-04** Opening data shall be loadable in three ways: start fresh, import customers, suppliers and items from spreadsheet templates with validation, or import opening balances (trial balance and open invoices) with a tie-out check.
- **FR-STR-ONB-05** The migration assistant (document 9, section 19) shall be offered for spreadsheets and exports from other small-business software when licensed; otherwise import templates with clear instructions shall be provided.
- **FR-STR-ONB-06** An accountant invitation shall give an external accountant a role with the right level of access (view, post journals, close periods) without consuming a full user license (section 11).
- **FR-STR-ONB-07** A setup checklist with progress shall stay visible on the home page until essential steps are done: first invoice issued, first purchase recorded, stock loaded, bank connected or imported, tax settings confirmed.
- **FR-STR-ONB-08** Targets: first invoice within 60 minutes of signup for at least 70 percent of trials that start the guided setup; basic setup complete within one day for at least 60 percent.
- **FR-STR-ONB-09** Signup shall be protected against abuse: rate limits, verification, blocked disposable domains, quotas on trial tenants, and automated cleanup of abandoned trials after a retention period.
- **FR-STR-ONB-10** The onboarding flow shall be available in English, Urdu and Arabic, work on phones, and allow resuming later from any device.
- **AC:** an owner signs up on a phone, answers the profile questions, imports 300 items and 40 customers from a spreadsheet, issues a first invoice with the correct tax and a printable template within an hour, and invites an accountant by email.

## 8. Trading starter pack (TRD)

An industry pack (document 1, section 6.2) that turns the platform into a ready trading and distribution setup. Services and retail profiles follow the same pattern.

- **FR-STR-TRD-01** The pack shall preload a chart of accounts for trading, a default tax configuration per country pack, numbering schemes, payment terms, units of measure with carton and piece conversions, and expense categories.
- **FR-STR-TRD-02** The core flow shall be buy, stock, sell: purchase order and supplier bill, goods receipt into one warehouse, quick invoice with stock reduction, customer receipt, supplier payment, with margin visible on each sale.
- **FR-STR-TRD-03** Items shall support several units (carton, dozen, piece), retail and wholesale price lists, barcode and label printing, and simple weighted-average valuation by default.
- **FR-STR-TRD-04** Customers and suppliers shall have simple statements and balances, credit limits with a warning, and printable or shareable ledgers.
- **FR-STR-TRD-05** Cash and bank handling shall include a cash book, bank accounts with statement import, cheque and post-dated cheque tracking where the country pack provides it, and petty cash.
- **FR-STR-TRD-06** Invoices, delivery challans, quotations and purchase orders shall be generated from bilingual templates with the company logo, tax details and QR or barcode elements required by the country pack, and shareable by email, print or messaging link.
- **FR-STR-TRD-07** Approvals shall use two default workflows only: owner approval for purchases above a threshold and owner approval for discounts above a percentage, both editable.
- **FR-STR-TRD-08** Reorder alerts shall warn when stock falls below a level per item and offer to create a purchase order.
- **FR-STR-TRD-09** Standard reports shall be limited to the useful few: daily sales, profit by item and customer, stock on hand and valuation, receivables and payables ageing, cash position, expense summary, sales tax summary.
- **FR-STR-TRD-10** Returns shall be handled as simple credit notes and supplier returns with stock adjustment.
- **FR-STR-TRD-11** The pack shall include sample data for a typical trader and a guided first-week checklist.
- **FR-STR-TRD-12** The pack shall be built entirely from configuration and the standard engines; where a quick screen is needed (FR-STR-SIM-05), it composes standard components.

## 9. Limits, metering and upgrade path (LIM)

- **FR-STR-LIM-01** Each edition shall be an entitlement template of keys and limits (document 14, section 8): full users, self-service users, companies, storage, API calls, AI tokens, custom fields per entity, saved reports, scheduled reports, workflows and webhooks.
- **FR-STR-LIM-02** Limits shall be enforced at the point of use with soft warnings (default 90 percent), clear messages that name the limit and the way to raise it, and hard stops only where the entitlement requires.
- **FR-STR-LIM-03** Raising limits shall be possible by add-on or edition change in the customer's own billing page, effective immediately, with proration.
- **FR-STR-LIM-04** Upgrading from Starter to Business or Enterprise shall change the entitlement only: no migration, no new installation; new features appear and existing data remains intact.
- **FR-STR-LIM-05** Moving to Enterprise-level deployment (private cloud, on-premise) shall use the tenant export and import path (document 2, TEN-06 and TEN-10) with verification.
- **FR-STR-LIM-06** Downgrades shall keep all data: features above the new edition become read-only or hidden with a clear explanation, and data created by them remains exportable.
- **FR-STR-LIM-07** Cancellation shall move the tenant to read-only, allow a full export, and retain data for a defined period (placeholder 90 days) before deletion with a certificate.
- **FR-STR-LIM-08** Fair-use limits shall protect shared infrastructure (request rates, import sizes, concurrent jobs) and be visible in the admin console.
- **AC:** a Starter tenant reaches 90 percent of its user limit, sees a message with an add-on option, buys three more full users, and the change applies immediately with prorated billing and no downtime.

## 10. Operations and cost to serve (OPS)

Small tenants pay little, so their cost to serve must be very low.

### 10.1 Tenancy for many small tenants

| Option | Description | When |
| --- | --- | --- |
| A. Database per tenant on pooled clusters (default) | Same architecture as other tenants (document 1, D-02) on shared PostgreSQL clusters with connection pooling; no new design | From the first Starter release |
| B. Pooled micro tier | Shared schema with tenant identifier and row-level security for very small tenants, behind the same tenant-resolution layer (document 1, section 5.1) | Only after review triggers below are met, by a new ADR |
| C. Private cloud or on-premise | Not offered for Starter | Enterprise only |

Review triggers for option B: more than 2,000 tenants per cluster group, infrastructure cost per Starter tenant above an agreed share of its revenue, or fleet migration time above an agreed window. Thresholds are to be set with real cost data.

### 10.2 Requirements

- **FR-STR-OPS-01** Provisioning, upgrade, suspension, export and deletion of tenants shall be fully automated and measured (provisioning under 10 minutes, no manual steps).
- **FR-STR-OPS-02** Idle tenants (no activity for a configurable period, placeholder 30 days) shall be hibernated: connections released and resources reclaimed while data stays intact, waking within seconds on next login.
- **FR-STR-OPS-03** Resource quotas and noisy-neighbor controls (connection limits, statement timeouts, job concurrency caps) shall apply to every tenant by edition.
- **FR-STR-OPS-04** Backup retention and environment options shall follow the edition (for example daily backups kept 14 days for Starter) while keeping per-tenant restore.
- **FR-STR-OPS-05** Fleet upgrades shall roll out automatically with canary tenants and require no action from small customers; maintenance windows shall be announced in the product.
- **FR-STR-OPS-06** Usage and cost meters per tenant (storage, compute proxies, AI tokens, support contacts) shall be recorded so the margin of each edition can be measured (document 1, section 7; kernel ENT-06).
- **FR-STR-OPS-07** Abuse and spam tenants shall be detected and suspended automatically, with a review queue.
- **FR-STR-OPS-08** Tenant data residency for small tenants shall follow the region chosen at signup, among the regions offered.
- **FR-STR-OPS-09** Support tooling shall allow scoped, time-boxed impersonation with tenant consent (document 4, SEC-09) to resolve issues quickly.

## 11. Support, accountants and partners (SUP)

- **FR-STR-SUP-01** Support for Starter shall be self-service first: searchable help, in-app guidance, the AI assistant over help content (document 9, section 14), community or knowledge base, and email or ticket with stated response targets.
- **FR-STR-SUP-02** Problem reports from any screen shall include context automatically (document 13, HLP-07) so support needs fewer questions.
- **FR-STR-SUP-03** Paid onboarding packages (remote setup, data import, training) shall be available as optional add-ons, deliverable by you or by partners.
- **FR-STR-SUP-04** An external accountant role shall give accountants access to a client tenant (view, post journals, close periods, tax reports) invited by the client, not counted as a full user, with all actions audited and revocable by the client.
- **FR-STR-SUP-05** A partner console shall let an accountant or reseller see all of their client tenants, switch between them with one sign-in, see health and renewal status, and start new client tenants under their account; commissions or margins are defined by a partner agreement. (wave C)
- **FR-STR-SUP-06** Partner and accountant accounts shall use multi-factor authentication, and client data access shall follow each client's grants only.
- **FR-STR-SUP-07** A referral and discount mechanism (coupons, partner codes) shall be supported in checkout.

## 12. Billing and commercial flow (BIL)

The vendor back office in this section is separate from the product's own Finance module; it bills the product's customers.

- **FR-STR-BIL-01** Self-serve checkout shall support plans and add-ons, monthly or annual terms, trial conversion, coupons, and invoices in the customer's country format.
- **FR-STR-BIL-02** Payment methods shall be provided through billing connectors per region (cards, bank transfer, local wallets and payment services), with receipts and tax invoices for the vendor's own sales.
- **FR-STR-BIL-03** Plan changes and renewals shall update the tenant's entitlement through the entitlement API automatically.
- **FR-STR-BIL-04** Failed payments shall trigger reminders and then the grace, read-only and locked states of the entitlement state machine (document 2, ENT-04), never data deletion.
- **FR-STR-BIL-05** Usage-based items (extra users, storage, AI tokens) shall be metered and billed with clear monthly statements.
- **FR-STR-BIL-06** Customers shall be able to view invoices, update payment details, change plan, and cancel from a billing page in the product.
- **FR-STR-BIL-07** On-premise and larger customers shall be billed through quotes, invoices and licenses issued by the vendor back office, with license files tied to the same entitlement records.

## 13. Owner and mobile conveniences (MOB)

- **FR-STR-MOB-01** The mobile experience shall let an owner see the owner dashboard, approve purchases and discounts, issue a quick invoice, record a payment received, check stock by item or barcode and see receivables due.
- **FR-STR-MOB-02** Invoices and statements shall be shareable as a link or file through messaging and email from web and mobile, in the customer's language.
- **FR-STR-MOB-03** The phone camera shall scan barcodes for stock and sales; offline use shall cover quick invoices and stock counts with sync (document 2, SYN).
- **FR-STR-MOB-04** Notifications for the owner (payment received, invoice overdue, stock low, approval waiting) shall be configurable and available by push and messaging channels where licensed.

## 14. Country starters

Planning inputs to be verified with local accountants and tax advisers; each is part of the country pack plus the profile.

| Topic | Pakistan | Saudi Arabia | United States |
| --- | --- | --- | --- |
| Chart of accounts | Simple trading chart aligned to local reporting | Simple trading chart aligned to local reporting | Simple small-business chart |
| Indirect tax | Federal and provincial sales tax settings with registration-based invoices, withholding handling | 15 percent VAT, simplified invoices | State sales tax setup through a rate service or simple table |
| Invoicing compliance | Authority integration where required by sector; sales tax invoice layouts | National e-invoicing phases for small taxpayers, QR codes | None mandated; customer-ready PDF and portal links |
| Payments | Cheque and post-dated cheque tracking, bank transfer, cash | Bank transfer and card, cash | ACH, card, check |
| Payroll for People Lite | Pakistan statutory contributions and tax slabs | Social insurance, wage protection file | Partner payroll (document 6, wave C) |
| Language | Urdu and English | Arabic and English | English |
| Typical add-ons | Delivery challan, van sales | Arabic invoice templates | 1099 reminders |

## 15. Success metrics

| Metric | Target (to validate in pilots) |
| --- | --- |
| Time to first invoice | Under 60 minutes for most trials that start setup |
| Basic setup complete | Within one day for most trials |
| Trial-to-paid conversion | Tracked from the first pilot and improved each release |
| Support contacts per tenant per month | Falling over time; automation resolves routine questions |
| Cost to serve per Starter tenant | Within the margin target set in the pricing document |
| Upgrade rate Starter to Business | Tracked as a signal of growth fit |
| Customer satisfaction | Measured after onboarding and quarterly |

## 16. Build slices, decisions and open items

### 16.1 Build slices (Step 15, after waves A and B foundations)

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S15.1 | Business-profile questionnaire and configuration mapper | FR-STR-SIM-01, 02 | Opus |
| S15.2 | Simple mode presentation: flags, menus, labels, role presets, add-this-feature, quick screens | FR-STR-SIM | Opus |
| S15.3 | Self-service signup, automated provisioning and trial lifecycle | FR-STR-ONB | Fable |
| S15.4 | Trading starter pack with practitioner review | FR-STR-TRD | Opus |
| S15.5 | Edition templates, limit enforcement, upgrade and downgrade | FR-STR-LIM | Fable |
| S15.6 | Tenant hibernation, quotas and cost metering | FR-STR-OPS | Fable |
| S15.7 | Self-serve billing integration and entitlement API | FR-STR-BIL | Opus |
| S15.8 | Accountant role and partner console | FR-STR-SUP-04 to 06 | Opus with Fable security review |
| S15.9 | Owner mobile features and sharing | FR-STR-MOB | Opus |
| S15.10 | Small-business import assistant | FR-STR-ONB-04, 05 | Opus |
| S15.11 | Support automation and in-app help | FR-STR-SUP-01, 02 | Opus |

S15.1 and S15.2 can start once the kernel metadata engine and settings exist (Step 2); the others follow the modules they package. A Service Starter can be released after Step 6, and the Trading Starter after Step 9.

### 16.2 New decisions proposed

| ID | Decision | Status |
| --- | --- | --- |
| D-26 | The product is sold as three editions (Starter, Business, Enterprise) defined as entitlement templates; names and limits are commercial choices | Proposed |
| D-27 | Small tenants run on the same architecture (database per tenant on pooled clusters) until the review triggers of section 10.1 justify a pooled micro tier by ADR | Proposed |
| D-28 | The Trading Starter launches without the Sales module, using direct invoicing in Finance and Inventory issues; a Service Starter can launch after wave A | Proposed |
| D-29 | Simple mode is a presentation and configuration layer (profiles, flags, labels, quick screens), never a separate code branch | Proposed |

### 16.3 Open items

Prices, edition names and numeric limits; whether Sales is promoted into wave B; trial length; local payment providers per country; whether an on-premise single-server Starter installer is offered; accountant and reseller program terms; which regions are offered at signup; thresholds for the pooled micro tier; whether People Lite is included in Starter or sold as an add-on.

## 17. Baseline 1.0 addendum (this document is now version 0.2)

*Added 14 November 2026 by the comprehensive review (Doc 16). Sections 1 to 16 are unchanged. The previous state (version 0.1) is kept in this document's version history and in the frozen originals archive. Items tied to a proposed decision take effect on its approval.*

### 17.1 Editions, size profiles and deployment tiers are three separate levers (proposed D-40)

An edition says what the customer has paid for. A size profile says how complex the product feels on day one. A deployment tier says where the tenant runs. The four customer segments of section 2 become four size profiles: Micro (5 to 20 people), SME (20 to 250), Mid (250 to 5,000) and Enterprise (5,000 to 50,000 and above). The business profiles of section 6 (Trading, Services, Retail) are a second axis: a tenant has one size profile and one business profile, and both are configuration packages.

- **FR-STR-SIM-12** The signup questionnaire (SIM-01) shall select a size profile as well as a business profile. A tenant administrator shall be able to change the size profile at any time within the tenant's entitlements.
- **FR-STR-SIM-13** Every feature hidden by a size profile shall exist in the data model with a real default record (one legal entity, one branch, one warehouse, one cost center, one approval route of zero steps), so that changing profile never needs a data conversion.
- **FR-STR-SIM-14** No requirement shall be implemented as edition-specific or profile-specific code. Behavior is gated by an entitlement key or a setting.

The defaults of each profile by module are in Doc 16 section 5.2.

### 17.2 Pooled deployment for small tenants (proposed D-30; changes section 10.1 and D-27)

Section 10.1 kept the pooled micro tier (option B) as a later possibility behind review triggers. Under D-30 the data model is pooling-ready from the first migration, and pooled mode is built and tested in Phase 1. What still waits is the commercial offer.

- **FR-STR-OPS-10** Starter tenants on hosted SaaS may be placed in pooled mode. Business and Enterprise tenants, and all on-premises installations, are always in dedicated mode.
- **FR-STR-OPS-11** Pooled mode shall not be sold until an independent security review of tenant isolation has passed.
- **FR-STR-OPS-12** A pooled tenant shall be movable to dedicated mode on upgrade or on request, without downtime beyond a stated maintenance window and with verification of row counts and ledger totals.

### 17.3 Starter availability by phase (proposed D-32; changes section 5.2 and D-28)

| Offering | v0.1 | Now |
| --- | --- | --- |
| Service Starter (Finance and People Lite) | End of wave A | Release 1.0, end of Phase 2 |
| Trading Starter | End of wave B, without Sales (option 1) | Release 2.0, end of Phase 3, **with Sales core**: quotation, order, delivery, invoice, returns |
| Sales extended (promotions, contracts, commissions) | Wave C | Phase 4, as add-ons |

Option 2 of section 5.2 (promote Sales) is therefore adopted. FR-STR-TRD-02 (quick invoice with stock reduction) is implemented on Sales core, not on direct Finance invoicing.

### 17.4 Build slices

S15.1 to S15.3 are built in Phase 2 for the Service Starter. S15.4 to S15.11 are built in Phase 3. Model assignment follows risk class (Doc 12 section 16.2): S15.3, S15.5 and S15.6 are class B (Opus builds, Fable reviews); the others are class C or D.
