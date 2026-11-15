# Doc 16 – ERP Baseline 1.0: Architecture Review, Unified Baseline and Development Strategy

Version 1.0 · 14 November 2026 · Status: for approval · No code has been written

## 1. Verdict and how to read this document

**Verdict: CONDITIONAL GO, for Phase 1 (platform foundation) only.** The planning set is broad, internally consistent after this review, and strong enough to start the foundation once the conditions in section 12 are met. It is not yet ready for unrestricted module development: no column-level data design exists for any business module, the security architecture is still a gap, and three significant architectural changes proposed here need your approval.

> **Update 15 November 2026 (Baseline 1.1).** You changed the module order and the model choice. Section 14 re-plans the phases: HRMIS first, then Inventory and Assets, then Finance, then Procurement and Supply Chain. It also allocates work to Opus 5.5 and Sonnet 5.5 only. Where section 14 conflicts with sections 4, 8, 9 or 12, section 14 wins once you approve it.

This document is the single entry point to the planning set. It holds the review findings, the decisions awaiting approval, the rules that apply across modules, the four-phase plan, the model allocation and cost scenarios, the governance rules, and the change history. Where it conflicts with an older statement in another document, this document and Doc 14 (decision log) win.

Three things to know before reading further:

- **Basis of the review.** I reviewed the sixteen documents as they stand in this project, by reading the architecture, data, decision and posting sections closely and by scripted cross-checks across the full text (about 1.1 million characters). It was not a line-by-line reading of every requirement; section 12.4 says what that leaves unchecked.
- **Proposed, not approved.** Decisions D-30 to D-40 are written into the affected documents as *proposed changes pending approval*, clearly marked, so you can see exactly what would change. Nothing in them is treated as approved until you say so.
- **No code.** Nothing here starts development. Work begins only on your explicit GO-AHEAD.

## 2. Current state of the planning set

### 2.1 What exists

Sixteen planning documents and roughly 2,400 to 2,500 numbered requirements. The two uploaded originals (the reference architecture and the first HRMIS specification, both Word files) remain untouched as uploaded.

| Area | State | Assessment |
| --- | --- | --- |
| Architecture principles, stack, modular monolith, module contracts | Designed | Sound. Preserve. |
| Kernel services (24 areas after D-20) | Designed at requirement level | Sound, but too large to build as one block. See F-01. |
| Tenancy | Designed (database per tenant) | Sound for mid and large tenants. Weak for thousands of very small tenants. See F-02. |
| Finance, Procurement, Inventory, Assets, Supply Chain, Sales | Designed at requirement level | Functionally broad. Cross-module exception rules were scattered or missing. See F-04. |
| HRMIS | v0.2 baseline plus v0.3 (geo-attendance, tasks, mobile) | v0.3 is the most detailed specification in the set. Its kernel requests were not yet accepted by the kernel document. See F-08. |
| Data standards and kernel data model | Designed | Standards are strong. Table-level detail exists only for the kernel. |
| **Column-level data model and entity relationship diagrams for business modules** | **Missing** | The largest gap. See F-05. |
| **Security architecture and threat model** | **Missing** (gap G-01) | Must exist before Phase 1 ends. |
| **Country pack detail (Pakistan payroll, tax, statutory)** | **Outline only** (gap G-05) | Must exist before Phase 2 payroll work. |
| Interface (API) specifications | Conventions only | Acceptable. Detailed contracts are produced per slice, by design. |
| Editions and licensing | Designed (Starter, Business, Enterprise) | Sound. Extended here with size profiles. See section 5. |
| Build plan | Waves A to D and Steps 0 to 15 | Regrouped into four phases. See section 8. |
| Model allocation | One table in Doc 12 | Replaced by section 9. |

### 2.2 What is preserved without change

The core technical choices hold up under review and are not reopened: modular monolith, .NET and PostgreSQL, React and React Native, one schema per module, no foreign keys across modules, events through an outbox, entitlements as a single service for both licence file and subscription, country and industry packs, and the rule that AI proposes and a person confirms.

### 2.3 What was duplicated or unnecessarily complicated

- **Two planning vocabularies.** Waves (A to D), build steps (0 to 15) and mobile releases (M1 to M4) overlapped. The four phases are now the only planning unit you need; steps and mobile releases map into them.
- **Kernel breadth on day one.** Rules engine, report builder, global search, collaboration and external portals were all implied before the first module. Most are deferred (F-01).
- **Geospatial extension.** HRMIS v0.3 allowed a database geospatial extension whose licence conflicts with selling source code. Replaced by an application library (F-07).
- **Model table naming a fourth model.** Doc 12 allocated work to a model outside the three you intend to use. Corrected.

## 3. Critical findings and resolutions

Severity: **Critical** blocks development or risks wrong financial records; **High** causes expensive rework if left; **Medium** should be fixed before the phase that touches it.

### F-01 The kernel is too large to build before the first module (High)

**Problem.** Doc 2 defines twenty-four kernel areas. Read literally, all of them come before any business module. That is many months with nothing a customer can use, and services built without a real module to test them tend to be wrong in detail.

**Alternatives.** (a) Build the full kernel first. (b) Build each kernel service only when a module needs it. (c) Define a fixed minimum kernel, and schedule every other service to the phase whose modules first need it.

**Resolution (D-33).** Option (c). The Minimum Viable Kernel is: Tenancy, Organization, Identity and access, Settings, Audit, Numbering, Parties, Catalog (basic), Tax (basic), Localization, Workflow (linear and conditional approvals), Notifications (in-app and email), Documents (attachments), Jobs, Entitlements, Integration basics (API keys, outbox, import and export). Deferred: Rules engine beyond workflow conditions, Metadata designer beyond custom fields, Configuration packages, Offline sync (Phase 2), AI gateway (thin in Phase 2), External portal, Global search, Collaboration (Phase 3), report and dashboard builders (Phase 3). *Updated: Baseline 1.0 addendum in Docs 1 and 2.*

### F-02 Database-per-tenant alone does not serve thousands of very small tenants (High)

**Problem.** Doc 1 section 5.1 fixes one database per tenant and states that business rows carry no tenant identifier. That is the right default for isolation, backup, on-premises delivery and large customers. But a Starter customer with ten staff then costs a full database: connection pools, migrations run thousands of times, catalog overhead and monitoring per database. At a few hundred tenants this is fine. At several thousand small tenants it becomes the dominant operating cost and makes every release slower. Adding a tenant identifier to every table later is one of the most expensive retrofits there is.

**Alternatives.** (a) Keep database per tenant only. (b) Shared database for everyone. (c) Hybrid-ready: one schema design that runs in both modes.

**Resolution (D-30, needs approval).** Option (c). Every tenant-scoped table carries `tenant_id` from the first migration, included in unique keys. In dedicated mode (the default, and the only mode for Business, Enterprise and on-premises) it is a constant and costs almost nothing. In pooled mode (SaaS Starter only) many small tenants share one database and row-level security policies enforce isolation. A tenant can be moved from pooled to dedicated by export and import without changing schema. Both modes are tested from Phase 1 by the same isolation test suite. Pooled mode is not offered commercially until it passes an independent security review. *Updated: Baseline 1.0 addendum in Docs 1, 3 and 15.*

**Why this reverses an earlier statement.** Doc 1 said a tenant identifier was redundant and invited mistakes. That is true if pooling is never needed. Your requirement that one platform serve 5 to 50,000 employees makes pooling likely, so the cheap insurance now outweighs the small redundancy.

### F-03 Posting to the ledger after the fact weakens financial integrity (Critical)

**Problem.** Doc 1 section 4.4 and Finance PST-07 let an operational document (a goods receipt, an invoice, a payroll run) complete while its ledger entry is created later through an event, with failures going to an exception queue. Inside one tenant database this is unnecessary, and it allows stock, payables or payroll to disagree with the ledger for an unbounded time. Auditors treat an unposted backlog as a control weakness, and a small business has nobody to work an exception queue.

**Alternatives.** (a) Keep event-based posting. (b) Post in the same database transaction as the source document. (c) Same-transaction posting by default, deferred posting as a policy choice.

**Resolution (D-31, needs approval).** Option (c). The default is atomic posting: the source module calls the Finance posting contract inside its own transaction; if posting fails (closed period, missing account mapping, invalid dimension, missing rate), the document does not complete and the user sees the reason at once. This is possible without breaking module isolation because all modules of a tenant share one database and the call goes through a published contract, not a table reference. Deferred posting remains available by policy for high-volume sources (for example point-of-sale batches) and when Finance is not licensed; then the exception queue applies with an ageing alert and a rule that a period cannot close while it is non-empty. Events are still published after commit for analytics and other listeners. *Updated: Baseline 1.0 addendum in Docs 1 and 5; notes in Docs 7, 8, 10 and 11.*

### F-04 Cross-module exception rules were scattered or missing (Critical)

**Problem.** Each module handled its own reversals and amendments, but nobody owned the cases that cross modules: reversing a receipt after the supplier invoice is matched, backdating stock into a closed costing period, a retroactive pay change after posting, an employee who is also a payee, an asset received through a purchase order then returned. These are where ERP systems produce wrong books.

**Resolution.** Section 6 of this document defines 32 binding cross-module rules (FR-XM-01 to FR-XM-32). Each module specification now points to them. Where a rule changes earlier wording, the module document carries an explicit note.

### F-05 No column-level data model exists for business modules (Critical for module work)

**Problem.** Doc 3 gives standards and kernel tables. No module has entity relationship diagrams, column lists, keys, constraints or indexes. If coding agents invent these slice by slice, the result will be inconsistent and expensive to repair.

