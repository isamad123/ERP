# Claude Code Operating Manual (v0.2)

Document 12 of the planning set | Prepared for Ibrahim | 10 November 2026 | How to build the ERP with Claude Code when you do not read code: working method, repository rules, prompts, reviews and verification

## 1. Purpose and principles

This manual turns the planning documents into a way of working. It gives you: the repository knowledge system that Claude Code reads every session, a ready-to-paste CLAUDE.md, a repeatable loop for building one slice of the product at a time, prompt templates and the first prompts, review prompts, a verification method that does not require reading code, and rules for choosing models.

Principles:

1. **Specifications are the source of truth, not the chat.** Claude Code forgets between sessions. Everything it must know lives in files in the repository (specs, ADRs, CLAUDE.md, checklists).
2. **Small slices.** One vertical slice (a screen, an API, its tables and tests) per session. Large prompts produce large, unreviewable changes.
3. **Tests are the contract.** A slice is done when automated checks pass, not when Claude says so.
4. **Separate the author from the reviewer.** A fresh session, or a different model, reviews each finished slice against the spec.
5. **You verify outcomes.** You check demos, test results, database invariants and review reports. You do not need to read source code, but you do need to refuse work that has no evidence.
6. **Decisions are written down.** Any deviation from the specs becomes an ADR (Architecture Decision Record) before the code changes.
7. **Humans for the highest-risk judgment.** A senior architect reviews each milestone, and an independent security test precedes the first customer.

Tool behavior and names change between releases of Claude Code. Before you start, check the current Claude Code documentation at docs.claude.com for installation, memory files (CLAUDE.md), plan mode, custom commands, subagents, hooks and permissions, and adjust section 3 where names differ.

## 2. Roles

| Role | Who | Responsibility |
| --- | --- | --- |
| Product owner | You | Decide scope and priorities, approve plans, accept slices by evidence, keep the specs current |
| Builder | Claude Code (Opus 5.5 for most work, Fable 5.2 for high-risk areas) | Plan, implement, test, document |
| Reviewer | A fresh Claude Code session (different model where useful) | Audit each slice against the spec and checklists; report defects without fixing silently |
| Technical adviser | A senior .NET and PostgreSQL architect (part-time, at milestones) | Independent review of architecture, security, data model and CI |
| Domain reviewers | Accountants and HR and tax practitioners per country | Validate Finance, payroll and statutory behavior |
| Security tester | External penetration tester | Test before the first production customer and each major release |

## 3. Environment and repository setup

### 3.1 What you need

