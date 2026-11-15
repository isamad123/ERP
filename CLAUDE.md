# ERP Platform: rules for Claude Code

## What this repository is
A modular-monolith ERP product (.NET 10, PostgreSQL, React, React Native). It is sold as SaaS and
on-premises, with or without source code, module by module. The first release (1.0) is **HRMIS web only**
(HR, attendance, leave, Pakistan payroll, self-service web). The Android app and the remaining HR features are Release 1.1 (D-48).

The product owner is not a programmer. Explain outcomes in plain language and always give evidence
(test output, query results, screenshots) instead of assurances.

## Read first, in this order
1. `docs/architecture-reference.md` — the short reference. Load this by default; do NOT load whole specifications.
2. The slice file in `docs/slices/` for the task in hand.
3. Only the specification sections the slice file cites (`docs/specs/`).
4. `docs/decisions/decision-register.md` for any decision the slice relies on.

## Order of authority when sources disagree
decision register → `docs/specs/16-erp-baseline.md` (sections 6 and 14) → data standards (`docs/specs/03-…`, including
section 14 as amended by D-47) → module specification → slice file → code. A lower source that disagrees with a
higher one is a defect in the lower one: stop and report it.

## Before you write any code
1. Confirm the slice cites requirement IDs whose `release` in `docs/registers/requirements.csv` is the release being built (1.0 first). Build nothing marked Later or Not planned.
2. Use plan mode. Present files, tables, endpoints, tests and open questions. Wait for approval.
3. If the specification is silent, ambiguous or contradictory, STOP and ask. Never invent a business rule.

## Non-negotiable engineering rules
- **Architecture:** modular monolith. Each module has Contracts, Domain, Application, Infrastructure, Api projects.
  Modules talk only through Contracts and events. No foreign keys or joins across module schemas. Modules depend on
  the kernel only (D-10).
- **Tenancy (D-02, D-47):** one separate PostgreSQL database per customer. Business tables have NO `tenant_id`
  column and NO tenant row-level security. Resolve the tenant server-side from host name or token only; never from
  request input. Every endpoint has a tenant-isolation test.
- **Database (spec 03):** UUIDv7 ids, `numeric(19,4)` money, no floating point, UTC `timestamptz`, standard audit
  columns, `row_version` on aggregate roots, constraints in the database, every foreign key indexed, effective-dated
  tables with exclusion constraints. Audit, payroll results after close and stock movements are append-only.
- **Migrations:** SQL-first, per module, expand-and-contract. Never edit a migration after it is merged.
  No module table is created before that module's data design is approved (D-35).
- **No Finance yet (D-43):** documents with financial effect raise accounting events through the kernel posting stub
  and produce journal export files. Never create ledger tables or call a Finance module in Phase 1.
- **Cross-module rules:** FR-XM-01 to FR-XM-32 (`docs/specs/16-erp-baseline.md` section 6) override module
  specifications. Phase 1 must honour FR-XM-26 (attendance locks at payroll calculation), FR-XM-28, FR-XM-29 and
  FR-XM-30. If a rule seems wrong, stop and escalate; never work around it.
- **Statutory content:** no tax rates, slabs, thresholds or statutory rules in code. They are country-pack data.
- **Time:** use the injected clock. Never call `DateTime.Now` or `UtcNow` in business code.
- **Configurability:** behaviour that differs by customer, edition or size profile is settings, entitlements or pack
  data, never code branches (D-29, D-40).
- **Security:** permission check on every endpoint, data scopes, field-level security on salary and personal data,
  parameterized SQL only, no secrets in the repository, sensitive fields encrypted, every change audited.
  Device biometrics only unlock an existing session; never store biometric templates.
- **Location data:** event-based capture only, never continuous tracking by default. No database geospatial
  extension (D-36); compute distance and containment in the application.
- **Localization:** no hard-coded user-facing strings. English and Urdu in Phase 1; layouts right-to-left safe.
- **Dependencies:** permissive open-source licences only (D-09). Check the licence before adding any package and
  record it in `docs/registers/dependencies.md`.
- **Baseline items:** do not change a decision, a published contract, an approved data design or an FR-XM rule.
  Raise a change request in `docs/registers/risks-and-changes.md` instead (D-39).

## Testing rules
- Every implemented requirement has automated tests that cite its FR id in the test name or attribute.
- Never delete, skip, weaken or comment out a test to make a build pass.
- Integration tests use a real PostgreSQL (Testcontainers). Do not mock the database.
- Before declaring a slice done run: build, unit, architecture, integration, tenant isolation, invariants, lint,
  licence and secret scans.

## Models and token use (D-45, docs/token-strategy.md)
- **Model check first:** read the `Model:` line of the slice file. If you are not running that model, stop at once
  and tell the product owner which model to select. Do no other work.
- Default to **Sonnet 5.5**. Use **Opus 5.5, high effort** only for slices marked class A or B (design, tenancy,
  permissions, payroll and tax, attendance locking, final settlement, audit, licensing).
- Generated code first: extend the code generator rather than hand-writing repeated code.
- One slice per fresh session. Load only CLAUDE.md, the architecture reference, the slice file and touched files.
- Quiet builds and tests; read only failures. Two failed attempts: stop and escalate.

## Scope discipline
- Implement only the slice. No extra features, no unrelated refactors, no silent specification changes.
- No stub code that pretends to work. TODOs only with an issue id, listed in the report.
- Seed data is idempotent and keyed by stable codes.

## Slice report (always end with this)
1. What was built, in plain language. 2. Requirement IDs covered. 3. Evidence: commands run and results.
4. Tables, endpoints and events added or changed. 5. What is not done. 6. Risks and open questions.
7. How the product owner can see it working.

## Commands
(Filled in by the first build slice: build, test, run, migrate, lint, gates.)

## Where things are
Specs: `docs/specs`. Decisions: `docs/decisions`. ADRs: `docs/adr`. Registers: `docs/registers`.
Slices: `docs/slices`. Handoff notes: `docs/handoff`.