**Resolution (D-35).** A *module data design gate*: before any module build starts, the architecture model produces that module's data design (entities, columns, keys, constraints, indexes, partitioning, ledger tables, diagram) as an appendix to the module specification, reviewed against Doc 3 and section 6 here. No migration for a module is written before its gate passes. Kernel data design is a condition for Phase 1. This is deliberately not done now for all modules at once, because designs made far ahead of build go stale. *Updated: Baseline 1.0 addendum in Docs 3 and 12.*

### F-06 Sales was scheduled too late for a trading business (High)

**Problem.** The Starter edition's main audience is small trading companies. Inventory and Procurement without Sales gives them stock but no invoices to customers except through Finance.

**Resolution (D-32).** Four phases replace waves as the planning unit, and Sales core (quotation, order, delivery, invoice, returns, basic pricing) moves into Phase 3 alongside Procurement and Inventory. *Updated: Baseline 1.0 addendum in Docs 1, 14 and 15.*

### F-07 Geospatial database extension conflicts with source-code licensing (Medium)

**Problem.** HRMIS v0.3 allowed a PostgreSQL geospatial extension. Its copyleft licence sits badly with D-09 (permissive dependencies only) and with on-premises source delivery.

**Resolution (D-36).** Store latitude, longitude and accuracy as plain columns, store fence shapes as standard geometry text, and compute distance and containment in the application with a permissively licensed library. Attendance volumes (a few points per employee per day against a handful of fences) do not need database-side spatial indexing. *Updated: Baseline 1.0 addendum in Doc 3; HRMIS v0.3 section 41.3.*

### F-08 HRMIS v0.3 kernel requests were not accepted by the kernel (High)

**Problem.** HRMIS v0.3 asked the kernel for eight extensions (KER-01 to KER-08: geospatial service, map component, device trust registry, push notification tokens and providers, classification and retention of location data, mobile API conventions, mobile session management, large media pipeline). Doc 2 did not contain them, so the two documents disagreed about what the kernel provides.

**Resolution (D-37).** v0.3 becomes the HRMIS baseline for build planning (v0.2 stays intact as the historical baseline), and Doc 2 accepts KER-01 to KER-08 with a phase for each. *Updated: Baseline 1.0 addendum in Doc 2 section 32.2; HRMIS v0.3 section 41.*

### F-09 No rule for what is in a release (High)

**Problem.** About 2,400 requirements with no marking of which are needed for the first sellable release. Coding agents will try to build everything in a section.

**Resolution (D-34).** At each phase entry, every requirement in scope is tagged R (in this release), L (later) or X (not planned), and only R items become slices. Tagging is a short architecture-model task per module, approved by you. Phase scope in section 8 is the starting point.

### F-10 Model allocation and change control were informal (Medium)

**Resolution (D-38, D-39).** Sections 9 and 11. *Updated: Baseline 1.0 addendum in Doc 12.*

### F-11 Small-business fit depended on edition alone (Medium)

**Problem.** Three editions describe what is licensed, not how complex the product feels. A 5,000-employee Business customer and a 30-employee one need different defaults.

**Resolution (D-40).** Four size profiles as configuration presets, independent of edition. Section 5. *Updated: Baseline 1.0 addendum in Doc 15.*

### F-12 Six defects in the data standards (High, corrected)

**Problem.** A second pass over Doc 3 against Finance and the module documents found defects that follow from decisions already made, so they need correction, not a new decision:

| ID | Defect | Consequence if left |
| --- | --- | --- |
| C-37 | The posting idempotency key (Doc 3 section 7.4, Finance PST-06) omits the ledger, although Finance COA-09 and PST-02 let one source post to several ledgers | The second ledger's entry collides or is silently skipped: wrong books in multi-ledger tenants |
| C-38 | Item and location dimensions are required (Doc 1 section 14.1, Doc 3 section 7.2) but absent from the journal line model | Schema surgery on the largest table when Inventory arrives |
| C-39 | Branch is a data-scope dimension and an organization entity, but no transactional table carries it | Branch-level security and reporting cannot work |
| C-40 | Posting updates one balance row per account in the posting transaction; Doc 16 section 5.4 says the opposite | Month-end posting serializes on a few rows |
| C-41 | The kernel item holds one cost and one price for all companies | Valuation or pricing code reads a value that is wrong for most companies |
| C-42 | Association tables without an id; server-generated ids versus records created offline | Inconsistent migrations; duplicate records on sync retry |

**Resolution.** All six are corrected in Doc 3 section 14.2 and referenced from Docs 1, 5, 7, 8, 10 and 11. They are registered as C-37 to C-42 in Doc 14 section 16.2.

### F-13 Erasure of personal data conflicts with append-only audit (Medium)

**Problem.** Audit rows are append-only and hold before and after values, which include personal data. Privacy laws in the target markets give people a right to erasure in some cases. The documents required both without saying how.

**Resolution.** Personal and sensitive fields are written to the audit log encrypted with a key per data subject; erasure destroys the key and leaves the chain intact. Records that law requires to be kept are excluded per country pack (Doc 3 section 14.3). The legal position in each country needs local counsel and is part of the security architecture (gap G-01).

### F-14 Entity relationship diagrams do not exist (noted, by design)

You asked for the diagrams to be reviewed. There are none: the set has table lists for the kernel (Doc 3 sections 4 to 6 and 13) and entity overviews per module. Drawing diagrams now, before column-level design, would give pictures of a model that will change. Each data design gate (F-05) delivers the diagram with the columns, and after that the diagram is generated from the real schema so it cannot drift.

### Findings that need no document change

- **Performance at 50,000 employees.** The design already covers the known pressure points (partitioned ledgers and attendance, set-based payroll, read replicas, background jobs, a reporting store). What is missing is proof; section 8 puts load tests with named targets in each phase.
- **Module standalone operation.** D-10 (hard dependency on kernel only) already allows any module to run alone. FR-XM-01 and FR-XM-02 keep that true under atomic posting.
- **Admin console, Analytics and AI, and UX documents** (Docs 4, 9, 13) are consistent with the baseline and are left unchanged. The map component accepted for KER-02 will be specified in Doc 13 at Phase 3 entry.

## 4. Decisions requiring your approval

### 4.1 New decisions from this review

| ID | Decision | Why it needs you | If rejected |
| --- | --- | --- | --- |
| **D-30** | Hybrid-ready tenancy: `tenant_id` on every tenant-scoped table; dedicated database by default; pooled mode with row-level security for SaaS Starter only | Reverses a stated rule in Doc 1; affects every table | Stay database-per-tenant only; accept higher running cost per small tenant and a hard retrofit later |
| **D-31** | Atomic ledger posting by default; deferred posting by policy | Changes a core Finance behaviour (PST-07) and every posting module | Keep event-based posting; add ageing alerts and close blocks to the exception queue |
| **D-32** | Four phases replace waves as the planning unit; Sales core moves to Phase 3 | Changes the approved wave plan (D-25) | Keep waves; Trading Starter waits for the old Wave C |
| **D-33** | Minimum Viable Kernel is binding; other kernel services are scheduled by phase | Defers services earlier documents implied were first | Longer foundation before any usable module |
| **D-34** | Release scope tagging (R, L, X) at each phase entry, approved by you | Gives you a recurring approval duty | Agents interpret scope themselves |
| **D-35** | Module data design gate before any module build | Adds an architecture step per module | Data model emerges slice by slice |
| **D-36** | Geospatial in the application with a permissive library; no database geospatial extension | Changes an HRMIS v0.3 allowance | Legal review of the extension's licence for source delivery |
| **D-37** | HRMIS v0.3 is the HRMIS baseline for build planning; kernel accepts KER-01 to KER-08 | Confirms v0.3 scope into the plan | HRMIS builds to v0.2; geo-attendance and tasks unscheduled |
| **D-38** | Three-model allocation and escalation rules (section 9) | Sets how your budget is spent | Ad hoc model choice |
| **D-39** | Change control: no change to an approved baseline item without a recorded decision (section 11) | Makes you the approver of baseline changes | Baseline drifts as agents work |
| **D-40** | Four size profiles (Micro, SME, Mid, Enterprise) as configuration presets over three editions | Adds to the commercial model | Edition alone sets defaults |

### 4.2 Decisions still open from earlier documents

- **D-05, D-07, D-09, D-10, D-11** (Doc 1 section 0): still marked proposed. The review found no reason to change any of them. D-09 (permissive licences only), D-10 (modules depend only on the kernel) and D-11 (shared master data lives in the kernel) are relied on throughout this baseline. *Recommendation: approve all five.*
- **D-26 to D-29** (Doc 15, Starter edition): no conflict found. *Recommendation: approve, with D-30 and D-40 as extensions.*
- **MD-01 to MD-28** (HRMIS v0.3 section 37): policy choices for attendance, tasks and mobile. Each has a documented default, so they do not block Phase 1. They must be settled before Phase 3 geo-attendance work. MD items that mention the geospatial extension are superseded by D-36 if you approve it.

### 4.3 Assumptions made to keep the review moving

| # | Assumption | Effect if wrong |
| --- | --- | --- |
| AS-1 | First paying customers are in Pakistan; KSA follows; US is later and needs separate payroll and tax partners | Reorders country packs in Phases 2 to 4 |
| AS-2 | First release is SaaS hosted by you; on-premises packaging is proven in Phase 2 but sold from Phase 3 | Moves installer and offline licence work earlier |
| AS-3 | You will engage a part-time senior technical adviser (human) for security review, production operations and escalations no model should own alone | The verdict conditions in section 12 cannot be met |
| AS-4 | A practising Pakistani payroll and tax professional validates the Pakistan pack before any customer payroll is run | Payroll release is blocked |
| AS-5 | Development is done through Claude Code with repository, automated checks and a test environment set up in Phase 1 | Governance in section 11 does not hold |
| AS-6 | Largest single tenant to design and test for: 50,000 employees, 2,000 concurrent users, 50 legal entities, 10 million ledger lines a year | Load test targets change |