| Item | Purpose |
| --- | --- |
| A private Git repository on a hosted service you own (for example GitHub or GitLab) | Source of truth, history, backups, CI |
| A development machine with Git, the .NET SDK, Node.js with pnpm, Docker, and an editor such as VS Code | Build and run |
| Claude Code installed and signed in | The builder |
| PostgreSQL and Valkey running in Docker (provided by the repo's compose file) | Local data stores |
| CI service (for example GitHub Actions) | Runs the gates on every change |
| A password manager and a secrets vault | Keys and credentials never go into the repository |

Versions of .NET, Node, PostgreSQL and libraries are chosen in Step 0 and recorded in an ADR; verify the current supported versions then.

### 3.2 Repository knowledge system

```
erp-platform/
|-- CLAUDE.md                      (root rules, section 4)
|-- docs/
|   |-- specs/                     documents 1 to 14 as Markdown (export from the planning docs)
|   |-- adr/                       one file per decision: 0001-modular-monolith.md ...
|   |-- checklists/                review-checklist.md, db-checklist.md, security-checklist.md, ui-checklist.md
|   |-- slices/                    one file per slice: plan, decisions, evidence, report
|   |-- runbooks/                  how to run, migrate, back up, restore, release
|-- .claude/
|   |-- commands/                  reusable prompts (section 5.4)
|   |-- agents/                    reviewer agents (section 8)
|   |-- settings.json              permissions and hooks
|-- src/ clients/ tools/ deploy/ build/ tests/   (as in Architecture section 13)
```

Keep specs as plain Markdown files in docs/specs so Claude can read exactly the sections a slice needs. Each module folder gets a short CLAUDE.md that states the module's boundaries, owned tables and events, and links to its spec sections.

### 3.3 Git discipline

- Work on short-lived branches named after the slice (for example feat/kernel-numbering). Never commit directly to main.
- Each slice ends in one pull request with a description, the gate results and the slice report attached. You merge only when the gates are green and the review report is clean.
- Commit messages follow a simple convention (type, scope, summary) so the history is searchable.
- Tag every successful milestone and keep a database backup of any long-lived test tenant.

## 4. The root CLAUDE.md (ready to paste)

Place this at the repository root. Adjust the commands after Step 0.

```markdown
# ERP Platform: rules for Claude Code

## What this repository is
A modular-monolith ERP product (.NET, PostgreSQL, React). It is sold as SaaS and on-premise,
with or without source, in modules. Specifications are in docs/specs and are the source of truth.
The product owner is not a programmer: explain outcomes in plain language and always provide evidence
(test output, queries, screenshots) instead of assurances.

## Before you write any code
1. Read this file, the slice file in docs/slices, the cited spec sections, and relevant ADRs.
2. Enter plan mode. Present a plan listing files, tables, endpoints, tests and open questions. Wait for approval.
3. If the spec is ambiguous or conflicts with another document, STOP and ask. Do not guess. Record the answer in an ADR or the spec.

## Non-negotiable engineering rules
- Architecture: modular monolith. Layers per module: Contracts, Domain, Application, Infrastructure, Api.
  Modules never reference each other except through Contracts and events. No cross-module foreign keys or joins
  (only the reporting layer reads published views). Business modules depend on the kernel only.
- Tenancy: one database per tenant. Resolve the tenant server-side only. Never accept a tenant id from input.
  Every endpoint must have a tenant-isolation test.
- Database: follow docs/specs (document 3). UUIDv7 ids, numeric(19,4) money, no floating point, UTC timestamps,
  standard audit columns, row_version, constraints in the database, every foreign key indexed.
  Posted ledger rows, stock movements, audit and AI logs are append-only.
- Migrations: SQL-first, per module, expand-and-contract. Never edit a migration that has been applied or merged.
- Money and statutory content: no tax rates, thresholds or statutory rules in code. They are pack data.
- Time: use the injected clock. Never call DateTime.Now or UtcNow in business code.
- Configurability: behavior that varies by customer is data (settings, rules, metadata, packs), not code branches.
- Security: permission check on every endpoint, field-level security on sensitive fields, parameterized SQL only,
  secrets never in the repo, encrypt sensitive fields, audit every change.
- Localization: no hard-coded user-facing strings. English, Urdu and Arabic, right-to-left safe layouts.
- Dependencies: only permissive open-source licenses. Run the license gate before adding any package; record in an ADR.
- AI features go only through the AI Gateway. AI proposes, humans confirm. AI never posts, approves or decides pay or hiring.

## Testing rules
- Every requirement you implement has automated tests that reference its FR id in the test name or attribute.
- Never delete, skip, weaken or comment out a test to make a build pass. If a test is wrong, explain why and get approval.
- Run the full gate before declaring a slice done: build, unit, architecture, integration (real PostgreSQL), tenant isolation,
  accounting and data invariants, contract tests, lint, license and security scans.
- Do not mock the database in integration tests.

## Scope discipline
- Implement only the slice. Do not add features, refactor unrelated code, or change specs silently.
- Leave TODOs only with an issue id and mention them in the report. No stub code that pretends to work.
- No sample or fake data in production code paths. Seed data is idempotent and keyed by stable codes.

## Definition of done
Code, migrations, tests, docs and the slice report are complete; all gates are green; the accounting and data invariants hold;
the spec acceptance criteria are demonstrated; a reviewer checklist is attached; open questions are listed.

## Slice report (always end with this)
1. What was built (plain language). 2. Requirements covered (FR ids). 3. Evidence: commands run and results.
4. Tables, endpoints and events added or changed. 5. What is not done or deferred. 6. Risks and open questions.
7. How the product owner can see it working (steps to try).

## Commands
(Filled in at Step 0: build, test, run, migrate, lint, gates.)

## Where things are
Specs: docs/specs. Decisions: docs/adr. Checklists: docs/checklists. Slice notes: docs/slices. Runbooks: docs/runbooks.
```

### 4.1 Per-module CLAUDE.md template

```markdown
# <Module> module rules
Owns schema: <schema>. Owns tables: <list>. Publishes events: <list>. Consumes events: <list>.
Hard depends on: kernel only. Optional integrations: <list> (must degrade gracefully when absent).
Spec: docs/specs/<document>, sections <list>. Extra rules: <module-specific invariants>.
```

## 5. The slice loop

Every piece of work follows the same seven steps. Do not skip steps to go faster; the loop is what makes unreviewed code trustworthy.

| Step | Who | What happens | Evidence |
| --- | --- | --- | --- |
| 1. Define | You | Pick the next slice from section 6; create its file in docs/slices from the template (section 5.3) | Slice file committed |
| 2. Plan | Claude, in plan mode | Reads CLAUDE.md, the slice file, the cited spec sections and ADRs; proposes a plan (files, tables, endpoints, tests, open questions) | Plan text; you approve or correct it |
| 3. Build | Claude | Implements in small commits, tests first where possible | Commits on the slice branch |
| 4. Gate | Claude and CI | Runs build, unit, architecture, integration, tenant-isolation, invariants, contract, lint, license and security checks | Gate output in the pull request |
| 5. Review | Fresh session (reviewer) | Audits the slice against the spec and checklists; reports defects without fixing silently; builder fixes; gates re-run | Review report in docs/slices |
| 6. Accept | You | Follow the slice report's 'how to see it working' steps; check each acceptance criterion; run the invariant queries | Your acceptance note |
| 7. Merge and record | You and Claude | Merge, tag milestones, update the traceability matrix, specs and ADRs | Updated docs |

### 5.1 What to check in a plan (step 2)

- The plan names the requirement IDs it covers and nothing else; there is no added scope.
- Tables follow the data standards (document 3): ids, money types, audit columns, constraints, indexes, partitioning where growth is unbounded.
- No module reaches into another module's tables; events and contracts are named.
- Every requirement has a named test, and there is a tenant-isolation test for each new endpoint.
- Configuration is data (settings, rules, metadata), not code branches; no statutory numbers in code.
- Open questions are listed, not guessed.
- If the plan is long, ask Claude to split the slice. A slice should be reviewable in one sitting.

### 5.2 Session hygiene

- One slice per session; start a new session for the next one. Long sessions drift and forget.
- Point Claude at files rather than pasting long text into chat.
- If Claude starts expanding scope or inventing, stop, restart from the plan, and add the lesson to CLAUDE.md.
- Ask Claude to save learned conventions into the module CLAUDE.md or an ADR before ending the session.
- Keep the repository, not the chat, as the memory.

### 5.3 Slice file template

```markdown
# Slice <ID>: <title>
Step: <0-6>  Model: <Opus 5.5 | Fable 5.2 | Sonnet>  Branch: <name>
Goal (one paragraph, plain language):
Spec references: <document and section list>
Requirements (FR ids): <list>
In scope: <list>    Out of scope: <list>
Dependencies (earlier slices): <list>
Acceptance criteria (copied from the spec): <list>
How the product owner will see it working: <steps>
Plan (filled by Claude, approved by owner): <...>
Evidence: <gate results, review report link, acceptance note>
Open questions: <list>
```

### 5.4 Reusable commands

Store these as custom commands in .claude/commands so every session uses the same wording. Check the current command syntax in the documentation.

| Command | Purpose | Core instruction |
| --- | --- | --- |
| /plan-slice \<id> | Start a slice | Read CLAUDE.md, docs/slices/\<id>.md, the cited specs and ADRs; enter plan mode; produce the plan and list open questions; do not write code |
| /build-slice \<id> | Implement the approved plan | Implement only the approved plan in small commits; write tests alongside; run the gates; produce the slice report |
| /gate | Run all checks | Run build, unit, architecture, integration, isolation, invariant, contract, lint, license and security scans; summarize pass and fail with counts and any skipped tests |
| /review-slice \<id> | Independent audit | Act as reviewer using docs/checklists; compare code and tests with the spec; list defects by severity with file references; do not fix |
| /isolation-check | Tenant safety | List all endpoints and jobs; confirm each has a cross-tenant test; run them; report gaps |
| /migration-check | Data safety | Review new migrations for expand-and-contract, locks, indexes, partitioning and reversibility; run them on two test tenants and on a populated previous version |
| /spec-trace | Traceability | Produce a matrix of requirement IDs to tests and code locations for the slice; list requirements without tests |

## 6. Build catalog

Models follow section 11. Specification references use D1 (Architecture), D2 (Kernel), D3 (Data), D4 (Admin console), D5 (Finance), D6 (HRMIS). The catalog is the backlog for slices; refine it as the specs for other modules arrive.

### Step 0: Foundation setup

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S0.1 | Repository, solution skeleton, build properties, formatting, CI pipeline | D1 13, 12 | Opus |
| S0.2 | Architecture tests, license gate, SBOM, secret scanning | D1 12 | Opus |
| S0.3 | ADR set from decisions D-01 to D-25 and stack choices | D1 0, 11 | Opus |
| S0.4 | Docker compose dev environment: PostgreSQL, Valkey, PgBouncer, seed scripts | D1 3, 11 | Opus |
| S0.5 | Import specs, CLAUDE.md files, checklists and commands | This manual | Sonnet or Opus |

### Step 1: Walking skeleton

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S1.1 | Building blocks: result types, domain base classes, clock, UUIDv7, error handling, logging | D1 4, D3 3 | Opus |
| S1.2 | Control-plane database, tenant registry, migration framework | D3 4, 9 | Fable |
| S1.3 | Tenant resolution, connection provider, pooling and limits | D1 5.1, D2 TEN | Fable |
| S1.4 | Tenant provisioning from template, fleet Migrator with canaries | D2 TEN-02, 11 | Fable |
| S1.5 | Authentication: OIDC login, tokens, sessions (minimal) | D2 IAM | Fable |
| S1.6 | Module manifest and loader, permission engine, entitlement stub | D1 4.2, D2 IAM, ENT | Fable |
| S1.7 | Sample module with one entity end to end: API, tables, outbox, audit, tests | D1 4, D3 7 | Opus |
| S1.8 | Web shell, login, metadata-driven list and form prototype | D1 4.8 | Opus |
| S1.9 | Tenant-isolation test suite | D2 25 | Fable |
| S1.10 | Single-server on-premise packaging and clean-machine install test | D1 3.3 | Opus |

### Step 2: Thin kernel

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S2.1 | Organization: companies, branches, units, calendars, periods | D2 ORG | Opus |
| S2.2 | Identity and access full: roles, scopes, field security, MFA, API keys, maker-checker | D2 IAM | Fable (engine), Opus (screens) |
| S2.3 | Settings and feature flags | D2 SET | Opus |
| S2.4 | Audit: change log, hash chain, partitions, access log | D2 AUD | Fable |
| S2.5 | Numbering with gap-free allocation | D2 NUM | Fable |
| S2.6 | Localization: i18n, right-to-left, Hijri, currencies, rates | D2 LOC | Opus |
| S2.7 | Parties, catalog, tax engine | D2 PTY, CAT, TAX | Opus |
| S2.8 | Metadata: custom fields, layouts, labels, status models | D2 MET | Opus |
| S2.9 | Workflow: sequential approvals, inbox, delegation | D2 WFL | Opus |
| S2.10 | Rules and expression engine with sandbox | D2 RUL | Fable |
| S2.11 | Notifications | D2 NTF | Opus |
| S2.12 | Documents and templates | D2 DOC | Opus |
| S2.13 | Jobs and scheduler | D2 JOB | Opus |
| S2.14 | Integration: API conventions, webhooks, import and export | D2 INT | Opus |
| S2.15 | Entitlements and licensing: issuer tool, verifier, state machine, metering | D2 ENT, D1 7 | Fable |
| S2.16 | Analytics thin: semantic layer, basic report builder | D1 8 | Opus |
| S2.17 | AI Gateway thin: gateway, guardrails, metering, configuration assistant skeleton | D2 AIG | Fable (guardrails), Opus |
| S2.18 | Encryption service and key management | D1 5.5, D3 | Fable |
| S2.19 | Admin console screens: users, roles, settings, organization, audit viewer | D4 | Opus |

### Step 3: Finance

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S3.1 | Chart of accounts, ledgers, dimensions | D5 COA | Opus |
| S3.2 | Journal engine, balances, immutability, ledger invariants | D5 JRN, D3 7.4 | Fable |
| S3.3 | Periods and close basics | D5 CLS | Opus |
| S3.4 | Posting engine, rules, simulator, exception queue | D5 PST | Fable |
| S3.5 | Accounts payable | D5 AP | Opus |
| S3.6 | Accounts receivable | D5 AR | Opus |
| S3.7 | Banking and reconciliation | D5 BNK | Opus |
| S3.8 | Cheques and post-dated cheques | D5 BNK-20 to 24 | Opus |
| S3.9 | Foreign currency and revaluation | D5 JRN-30 to 33 | Fable |
| S3.10 | Tax postings, returns, withholding (Pakistan pack baseline) | D5 TAX | Opus with practitioner review |
| S3.11 | Budgeting basic | D5 BUD | Opus |
| S3.12 | Financial reports and report writer | D5 RPT | Opus |
| S3.13 | Accounting Export connector | D2 INT-09 | Opus |
| S3.14 | Opening balance import and tie-out | D5 JRN-11 | Opus |
| S3.15 | Finance whole-module audit against the spec | D5 | Fable |

### Step 4: HRMIS core

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S4.1 | Person and employment model with effective dating and impact analysis | D6 EMP, GHR | Fable |
| S4.2 | Positions, grades, jobs, restructure tool | D6 POS | Opus |
| S4.3 | Policy framework and resolution view | D6 POL | Opus |
| S4.4 | Documents and letters | D6 DOC | Opus |
| S4.5 | Attendance processing engine and exceptions | D6 ATT | Opus, Fable review |
| S4.6 | Devices, biometric driver interface, mobile capture | D6 ATT-01 to 05 | Opus |
| S4.7 | Leave engine and ledger | D6 LVE | Opus |
| S4.8 | Onboarding and offboarding basic | D6 ONB | Opus |
| S4.9 | Self-service portals and mobile | D6 SSP | Opus |
| S4.10 | HR dashboards and reports | D6 RPT | Opus |

### Step 5: Payroll (Pakistan)

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S5.1 | Payroll engine core: components, structures, run snapshot, trace, immutability | D6 PAY | Fable |
| S5.2 | Pakistan tax and statutory pack | D6 PAY-17 to 22, CPL | Fable with practitioner review |
| S5.3 | Run lifecycle, validation, variance, approvals | D6 PAY-10 to 16 | Opus |
| S5.4 | Payment files, payslips, bank status import | D6 PAY-23 to 28 | Opus |
| S5.5 | Loans and expense claims | D6 LON, EXP | Opus |
| S5.6 | Final settlement | D6 PAY-26, 27 | Fable |
| S5.7 | Payroll postings to Finance and export | D6 PAY-30, D5 PST | Opus |
| S5.8 | Parallel-run comparison tool | D6 PAY-35 | Opus |
| S5.9 | Compensation and compliance reports | D6 CMP, CPL | Opus |

### Step 6: Hardening and release

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S6.1 | Performance tests and tuning at 1.5 times load | D1 3.7, D5 21, D6 GHR-01 | Fable |
| S6.2 | Security hardening and internal security review | D1 10 | Fable |
| S6.3 | Release pipeline: obfuscation, signing, per-edition packages, offline update bundles | D1 7 | Opus (Fable for license protections) |
| S6.4 | Backup and restore drills, upgrade rehearsals, runbooks | D1 5.9 | Opus, Fable for upgrade rehearsal |
| S6.5 | Pilot support and defect fixing | All | Opus |

### 6.1 Additions from the consistency review (document 14, decisions D-20 and C-30)

Specification references D7 to D14 follow the same pattern as D1 to D6: D7 Procurement, D8 Inventory and Assets, D9 Analytics and AI, D10 Supply Chain, D11 Sales, D13 UX, D14 consistency review. The catalog below completes the build plan for waves B and C; prompts for these steps are written just before each slice from the slice file and the template of section 7.1.

**Step 2 additions (kernel capabilities found missing)**

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S2.20 | Global search and command palette | D2 SRC, D13 IA-03 | Opus |
| S2.21 | Collaboration: comments, mentions, tasks, activity feed | D2 COL | Opus |
| S2.22 | Offline sync framework: device registry, sync protocol, outbox, conflicts, remote wipe | D2 SYN, D13 MOB | Fable |
| S2.23 | Print service for documents, labels and cheques | D2 DOC-09 to DOC-11 | Opus |

**Step 7: Procurement (wave B)**

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S7.0 | External portal framework: separate authentication realm, invitations, isolation, branding | D2 PRT | Fable |
| S7.1 | Supplier management and onboarding | D7 SUP | Opus |
| S7.2 | Requisitions, catalogs and price lists | D7 REQ, CAT | Opus |
| S7.3 | RFQ and tenders including bid secrecy | D7 SRC | Opus with Fable review |
| S7.4 | Purchase orders, revisions, commitment events | D7 POR | Opus |
| S7.5 | Receiving and service acceptance with the ReceiveGoods contract | D7 RCV, D14 D-15 | Opus |
| S7.6 | Invoice matching and hand-off to Finance | D7 MAT, D14 D-12 | Fable |
| S7.7 | Contracts | D7 CTR | Opus |
| S7.8 | Supplier portal | D7 PRT | Opus with Fable security review |
| S7.9 | Controls and analytics content | D7 CTL, ANL | Opus |

**Step 8: Inventory (wave B)**

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S8.1 | Stock attributes, warehouses, locations | D8 ITM, WHS | Opus |
| S8.2 | Stock ledger and movements | D8 MOV | Fable |
| S8.3 | Valuation and landed cost | D8 VAL | Fable |
| S8.4 | Batch, serial, expiry and recall | D8 TRK | Opus |
| S8.5 | Reservations and availability | D8 RSV | Fable |
| S8.6 | Counts | D8 CNT | Opus |
| S8.7 | Replenishment and classification, UpdatePlanningParameters contract | D8 RPL, D14 D-13 | Opus |
| S8.8 | Quality inspection | D8 QLT | Opus |
| S8.9 | Warehouse operations and mobile | D8 OPS | Opus with Fable review of offline behavior |
| S8.10 | Inventory reports | D8 IRP | Opus |

**Step 9: Asset Management (wave B)**

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S9.1 | Register and classes | D8 AMS | Opus |
| S9.2 | Acquisition and construction in progress | D8 ACQ | Opus |
| S9.3 | Depreciation engine | D8 DEP | Fable |
| S9.4 | Transfers, adjustments, revaluation, impairment | D8 TRF | Opus |
| S9.5 | Disposal | D8 DSP | Opus |
| S9.6 | Physical verification on mobile | D8 VER | Opus |
| S9.7 | Maintenance | D8 MNT | Opus |
| S9.8 | Custody, insurance, reports and Finance reconciliation | D8 CST, INS, ARP | Opus |

**Step 10: Sales and Distribution (wave C)**

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S10.1 | Customer sales data, hierarchies, territories | D11 CUS | Opus |
| S10.2 | Pricing determination engine, discounts, promotions | D11 PRC | Fable |
| S10.3 | Quotations and pipeline | D11 QUO | Opus |
| S10.4 | Orders with availability and credit checks | D11 SOR | Opus |
| S10.5 | Contracts and recurring billing | D11 CTR | Opus |
| S10.6 | Delivery with the IssueGoods contract | D11 DLV, D14 D-15 | Opus |
| S10.7 | Billing documents, legal numbering, e-invoicing interface | D11 BIL, D14 D-16 | Fable |
| S10.8 | Returns and credits | D11 RET, D14 D-17 | Opus |
| S10.9 | Commissions | D11 COM | Opus |
| S10.10 | Channels: customer portal, mobile field sales, van sales | D11 CHN | Opus with Fable review of offline behavior |
| S10.11 | Sales reports | D11 SAN | Opus |

**Step 11: Supply Chain (wave C)**

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S11.1 | Planning master data and data quality | D10 PMD | Opus |
| S11.2 | Demand planning and forecasting | D10 DEM | Opus, Fable for the forecasting engine |
| S11.3 | Supply planning engine | D10 SUP | Fable |
| S11.4 | Sales and operations planning | D10 SOP | Opus |
| S11.5 | Inventory optimization and rebalancing | D10 OPT | Fable |
| S11.6 | Order promising and fulfillment | D10 OFM | Opus |
| S11.7 | Transportation, tracking, proof of delivery, freight settlement | D10 TMS | Opus |
| S11.8 | Advanced warehouse management | D10 WMS | Opus |
| S11.9 | Supplier collaboration and returns | D10 SCO, RLG | Opus |
| S11.10 | Control tower and analytics | D10 CTW, SCA | Opus |

**Steps 12 to 14: intelligence, talent and United States readiness**

| Slice group | Task | Spec | Model |
| --- | --- | --- | --- |
| Step 12 | Analytics API and external BI; explorer and pivot; document intelligence; migration assistant; anomaly and forecasting services; Superset adapter and analytical extract; evaluation and red-team expansion | D9 | Opus, Fable for prediction services and safety suites |
| Step 13 | HRMIS recruitment, performance, learning, employee relations; Saudi pack; asset custody integration | D6, D8 | Opus, Fable for the Saudi payroll and end-of-service logic |
| Step 14 | United States readiness: partner payroll integration, benefits, compliance | D6 | Opus with practitioner review |

**Step 15: Editions and Starter edition (document 15; Service Starter after Step 6, Trading Starter after Step 9)**

| Slice | Task | Spec | Model |
| --- | --- | --- | --- |
| S15.1 | Business-profile questionnaire and configuration mapper | D15 SIM-01, SIM-02 | Opus |
| S15.2 | Simple mode presentation: flags, menus, labels, role presets, add-this-feature, quick screens | D15 SIM | Opus |
| S15.3 | Self-service signup, automated provisioning and trial lifecycle | D15 ONB | Fable |
| S15.4 | Trading starter pack with practitioner review | D15 TRD | Opus |
| S15.5 | Edition templates, limit enforcement, upgrade and downgrade | D15 LIM | Fable |
| S15.6 | Tenant hibernation, quotas and cost metering | D15 OPS | Fable |
| S15.7 | Self-serve billing integration and entitlement API | D15 BIL | Opus |
| S15.8 | Accountant role and partner console | D15 SUP-04 to SUP-06 | Opus with Fable security review |
| S15.9 | Owner mobile features and sharing | D15 MOB | Opus |
| S15.10 | Small-business import assistant | D15 ONB-04, ONB-05 | Opus |
| S15.11 | Support automation and in-app help | D15 SUP-01, SUP-02 | Opus |

## 7. First prompts

Prompts for later steps depend on what the code looks like by then, so they are written just before each slice from the slice file and the template below. The first prompts can be used as they are.

### 7.1 Prompt template

```
You are implementing slice <ID>: <title> in the ERP repository.
Read first: CLAUDE.md, docs/slices/<ID>.md, docs/specs/<files and sections>, docs/adr/*, module CLAUDE.md files.
Goal: <one paragraph in plain language>.
In scope: <list>. Out of scope: <list>.
Requirements to satisfy: <FR ids>.
Constraints: follow CLAUDE.md; follow the data standards in docs/specs/03-data.md; no new package without the license gate.
Process: 1) Enter plan mode and present the plan with files, tables, endpoints, tests and open questions; wait for my approval.
2) After approval implement in small commits with tests alongside. 3) Run all gates. 4) Produce the slice report.
Acceptance: <criteria copied from the spec>.
If anything in the spec is unclear or conflicts, stop and ask before coding.
```

### 7.2 Prompt S0.1: repository skeleton and CI

```
You are implementing slice S0.1: repository, solution skeleton and CI.
Read first: CLAUDE.md, docs/specs/01-architecture.md sections 4, 11, 12 and 13.
Goal: create an empty but buildable solution that matches the repository structure in section 13, with the shared build
properties, formatting rules, nullable reference types and warnings as errors, and a CI pipeline that builds and runs
the (initially empty) test projects on every pull request.
In scope: solution and project files for Host, BuildingBlocks and the test projects; Directory.Build.props and
Directory.Packages.props with central package management; .editorconfig; global.json; a README that explains how to build and test;
the CI workflow; a branch protection recommendation for main.
Out of scope: any business code, database code or UI.
Constraints: choose current supported LTS versions of .NET and Node after checking the official support pages; record the choice
and the reasons in an ADR (docs/adr/0002-runtime-versions.md). Do not add packages beyond those needed for building and testing.
Process: plan mode first; wait for my approval. Then implement, run the build locally, and show me the CI run result.
Acceptance: a fresh clone builds and passes (empty) tests with one command; CI is green; the ADR exists; the README steps work
on a clean machine as written.
End with the slice report.
```

### 7.3 Prompt S0.5: import specs and knowledge files

```
You are implementing slice S0.5: knowledge files.
Goal: make the repository self-explanatory to future sessions.
Tasks: place the planning documents under docs/specs as Markdown files named 01-architecture.md, 02-kernel.md, 03-data.md,
04-admin.md, 05-finance.md, 06-hrms.md, 07-procurement.md, 08-inventory-assets.md, 09-analytics-ai.md, 10-supply-chain.md, 11-sales.md, 12-operating-manual.md, 13-ux.md, 14-consistency-review.md (I will supply the exported text); create docs/checklists with the review, database,
security and UI checklists derived from the specs; create the root CLAUDE.md (I will paste the text) and per-module CLAUDE.md
stubs from the template; create the .claude/commands files for the commands in the operating manual section 5.4; create docs/adr with
the first ADRs for decisions D-01 to D-25 (document 14) and a docs/events registry from the canonical event catalog of document 14, section 4.
Constraints: do not change the wording of the specs; if you find contradictions or gaps between documents, list them in
docs/specs/OPEN-QUESTIONS.md instead of resolving them yourself.
Acceptance: every spec file opens and renders; OPEN-QUESTIONS.md lists any issue found; the commands exist and each prints its own purpose when run.
End with the slice report.
```

### 7.4 Prompt S1.2 and S1.3: control plane and tenant resolution (use Fable 5.2)

```
You are implementing slices S1.2 and S1.3: the control-plane database, tenant registry and tenant resolution.
Read first: CLAUDE.md, docs/specs/01-architecture.md sections 3, 5.1 and 5.2, docs/specs/02-kernel.md section 3,
docs/specs/03-data.md sections 3, 4 and 9.
Goal: a request is routed to the correct tenant database by server-side resolution only, and tenants live in separate databases
listed in a control-plane registry.
In scope: the cp schema tables from document 3 section 4 that these two slices need; the migration framework (per-module folders,
versioned SQL scripts, history table, ability to run against many databases); ITenantContext and ITenantConnectionProvider;
resolution from host name and from token claim; per-tenant connection pooling limits and statement timeouts; caching of tenant records
with invalidation; a fail-closed rule: no tenant means no database access.
Out of scope: provisioning flows (S1.4), authentication details (S1.5), any business tables.
Constraints: no static connection strings; the tenant id never comes from request bodies or query strings; secrets are referenced, never stored in plain text;
there must be an integration test using two real PostgreSQL databases proving that data written for tenant A is invisible to tenant B through every access path you create.
Process: plan mode first, listing the tables, classes, tests and any open questions; wait for approval. Then implement and run the gates.
Acceptance: two tenants can be created manually in the registry; requests with different host names reach different databases; removing
the tenant context causes a clear failure; all isolation tests pass; migration framework applies a script to both databases and records history.
End with the slice report, including the exact commands I can run to see it working.
```

## 8. Review prompts and reviewer agents

Run reviews in a fresh session so the reviewer has not seen the builder's reasoning. For high-risk slices use a different model for the review than for the build. Store these as reviewer agents or commands (check current syntax). Reviewers report; they do not fix.

### 8.1 Specification compliance review

```
You are an independent reviewer for slice <ID>. Do not modify code.
Read docs/slices/<ID>.md, the cited spec sections, CLAUDE.md and docs/checklists/review-checklist.md.
For every requirement ID in scope, find the code and the test that proves it. Produce a table: requirement, code location,
test, verdict (proven, partly, missing, contradicted). List every behavior in the code that is not in the spec.
List skipped, disabled or weakened tests. Rate each defect critical, major or minor and explain the risk in plain language.
End with a go or no-go recommendation.
```

### 8.2 Security review

```
You are a security reviewer for <module or slice>. Do not modify code. Use docs/checklists/security-checklist.md.
Check: authentication and authorization on every endpoint; tenant resolution and isolation; field-level security and masking;
encryption of sensitive fields and key handling; SQL injection and unsafe string building; input validation; file upload handling;
secrets in code or logs; sensitive data in logs and error messages; rate limits; dependency vulnerabilities; AI prompt-injection paths.
Produce findings with severity, location, exploit scenario in plain language, and a recommended fix.
```

### 8.3 Data and migration review

```
You are a database reviewer for slice <ID>. Do not modify code. Use docs/checklists/db-checklist.md and docs/specs/03-data.md.
Check naming, standard columns, types, constraints, indexes (each justified by a query), partitioning and retention for growing tables,
append-only protection, foreign key rules (no cross-module keys), effective-date exclusion constraints, encryption of sensitive columns.
Review each migration for lock impact on large tables, expand-and-contract safety, reversibility and idempotent seed data.
Run the migrations on two tenant databases and on a populated previous version and report results.
```

### 8.4 Performance review

```
You are a performance reviewer for slice <ID>. Do not modify code.
Identify queries on large tables, missing or unused indexes, N+1 patterns, unbounded lists, synchronous work that should be a job,
lock contention risks, and hot rows. Run or propose the k6 and database tests that check the targets in the specs and report measured results.
```

### 8.5 User experience and accessibility review

```
You are a UX reviewer for slice <ID>. Use docs/checklists/ui-checklist.md. Check keyboard operation, focus order, contrast, labels,
screen reader names, right-to-left layout, Urdu and Arabic text, long text handling, empty states, error messages, loading states, and mobile widths.
Provide screenshots or Playwright traces as evidence and list defects with severity.
```

### 8.6 Dependency and license review

```
List every package added or updated in this slice with version and license. Flag any license that is not permissive, any package
without recent maintenance, any known vulnerability, and any package that duplicates existing functionality. Confirm the license gate output
and SBOM are attached.
```

### 8.7 Event and contract conformance review

```
You are an independent reviewer for slice <ID>. Do not modify code.
Read docs/events (the canonical event catalog of document 14, section 4) and the module CLAUDE.md files.
Compare the events and contracts implemented in this slice with the catalog. List: events published without any consumer; events consumed
without a publisher; name or schema-version mismatches; consumers that are not idempotent; commands without idempotency keys; calls that bypass the
owning module's contract; and any module that reads another module's tables. Run the contract tests and report the results.
End with a go or no-go recommendation.
```

## 9. Verifying work without reading code

### 9.1 The evidence pack for every slice

| Evidence | What you look for |
| --- | --- |
| Gate summary | All gates green; number of tests run and passed; skipped tests equal zero or each explained |
| Requirement matrix | Every FR in scope has a named test; no row marked missing |
| Review report | No open critical or major defects; minor ones listed |
| Demo steps | You can follow the steps in the slice report and see the behavior |
| Data checks | The invariant queries (9.2) return the expected results |
| Diff size | A slice that touches far more files than its plan is a warning sign |

### 9.2 Invariant queries you can run

Ask your technical adviser or Claude to set up a read-only database user for you, then run checks like these against a test tenant.

```sql
-- 1. Posted journal entries that do not balance (must return no rows)
SELECT l.entry_id
FROM fin.journal_lines l JOIN fin.journal_entries e ON e.id = l.entry_id
WHERE e.status = 'posted'
GROUP BY l.entry_id HAVING SUM(l.base_debit) <> SUM(l.base_credit);

-- 2. Gaps in a gap-free number series (must return no rows)
SELECT n + 1 AS missing_after
FROM (SELECT CAST(substring(document_no from '[0-9]+$') AS int) AS n
      FROM fin.journal_entries WHERE company_id = :company AND entry_no LIKE :prefix) s
WHERE NOT EXISTS (SELECT 1 FROM fin.journal_entries e2
                  WHERE CAST(substring(e2.entry_no from '[0-9]+$') AS int) = s.n + 1)
  AND s.n < (SELECT MAX(CAST(substring(entry_no from '[0-9]+$') AS int)) FROM fin.journal_entries WHERE company_id = :company);

-- 3. Foreign keys without a supporting index (must return no rows)
SELECT conrelid::regclass AS table_name, conname
FROM pg_constraint c
WHERE contype = 'f' AND NOT EXISTS (
  SELECT 1 FROM pg_index i WHERE i.indrelid = c.conrelid AND (i.indkey::int2[])[0:cardinality(c.conkey)-1] = c.conkey);
```

These are examples; the real versions are written as part of the invariant suite in each slice and run automatically.

### 9.3 Techniques that catch weak work

- **Sabotage test.** Ask the reviewer to deliberately break one rule (for example let a posted entry be edited, or remove a tenant check) in a scratch branch and confirm that a test fails. A test suite that stays green after sabotage is not protecting you.
- **Mutation testing** on critical engines (ledger, posting, payroll, numbering, permissions): a tool that makes small code changes and checks that tests notice. Ask Claude to set this up for those modules in Step 2 to Step 5.
- **Sampling.** For each slice pick three requirements at random and ask a fresh session to show, step by step, which test proves each one.
- **Scenario scripts.** Run the end-to-end scenarios written in the specs (for example procure to pay, month-end close, payroll run) by hand in the UI.
- **Real-data tie-outs.** For Finance and payroll, load a real customer's trial balance, open items and a payroll month and compare with their existing reports before trusting the logic.
- **Second opinion.** Run the same review on a different model and compare findings.

### 9.4 Red flags

| Red flag | Response |
| --- | --- |
| Tests deleted, skipped, commented out or loosened | Reject; require restoration and an explanation |
| 'All tests pass' without output | Ask for the gate output and the test counts |
| New features or files outside the plan | Reject the extra scope; split into a new slice |
| Statutory numbers (rates, thresholds) appear in code | Reject; they belong in packs |
| Sample data or placeholder logic in real code paths | Reject; require real implementation or an explicit deferral |
| Cross-module imports or foreign keys | Reject; require events or contracts |
| Claude cannot explain a design choice in plain language | Stop; ask for an ADR and re-plan |
| Repeated fixes causing new failures | Stop; restart from the plan in a fresh session |

## 10. Milestone reviews and human checkpoints

| Milestone | Trigger | Reviewer | Focus | Output |
| --- | --- | --- | --- | --- |
| M1 | End of Step 1 | Technical adviser | Tenancy, migrations, provisioning, module loading, packaging, CI quality | Go or fix list before kernel work |
| M2 | End of Step 2 | Technical adviser and security reviewer | Identity, permissions, audit, encryption, entitlements, rules sandbox, data model against document 3 | Security and data model findings closed |
| M3 | End of Finance | Accountant plus adviser | Ledger, posting engine, close, reports against real data; code audit of ledger and posting | Sign-off by an accountant, defects closed |
| M4 | End of HRMIS and payroll | Payroll practitioner plus adviser | Payroll accuracy against independent calculations, statutory pack, parallel-run results | Practitioner sign-off |
| M5 | Before first production customer | External penetration tester, lawyer, operations reviewer | Penetration test, license and EULA, backup and restore drill, upgrade rehearsal, support readiness | Release decision |

Give the adviser: read access to the repository, the specs and ADRs, the latest gate reports, the slice reports and review reports, and a list of your own questions. Ask for written findings.

## 11. Choosing models

This assignment is based on risk and blast radius, not measured benchmarks. Check available models with the model command in Claude Code, and confirm the current Fable version, which is 5.2 as far as I know.

| Use | Work |
| --- | --- |
| Fable 5.2 | Tenant resolution and isolation, migrator, authentication and permission engines, encryption, audit chain, numbering, rules sandbox, entitlements and license verification, ledger and posting engine, foreign-currency revaluation, payroll engine and final settlement, performance and security hardening, whole-module audits, milestone code reviews |
| Opus 5.5 | Most module features, APIs, screens, admin console, importers and integrations, report and dashboard builders, tests, documentation, defect fixing |
| Sonnet 5.5 \[Baseline 1.0: this table is replaced by section 16.2\] | Scaffolding, boilerplate, translations, seed data, mechanical refactors |

Practical rules:

- Run the first two high-risk slices (for example S1.3 and S3.2) with both Fable 5.2 and Opus 5.5 on separate branches, compare reviewer findings and test failures, and then confirm the assignment above with your own evidence.
- Review high-risk slices with a different model than the one that built them.
- If a model declines or hedges on a security-related task, run that task on another model and keep the first for review.
- Match the model to the slice file's Model field so the choice is recorded and repeatable.

## 12. Risk controls for the build environment

- Keep the repository private, with multi-factor authentication on the hosting account, branch protection on main, and required gates before merging.
- Never place real credentials, customer data, production connection strings or the license-signing private key where Claude Code can read them. Use a secrets vault for CI and a separate offline machine or HSM for the license-signing key.
- Limit Claude Code's permissions to the repository folder and approved commands; review any request to run unusual commands or install tools.
- Use only synthetic or masked data in development. Real customer data stays in customer environments.
- Back up the repository and CI artifacts outside the hosting provider, and keep release builds and signing material under separate control.
- Record every dependency and license in the SBOM; block non-permissive licenses automatically.
- Make sure intellectual property is yours: assignments from any human contributors, your own EULA, and legal review of the terms that govern ownership and use of AI-generated code; ask your lawyer to confirm this before commercial release.
- Keep a decision log (ADRs) so a new engineer or adviser can understand why things are built the way they are.

## 13. Common failure modes and responses

| Failure mode | Response |
| --- | --- |
| Claude declares done while gates fail | Run /gate yourself and paste results; require fixes before review |
| Different sessions build in different styles | Strengthen CLAUDE.md and module files; add architecture tests for the style |
| Invented libraries or APIs | Require the license gate and a compile; reviewers flag unknown packages |
| Specs contradict each other | Record in OPEN-QUESTIONS.md, decide, update the specs and the ADR, then continue |
| Huge diffs that cannot be reviewed | Split the slice; one concern per branch |
| Performance degrades over time | Keep performance tests in CI with budgets; review queries each slice |
| Silent assumptions about tax or labor law | Pack data only; practitioner validation; reviewers search for constants in code |
| Context overload in long sessions | New session per slice; point to files; use short prompts |
| You are unsure what the code does | Ask for a plain-language explanation and a diagram, and have the adviser confirm at milestones |

## 14. Glossary

| Term | Meaning |
| --- | --- |
| ADR | A short written record of an architecture decision and its reasons |
| CI | Automated system that builds and tests every change |
| Gate | An automated check that must pass before a change is accepted |
| Slice | A small vertical piece of work: screen, API, data and tests for one capability |
| Migration | A versioned script that changes the database structure safely |
| Tenant | One customer's isolated space in the product |
| Invariant | A rule that must always be true, such as debits equal credits |
| Outbox | A table that guarantees events are published exactly once after a change commits |
| Idempotent | Safe to repeat: running it twice has the same effect as once |
| SBOM | A list of all software components and licenses in a build |
| Pull request | A proposed change that is reviewed and checked before it joins the main code |

## 15. Open items and next steps

**Decisions for you:** code hosting and CI provider; when to engage the technical adviser (I recommend before Step 1 so the milestone M1 review is not the first contact); whether to run the Fable versus Opus comparison; product name and namespace; who will act as accountant and payroll practitioner reviewers for Pakistan.

**You can start Step 0 now:** documents 1 to 6 and this manual are enough to begin slices S0.1 to S0.5. Export the six specs as Markdown, create the repository, install Claude Code, paste the root CLAUDE.md, and run the S0.1 prompt.

**Remaining planning documents:** 7 Procurement, 8 Inventory and Asset Management, 10 Supply Chain, 11 Sales and Distribution, 9 Analytics, Reporting and AI, and 13 UX and Design System Principles. These are completed while the first steps are being built; each new specification adds slices to the catalog in section 6.

## 16. Baseline 1.0 addendum (this document is now version 0.2)

*Added 14 November 2026 by the comprehensive review (Doc 16). Sections 1 to 15 are unchanged except one marked note in section 11. The previous state (version 0.1) is kept in this document's version history and in the frozen originals archive. This addendum takes effect on approval of decisions D-32, D-34, D-35, D-38 and D-39. Nothing here starts development.*

### 16.1 Build steps mapped to the four phases (proposed D-32)

| Phase | Steps and slices from section 6 | Change from v0.1 |
| --- | --- | --- |
| 1 Platform foundation | Step 0, Step 1, Step 2 limited to the Minimum Viable Kernel (Doc 2 section 32.1) | Rules engine, layout designer, configuration packages, AI gateway, search, collaboration and builders leave Step 2. Both tenancy modes are built in Step 1. S2.20 (portal), S2.22 (search), S2.23 (collaboration) move to Phase 3; S2.21 (offline sync) to Phase 2 |
| 2 First usable ERP | Step 3 Finance, Step 4 HRMIS core, Step 5 Payroll, Step 6 hardening; offline sync; mobile M1; S15.1 to S15.3 for the Service Starter | Posting slices implement the atomic posting contract (Finance PST-12 to PST-16) |
| 3 Operations suite | Steps 7 to 9 (Procurement, Inventory, Assets), Sales core slices from the Sales step, geo-attendance and task slices, portal, search, collaboration, builders, KSA pack, installer, S15.4 to S15.11 | Sales core moves up; one shared data design gate for four modules |
| 4 Enterprise and planning | Remaining steps: Supply Chain, Sales extended, consolidation, cost accounting, HR talent, US readiness, iOS | Sequence set at Phase 3 exit |

### 16.2 Model allocation (proposed D-38; replaces the table in section 11)

Three models are used: Fable 5.2, Opus 5.5 and Sonnet 5.5. Allocation follows the risk class of the slice, not the module.

| Class | Meaning | Build | Review |
| --- | --- | --- | --- |
| A | Architecture and irreversible choices: decisions, data design gates, public contracts, security model, costing and payroll calculation design | Fable 5.2 | You approve; human adviser on security |
| B | Integrity-critical code: posting, close, costing, payroll calculation, tax, authorization, tenancy filters, ledger migrations, sync conflicts | Opus 5.5, high effort | Fable 5.2 reviews every slice |
| C | Substantive logic with limited blast radius | Opus 5.5 | Automated checks; Fable samples at milestones |
| D | Routine work from an existing pattern and an exact brief | Sonnet 5.5 | Automated checks; Opus samples |

This changes v0.1 in one important way: Fable no longer builds the high-risk slices by default. It designs them and reviews them, and Opus builds. The task-level table and the escalation rules are in Doc 16 sections 9.3 and 9.4. The comparison run described in section 11 (same slice on two models) stays, and its results adjust this table (Doc 16 section 9.6).

### 16.3 Slice file additions

Every slice file gains four fields: **Class** (A to D), **Model**, **Release tag source** (the requirement IDs tagged R that it implements, proposed D-34) and **FR-XM rules touched**. A slice with no R-tagged requirement is not built. A slice that touches an FR-XM rule includes that rule's integration test in its definition of done.

### 16.4 Gates added to the slice loop

1. **Data design gate** (proposed D-35). Before the first slice of a module, a Fable session produces the module data design and you approve it. Slices that add or alter tables cite the design; a difference between the generated data dictionary and the approved design fails the build.
2. **Release scope tagging** (proposed D-34). At phase entry a Fable session tags every requirement in scope R, L or X; you approve the list.
3. **Phase exit review.** A fresh Fable session reviews against the completion conditions of Doc 16 section 8.

### 16.5 Session rules that control token use

- A session loads the short architecture reference (`docs/architecture-reference.md`, under 3,000 words), the module guide and one slice brief. It does not load full specifications; it opens the cited sections only.
- Stable material goes first in the prompt so it is cached. One slice per session. End with a handoff note; start the next session from the note.
- Opus builds the first example of each pattern; Sonnet work on that pattern starts only afterwards.
- Two failed attempts at the same problem: stop and escalate one level.
- Record model, class, tokens and first-time pass for every slice from the first slice.

### 16.6 Additions to the root CLAUDE.md (section 4)

Append to the non-negotiable engineering rules once the decisions are approved:

```
- Every tenant-scoped table has tenant_id, a row-level security policy, and tenant_id
  leading its unique keys. Never accept tenant_id from a request.
- A document with financial effect posts through the Finance posting contract inside
  its own transaction. Never write to another module's tables.
- Cross-module rules FR-XM-01 to FR-XM-32 (docs/baseline.md section 6) override module
  specifications. If a rule seems wrong, stop and escalate; do not work around it.
- No database geospatial extension. No dependency without a permissive license.
- Do not change a decision, a published contract, an approved data design or an FR-XM
  rule. Raise a change request instead.
- Build only requirements tagged R for the current release.
- If the brief does not contain a decision you need, stop and ask. Do not invent
  business rules.
```

### 16.7 Registers kept in the repository (proposed D-39)

Decision log, requirement register, contract register, component register, generated data dictionary, risk and exception log. Their owners and the change-control rule are in Doc 16 section 11. From the first day of Phase 1 the repository copy of each planning document is the source of truth.

### 16.8 Baseline 1.1 changes (15 November 2026)

Sections 16.1 and 16.2 are superseded once D-41 and D-45 are approved. Phase 1 is now the minimum kernel plus HRMIS, and its build order is in Doc 16 section 14.8. Fable 5.2 is not used: class A and B work is done by Opus 5.5 at high effort, and every class B slice is reviewed by a separate, fresh Opus session. Class D work goes to Sonnet 5.5 (Doc 16 section 14.6). The slice file's Model field takes only the values Opus 5.5 or Sonnet 5.5.
