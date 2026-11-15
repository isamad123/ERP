# Architecture reference (Baseline 1.1)

This is the short reference every build session loads. It summarizes approved decisions; the full text is in
`docs/specs/`. If this file and a specification disagree, the specification and decision register win, and this
file must be corrected.

## 1. Product
- One ERP product for organizations of 5 to 50,000+ employees, any industry. Pakistan first, then Saudi Arabia, then the US.
- Sold as SaaS or on-premises, by module, by user or employee, one-time or subscription, with or without source.
- Behaviour that varies by customer is configuration: settings, entitlements, country packs, industry packs, size profiles.

## 2. Phases (D-41)
| Phase | Scope |
| --- | --- |
| 1 | Minimum kernel + HRMIS core + Pakistan payroll + self-service web + Android M1 |
| 2 | Inventory and Asset Management (simple supplier receipt and customer issue documents included) |
| 3 | Finance (starts per customer at a cutover date with opening balances) |
| 4 | Procurement, Supply Chain, core Sales |

## 3. System shape
- **Modular monolith** (D-01). One API host, one worker host, one migrator. Modules are loaded by the host and
  switched on by entitlement.
- **Each module** has five projects: Contracts (public interfaces and event types), Domain, Application,
  Infrastructure, Api. Modules reference other modules only through Contracts.
- **Kernel** (platform services) is shared by all modules. Business modules depend only on the kernel (D-10).
- **Events** go through a transactional outbox written in the same transaction as the change, and are
  dispatched in process. Consumers are idempotent through an inbox table.
- **Clients:** React 19 web app (shell plus module packages, schema-driven forms) and a React Native (Expo)
  Android app with offline SQLite.

## 4. Tenancy (D-02, D-47)
- **One separate PostgreSQL database per customer**, in SaaS and on-premises alike.
- A small **control-plane** database holds the tenant registry, encrypted connection references, entitlements and
  licence state, schema versions and usage meters.
- The tenant is resolved server-side from host name or token. Never from request body, query string or header input.
- Business tables carry **no `tenant_id`**. Isolation is by database. A tenant-isolation test covers every endpoint.
- Small customers share database servers (each still with its own database) with PgBouncer pooling and idle hibernation.
- Within a tenant, `company_id` (legal entity) is on every company-scoped table and leads composite indexes.

## 5. Kernel services in Phase 1 (D-33, D-42)
| Service | Phase 1 scope |
| --- | --- |
| Tenancy and provisioning | Control plane, provisioning, fleet migration with canaries, backup and restore per tenant |
| Organization | Companies, branches, locations, org units, hierarchies, fiscal and holiday calendars |
| Identity and access | Users, roles, permissions, data scopes (own, team, subtree, company), field security, MFA, sessions, delegation, maker-checker, mobile device-bound sessions (KER-07) |
| Settings and feature flags | Layered settings (tenant, company, role, user) |
| Audit | Append-only change log with hash chain; access log for sensitive fields |
| Numbering | Schemes per company and document type; gap-free only where required |
| Localization | English and Urdu, right-to-left safe; Arabic and Hijri come with the Saudi pack |
| Workflow | Linear and conditional approvals, delegation, escalation, never auto-approve |
| Notifications | In-app, email, push with provider adapters (KER-04) |
| Documents | Attachments, templates for letters and payslips, PDF generation |
| Jobs | Scheduled and chunked resumable jobs |
| Entitlements | Signed entitlement, licence file, expiry states Active → Warning → Grace → Read-only → Locked |
| Integration basics | API keys, outbox, import and export templates, mobile API conventions (KER-06) |
| Offline sync (thin) | Device registry, idempotent action receipt, conflict records, for attendance and leave |
| Posting stub | Records accounting events and produces journal export files (D-43) |

Not in Phase 1: parties, item catalog, kernel tax (Phase 2); report and dashboard builders, portals, global search,
collaboration, AI gateway, layout designer, custom objects (later).

## 6. Data standards (spec 03, sections 3 to 10 and 14 as amended by D-47)
- Schema per module (`hrm`, `org`, `iam`, `cfg`, `wf`, `aud`, `doc`, `ntf`, `itg`, `job`, `syn`, `meta`).
- snake_case; plural tables; `id uuid` (UUIDv7) primary keys; foreign keys indexed; constraint prefixes pk_, fk_, uq_, ck_, ex_.
- Standard columns: `company_id`, `created_at/by`, `updated_at/by`, `row_version`, soft delete on master data,
  `custom jsonb` for custom fields, `valid_from/valid_to` for effective dating, `name_i18n jsonb`.