## 5. Scalability model: one platform from 5 to 50,000+ employees

### 5.1 Three levers, kept separate

One codebase and one data model serve every size. What differs is controlled by three independent levers:

| Lever | Question it answers | Mechanism | Who changes it |
| --- | --- | --- | --- |
| **Edition** (Starter, Business, Enterprise) | What has the customer paid for? | Entitlement template: modules, limits, features | You, through licence or subscription |
| **Size profile** (Micro, SME, Mid, Enterprise) | How complex should it feel on day one? | Configuration preset: defaults, visible fields, approval depth, menus | Tenant administrator, any time, within entitlements |
| **Deployment tier** (pooled, dedicated, dedicated cluster, on-premises) | Where does it run and how is it isolated? | Infrastructure placement | Your operations |

Editions are the commercial product. Profiles are not separate software and not a price: they are starting configurations that a customer grows out of by changing settings, without migration. This answers the edition-versus-tier-versus-profile question with *a combination, each doing one job*.

### 5.2 Size profiles (D-40)

|  | Micro (5 to 20) | SME (20 to 250) | Mid (250 to 5,000) | Enterprise (5,000 to 50,000+) |
| --- | --- | --- | --- | --- |
| Typical edition | Starter | Starter or Business | Business | Enterprise |
| Legal entities | One | One to three | Several | Many, with consolidation |
| Branches and warehouses | One of each | A few | Many | Many, with regions |
| Approvals | None or one step | One or two steps by amount | Matrix by amount, department and type | Matrix plus delegation, escalation, segregation of duties |
| Finance | Simple chart, one currency, cash and bank, tax invoices; periods close softly | Cost centres, budgets as warnings | Multi-currency, hard budget control, period locks by sub-ledger | Multi-ledger, intercompany, consolidation, cost accounting |
| HR | Employee list, leave, simple payroll | Attendance, shifts, loans | Positions, grades, geo-attendance, tasks | Talent, workforce planning, multi-country payroll |
| Inventory | One warehouse, average cost, no bins | Several warehouses, batches where needed | Bins, serials, cycle counts, FIFO or standard cost | Advanced planning, quality, landed cost at scale |
| Procurement | Direct purchase order | Requisition and order | Quotations, contracts, three-way match | Tendering, supplier portal, framework agreements |
| Roles | Owner, Staff, Accountant | About eight standard roles | Full role catalogue | Custom roles, field-level security |
| Setup time target | Under one hour with the wizard | One to three days | Weeks, with partner | Project |
| Deployment tier | Pooled (if D-30 approved) or dedicated | Dedicated database, shared server | Dedicated database, sized server | Dedicated cluster or on-premises |

**Rule SC-1.** Every feature that a Micro profile hides must still exist in the data model with a safe default (one legal entity, one warehouse, one cost centre, one approval step of zero length), so switching profile never needs a data conversion.

**Rule SC-2.** Complexity is hidden, not removed: the hidden default record is real. A Micro tenant has one warehouse row, one fiscal calendar and one default approval route.

**Rule SC-3.** No requirement may be written as edition-specific code. It is written once and gated by an entitlement key or a setting.

### 5.3 Organizational scalability

The kernel Organization service already models tenant, legal entity (company), branch or location, department, cost centre, profit centre, warehouse and position, each effective-dated, with several named hierarchies (legal, reporting, cost, geographic). The review confirms this is sufficient. Two clarifications are now binding:

- Every transactional row carries its legal entity. Reports, numbering, tax, periods and security scope by legal entity first.
- Centralized and decentralized operation is a setting per process (for example central purchasing with local receiving), not a structural choice.

### 5.4 Performance pressure points and the planned answer

| Pressure point | Where it bites | Planned answer | Proven in |
| --- | --- | --- | --- |
| Many small tenants | Connections, migrations, monitoring per database | Pooled mode (D-30); connection pooler; fleet migrator with canaries | Phase 1 |
| Payroll for 50,000 employees | Row-by-row calculation | Set-based calculation in batches, parallel by pay group, resumable | Phase 2 load test |
| Attendance events | Tens of millions of rows a year | Monthly partitions, summary tables, archive policy | Phase 3 load test |
| Ledger and stock ledger growth | Reports slow down over years | Partition by fiscal year, period balance tables, no reporting from raw lines | Phase 2 and 3 |
| Month-end concurrency | Posting contention on account balances | Balances updated by append and periodic roll-up, not by locking one row per account | Phase 2 |
| Reports and dashboards | Compete with transactions | Read replica and reporting store; heavy reports run as jobs | Phase 3 |
| Document numbering | Gapless sequences serialize users | Gapless only where law requires; otherwise cached ranges | Phase 1 |
| Search | Table scans | Indexed search service, deferred to Phase 3 | Phase 3 |
| API load | Integrations flood the system | Rate limits per key, bulk endpoints, idempotency keys | Phase 1 |
| Planning runs (supply chain) | Long computations | Background job with snapshot input | Phase 4 |

Numbers attached to these (response times, throughput) are test targets set at phase entry, not claims about a system that does not exist yet.

## 6. Cross-module transaction rules (FR-XM)

These rules bind every module. A module specification may be stricter but may not contradict them. Each rule becomes at least one automated integration test. Rules marked (D-31) depend on that decision; if it is rejected they are restated for deferred posting.

### 6.1 Posting and integrity

- **FR-XM-01 Atomic posting (D-31).** A document with financial effect shall complete and create its ledger entry in one database transaction through the Finance posting contract. If posting is refused, the document shall remain in its previous state and the user shall see the reason and the fix.
- **FR-XM-02 Operation without Finance.** Where Finance is not licensed, the posting contract shall be satisfied by a kernel stub that records an accounting event for later export. No module shall test whether Finance is present.
- **FR-XM-03 Deferred posting by policy.** Where a source is configured for deferred posting, failed postings shall enter the exception queue with an owner and an age; items older than a configured limit shall alert; a period shall not close while the queue holds items dated in it.
- **FR-XM-04 One source, one entry, traceable both ways.** Every ledger entry shall carry the type and identifier of its source document, and every source document shall show its entries. Re-processing the same source shall never create a second entry (idempotency by source key).
- **FR-XM-05 Posted means immutable.** A posted document shall never be edited or deleted. Correction is by reversal (a full counter-entry linked to the original) followed, if needed, by a new document.
- **FR-XM-06 Cancellation versus reversal.** A document not yet posted may be cancelled and keeps its number with status cancelled. A posted document may only be reversed. Legal document numbers shall never be reused.
- **FR-XM-07 Reversal date.** A reversal shall be dated in the original period if that period is open, otherwise on the first day of the earliest open period, with the original date kept for reference. Posting into a closed period shall require reopening by an authorized role, with reason, and shall be audited.
- **FR-XM-08 Reversal order.** A document shall not be reversed while a later document depends on it. The system shall name the blocking documents and offer the correct order (for example: unmatch invoice, then reverse receipt).

### 6.2 Procure to pay

- **FR-XM-09 Receipt before invoice.** A goods receipt shall debit stock or expense and credit a goods-received-not-invoiced account at order price. Matching the supplier invoice clears that account.
- **FR-XM-10 Invoice before receipt.** An invoice arriving first shall be held unmatched or, by policy, posted to an invoice-received-not-delivered account. It shall not be payable past a configured tolerance without a receipt, unless an authorized override with reason is recorded.
- **FR-XM-11 Price and quantity variance.** A difference between invoice and order price within tolerance shall post to inventory (average and FIFO costing, if stock remains) or to price variance (standard costing, or stock already consumed). Outside tolerance, the invoice shall be blocked for approval.
- **FR-XM-12 Reversing a receipt after invoice.** A receipt matched to an invoice shall not be reversed directly. The route is a supplier return document, which reduces stock at the original receipt cost and raises an expected credit note; the credit note clears it.
- **FR-XM-13 Partial chains.** Requisition, order, receipt, invoice and payment shall each allow partial fulfilment with open quantity and open amount tracked per line. A line shall close by full fulfilment or by an explicit short-close with reason; short-closing shall release any remaining budget commitment.
- **FR-XM-14 Amending an approved order.** A change to price, quantity, supplier, terms or delivery beyond tolerance shall create a new version, require re-approval of the difference, keep the earlier version readable, and shall not go below quantities already received or invoiced.
- **FR-XM-15 Return after payment.** Returning goods already paid for shall create a supplier debit balance, settled by refund or offset against later invoices; it shall not reopen the paid invoice.
- **FR-XM-16 Landed cost after receipt.** Late freight, duty or handling shall be allocated to the receipt; the share for stock still on hand adjusts inventory value, the share for stock already issued posts to cost of goods sold or variance.

### 6.3 Inventory

- **FR-XM-17 Backdated stock movements.** A movement dated before later movements of the same item shall be allowed only inside an open inventory period and shall trigger cost recalculation for affected later movements, posting the difference as a dated adjustment. Movements into a closed inventory period shall be refused.
- **FR-XM-18 Negative stock.** Negative stock shall be refused by default. Where policy allows it for an item or warehouse, issues shall be costed at last known cost and corrected automatically when the covering receipt is posted.
- **FR-XM-19 Transfers.** A transfer between warehouses of one legal entity shall move stock through an in-transit location with no profit and no revenue. A transfer between legal entities shall be an intercompany sale and purchase at the transfer price, with matching receivable and payable and elimination data for consolidation.
- **FR-XM-20 Reservations.** Stock reserved for an order shall not be issued to another document without an explicit release. Reservations shall expire by policy and shall be released on cancellation or short-close.
- **FR-XM-21 Count adjustments.** Stock count differences above a value threshold shall require approval before posting, and shall post to a dedicated gain or loss account with the count as source.

