# ERP Platform: rules for Claude Code

## What this repository is
A modular-monolith ERP product (.NET 10, PostgreSQL, React, React Native). It is sold as SaaS and
on-premises, with or without source code, module by module. The first release (1.0) is **HRMIS web only**
(HR, attendance, leave, Pakistan payroll, self-service web). The Android app and the remaining HR features are Release 1.1 (D-48), except
performance management and learning, which are in 1.0 (D-49).

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
  specifications. Release 1.0 must honour the eight rules tagged 1.0 in `requirements.csv` (CR-052 Q10): FR-XM-05 and
  FR-XM-06 (finalized documents immutable, correction by reversal, numbers never reused), FR-XM-26 (attendance locks
  at payroll calculation), FR-XM-27 (one summarized payroll journal per legal entity, export form until Finance),
  FR-XM-28, FR-XM-29, FR-XM-30 and FR-XM-32 (approvals escalate, never auto-approve, nobody approves their own request).
  If a rule seems wrong, stop and escalate; never work around it.
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
  permissions, payroll and tax, attendance locking, final settlement, audit, licensing), or where the approved build
  plan names Opus (CR-052 Q6).
- Generated code first: extend the code generator rather than hand-writing repeated code.
- One slice per fresh session. Load only CLAUDE.md, the architecture reference, the slice file and touched files.
- Quiet builds and tests; read only failures. Two failed attempts: stop and escalate.

## Scope discipline
- Implement only the slice. No extra features, no unrelated refactors, no silent specification changes.
- No stub code that pretends to work. TODOs only with an issue id, listed in the report.
- Seed data is idempotent and keyed by stable codes.

## Branches and merging (product owner, 2026-11-15)
- `main` is the single line of development. Every slice starts from the latest `main`.
- Work on the session's branch, then open a pull request to `main` and merge it once CI is green
  and the slice report is done. Do not leave finished work on side branches.
- Standing instruction (product owner, 2026-11-18): commit, push, open the pull request and merge it yourself when CI is green and the slice report is done. Do not ask for permission each time.
- Never merge with failing CI. If `main` moved on, update the branch from `main` and rerun CI before merging.
- Never merge a class B slice (or a part named in `docs/token-strategy.md` section 7) before its fresh Opus review is
  in `docs/reviews/` and its high and medium findings are fixed or ruled on (CR-052 Q8).

## Slice report (always end with this)
1. What was built, in plain language. 2. Requirement IDs covered. 3. Evidence: commands run and results.
4. Tables, endpoints and events added or changed. 5. What is not done. 6. Risks and open questions.
7. How the product owner can see it working.

## Commands
Run from the repository root. Tests use Microsoft Testing Platform (`global.json`), so `dotnet test` takes `--project`/`--solution`.
- Restore and build (warnings are errors; this is also the lint step: analysers + `.editorconfig`): `dotnet build Erp.sln -v q`
- Local PostgreSQL 16 for integration tests (when Docker is not running):
  `sudo service postgresql start && sudo -u postgres psql -c "alter user postgres password 'postgres'"` (once per server, for the
  100-transaction numbering test, S1.08: `sudo -u postgres psql -c "alter system set max_connections = 300" && sudo service postgresql restart`) then
  `export ERP_TEST_PG="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres"`.
  Without `ERP_TEST_PG` the tests start `postgres:16` through Testcontainers (needs Docker).
- All tests: `dotnet test --solution Erp.sln --no-build`
- One suite: `dotnet test --project tests/Erp.UnitTests --no-build` (also `Erp.ArchitectureTests`, `Erp.IntegrationTests`)
- Licence gate (after restore): `python3 build/licence-gate.py --self-test && python3 build/licence-gate.py`
- Run the API: `dotnet run --project src/Host/Erp.Host.Api --urls http://127.0.0.1:5080`, then `curl http://127.0.0.1:5080/health`
- Run the worker: `dotnet run --project src/Host/Erp.Host.Worker`
- Generate code from the data design (after any change to `docs/data-design/*.yaml` or `tools/Erp.CodeGen/*.yaml`):
  `dotnet run --project tools/Erp.CodeGen -- generate`, then rebuild the data dictionary with
  `dotnet run --project tools/Erp.CodeGen -- dictionary --connection "$ERP_TEST_PG"`. Never edit `*.g.cs` or other `<auto-generated>` files.