- Money `numeric(19,4)` with currency; rates `numeric(19,10)`; quantities `numeric(19,6)`; no floating point.
- All timestamps UTC `timestamptz`; business dates `date`; injected clock.
- Enumerations are text with check constraints. jsonb only for user-defined or sparse data.
- Sensitive fields: encrypted ciphertext plus blind index for lookup. Personal data in the audit log is encrypted
  per data subject so erasure is possible without breaking the chain.
- Effective-dated tables use exclusion constraints (btree_gist) to forbid overlaps.
- Transactional headers and lines carry `branch_id`; association tables may use composite keys.
- Records created offline get their id from the server; the client sends an idempotency key.
- Growing tables (attendance events, audit, job logs) are partitioned by month from the first migration.
- Migrations: SQL-first, per module, expand and contract, tested on a fresh database and on previous-release data.

## 7. HRMIS Phase 1 scope (spec 06a v0.3.1, D-42)
- **In:** POL policy framework; EMP employee records; POS organization and positions; DOC documents and letters;
  ATT-01 to ATT-22 (biometric import, web and mobile check-in with one circular office fence, exceptions,
  regularization); LVE leave; ONB basic onboarding and offboarding; SSP self-service web; RPT basic reports;
  PAY payroll engine; LON loans and advances; EXP basic expenses; CPL Pakistan statutory; Android M1 (profile,
  leave, payslip, attendance).
- **Later:** full geo-attendance (ATT-23 onward), field tasks (TFA), workspaces (WSP), recruitment, performance,
  learning, employee relations, asset custody, Saudi pack, US readiness, iOS.
- Exact scope is the R-tagged list in `docs/registers/requirements.csv`, approved by the product owner.

## 8. Rules that bind Phase 1 (spec 16 section 6)
- **FR-XM-26** Attendance and leave lock when payroll is calculated; later changes are retroactive adjustments in the next open payroll.
- **FR-XM-27** (export form until Finance exists) One summarized payroll journal per legal entity and cost allocation; payslip reversal only before payment; afterwards, adjustment or off-cycle run.
- **FR-XM-28** A person is one record with several roles.
- **FR-XM-29** Final settlement is blocked by open advances, loans, unreturned items, unsubmitted claims and pending approvals.
- **FR-XM-30** Transfer between legal entities ends one employment and starts a linked one, with explicit carry-over rules.
- **FR-XM-32** Approvals escalate and never auto-approve; nobody approves their own request; overrides appear on an exception report.
- **FR-XM-05/06** Finalized documents are immutable; correction by reversal; legal numbers never reused.

## 9. Without Finance (D-43)
- Payroll and (in Phase 2) stock raise accounting events through the kernel posting stub.
- Journal export files per payroll run and per period are mapped to the customer's chart of accounts.
- The Finance posting contract is defined in Phase 2 so later modules need no rework; atomic posting (D-31) applies from Phase 3.

## 10. Commercial model (D-05, D-26, D-40)
- One entitlement service drives both on-premises licence files and SaaS subscriptions.
- Three editions (Starter, Business, Enterprise) are entitlement templates; names, limits and prices are commercial choices.
- Four size profiles (Micro, SME, Mid, Enterprise) are configuration presets. Hidden features still have real
  default records (one company, one branch, one approval route), so switching profile never converts data.
- No edition-specific or profile-specific code; gate by entitlement key or setting.

## 11. Security and privacy
- OIDC through OpenIddict, MFA, password and session policies, lockout; SSO later.
- Permission-based roles, data scopes, field-level security, maker-checker for sensitive changes, segregation of duties.
- TLS everywhere, field-level envelope encryption, per-tenant keys, encrypted backups, secrets outside the repository.
- Location capture is event-based only. Biometric templates are never stored; device biometrics unlock an existing session.
- Statutory and privacy content is planning input to be verified by practitioners and counsel; never presented as confirmed compliance.
- A human security review is required before the first customer goes live (D-46).

## 12. Quality targets (to be proven by test, not claimed)
- Standard page p95 under 2 seconds. Payroll for 10,000 employees under 15 minutes; load test at 50,000.
- Isolation, audit and invariant tests run on every build.
- SaaS target 99.9 percent monthly, RPO 15 minutes, RTO 4 hours; daily encrypted backups, per-tenant restore.

## 13. Governance (D-34, D-35, D-39, D-45)
- Build only R-tagged requirements. Each module's data design is approved before its first migration.
- Registers in `docs/registers/`: requirements, contracts, components, dependencies, risks and change requests.
- Baseline items change only through a recorded change request approved by the product owner.
- Models: Opus 5.5 for design and integrity-critical code with fresh-session review; Sonnet 5.5 for routine work.