### 6.4 Order to cash

- **FR-XM-22 Partial delivery and credit hold.** Sales orders shall allow partial delivery and partial invoicing per line. A customer over credit limit or with overdue balances shall be held at order confirmation and again at delivery; release shall require an authorized role and a reason.
- **FR-XM-23 Customer returns.** A return shall reference the original invoice where known, bring stock back at the original issue cost, and raise a credit note. After payment, the credit shall be refundable or offset.

### 6.5 Assets

- **FR-XM-24 Capitalization.** An asset bought through a purchase order shall be received into an asset-under-construction or clearing account and capitalized by an explicit step that creates the asset record; receipt alone shall not start depreciation. Returning it before capitalization follows FR-XM-12; after capitalization it is a disposal.
- **FR-XM-25 Depreciation and corrections.** Depreciation shall run once per asset per period. A change to cost, life or method shall apply prospectively by default; a retrospective change shall post a catch-up entry in the current open period, never into closed periods. Transfer of an asset between cost centres or legal entities shall split depreciation by date.

### 6.6 People and payroll

- **FR-XM-26 Attendance lock.** Attendance and leave for a pay period shall be locked when payroll is calculated. A later change shall be accepted only as a retroactive adjustment carried into the next open payroll, never by editing the locked period.
- **FR-XM-27 Payroll posting and reversal.** An approved payroll shall post one summarized entry per legal entity and cost allocation, with employee-level detail kept in payroll. A single payslip shall be reversible before payment; after payment, correction is by adjustment in a later run or an off-cycle run.
- **FR-XM-28 One person, several roles.** An employee who is also paid as a supplier or buys as a customer shall be one party record with several roles. Expense claims, advances and loans shall settle through the employee's payable or receivable, and payroll deductions shall reference them.
- **FR-XM-29 Separation with open items.** Final settlement shall not complete while the employee holds open advances, loans, unreturned assets, unsubmitted expense claims, open tasks requiring handover or pending approvals in their queue. Each shall be recovered, written off with approval, or reassigned.
- **FR-XM-30 Transfer between legal entities.** Moving an employee to another legal entity shall end the first employment and start a second, linked, with service continuity, leave balances and loan balances carried by explicit rule, and cost charged to each entity by date.

### 6.7 Control

- **FR-XM-31 Budget commitment life cycle.** Where budget control is on: requisition creates a pre-commitment, order converts it to a commitment, receipt or invoice converts it to actual, and cancellation or short-close releases it. A check that fails shall block or warn by policy; an override shall require a named role and reason. Changing a cost centre on an open document shall move its commitment.
- **FR-XM-32 Approvals, delegation and exceptions.** An approval request shall escalate after its time limit to the next level and shall never auto-approve. Delegation shall be time-bound and recorded. Nobody shall approve their own document. Any use of an override permission (closed-period posting, credit release, tolerance override, budget override, negative stock) shall appear on a single audit exception report per period.

### 6.8 Period close order

Periods close per legal entity in this order, and each step blocks the next: inventory (all movements costed) → payables and receivables sub-ledgers → payroll (all runs posted) → assets (depreciation run) → foreign currency revaluation → general ledger. Reopening runs in reverse and requires the same authority as FR-XM-07.

## 7. Unified baseline register and document versions

Baseline 1.0 is the set of documents below at the versions shown. Twelve documents were updated; four needed no change and were not re-versioned.

### 7.1 How originals are preserved

Each updated document keeps its original text in place and carries its changes in a clearly dated closing section, plus a few marked notes at the sentences a proposed decision would replace. Nothing was deleted. The pre-review text of every document is preserved twice: in a frozen archive file (Markdown and Word copies of all sixteen documents as they stood before this review) delivered with this document, and in each online document's own version history. The two Word files you uploaded at the start are untouched.

### 7.2 Document register