- Migrations are generated SQL under `*/Migrations` (kernel, hrm) and `ControlPlane/Migrations`. Merged files are frozen: a later
  change is a new file (rules.yaml `implemented` entries and grants are emitted as new files, S1.03).
- Migrator and operator commands (S1.03, S1.05): `dotnet run --project src/Host/Erp.Host.Migrator -- help` (migrate, cluster add, signing-key add,
  entitlement sign, tenant provision/list/suspend/resume/backup/restore/admin-activation-link, auth-key rotate; Super Admin recovery: tenant admin-role-repair/admin-grant/admin-revoke, S1.06-r1; audit anchors-publish and audit verify, S1.07). Settings are environment variables: `docs/runbooks/tenant-operations.md`.
  Backup and restore drill: `docs/runbooks/tenant-backup-restore.md` (needs pg_dump/pg_restore of the server's major version, 16).
  `migrate` records control-plane scripts with checksums in `cp.schema_history` like customer scripts in `meta.schema_history` (S1.11f2, CR-006);
  provisioning holds a per-customer advisory lock in the control-plane database, so a second run for the same customer waits (`ProvisioningLock`, R-031).
- CI: `.github/workflows/ci.yml` runs restore, build, generator drift, TypeScript type-check, unit, architecture, integration
  (PostgreSQL 16 service), data-dictionary drift and the licence gate.
- Tenant-isolation suite (S1.04): `dotnet test --project tests/Erp.IntegrationTests --no-build --filter-trait "Suite=TenantIsolation"`; CI job `tenant-isolation`.
  Every endpoint is found automatically; one the suite cannot probe fails it (see `docs/handoff/2026-11-15-S1.04.md`).
- Access control (S1.06): `generate` also writes each module's permission manifest (`*/Access/permissions.g.json`); baseline roles are
  data in `*/Access/baseline-roles.json`, applied by provisioning and `migrate`. Access tests use the real engine (`tests/Erp.IntegrationTests/Access`);
  the isolation suite keeps allow-all doubles (`AllowAllAccess`).
- Numbering (S1.08): one kernel service (`INumberAllocator`) gives every document number; tables declare `numbering:` in the data design.
  Default schemes are seeded by provisioning and `migrate` (`NumberingSetup`). Invariant 6 tests: `--filter-trait "Invariant=6"`.
- Settings and flags (S1.09a): declared in `<module>.Infrastructure/Settings/settings.yaml`; `generate` checks the manifest and writes
  `Access/settings.g.json` and `*SettingKeys.g.cs`; `migrate` and provisioning register them (`SettingsSetup`). Values change only
  through the settings services (`/api/cfg/settings`, `/api/cfg/feature-flags`). Hosts re-check `cfg.config_versions` every
  `Settings:RefreshMilliseconds` (default 1000; tests use 0). The two-process test starts `src/Host/Erp.Host.Api/bin/<config>/net10.0/Erp.Host.Api.dll`,
  so build the solution before the integration tests.
- Entitlements (S1.09b): claims follow the D-24 key catalogue (`Erp.Kernel.Infrastructure/Entitlements/entitlement-keys.json`); data-design
  key `entitlement:` (file `- defaults:` entry, table override) gates generated endpoints. Operator commands `entitlement draft|apply|history`,
  `licence refresh`, `usage statement`; the worker host runs the hourly licence loop. Tests change a licence through
  `TenancyFixture.SetClaimAsync` / `SetDaysAfterEndAsync` (the API reads `cp.entitlements`, never `meta.local_entitlement`).
- Workflow (S1.10a, S1.10b): document types in `<module>.Infrastructure/Access/workflow.json`; engine `Erp.Kernel.Infrastructure/Workflow/WorkflowEngine*.cs`.
  Escalation runs in the worker host every minute and on demand with `workflow escalate [--tenant]`; tests run it through
  `WorkflowRig.EscalateAsync` and move time with `WorkflowRig.ClockAt` (see `docs/handoff/2026-11-17-S1.10b.md`).
- Jobs and events (S1.11a): job manifests in `<module>.Infrastructure/Access/jobs.json` (seeded by `migrate` and provisioning, `JobSetup`),
  handlers `IJobHandler`, consumers `IEventConsumer`; the worker host (`WorkerHost.Configure`) runs `JobWorker` and `EventDispatcher`.
  Tests build workers through `JobRig` (`tests/Erp.IntegrationTests/Jobs/JobSupport.cs`): `rig.Worker()`, `rig.QueueAsync`, `rig.RunUntilIdleAsync`,
  time moves with `rig.Clock`. The job centre API is under `/api/jobs` (generated `/api/job/*` only read).
- Notifications (S1.11b1): catalogue of notifiable events in `<module>.Infrastructure/Access/notification-events.json` (seeded by `migrate` and provisioning, `NotificationSetup`),
  consumer `ntf.notifications` registered in `WorkerHost.Configure`; customer texts and rules change only through `/api/ntf/*` (generated `ntf` endpoints read only). Tests: `tests/Erp.IntegrationTests/Notifications`
  (events are written to the outbox as the kernel writes them and handled by the real `EventDispatcher` through `JobRig`).
- E-mail (S1.11b2): consumer `ntf.notifications` also queues `ntf.outbound_messages`; jobs `kernel.email_send` (every minute) and `kernel.email_bounces` (IMAP, 15 minutes) in the worker;
  worker settings `Mail:*` and `Alerts:OperatorEmail`, operator commands `mail sender|server|clear|show` (`docs/runbooks/mail-setup.md`). Tests send to `TestMailServer` / `TestImapServer`
  (`tests/Erp.IntegrationTests/Notifications`) through `TestMailServer.Use`; identity links and codes are read with `IdentityHarness.EmailedAsync` (decrypts the queued secret).
- Invariants (spec 03 section 10): 3, 5, 6, 8, 9 and 10 have tests (5 is `R06_effective_dated_table_refuses_overlapping_periods`,
  10 is the tenant-isolation suite), each tagged `[Trait("Invariant", "n")]` and run with `--filter-trait "Invariant=n"` (S1.11f1, CR-053). 1, 2, 4, 7 and
  12 to 14 concern ledgers, stock and posting and arrive with those features (D-43). Until they are tested, a slice report lists them as not applicable.
- Secret scan (S1.11f1, CR-053): gitleaks 8.28.0 runs first in the CI job `build-and-test` over the history reachable from the checked-out commit and the working tree and fails on any
  finding; configuration `.gitleaks.toml` (default rules, exact-value allowances only). Run locally with the same binary: `gitleaks detect -c .gitleaks.toml --redact`.

## Front-end rules (spec 13 section 24.1, shortened; S1.12a, CR-063)
- Use only the design system (`web/packages/ui`). No other UI library without an ADR and the npm licence gate.
- Design tokens only (CSS variables from `web/packages/ui/tokens/tokens.json`): no raw colours, sizes, fonts or z-index.
- Logical CSS properties only (start/end, inline/block), never left/right. Every screen works in English and Urdu (RTL).
- No hard-coded user-facing text: messages in `web/packages/i18n/messages/{en,ur}.json` (ICU). Server texts are keys.
- Numbers, money and dates through `@erp/i18n` formatters; money is an exact decimal string, never a float.
- Keyboard first: accessible names, visible focus, no serious or critical axe finding. Loading, empty, error and
  permission-denied states on every screen. No token or personal data in browser storage.
- Stay within the budgets (shell under 300 KB, route chunks under 200 KB compressed); lazy-load module routes.
- Assets are bundled; nothing is fetched from another host. The content security policy allows no inline script or style.

## Web commands (S1.12a)
Run from `web/` (Node 22, pnpm through corepack): `pnpm install --frozen-lockfile`, then `pnpm lint`, `pnpm typecheck`,
`pnpm test` (vitest), `pnpm contrast`, `pnpm build` (production build and size budgets), `pnpm build:e2e` (test build with
the component catalogue). npm licence gate (repository root): `python3 build/licence-gate.py --npm web`.
Browser tests (needs `pnpm build:e2e` first and PostgreSQL as above; Chromium of Playwright 1.63.0):
`dotnet test --project tests/Erp.IntegrationTests --no-build --filter-trait "Suite=WebE2E"` (`E2E_GREP` runs matching
scenarios only). Screenshots land in `web/apps/web/test-results/screenshots`; CI job `web` keeps them as an artifact.
The normal integration run leaves the browser tests out: `--filter-not-trait "Suite=TenantIsolation" --filter-not-trait "Suite=WebE2E"`,
so "all tests" with `dotnet test --solution Erp.sln` needs the web test build. Run the API host with the web application:
`Web__Root=web/apps/web/dist dotnet run --project src/Host/Erp.Host.Api --urls http://127.0.0.1:5080`.
Shell (S1.12b): each web module declares its screens in `web/apps/web/src/modules/<module>.ts` (path, label key, icon, menu group,
permission, feature); `navigation/screens.ts` decides licence → locked → permission → screen; who is signed in comes from
`GET /api/kernel/me` (`useShell()`, `useWritable()`). A later screen slice adds its component to its declaration. The browser tests'
control server also gives roles, adds companies, changes the licence and adds notifications (`e2e/support.ts` `server.*`).
List and form engine (S1.12c): `web/packages/engine` (`ListPage`, `FormPage`, `DataGrid`, `HistoryPanel`) makes screens from
`web/generated/metadata/<table>.json` and `validation/<table>.json`; see `web/apps/web/src/modules/kernel/branches.tsx`. A screen
declaration lists its `tables`, and `modules/labels.test.ts` fails until their Urdu labels are in `web/packages/i18n/labels/<module>.ur.json`.
Generated lists take `sort` (indexed columns) and `search` (code and name); saved views live in `cfg.saved_views` (`/api/cfg/saved-views`).
Approvals (S1.12d): `/approvals` (`web/apps/web/src/modules/kernel/approvals.tsx`) shows every registered document type's tasks and the pending
maker-checker requests; a module adds no screen work, only its `workflow.json` (fields, title, link). Decisions shown are the task's `context.actions`
from `GET /api/wf/tasks/{id}`; pending changes come from `GET /api/iam/maker-checker-requests/pending`. Browser tests start requests through the
control server (`server.route`, `server.request`, `server.delegate`, `server.heldSetting` in `e2e/support.ts`).

User console (S1.13a): `/admin/users` (`web/apps/web/src/modules/admin/users*.tsx`, API client `web/apps/web/src/api/users.ts`). Server views:
`GET /api/iam/user-overview`, `/api/iam/users/{id}/access`, `/api/iam/people-lookup` (`UserConsoleEndpoints.cs`, `UserConsoleService.cs`). A username never equals
another user's e-mail (trigger `tg_users_username_email_clash`, a rules.yaml `functions` entry: new rules never go in as design notes, the first migrations are frozen).
Browser tests: `server.licence({ usersLimit })` changes the named-user limit.

## Where things are
Specs: `docs/specs`. Decisions: `docs/decisions`. ADRs: `docs/adr`. Registers: `docs/registers`.
Slices: `docs/slices`. Handoff notes: `docs/handoff`.