| Doc | Title | Was | Now | What changed | Where |
| --- | --- | --- | --- | --- | --- |
| 1 | [Architecture and Foundation](https://claude.ai/artifact/CtRMceEg7mVs2TQmBart8H) | 0.2 | **0.3** | Hybrid tenancy, atomic posting, minimum kernel, four phases, corrections, map of authoritative texts | Section 18; notes in 4.4, 5.1, 14 |
| 2 | [Platform Kernel](https://claude.ai/artifact/Jq1375pPY7upT57N7fpz5f) | 0.1 | **0.2** | Kernel service schedule by phase; KER-01 to KER-08 accepted with requirement IDs; tenancy mode requirements; posting stub | Section 32 |
| 3 | [Data Architecture Standards](https://claude.ai/artifact/TfSe2LMmrbsmGNzMZv7XZn) | 0.1 | **0.2** | Rules R-16 to R-21; corrections C-37 to C-42; geospatial columns; data design gate; four new invariants | Section 14 |
| 4 | [Admin Console](https://claude.ai/artifact/UfsfDkSETpbRez5TTh76oG) | 0.1 | 0.1 | No change needed |  |
| 5 | [Finance](https://claude.ai/artifact/GMMX2msXhLbxgH8hDSa9S9) | 0.1 | **0.2** | Posting contract PST-12 to PST-16; ledger in idempotency key; FR-XM ownership; scope by phase | Section 24; notes at PST-06, PST-07 |
| 6 | [HRMIS v0.2](https://claude.ai/artifact/7kZfQhSEjdArATVY6Senf5) | 0.2 | 0.2 | Kept untouched as the historical baseline |  |
| 6a | [HRMIS v0.3](https://claude.ai/artifact/AH7R4xFpAnZ7wj6T75oZJH) | 0.3 | **0.3.1** | Build baseline status; outcome of kernel requests; geospatial rule; mapping to phases; FR-XM ownership. No requirement ID changed | Section 41 |
| 7 | [Procurement](https://claude.ai/artifact/1PiaE21BTnqmdV7XRVz2DW) | 0.1 | **0.2** | FR-XM mapping; phase scope; data notes | Section 23 |
| 8 | [Inventory and Asset Management](https://claude.ai/artifact/3uhGMbD2xnpNivHXy2bzyA) | 0.1 | **0.2** | FR-XM mapping; stock balance pattern; phase scope | Section 33 |
| 9 | [Analytics, Reporting and AI](https://claude.ai/artifact/61cTGbbpdWpErrB4Dkfq3f) | 0.1 | 0.1 | No change needed; its phase is set in Doc 2 section 32.1 |  |
| 10 | [Supply Chain](https://claude.ai/artifact/PN73horBc93GkCS1W9PmSq) | 0.1 | **0.2** | Phase 4; FR-XM boundaries; data notes | Section 24 |
| 11 | [Sales and Distribution](https://claude.ai/artifact/SWr3qNtV6Nw5ek7aQEHUi6) | 0.1 | **0.2** | Sales core defined and moved to Phase 3; FR-XM mapping | Section 23 |
| 12 | [Claude Code Operating Manual](https://claude.ai/artifact/59YEQYYi7ao7ucgfB4spNR) | 0.1 | **0.2** | Steps mapped to phases; model allocation by risk class; slice fields; gates; session rules; CLAUDE.md additions | Section 16; note in 11 |
| 13 | [UX and Design System](https://claude.ai/artifact/9VjAXXdEVRztiSGwm6H9BD) | 0.1 | 0.1 | No change now; gains the map component at Phase 3 entry |  |
| 14 | [Consistency Review and Decision Log](https://claude.ai/artifact/2M3f4KBjTDgo1Ch1UTsJX3) | 0.1 | **0.2** | D-30 to D-40; C-37 to C-46; phase plan; gap status | Section 16 |
| 15 | [Editions and Starter Edition](https://claude.ai/artifact/NViubu68f2szqEbFxrrtu9) | 0.1 | **0.2** | Size profiles; pooled deployment; Starter availability by phase with Sales core | Section 17 |
| 16 | This document |  | **1.0** | New |  |

### 7.3 Order of authority

When two texts disagree: decision log (Doc 14 sections 6 and 16.1) → this document → data standards (Doc 3) → module specification → slice brief → code. Inside one document, its Baseline 1.0 section overrides its earlier sections once the decision it cites is approved; corrections C-37 to C-46 apply now.

### 7.4 Shared registries

| Registry | Authoritative location |
| --- | --- |
| Decisions D-01 to D-40 | Doc 14 sections 6 and 16.1 |
| Cross-module rules FR-XM-01 to FR-XM-32 | Section 6 of this document |
| Canonical events, ownership and contract matrix, naming and prefixes, entitlement keys | Doc 14 sections 4, 5, 7, 8 |
| Shared master data (parties, items, units, tax, organization, calendars) | Kernel: Doc 2 sections 4, 9 to 12; tables in Doc 3 sections 5.2 and 5.3 |
| Module-owned data | Each module specification's data model section, to be brought to column level at its data design gate |
| Gaps G-01 to G-18 | Doc 14 sections 10 and 16.4 |
| HRMIS policy decisions MD-01 to MD-28 | HRMIS v0.3 section 37 |

## 8. Four-phase development strategy

Phases are ordered by real dependency: nothing can post without the ledger, nothing can be approved without workflow, nothing can be sold without entitlements, and stock, purchasing and sales depend on each other more than on anything else. Each phase ends in a release a customer can use. Durations are deliberately not given: with AI-assisted development and one non-programmer owner, the pace depends on review capacity more than on coding speed, and any number now would be invented.

|  | Phase 1 Platform foundation | Phase 2 First usable ERP | Phase 3 Operations suite | Phase 4 Enterprise and planning |
| --- | --- | --- | --- | --- |
| Release | Internal 0.5 | **1.0** | **2.0** | **3.0** |
| Who can use it | Nobody outside the team | Service businesses: accounts, people, payroll | Trading and distribution businesses; mid-size organizations | Groups, manufacturers' supply chains, multi-country |
| Sellable editions | None | Starter (Service), Business (Finance and HR) | Starter (Trading), Business, on-premises | Enterprise |
| Build steps (Doc 12) | 0 to 2, kernel limited to the Minimum Viable Kernel | 3 to 6 | Procurement, Inventory, Assets, Sales | Supply Chain and Steps 12 to 14 |

### Phase 1: Platform foundation

**Scope.** Repository, automated build and checks, environments. Minimum Viable Kernel (F-01): tenancy in both dedicated and pooled modes, organization, identity and roles with data scopes, settings, audit, numbering, parties, basic catalog and tax, localization (English, Urdu, Arabic with right-to-left), linear and conditional approvals, notifications, attachments, jobs, entitlements with licence file and expiry states, API conventions, import and export. Admin console essentials. Design system and application shell. Tenant provisioning and fleet migration tooling.

**Sequence.** Kernel data design gate → repository and pipeline → walking skeleton (one trivial screen through every layer, both tenancy modes) → tenancy and identity → organization and settings → audit and numbering → master data → workflow and notifications → entitlements → admin console → hardening.

**Depends on.** Approval of D-30, D-31, D-33; conditions in section 12.

**Milestones.** M1.1 skeleton deployed; M1.2 two tenants isolated in both modes; M1.3 approval flow end to end; M1.4 licence expiry states demonstrated; M1.5 foundation review.

**Validation.** Isolation test suite passes in both modes (no query can read another tenant's row). Architecture tests enforce module boundaries. Load test: 500 tenants provisioned and migrated; 500 concurrent users on kernel screens. Security architecture and threat model written and reviewed by the human adviser. Backup and restore of one tenant demonstrated.

**Complete when.** All milestones pass, the foundation review by the architecture model finds no critical item open, and you sign off. No business module starts before this.

### Phase 2: First usable ERP (Finance and HR)

**Scope.** Finance core: chart of accounts, journals, posting engine with atomic posting contract, payables, receivables, bank and cash, tax, periods and close, core statements, budgets as warnings. HRMIS core from v0.3: organization and positions, employee records, lifecycle, leave, attendance (basic and biometric import; simple geo check-in), payroll engine, Pakistan pack (payroll, income tax, statutory funds, sales tax invoices), employee self-service web. Offline sync service and Android app release M1 (profile, leave, payslip, simple attendance). Thin AI gateway with two or three assistive skills. Standard reports and fixed dashboards. Starter wizard for the Service profile.

**Sequence.** Finance and HRMIS data design gates → Finance ledger and posting → HR core records (in parallel) → payables, receivables, bank → leave and attendance → payroll engine → Pakistan pack → payroll-to-ledger posting → period close → reports → sync and mobile M1 → Starter wizard → hardening and pilot.

**Depends on.** Phase 1 complete. Pakistan pack specification written and validated by a practitioner (gap G-05). A pilot customer identified.

**Milestones.** M2.1 trial balance from posted journals; M2.2 invoice to payment both sides; M2.3 first payroll run posted to the ledger; M2.4 month-end close; M2.5 mobile M1 on test devices; M2.6 pilot go-live.

**Validation.** Ledger invariants hold after every test run (debits equal credits, sub-ledgers reconcile to control accounts). FR-XM-01 to 08 and 26 to 30 pass. Payroll results match the practitioner's hand-calculated cases. Load test: payroll for 50,000 employees completes within the target set at phase entry; one million ledger lines without report degradation. Independent financial-integrity review by the architecture model.

**Complete when.** A pilot organization runs one full month (transactions, payroll, close) without a manual database correction.

### Phase 3: Operations suite

**Scope.** Procurement, Inventory, Asset Management, Sales core. Supplier and customer portals. HRMIS geo-attendance (all modes), task and field activity management, mobile M2 and M3. Budget control with commitments. Report builder and dashboard builder. Global search, collaboration. KSA pack (VAT, e-invoicing, Arabic documents, statutory HR). On-premises packaging and installer. Starter wizard for the Trading profile. Kernel acceptance of KER-01 to KER-08 delivered.

**Sequence.** Data design gates for the four modules together (they share item, warehouse and costing concepts) → Inventory ledger and costing → Procurement → three-way match with Finance → Sales → Assets → budget commitments → portals → geo-attendance and tasks → mobile M2, M3 → builders → KSA pack → on-premises packaging → hardening.

**Depends on.** Phase 2 release. HRMIS decisions MD-01 to MD-28 settled. KSA practitioner input.

**Milestones.** M3.1 stock valued and reconciled to ledger; M3.2 requisition to payment; M3.3 order to cash; M3.4 asset acquisition to depreciation; M3.5 geo-attendance in the field; M3.6 trading pilot; M3.7 on-premises install by someone other than the team.

**Validation.** FR-XM-09 to 25 and 31 to 32 pass. Stock ledger reconciles to the general ledger after randomized transaction tests including reversals and backdating. Load test: ten million stock movements; attendance events for 50,000 employees for a year. Privacy review of location data. External penetration test before on-premises or pooled-mode sale.

**Complete when.** A trading pilot runs purchase, stock, sales and month-end for one month, and one on-premises installation is completed from the installer alone.

### Phase 4: Enterprise and planning

**Scope.** Supply Chain (demand, replenishment planning, distribution, logistics). Finance: consolidation, intercompany automation, cost accounting, multi-ledger. HR talent: recruitment, performance, learning, workforce planning. Advanced inventory (quality, advanced warehousing) and procurement (tendering, contracts). US readiness (sales tax and payroll through partner integrations). iOS app (M4). Advanced analytics and AI skills. Industry packs. Scale hardening: dedicated cluster topology, archiving, disaster recovery drills.

**Sequence.** Set at Phase 3 exit by customer demand; items are largely independent of one another, which is why they are last.

**Milestones.** M4.1 planning run produces purchase and transfer proposals; M4.2 consolidated statements for a three-entity group; M4.3 iOS release; M4.4 disaster recovery drill; M4.5 enterprise reference customer.

**Validation.** Full-scale test at AS-6 volumes. Recovery objectives demonstrated, not stated. Upgrade test from Release 1.0 data through 3.0.

**Complete when.** An organization of several legal entities runs consolidated close on the product.

### Small organizations during the build

A small service business can go live at Release 1.0 and a small trading business at 2.0. Later phases reach them as entitlements and settings on the same database, never as a migration (rules SC-1 to SC-3). Modules not yet built are simply absent entitlement keys.

### How rework is kept low

- The two riskiest contracts (tenancy and ledger posting) are fixed and tested in Phases 1 and 2, before anything depends on them.
- Modules that share concepts get their data design together (Phase 3).
- Each module is built against published contracts of the others, with stubs until the real module exists (FR-XM-02 is the pattern).
- Kernel services are built when their first real user arrives, not before.

## 9. AI model allocation, handoff and escalation (D-38)

### 9.1 What is verified and what is not

**Verified** on Anthropic's published pricing page on 14 November 2026: list prices for the three models (section 10); that models from Claude 4.6 onward include the one-million-token context window at standard pricing; Anthropic's own one-line positioning (Fable 5.2 "for demanding reasoning and long-horizon agentic work", Opus 5.5 "for long-running agentic coding and knowledge work", Sonnet 5.5 "the best combination of speed and intelligence"); a 50 percent batch discount; cached input read at 2.5 to 5 percent of the input price.

**Not verified, and therefore not claimed:** benchmark scores, defect rates, or how much better one model is than another at this kind of work. The allocation below rests on the price ratio (Fable costs twice Opus, Opus twice Sonnet, per token) and on the vendor's positioning. It must be checked against your own results: section 9.6 says how.

**Your proposed allocation is sound in shape.** The review changes three things: escalation is driven by the *risk class of the task*, not by which module it belongs to; Fable reviews by sampling and at gates rather than continuously; and Sonnet's share is made conditional on reusable patterns existing first.

### 9.2 Risk classes

| Class | Meaning | Examples | Default model | Review |
| --- | --- | --- | --- | --- |
| **A** Architecture and irreversible | Wrong choice is expensive or impossible to undo | Tenancy, posting engine design, data design gates, security model, costing method, payroll calculation design, public contracts | Fable, high reasoning effort | You approve; human adviser on security |
| **B** Integrity-critical implementation | Wrong code produces wrong books, wrong pay or a data leak | Posting, period close, costing, payroll calculation, tax, authorization, tenancy filters, migrations on ledger tables, sync conflict handling | Opus, high effort | Fable reviews every slice of this class |
| **C** Substantive implementation | Real logic, limited blast radius | Module business logic, workflows, integrations, complex screens, reports with calculations, debugging | Opus, default effort | Opus self-review plus automated checks; Fable samples at milestones |
| **D** Routine | Pattern exists; specification is exact | List and form screens, simple validations, standard reports, translations, seed data, test cases from stated rules, documentation formatting | Sonnet | Automated checks; Opus reviews a sample |

### 9.3 Task-level allocation

| Work | Class | Model | Escalate when |
| --- | --- | --- | --- |
| Enterprise architecture, decisions, this baseline | A | Fable | Always to you for approval |
| Module data design, diagrams | A | Fable | Conflict with Doc 3 standards |
| Migrations: ledger, tenancy, identity tables | B | Opus, Fable review | Any destructive or non-reversible step |
| Migrations: other tables from an approved design | C | Opus | Design does not fit the need |
| Authentication, authorization, data scopes | B | Opus, Fable review | Any new permission concept |
| Organization management | C | Opus | Hierarchy rule unclear |
| HRMIS: employee records, lifecycle, leave | C; screens D | Opus; Sonnet | Rule missing from specification |
| HRMIS: attendance engine, geo validation, sync | B | Opus, Fable review | Privacy or conflict rule unclear |
| HRMIS: payroll engine and country rules | A design, B build | Fable then Opus | Any statutory interpretation: to the practitioner, not a model |
| HRMIS: tasks, self-service, workspaces | C; screens D | Opus; Sonnet |  |
| Finance: posting, close, currency, tax | A design, B build | Fable then Opus | Any FR-XM rule in doubt |
| Finance: payables, receivables, bank | C | Opus | Matching or allocation edge case |
| Inventory costing, stock ledger | A design, B build | Fable then Opus | Backdating, negative stock |
| Inventory, procurement, sales, assets: documents and flows | C; screens D | Opus; Sonnet | Cross-module rule involved |
| Supply chain planning logic | A design, C build | Fable then Opus |  |
| APIs and integrations | C; contracts A | Opus; Fable for contract shape | Breaking a published contract |
| Shared frontend components, design system | C | Opus |  |
| Screens built from those components | D | Sonnet | Component missing: to Opus |
| Reports and dashboards | D; C if calculated | Sonnet; Opus | Figures must reconcile to ledger |
| Security implementation | B | Opus, Fable review, human adviser | Always reviewed |
| Automated tests: integrity and cross-module | B | Opus |  |
| Automated tests: routine | D | Sonnet |  |
| Performance testing and tuning | C | Opus | Needs schema change: to Fable |
| Debugging | Starts with the model that wrote it |  | Two failed attempts: one level up |
| Deployment and operations tooling | C | Opus, human adviser for production |  |
| Documentation | D | Sonnet | Specification change: to Fable |

### 9.4 Handoff and escalation rules

1. **One owner per component.** The component register in the repository names the owning class and model. No model redesigns a component it does not own; it raises a change request.
2. **Sonnet to Opus** when: the task needs a decision the slice brief does not contain; it touches a class B file; a test fails twice after its own fixes; or it would add a new pattern.
3. **Opus to Fable** when: an approved decision, data design or contract would have to change; a FR-XM rule is ambiguous or seems wrong; a security, tenancy or financial-integrity question arises; or two reasonable designs exist with different long-term cost.
4. **Fable to Opus** always as a written decision record: what was decided, why, what to change, which tests prove it. Fable does not write the fix itself except as a short illustration.
5. **Any model to you** when: scope, cost, a business rule not in the documents, or a legal or statutory interpretation is in question. Models do not invent business requirements.
6. **Handoff note** at the end of every session: what was done, what is open, which files, which decisions were relied on. The next session starts from the note and the slice brief, not from a re-read of the specifications.
7. **No silent baseline change.** An escalation that changes the baseline updates the decision log and the affected document in the same change (section 11).

### 9.5 Where Fable reviews, and where it does not

Reviews: every data design gate; every class B slice; each phase-exit review; any change request against the baseline; a random sample of class C slices at each milestone (start at one in ten, adjust by findings). Does not review: class D work, routine pull requests, formatting. A fresh Fable session that has not seen the implementation conversation performs the review, so the work is not marking itself.

### 9.6 Checking the allocation against reality

For the first twenty slices of Phase 1, record per slice: model, class, tokens used, whether automated checks passed first time, and review findings. If Sonnet's class D slices pass review as often as Opus's, widen class D. If Opus's class B slices rarely draw Fable findings, reduce Fable review to sampling. If either model fails often, tighten the brief before changing the model. This turns the allocation from an assumption into a measurement within the first weeks.

## 10. Token and cost scenarios

### 10.1 Verified prices

Anthropic list prices in US dollars per million tokens, read from the published pricing page on 14 November 2026. Prices can change; re-check before budgeting.

| Model | Input | Output | Cache write (5 min) | Cache write (1 hour) | Cached input read | Batch input / output |
| --- | --- | --- | --- | --- | --- | --- |
| Claude Fable 5.2 | 8.00 | 40.00 | 10.00 | 16.00 | 0.20 | 4.00 / 20.00 |
| Claude Opus 5.5 | 4.00 | 20.00 | 5.00 | 8.00 | 0.20 | 2.00 / 10.00 |
| Claude Sonnet 5.5 | 2.00 | 10.00 | 2.50 | 4.00 | 0.10 | 1.00 / 5.00 |

Also stated on that page: fast mode costs double (not recommended here); a 1.1 times multiplier applies to US-only inference; recent models produce roughly 30 percent more tokens for the same text than older ones, so older rules of thumb under-count.

### 10.2 How the estimate is built (assumptions, not facts)

Nobody can know the token use of a project of this size in advance. The estimate is built from working sessions, because that is how agentic coding consumes tokens: each session re-sends its context on every step, so input volume is many times output volume, and most of that input is served from cache.

| Assumption | Optimistic | Realistic | Conservative |
| --- | --- | --- | --- |
| Working sessions over all four phases (slices, reviews, fixes) | 800 | 1,400 | 2,600 |
| Input tokens per session, all kinds (million) | 8 | 12 | 18 |
| Output tokens per session (million) | 0.15 | 0.20 | 0.30 |
| Share of input served from cache | 90% | 85% | 75% |
| Of the uncached input: written to cache / fresh | 60% / 40% | 60% / 40% | 60% / 40% |
| Share of tokens by model: Fable / Opus / Sonnet | 10 / 55 / 35 | 10 / 55 / 35 | 10 / 55 / 35 |

The scenarios differ mainly in rework. Optimistic assumes tight briefs and few failed slices. Conservative assumes frequent re-reading of large documents, long debugging sessions and repeated attempts.

### 10.3 Result at list prices

|  | Optimistic | Realistic | Conservative |
| --- | --- | --- | --- |
| Total input tokens | 6.4 billion | 16.8 billion | 46.8 billion |
| of which cached reads | 5.76 billion | 14.28 billion | 35.1 billion |
| of which cache writes | 0.38 billion | 1.51 billion | 7.02 billion |
| of which fresh input | 0.26 billion | 1.01 billion | 4.68 billion |
| Total output tokens | 120 million | 280 million | 780 million |
| Fable 5.2 cost | $1,200 | $3,700 | $14,600 |
| Opus 5.5 cost | $3,600 | $11,000 | $42,000 |
| Sonnet 5.5 cost | $1,100 | $3,500 | $13,400 |
| **Total model cost** | **about $5,900** | **about $18,300** | **about $70,000** |
| Phase 1 (15%) | $900 | $2,700 | $10,500 |
| Phase 2 (30%) | $1,800 | $5,500 | $21,000 |
| Phase 3 (30%) | $1,800 | $5,500 | $21,000 |
| Phase 4 (25%) | $1,500 | $4,600 | $17,500 |
| Same work, all on Fable | $11,800 | $37,200 | $145,900 |
| Same work, all on Opus | $6,500 | $20,000 | $76,400 |
| Same work, all on Sonnet | $3,200 | $10,000 | $38,200 |

Read this as an order of magnitude: **somewhere between several thousand and several tens of thousands of dollars of model usage for the whole product**, with the realistic case near eighteen thousand. The spread between scenarios (twelve times) is far larger than the saving from the model mix (about ten percent against all-Opus). **Discipline in how sessions are run matters much more than which model runs them.** The mix earns its place through quality (Fable on the decisions that are expensive to get wrong), not through price.

Two caveats. First, if you work through a Claude subscription plan instead of pay-per-token API billing, you pay a fixed fee with usage limits; these dollar figures then indicate how much usage you need, not what you pay. Second, the all-Sonnet line is cheaper on paper only if Sonnet needs no more attempts than Opus on class B and C work, which is not established.

### 10.4 Where the tokens go (assumed shares of the realistic case)

| Activity | Share of cost | Main lever |
| --- | --- | --- |
| Code generation | 35% | Reusable patterns and components |
| Repeated context loading and document ingestion | 20% | Short reference files; caching; slice briefs |
| Debugging and rework | 18% | Tests written first; small slices |
| Testing and integration | 12% | Shared test fixtures |
| Code and design reviews | 12% | Review by risk class, not everything |
| Final architectural validation per phase | 3% | Fresh session with a prepared evidence pack |

### 10.5 Rules that keep token use down

1. **A short architecture reference** (target under 3,000 words) is the only architecture text loaded by default. The full documents are opened by section, only when a slice needs them.
2. **One slice brief per session**: the requirements in scope, the contracts touched, the tests to pass. A session never begins with "read the specification".
3. **Stable material first** in every prompt (reference, module guide, brief), so it is cached; changing material last.
4. **Small sessions.** End the session at the end of the slice; start the next from the handoff note. Long sessions re-send long histories.
5. **Patterns before volume.** Opus builds one example of each kind (list screen, document with lines, posting document, report); Sonnet copies the pattern. No class D work starts before its pattern exists.
6. **Batch processing** for work that can wait (bulk test generation, translation, documentation), at half price.
7. **Stop rule.** Two failed attempts at the same problem: stop, escalate one level, do not loop.
8. **Measure.** Record tokens per slice from the first slice (section 9.6) and replace these assumptions with your own numbers at the end of Phase 1.

### 10.6 Total engineering cost

Model usage is unlikely to be your largest cost. Budget separately for: the human technical adviser; the payroll and tax practitioners for each country pack; external penetration testing; hosting for development, test and production; mobile store accounts and devices; your own review time, which is the real limit on speed. These are not estimated here because they depend on local rates and choices you have not yet made.

## 11. Governance and quality control (D-39)

### 11.1 Single source of truth

From the first day of Phase 1 the repository is the source of truth, not these online documents. Each planning document is exported once into the repository as Markdown under `docs/`, and from then on is changed only through the same reviewed change process as code. Order of authority when two sources disagree: decision log → this baseline → data standards → module specification → slice brief → code. A lower source that disagrees with a higher one is a defect in the lower one.

### 11.2 The registers every model works from

| Register | Holds | Changed by |
| --- | --- | --- |
| Decision log (one file per decision) | Context, options, decision, consequences, status | Fable drafts; you approve |
| Requirement register | Every requirement ID, release tag (R, L, X), slice, test IDs, status | Updated in the change that implements it |
| Contract register | Published module contracts and events with versions | Fable for shape; Opus for additions within shape |
| Component register | Reusable components and patterns, owner class | Opus |
| Data dictionary | Generated from migrations; compared with the approved data design | Generated; differences fail the build |
| Risk and exception log | Open risks, overrides granted, deviations from standards | Any model raises; you close |

### 11.3 Change control

- A change that stays inside an approved specification needs no approval beyond normal review.
- A change to a **baseline item** (a decision, a FR-XM rule, a published contract, an approved data design, a kernel service boundary, a security rule) needs a change request: what, why, alternatives, documents and code affected, migration impact. Fable assesses; you approve; the decision log, the documents and the code change together in one change set.
- Emergency fixes may go first and be recorded within one working day, but never for baseline items.

### 11.4 Database migration governance

Migrations are written as reviewed SQL, forward-only, in expand-then-contract steps, never edited after merge. Every migration runs against a copy of realistic data in the pipeline and against both tenancy modes. Migrations on ledger, tenancy and identity tables are class B. The fleet migrator applies to canary tenants first. A release is not shippable unless upgrade from the previous release is tested.

### 11.5 Contract management

Module contracts and events are versioned. Additive change is allowed; removal or meaning change needs a new version and a deprecation period of at least one release. Contract tests run for every consumer in the pipeline. External API follows the same rule with a longer deprecation period.

### 11.6 Quality gates

| Gate | Applies to | Passes when |
| --- | --- | --- |
| Slice done | Every slice | Builds; unit and integration tests pass; architecture tests pass; requirement register updated; handoff note written |
| Integrity suite | Any change touching posting, stock, payroll, tenancy | Ledger and stock invariants hold; FR-XM tests pass; isolation suite passes |
| Regression | Every merge | Full automated suite passes; no test deleted or weakened without a recorded reason |
| Security | Every merge, and per phase | Dependency and secret scans clean; authorization tests pass; per phase, review against the threat model; external penetration test before Release 2.0 |
| Performance | Per milestone | Load targets set at phase entry are met; no query without an index plan on large tables |
| Data design gate | Before each module | Section 3, F-05 |
| Phase exit | End of each phase | Completion conditions in section 8; Fable review with no critical finding; your sign-off |

### 11.7 Keeping documents, schema, code and tests in step

The requirement ID is the thread. A requirement is implemented by a slice, which cites the ID in its brief, its tests and its change description. The pipeline reports requirements tagged R with no test, and tests citing no requirement. The data dictionary is generated from the real schema and compared with the approved design. A specification change without a matching register update fails review.

### 11.8 Release management

Versioned releases with release notes generated from the requirement register. Feature switches for unfinished work. SaaS: staged rollout, canary tenants first. On-premises: signed installer, tested upgrade path from the two previous releases, documented rollback.

### 11.9 What needs a human

No model, including the one writing this, should be the last line of defence for: production security, statutory payroll and tax correctness, legal terms and privacy compliance, and the decision to release. These need the technical adviser, the practitioners, and you.

## 12. Readiness validation and conditions

### 12.1 Check that the documents actually carry the resolutions

After the updates, each changed document was exported again and checked by script against its pre-review text and against this document.

| Check | Result |
| --- | --- |
| Each of the twelve updated documents contains its Baseline 1.0 section and every requirement, rule and correction ID this document says it contains | Passed for all twelve |
| Original text unchanged outside the new sections | Passed. The only original lines altered are the title line of each document (version number) and six marked notes: three in Doc 1 (sections 4.4, 5.1, 14), two in Doc 5 (PST-06, PST-07), one in Doc 12 (section 11) |
| HRMIS requirement IDs preserved | 638 distinct IDs before, 638 after, none lost |
| New requirement IDs do not collide with existing ones | Passed (kernel, Finance, Editions) |
| Every FR-XM reference in any document points to a rule that exists | Passed (references span FR-XM-01 to FR-XM-32) |
| Findings F-01 to F-13 each have text in the documents named in their resolution | Passed. F-09 is a process rule recorded in Doc 12 section 16.3 and 16.4; F-14 needs no text |
| Docs 4, 6, 9 and 13 untouched | Confirmed |

### 12.2 Readiness by area

| Area | State | What is still needed |
| --- | --- | --- |
| Architecture principles, stack, module boundaries | Ready | Your decision on D-30, D-31, D-33 |
| Kernel functional specification | Ready for Phase 1 scope | Kernel data design to column level (first design task after go-ahead) |
| Data standards | Ready | Nothing; corrections applied |
| Cross-module rules | Ready | Nothing; they become tests |
| Finance specification | Ready for design gate | Column-level data design at Phase 2 entry |
| HRMIS specification (v0.3.1) | Ready for design gate | Same; Pakistan pack validated by a practitioner; MD-01 to MD-28 before Phase 3 |
| Procurement, Inventory, Assets, Sales | Ready as requirements | Shared data design at Phase 3 entry |
| Supply Chain | Ready as requirements | Nothing before Phase 4 |
| Security architecture and threat model | **Not written** | Phase 1 completion condition; needs the human adviser |
| Operations, release and disaster recovery design | **Not written** | Phase 1, with the adviser |
| Pakistan country pack detail | **Outline only** | Phase 2 entry condition |
| Pricing, legal terms | Not written | Before first sale, not before development |
| Build method, model allocation, governance | Ready | Your decision on D-34, D-35, D-38, D-39 |

### 12.3 Verdict

**CONDITIONAL GO, for Phase 1 only.** The architecture is coherent, the documents agree with one another, the main integrity and scale risks have a written answer, and the first phase is fully specified at requirement level. The verdict is not GO because three significant decisions await you and because no model should start a multi-tenant financial system without a human security reviewer in place. It is not NO-GO because none of the open items requires rethinking the architecture.

**Conditions before Phase 1 starts**

1. You approve, change or reject D-30 to D-40, and confirm the earlier proposed decisions D-05, D-07, D-09, D-10, D-11 and D-26 to D-29.
2. A part-time senior technical adviser is engaged (assumption AS-3).
3. Hosting provider, region and repository are chosen.
4. You give an explicit go-ahead. The first task after it is the kernel data design gate, which is still design, not code.

**Conditions to complete Phase 1:** security architecture and threat model written and reviewed; operations and release design written; master test plan; isolation suite passing in both tenancy modes.

**Conditions to enter Phase 2:** Finance and HRMIS data designs approved; Pakistan pack specified and validated by a practitioner; a pilot organization identified; release scope tagged.

**Conditions to enter Phase 3:** HRMIS decisions MD-01 to MD-28 settled; Saudi practitioner input; shared data design for the four operations modules; pricing set.

### 12.4 Limits of this review and where my own recommendations need a second opinion

- **Depth.** Close reading covered architecture, kernel data model, decisions, posting, and the cross-module flows. The remaining requirement text was checked by search and script, not read line by line. Individual requirements inside modules may still conflict in ways only a design gate will expose. That is the reason the gates exist.
- **D-30 (pooled tenancy).** It adds a tenant column and security policies to every table and a second mode to test. If you never sell to thousands of very small customers, it is cost without benefit. A mistake in a policy is a data leak between customers. The adviser should review this decision specifically before you approve it.
- **D-31 (atomic posting).** It makes source modules wait for Finance and fail when Finance refuses. That is the intended effect, but it couples availability and puts more work inside one transaction. The balance-delta correction (C-40) is what keeps this fast; it must be load-tested in Phase 2 before other modules depend on it.
- **Statutory content.** Nothing about Pakistani, Saudi or United States tax, payroll or privacy law in any document has been verified. All of it is planning input for practitioners and counsel.
- **Performance.** No target is proven. Every number is a test target.
- **Model comparison.** The allocation rests on published prices and positioning, not on measured quality. Section 9.6 replaces assumption with measurement in the first twenty slices.
- **Cost.** The token scenarios span a factor of twelve. Treat the realistic case as a budget line to revisit after Phase 1, not as a quote.

### 12.5 What I need from you

1. A decision on each of D-30 to D-40 (section 4.1). The three that shape everything else are D-30, D-31 and D-33.
2. Confirmation or correction of assumptions AS-1 to AS-6 (section 4.3), especially first market, SaaS-first and the adviser.
3. Your go-ahead, or the changes you want first.

## 13. Consolidated change history

All changes were made on 14 November 2026. Earlier history is in Doc 14 section 14.

| Doc | Version | Change | Reason | Effect on other documents |
| --- | --- | --- | --- | --- |
| 16 | new, 1.0 | This document | Single entry point, review record, cross-module rules, phase plan, model strategy, governance | Becomes second in the order of authority after the decision log |
| 1 | 0.2 to 0.3 | Section 18; notes in 4.4, 5.1, 14 | F-01, F-02, F-03, F-06, F-07, F-12 | Docs 2, 3, 5, 12, 14, 15 carry the detail |
| 2 | 0.1 to 0.2 | Section 32: service schedule, KER-01 to KER-08 accepted, tenancy mode requirements, posting stub | F-01, F-02, F-03, F-08 | HRMIS v0.3 section 41 records the outcome; Doc 13 gains a map component later |
| 3 | 0.1 to 0.2 | Section 14: R-16 to R-21, C-37 to C-42, geospatial columns, data design gate, invariants 11 to 14 | F-02, F-05, F-07, F-12, F-13 | Every module's data design must follow it; Docs 5, 7, 8, 10, 11 reference it |
| 5 | 0.1 to 0.2 | Section 24: PST-12 to PST-16; ledger in the idempotency key; FR-XM ownership; scope by phase | F-03, F-04, F-12 | Posting modules (6a, 7, 8, 10, 11) call the contract |
| 6a | 0.3 to 0.3.1 | Section 41: build baseline status, kernel request outcomes, geospatial rule, phase mapping, FR-XM ownership | F-07, F-08, F-04 | Closes its own section 40 list |
| 7 | 0.1 to 0.2 | Section 23: FR-XM mapping, phase scope, data notes | F-04, F-06, F-12 | None |
| 8 | 0.1 to 0.2 | Section 33: FR-XM mapping, stock balance pattern, phase scope | F-04, F-12 | None |
| 10 | 0.1 to 0.2 | Section 24: Phase 4, boundaries, data notes | F-04, F-06 | None |
| 11 | 0.1 to 0.2 | Section 23: Sales core defined and moved to Phase 3, FR-XM mapping | F-06, F-04 | Doc 15 Trading Starter now includes Sales core |
| 12 | 0.1 to 0.2 | Section 16: steps to phases, model allocation by risk class, slice fields, gates, session rules, CLAUDE.md additions; note in section 11 | F-09, F-10, F-05 | Slices in Docs 14 and 15 take class and phase from here |
| 14 | 0.1 to 0.2 | Section 16: D-30 to D-40, C-37 to C-46, phase plan, gap status | Keeps the decision log complete | Authoritative for decision status |
| 15 | 0.1 to 0.2 | Section 17: size profiles, pooled deployment, Starter availability by phase | F-02, F-06, F-11 | D-27 absorbed by D-30; D-28 changed by D-32 |
| 4, 6, 9, 13 | unchanged | None | Consistent with the baseline | None |

The pre-review text of all sixteen documents is in the frozen originals archive delivered with this review and in each document's version history.

## 14. Baseline 1.1: HRMIS-first re-plan (15 November 2026)

The reason for this re-plan is budget and time to market. The architecture does not change. What changes is the order modules are built in, how much of the kernel comes first, and which models do the work. Decisions D-41 to D-46 are proposed and need your approval. Sections 1 to 13 stay as the record of Baseline 1.0.

### 14.1 New phase order (D-41)

| Phase | Modules | Release | Who can buy it |
| --- | --- | --- | --- |
| 1 | Minimum kernel + **HRMIS** (HR, attendance, leave, Pakistan payroll), self-service web, Android app M1 | 1.0 "HR and Payroll" | Any organization in Pakistan, 5 to 50,000 staff |
| 2 | **Inventory and Asset Management** | 2.0 | Adds stock and asset registers |
| 3 | **Finance** | 3.0 | Full accounting; first integrated ERP |
| 4 | **Procurement and Supply Chain** | 4.0 | Procure-to-pay and planning |

Sales is not in your list. **Proposed default:** core Sales goes into Phase 4 with Procurement, and Phase 2 Inventory gets simple receipt and issue documents so it can work without Sales and Procurement (14.4). If you want Sales earlier, tell me.

The architecture already allows this order: every module depends only on the kernel (D-10), and Finance is optional for every other module (FR-XM-02). This order puts operational modules before the ledger. That is unusual, and section 14.3 says how it is handled.

### 14.2 Phase 1 scope (D-42)

**Kernel, minimum for HRMIS:** tenancy (database per tenant), organization, identity and roles with data scopes, settings, audit, numbering, localization (English, Urdu, right-to-left ready), workflow (linear and conditional approvals), notifications (in-app, email, push for the app), documents (attachments and templates for letters and payslips), jobs, entitlements and licence file, import and export, a thin offline sync for the app.

**Moved out of Phase 1:** parties, item catalog and kernel tax (Phase 2, needed by Inventory); the posting contract and accounting export (Phase 2, a stub); the AI gateway, report builder, portals, search and collaboration (Phase 3 or later); Arabic and the Hijri calendar (with the Saudi pack).

**HRMIS in Phase 1 (from v0.3.1):** POL, EMP, POS, DOC, ATT-01 to ATT-22 (biometric import, web and mobile check-in with one circular office fence), LVE, ONB basic, SSP, RPT basic, PAY, LON, EXP basic, CPL for Pakistan, Android M1 (profile, leave, payslip, attendance).

**HRMIS later:** full geo-attendance and field tasks (ATT-23 onwards, TFA, WSP, Phase 2), recruitment, performance, learning, relations (Phase 3 or 4), the Saudi pack, US readiness, iOS.

**Release scope tagging (D-34)** is still done before coding: I tag each Phase 1 requirement R, L or X, and you approve the list.

### 14.3 Operating without Finance until Phase 3 (D-43)

- In Phases 1 and 2, the payroll and stock documents that have financial effect raise accounting events through the kernel stub (FR-XM-02). They do not post to a ledger.
- The customer gets a **journal export**: a payroll journal per run, and a stock valuation and movement journal per period. The files are mapped to the customer's chart of accounts and can be imported into QuickBooks, Tally, Excel or similar.
- When Finance goes live in Phase 3, a customer starts it at a cutover date with opening balances. History is not replayed into the ledger.
- The atomic posting rule (D-31) and FR-XM-01 apply from Phase 3. The posting contract is defined in Phase 2, so Inventory is written against it from the start and does not need rework.
- The rules that do not need a ledger still apply from Phase 1. For example, FR-XM-26: attendance locks when payroll is calculated. FR-XM-29: final settlement is blocked by open items.

### 14.4 Inventory in Phase 2 without Procurement and Sales

Inventory ships with simple **goods receipt from supplier** and **goods issue to customer or department** documents, using kernel parties and items. When Procurement and Sales arrive in Phase 4, these documents become what purchase orders and sales orders create. A customer's data then carries forward without conversion. Stock is valued (weighted average or FIFO) in Inventory's own valuation records. The value reaches a ledger only through the export until Finance exists.

> **Decided 15 November 2026:** D-44 was rejected and replaced by **D-47**: a separate database for every customer, with no `tenant_id` on business tables and no shared-database mode (Doc 14 section 16.6). The subsection below is kept only as the record of what was proposed.

### 14.5 Tenancy simplified (D-44; replaces D-30)

- Phase 1 builds **database per tenant only**. Pooled mode is not built and not tested.
- Every tenant table still carries `tenant_id` and has it in its unique keys. This costs almost nothing now, and it keeps pooled mode possible later without rebuilding tables.
- Row-level security policies and the second isolation test mode are dropped from Phase 1. They are added only if you later decide to serve thousands of very small customers.

### 14.6 Models: Opus 5.5 and Sonnet 5.5 only (D-45; replaces D-38)

| Class | Work | Build | Review |
| --- | --- | --- | --- |
| A | Decisions, data designs, contracts, security model, payroll calculation design | Opus 5.5, high effort | You approve; a fresh Opus session checks the design against Docs 3 and 16 |
| B | Integrity-critical code: tenancy, authorization, payroll calculation, Pakistan tax, attendance locking, stock costing, migrations on core tables | Opus 5.5, high effort | A **separate, fresh Opus session** reviews every slice; it has not seen the build conversation |
| C | Module business logic, workflows, integrations, calculated reports | Opus 5.5 (Sonnet once a pattern exists) | Automated checks; Opus samples one in five |
| D | Screens from existing components, simple validations, standard reports, translations, seed data, routine tests | Sonnet 5.5 | Automated checks; Opus samples |

The escalation rules of section 9.4 still apply, with Opus taking Fable's place. Two failed attempts at the same problem means stop and escalate to you, not to a third model. Without Fable, the independent check on high-risk code is a fresh session plus automated tests. That makes the payroll practitioner's test cases and the integrity tests more important, not less.

**Effect on cost.** Fable was about 20 percent of model cost in the Baseline 1.0 estimate (section 10.3: $3,700 of $18,300 in the realistic case). Moving that work to Opus saves about $1,800 on the whole product. A bigger saving comes from building only HRMIS in Phase 1: that phase is now roughly 20 to 25 percent of the total effort. Session discipline (section 10.5) remains the largest lever. All figures are estimates at list prices; re-check prices before budgeting.

### 14.7 Conditions to start Phase 1 (D-46; replaces 12.3)

1. You approve D-41 to D-46, and the earlier D-05, D-07, D-09, D-10, D-11, D-33 to D-37 and D-39 as amended here.
2. A code repository exists (GitHub, private). I can work in it once it is connected to this session.
3. Development and testing start on a local or low-cost cloud environment. A hosting choice for production is needed before the first customer, not before coding.
4. The Pakistan payroll and tax rules are written up (gap G-05) before the payroll slices. The kernel and core HR slices can start without them. A practising payroll professional checks the test cases before any real payroll is run.
5. A human security review happens before the first customer goes live. Under budget limits, a one-off external review can replace the part-time adviser, but it cannot be skipped.
6. Your explicit go-ahead.

The first work after go-ahead is still design: release-scope tagging, then the kernel and HRMIS data design (class A, Opus). Code starts after you approve those two.

### 14.8 Phase 1 build order

1. Repository, pipeline, standards, root CLAUDE.md, architecture tests.
2. Walking skeleton: one screen through every layer; two tenants isolated.
3. Identity, roles and data scopes; organization; settings; audit; numbering.
4. Workflow and notifications; documents and templates; jobs; entitlements.
5. HRMIS: employee records, organization and positions, documents and letters.
6. Attendance (biometric import, web and mobile check-in, exceptions) and leave.
7. Payroll engine, loans and advances, Pakistan statutory pack, payslips, payroll journal export.
8. Self-service web, Android M1, offline sync for attendance and leave.
9. Reports, admin console essentials, hardening, load test (payroll for 50,000 employees), backup and restore.
10. Pilot.

**Phase 1 complete when** a pilot organization runs two payroll cycles that match its existing process, and a month of attendance and leave, without a manual database correction.
