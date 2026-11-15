# Admin Console, User Management and Settings: Functional Specification (v0.1)

Document 4 of the planning set | Prepared for Ibrahim | 10 November 2026 | Companion to the Platform Kernel Functional Specification

## 1. Purpose, scope and conventions

### 1.1 Purpose

This document specifies the screens and behaviors through which administrators operate the product: user and role management, security settings, organization setup, the configuration studio (fields, forms, workflows, rules, templates), settings, localization, integrations, data management, operations, and the vendor-side platform console. The engines behind these screens are specified in document 2; this document defines what an administrator sees and can do.

The design goal is that a non-developer administrator or implementation consultant can configure a complete tenant safely, and that the product compares well with the administration experience of established ERP products.

### 1.2 Two consoles

| Console | Used by | Scope |
| --- | --- | --- |
| Tenant Admin Console | Tenant Admin, Company Admin, Implementation Partner | One tenant (and its companies); includes the configuration studio |
| Platform Console | Vendor staff | Control plane: tenants, entitlements, packs, fleet migrations, usage. Never shows tenant business data without a support grant |

On-premise installs include both: the Platform Console is reduced to the license page, version and update management, and health.

### 1.3 Conventions

- Requirement IDs follow FR-ADM-\<area>-\<number>. Priority (M), (S), (C) as in document 2; unmarked is (M). Wave A unless stated.
- Every admin capability is also available through the public API with the same permissions (document 2, section 1.2).
- Every admin change is audited with before and after values.

### 1.4 Design principles

1. Guided: wizards and checklists for first-time setup; sensible defaults from packs.
2. Safe: preview, validation, dependency view, sandbox testing and rollback before anything affects production.
3. Searchable: one search box finds any setting, user, role, field, workflow or template.
4. Least privilege: admin screens are themselves permission-controlled; delegated administration is supported.
5. Transparent: every effective value shows where it came from and who changed it.
6. Fast for experts: bulk actions, keyboard navigation, import and export on every list.
7. Accessible and multilingual: WCAG 2.1 AA, English, Urdu and Arabic with right-to-left layout.

## 2. Console structure and navigation

### 2.1 Tenant console sections

| Section | Contents |
| --- | --- |
| Home | Setup progress, health, license state, pending approvals for admin actions, recent changes, announcements |
| Organization | Companies, branches, locations, organization units, hierarchies, fiscal calendars and periods |
| Users and access | Users, groups, roles, permissions, data scopes, field security, SoD rules, delegations, access reviews, sessions |
| Security | Authentication policies, MFA, SSO and directory, API clients, network rules, encryption and key status, privacy tools |
| Configuration studio | Custom fields, custom objects, forms and lists, labels, menus, status models, numbering, workflows, rules, templates, notifications |
| Settings | Settings center by module, feature flags, defaults per company |
| Localization | Languages, translations, currencies and rates, calendars, holidays, formats |
| Integrations | API keys, webhooks, connectors, import and export templates, file exchange, usage |
| Data | Import and export center, bulk update, duplicate management, archival, environment copies, backups |
| Operations | Job center, notification log, audit viewer, system health, usage and license |
| Packs and packages | Installed packs, updates, configuration packages, promotion |
| AI controls | AI switches per module and group, provider and data policy, interaction log, usage |

### 2.2 Common behaviors

- **FR-ADM-NAV-01** A global search (keyboard shortcut) shall find settings, users, roles, fields, workflows, templates, jobs and help, and open them directly.
- **FR-ADM-NAV-02** Every list in the console shall support filters, sorting, column selection, saved views, bulk actions where meaningful, and export.
- **FR-ADM-NAV-03** Every configuration object shall show status (draft, active, retired), version, last changed by and when, and a history tab.
- **FR-ADM-NAV-04** Changes shall be saved as drafts, previewed, validated, and then published; publishing in production may require maker-checker according to settings.
- **FR-ADM-NAV-05** The console shall show an environment banner (production, UAT, sandbox) on every page and prevent accidental production edits through a confirmation step when configured.
- **FR-ADM-NAV-06** A dependency panel shall show what uses an object (for example which workflows use a role) before it is changed or retired.
- **FR-ADM-NAV-07** A change summary and diff shall be shown before publishing and stored with the change.
- **FR-ADM-NAV-08** Contextual help shall be available on every screen, with links to documentation and the AI assistant where enabled.

## 3. First-run setup and go-live readiness (ONB)

- **FR-ADM-ONB-01** A first-run wizard shall guide the tenant admin through: company profile, country and industry pack selection, language and calendar, currency and fiscal year, modules to enable (within the entitlement), organization structure, first users and roles, and notification and email settings.
- **FR-ADM-ONB-02** The wizard shall create everything through the same engines as manual setup and shall be resumable, skippable by step, and repeatable for additional companies.
- **FR-ADM-ONB-03** A readiness checklist per module shall show required configuration (for example chart of accounts loaded, periods open, numbering set, approvers defined) with status and links, and shall block go-live of a module until mandatory items are complete unless overridden with a reason.
- **FR-ADM-ONB-04** The wizard shall offer to import master data from templates (chart of accounts, partners, items, employees) through the import center with validation previews.
- **FR-ADM-ONB-05** Implementation partners shall be able to work in a tenant with delegated, time-limited permissions and a visible project mode that records configuration work. (S)
- **FR-ADM-ONB-06** The AI configuration assistant, where enabled, shall be offered in the wizard to draft fields, workflows and reports from plain-language descriptions, always as drafts for review. (S)
- **AC:** a new tenant with the Pakistan pack reaches a state where users can post a first journal and submit a first leave request using only the wizard and import templates, without developer help.

## 4. User management (USR)

### 4.1 User directory

- **FR-ADM-USR-01** The user list shall show name, username, email, status, roles, companies, last login, MFA state and license seat consumption, with filters on any of these.
- **FR-ADM-USR-02** Admins shall create users individually, invite by email with expiring links, or import in bulk from a template with validation and a preview of errors; imports shall create invitations rather than passwords.
- **FR-ADM-USR-03** User statuses shall be invited, active, locked, disabled, and archived, with allowed transitions, reasons, and effects on sessions and workflow tasks (reassignment prompt on disable).
- **FR-ADM-USR-04** Each user record shall show profile, roles with effective dates and scope, data scopes, delegations, devices and sessions, MFA devices, login history, and recent activity from the audit log.
- **FR-ADM-USR-05** Admins shall reset passwords by sending a link, force password change, unlock accounts, revoke sessions, and enroll or reset MFA, each with an audit entry.
- **FR-ADM-USR-06** User groups and teams shall be definable for assignment of roles, notifications and data scopes in bulk.
- **FR-ADM-USR-07** Users may link to an employee record when HRMIS is licensed; the link is optional and managed from either side.
- **FR-ADM-USR-08** Licensed seat consumption (named, concurrent) shall be visible with warnings at configurable thresholds, and creation shall stop at the hard limit with an explanatory message.
- **FR-ADM-USR-09** Bulk actions shall include assign or remove role, change company, disable, extend access end date, and export.
- **FR-ADM-USR-10** Self-service profile pages shall let users manage their own language, time zone, calendar, notification preferences, MFA devices, active sessions and personal API tokens if permitted.
- **FR-ADM-USR-11** A permission preview mode ('view as role' or 'test as user') shall let administrators see menus and screens as a given role would, in a read-only simulation that cannot post or change data and is audited. (S)
- **FR-ADM-USR-12** Offboarding checklist: disabling a user shall show open tasks, owned documents and scheduled jobs for reassignment, and optionally trigger deprovisioning of linked directory accounts through SCIM or LDAP.
- **AC:** an admin imports 500 users from a spreadsheet, fixes the 12 rows with errors from the preview, sends invitations, and sees seat consumption update.

### 4.2 Authentication administration

- **FR-ADM-USR-20** Admins shall configure password policy (length, complexity, history, expiry, lockout thresholds) per tenant, with optional stricter policy for privileged roles.
- **FR-ADM-USR-21** MFA policy shall be configurable per tenant, role, or user, with allowed methods and grace periods for enrollment.
- **FR-ADM-USR-22** SSO configuration screens shall support OIDC and SAML identity providers with metadata import, claim and role mapping, test sign-in, just-in-time provisioning rules, and the option to keep local admin break-glass accounts.
- **FR-ADM-USR-23** Directory configuration shall support LDAP and Active Directory connections with attribute mapping, group-to-role mapping, scheduled sync, and a test and dry-run view.
- **FR-ADM-USR-24** Session policy shall configure idle timeout, absolute timeout, concurrent session limit and remember-device duration.
- **FR-ADM-USR-25** Network policy shall allow IP allow lists per tenant, role or API client, and optional country restrictions. (S)

## 5. Roles, permissions and data access (ROL)

- **FR-ADM-ROL-01** The permission catalog shall be browsable by module and resource with descriptions, sensitivity flags and the roles that hold each permission.
- **FR-ADM-ROL-02** The role designer shall create roles by selecting permissions with search and module grouping, copy from baseline or another role, compare two roles side by side, and show the number of users holding the role.
- **FR-ADM-ROL-03** Baseline roles shipped by modules and packs shall be copied into the tenant and editable; a pack upgrade shall propose, not force, changes to baseline roles.
- **FR-ADM-ROL-04** Role assignment shall support company scope, start and end dates, reason, and approval through maker-checker for sensitive roles.
- **FR-ADM-ROL-05** An effective-access viewer shall answer 'can this user do X on this record, and why' by showing the roles, scopes and rules that grant or deny the action.
- **FR-ADM-ROL-06** The data scope designer shall define scopes by company, branch, department, hierarchy position (own, team, subtree), and attribute conditions, with a test that lists the records a sample user would see.
- **FR-ADM-ROL-07** The field security matrix shall set hidden, masked, read-only or editable per field, per role, for standard and custom fields, with a preview of a form under a role.
- **FR-ADM-ROL-08** Segregation-of-duties rules shall be definable from pairs of permissions or role combinations, with severity; the console shall report existing conflicts and block or warn on new assignments.
- **FR-ADM-ROL-09** Delegations and temporary access shall be created by admins or by users within allowed limits, with automatic expiry and audit.
- **FR-ADM-ROL-10** Access reviews shall let admins launch periodic recertification campaigns: managers or owners confirm or revoke each user's roles within a due date, with reminders and a report. (S)
- **FR-ADM-ROL-11** A permission change report shall list who changed which role or assignment when, and a role usage report shall list unused permissions and inactive users.
- **FR-ADM-ROL-12** Export and import of roles as part of configuration packages shall be supported.
- **BR:** a user cannot grant permissions they do not hold; the last tenant admin cannot be removed; changes to roles holding sensitive permissions require maker-checker when that policy is enabled.
- **AC:** the effective-access viewer explains that a manager cannot see salary because the field policy hides it for the Manager role, and shows the role and policy responsible.

## 6. Security and privacy administration (SEC)

- **FR-ADM-SEC-01** A security dashboard shall show failed logins, locked accounts, MFA coverage, privileged users, API keys near expiry, stale accounts, license state and recent security events.
- **FR-ADM-SEC-02** API client management shall create clients and keys with scopes, expiry, IP allow lists and rate limits; show the secret once; rotate, revoke, and show last use and usage statistics.
- **FR-ADM-SEC-03** Alert rules shall notify designated users on configurable security events (for example repeated failures, new admin, bulk export, permission change, license change).
- **FR-ADM-SEC-04** An encryption and keys page shall show key status (version, rotation date, next rotation) and allow initiating rotation where the deployment mode permits, with progress and audit.
- **FR-ADM-SEC-05** Data classification shall let admins mark fields as sensitive, which drives masking, audit of views and export approval.
- **FR-ADM-SEC-06** Privacy tools shall handle data subject requests: locate personal data across modules for a person, export it, and execute erasure or anonymization subject to retention rules and legal holds, with a case record and audit.
- **FR-ADM-SEC-07** Export controls shall configure which exports need approval, row limits, and watermarking.
- **FR-ADM-SEC-08** A login page and notification branding page shall set tenant branding, terms notices, and a login banner.
- **FR-ADM-SEC-09** Support access grants raised by the vendor shall appear for tenant admin approval with scope, reason and duration, can be revoked at any time, and every action taken under a grant is shown in the audit viewer.
- **AC:** a tenant admin approves a two-hour vendor support grant limited to configuration data; the grant expires automatically and the audit viewer lists every action taken under it.

## 7. Organization administration (ORG)

- **FR-ADM-ORG-01** Company setup shall capture legal name, multilingual names, country, base and group currency, registrations (tax IDs and others with validity dates), bank accounts, default language, calendars and letterhead, with country-pack validation of formats.
- **FR-ADM-ORG-02** Branch and location screens shall support address, map pin, time zone, and operating hours.
- **FR-ADM-ORG-03** The organization chart editor shall show units as a tree and a diagram, support drag and drop re-parenting with effective dates, bulk import, and compare versions as of two dates.
- **FR-ADM-ORG-04** Unit types, labels and hierarchy kinds shall be maintainable by the tenant, including renaming standard terms per language.
- **FR-ADM-ORG-05** The fiscal calendar manager shall create years and periods from patterns, adjust period dates before use, open and close periods by module with checks and a closing checklist, and show who closed what.
- **FR-ADM-ORG-06** A company switcher shall be available to users with multiple companies, with the active company clearly shown on every screen.
- **FR-ADM-ORG-07** Company-level settings overrides shall be visible from the company page with a link to the settings center.
- **AC:** an admin creates two companies with different currencies and fiscal calendars, a five-level organization with matrix reporting, and closes the first period of one company without affecting the other.

## 8. Configuration studio (STU)

The studio is the heart of configurability. Every designer follows the same lifecycle: draft, validate, preview, test in sandbox, publish (with maker-checker if configured), monitor, and roll back.

### 8.1 Common designer requirements

- **FR-ADM-STU-01** Each designer shall autosave drafts, validate continuously with plain-language errors, show a live preview with sample data, and list impacted objects before publishing.
- **FR-ADM-STU-02** Each object shall be versioned with comparison between versions and one-click revert to a prior version.
- **FR-ADM-STU-03** Each designer shall support export and import of its objects as configuration packages and as JSON for support purposes.
- **FR-ADM-STU-04** Designers shall be permission-controlled separately (for example a role can edit forms but not workflows).
- **FR-ADM-STU-05** All text in a designer (labels, help, messages) shall be translatable per language with a side-by-side translation view.

### 8.2 Custom fields and objects

- **FR-ADM-STU-10** The field designer shall list fields by entity, with type, usage, status and promoted-index flag; creation shall use a guided form for types, validation (min, max, pattern, list), default, uniqueness, conditional visibility and requirement, and role permissions.
- **FR-ADM-STU-11** Option lists shall be managed centrally with effective dates, translations, ordering and import.
- **FR-ADM-STU-12** The object designer shall create custom objects with fields, relationships, numbering, status model, permissions, forms and lists in one guided flow, with generated screens and API documentation available immediately.
- **FR-ADM-STU-13** Retiring a field or object shall show the data volume and dependencies and soft-retire with an explicit later purge. (M)
- **FR-ADM-STU-14** The field designer shall warn about performance impact when enabling filters or sorts on custom fields in large tables and offer promotion to indexed storage. (S)

### 8.3 Forms, lists and menus

- **FR-ADM-STU-20** The form designer shall provide drag and drop layout of sections, tabs, columns and fields, conditional sections and fields (by value, role, status), read-only and required rules, field help, and separate mobile variants.
- **FR-ADM-STU-21** The form designer shall configure line-item grids (columns, order, defaults, inline calculations, totals) for document entities.
- **FR-ADM-STU-22** The list designer shall configure default columns, filters, grouping, sorting, totals, row colors by condition, row actions, bulk actions, and quick filters.
- **FR-ADM-STU-23** Layouts shall be assignable per role, company or branch, with a visible precedence order and a test-as-role preview.
- **FR-ADM-STU-24** The menu designer shall arrange navigation per role with groups, ordering, icons, links to reports, dashboards and external URLs, and hide entries by entitlement and permission automatically.
- **FR-ADM-STU-25** Labels and terminology shall be editable in a searchable table with per-language values and a view of where each label appears.

### 8.4 Status models and numbering

- **FR-ADM-STU-30** The status model designer shall display states and transitions as a diagram, set who may transition, required fields per state and automatic actions, and shall block removal of states with locked meanings.
- **FR-ADM-STU-31** The numbering designer shall build patterns from tokens (prefix, company code, branch code, year, month, sequence), show example numbers, set reset rule and gap-free option, and show the next number and void history.
- **FR-ADM-STU-32** Changes to numbering after use shall create a new version applying from a chosen date and never renumber existing documents.

### 8.5 Workflow designer

- **FR-ADM-STU-40** The workflow designer shall provide a visual canvas for steps, conditions, parallel branches, approver resolution, escalation and automated actions, with a palette of step types and templates from packs.
- **FR-ADM-STU-41** Approver resolution shall be configured through pickers (user, role, hierarchy level, document field, expression) with a preview of who would be resolved for a sample document.
- **FR-ADM-STU-42** A simulator shall run the workflow against sample or recent documents, show the route taken and resolved approvers, and flag steps that cannot resolve an approver.
- **FR-ADM-STU-43** An instance monitor shall show running instances by definition, with stuck or overdue items, the ability to reassign, and migration of running instances to a new version when allowed.
- **FR-ADM-STU-44** Delegation rules and out-of-office settings shall be administrable centrally and by users for themselves.
- **FR-ADM-STU-45** Workflow analytics shall show cycle times, bottleneck steps, rejection rates and SLA breaches.

### 8.6 Rules editor

- **FR-ADM-STU-50** The rules editor shall offer a guided condition builder, a decision-table editor, and an expression editor with autocomplete, function help, syntax checking and a plain-language rendering.
- **FR-ADM-STU-51** Each rule shall have saved test cases with expected results, a run-all-tests action, and a trace view showing which branch produced the result.
- **FR-ADM-STU-52** The editor shall show where each rule is used and block deactivation of rules in use unless a replacement is chosen.
- **FR-ADM-STU-53** Rules in sensitive contexts (posting, payroll, tax) shall require passing tests and maker-checker to activate.

### 8.7 Templates and notifications

- **FR-ADM-STU-60** The template designer shall edit print and letter templates visually with fields, tables, conditionals, images, barcodes and QR codes, header and footer, page setup, language variants, and preview against real sample records.
- **FR-ADM-STU-61** Template assignments shall be by document type, company, branch and language, with default fallbacks.
- **FR-ADM-STU-62** The notification designer shall configure events, audiences, conditions, channels, templates per language, throttling and mandatory flags, with test send.
- **FR-ADM-STU-63** A delivery log shall show messages with status, retries and failure reasons, and allow resend.
- **AC:** an admin builds a three-step approval for purchase requisitions with a threshold condition, tests it in the simulator on five sample documents, assigns it to one company, and publishes it through maker-checker; running documents stay on the previous version.

## 9. Settings center (SET)

- **FR-ADM-SET-01** The settings center shall list all settings by module and area with search, showing name, description, current effective value, source layer, default, last change and who changed it.
- **FR-ADM-SET-02** Setting editors shall be typed (toggle, number, choice, list, account or entity picker, expression) with validation, units, and help text in the user's language.
- **FR-ADM-SET-03** A layered view shall show the value at product, pack, tenant, company and role levels with override and reset-to-inherited actions.
- **FR-ADM-SET-04** Settings requiring approval shall create a change request with maker-checker; others apply immediately with audit.
- **FR-ADM-SET-05** Compare shall show setting differences between two companies or two environments, with the ability to copy values.
- **FR-ADM-SET-06** Settings history shall be browsable and any change reversible.
- **FR-ADM-SET-07** Feature flags shall be listed with description, scope and state, with owner and expected removal date for temporary flags. (S)
- **FR-ADM-SET-08** A recommended-settings advisor shall flag risky or inconsistent combinations (for example gap-free numbering with manual numbering, base currency changes after posting) and suggest fixes. (S)
- **FR-ADM-SET-09** Import and export of settings shall be supported through configuration packages.

## 10. Localization administration (LOC)

- **FR-ADM-LOC-01** Language management shall enable languages, set the default and fallback, and show translation completeness per module.
- **FR-ADM-LOC-02** A translation workbench shall list strings with source and target side by side, status (missing, machine-suggested, reviewed), export and import in standard formats, and optional AI-assisted suggestions that require review.
- **FR-ADM-LOC-03** Currency management shall maintain currencies and rate types, rate tables with manual entry and import, rate source connectors, and a rate gap report.
- **FR-ADM-LOC-04** Calendar management shall maintain holiday calendars by company, branch and location, import holidays from packs, and manage Hijri adjustments by method and manual overrides.
- **FR-ADM-LOC-05** Format settings shall set date, time, number, address and name formats per company and user default.
- **FR-ADM-LOC-06** The administrator shall be able to preview any screen or document in a chosen language and direction.

## 11. Integration administration (INT)

- **FR-ADM-INT-01** The API console shall list clients and keys (SEC), show interactive API documentation by module and version, and a sandbox for trying calls.
- **FR-ADM-INT-02** The webhook manager shall register endpoints, choose events, show signing secrets once, test deliver, and display delivery logs with retry, replay and dead-letter handling.
- **FR-ADM-INT-03** The connector manager shall list available connectors (banks, tax portals, e-invoicing, biometric devices, messaging, accounting export), with configuration forms, credential storage, health checks, last run, and logs.
- **FR-ADM-INT-04** The import template designer shall map source columns to fields with transformations, lookups, defaults and validations, test with a sample file and save templates for reuse and schedule.
- **FR-ADM-INT-05** The file exchange manager shall configure scheduled inbound and outbound SFTP or folder jobs with archiving and error handling.
- **FR-ADM-INT-06** An integration monitor shall show volumes, error rates, latency and recent failures per client, connector and webhook with alerts.
- **FR-ADM-INT-07** The Accounting Export connector shall let admins map events and accounts to an external accounting format when Finance is not licensed.

## 12. Data management (DAT)

- **FR-ADM-DAT-01** The import center shall run imports of master and transactional data through templates, show validation results before commit, process asynchronously with progress, produce error files, and support rollback of a batch where feasible.
- **FR-ADM-DAT-02** Bulk update shall let permitted admins change fields on filtered record sets with preview, approval for large changes, and audit.
- **FR-ADM-DAT-03** The duplicate manager shall find and merge duplicate partners, items and employees using configurable match rules, with a review queue and merge audit.
- **FR-ADM-DAT-04** The export center shall run permitted exports with approval rules, row limits, watermarking, and a log; exports of sensitive data require reason capture.
- **FR-ADM-DAT-05** An AI-assisted migration assistant shall propose column mappings and flag data issues for human review before import. (S)
- **FR-ADM-DAT-06** Archival management shall show data volumes by module, apply retention rules, and run archive jobs with verification.
- **FR-ADM-DAT-07** Environment management shall create sandbox or UAT copies with masking profiles, refresh them on request, and compare configuration between environments before promotion.
- **FR-ADM-DAT-08** Backup status shall show last successful backups, restore points and verification results; on-premise installs shall offer start, schedule and restore-to-sandbox actions subject to permission.
- **FR-ADM-DAT-09** A portable tenant export shall package data and configuration for exit or migration, with integrity checks.
- **AC:** a 50,000-row item import runs in the background, reports 143 errors in a downloadable file, commits the valid rows after confirmation, and rolls back the batch if the admin chooses within the allowed window.

## 13. Operations administration (OPS)

- **FR-ADM-OPS-01** The job center shall list scheduled, queued, running, failed and completed jobs with filters, progress, logs, results, rerun, cancel, and schedule editing for permitted jobs.
- **FR-ADM-OPS-02** The audit viewer shall search the change log, access log and security events by user, record, entity, date, action and source (UI, API, import, job, AI), show field-level differences, export with approval, and verify chain integrity on demand.
- **FR-ADM-OPS-03** The record history view (from any record) shall show the same data scoped to that record for users with permission.
- **FR-ADM-OPS-04** The notification log shall show sent, failed and queued messages with resend.
- **FR-ADM-OPS-05** A system health page shall show service status, queue depth, outbox lag, failed postings, storage use, database size, slow queries summary, background worker status and the last backup, with alert subscriptions.
- **FR-ADM-OPS-06** The posting exception queue (provided by the owning modules) shall be reachable from operations, showing failed cross-module postings with reason, correction and replay actions.
- **FR-ADM-OPS-07** The license and usage page shall show edition, licensed modules, limits and consumption, expiry and maintenance dates, state (active, warning, grace, read-only, locked), upload of a new license file, and usage statements per month.
- **FR-ADM-OPS-08** Announcements shall let admins post banners and messages to all users, roles or companies with schedule and acknowledgement tracking. (S)
- **FR-ADM-OPS-09** A release notes and version page shall show installed module versions, available updates (subject to maintenance entitlement), and the change log for each version, with on-premise update bundle upload and pre-checks.
- **FR-ADM-OPS-10** Scheduled report delivery administration shall show all schedules with owners, recipients and failures.

## 14. Packs and configuration packages (PKG)

- **FR-ADM-PKG-01** The pack manager shall list installed packs with version, contents summary, compatibility, overrides detected, and available updates with release notes.
- **FR-ADM-PKG-02** Pack installation and upgrade shall show a preview of added, changed and conflicting items; conflicts shall be resolved item by item (keep tenant version, take pack version, merge), and the result applied through maker-checker.
- **FR-ADM-PKG-03** The package manager shall export selected configuration objects (or everything) as a package with dependency detection, import packages with validation, and show diffs against the current environment.
- **FR-ADM-PKG-04** Promotion between environments shall run validation and automated tests, take a rollback snapshot, apply after approval, and show a result report.
- **FR-ADM-PKG-05** Rollback shall restore the snapshot of configuration without touching transactional documents, with a report of what changed back.
- **FR-ADM-PKG-06** Implementation partners shall be able to maintain their own template packs and apply them to tenants they are authorized for. (S)

## 15. AI controls (AIC)

- **FR-ADM-AIC-01** AI settings shall switch AI on or off per tenant, module, user group and skill, and show which skills are available under the entitlement.
- **FR-ADM-AIC-02** Data policy shall define which data classes may be sent to an external provider, which provider and region apply, whether a local model is used, and whether retrieval over documents is enabled.
- **FR-ADM-AIC-03** The interaction log shall show AI conversations and tool calls with user, skill, inputs, outputs, confirmations, outcomes and token use, with filters and export.
- **FR-ADM-AIC-04** Usage and limits shall show tokens and calls by module and month against the entitlement, with alerts.
- **FR-ADM-AIC-05** The configuration assistant shall be accessible from every designer, producing drafts that appear as proposed changes with diff and sandbox test, never applied directly.
- **FR-ADM-AIC-06** Admins shall be able to disable AI for specific sensitive modules or fields (for example payroll data) regardless of user group.
- **FR-ADM-AIC-07** A feedback mechanism shall let users rate AI responses, flag problems, and route flagged items to admins.

## 16. Platform Console (vendor) (PLT)

- **FR-ADM-PLT-01** The tenant list shall show name, status, region, cluster, edition, modules, users, storage, license state, version, last activity and health, with filters and export.
- **FR-ADM-PLT-02** A provisioning wizard shall create a tenant from a template: details, region and cluster selection, modules and packs, entitlement, domain, administrator, and smoke test result, with progress and failure recovery.
- **FR-ADM-PLT-03** Entitlement management shall create and edit entitlements (editions, modules, limits, term, maintenance expiry, add-ons) with history, effective dates and approval, and link to license issuance for on-premise customers.
- **FR-ADM-PLT-04** The license issuer (offline tool) shall produce signed license files from an entitlement, record issued licenses, and support revocation lists and re-issue; the console shall display issued licenses without holding private keys.
- **FR-ADM-PLT-05** The fleet dashboard shall show module versions per tenant, pending migrations, migration runs with canaries and results, failed tenants in maintenance, and the ability to retry or roll back per tenant.
- **FR-ADM-PLT-06** The pack catalog shall publish signed packs, manage versions and compatibility, and show which tenants run which pack versions.
- **FR-ADM-PLT-07** Usage and billing support shall show monthly meters per tenant (users, employees, companies, storage, API calls, AI tokens), produce usage statements, and export to a billing system.
- **FR-ADM-PLT-08** Health and capacity views shall show cluster capacity, database sizes, slow tenants, queue backlogs, and alerts, with tenant move planning.
- **FR-ADM-PLT-09** Support tools shall request tenant support grants, show active grants, and open a time-boxed, scoped view of tenant configuration or data only while a grant is active, with all actions in the platform and tenant audit logs.
- **FR-ADM-PLT-10** Platform users and roles shall be managed with MFA mandatory, separation of duties between provisioning, licensing and support roles, and a complete platform audit.
- **FR-ADM-PLT-11** Tenant lifecycle actions (suspend, resume, move, export, offboard, delete) shall be available with approvals, legal-hold checks and retention windows.
- **FR-ADM-PLT-12** For on-premise installs, a reduced console shall show license state, upload of new license, installed versions, update bundle application, health, and diagnostic bundle export.

## 17. Experience standards for admin screens (UXA)

- **FR-ADM-UXA-01** Every list and designer shall be usable by keyboard, meet WCAG 2.1 AA contrast and screen-reader labeling, and support right-to-left languages.
- **FR-ADM-UXA-02** Destructive and wide-impact actions shall require confirmation that states the impact (counts of affected users or records) and offer undo where technically possible.
- **FR-ADM-UXA-03** Empty states shall explain what the area is for and offer the next action or an import option.
- **FR-ADM-UXA-04** Error messages shall say what happened, why, and how to fix it, with a correlation id for support.
- **FR-ADM-UXA-05** Long forms shall autosave drafts and warn before losing changes.
- **FR-ADM-UXA-06** Admin screens shall work on tablets; critical admin approvals and monitoring shall work on phones.
- **FR-ADM-UXA-07** Help shall be contextual, searchable, and available in the three launch languages, and shall include guided tours for first-time administrators.
- **FR-ADM-UXA-08** Large lists shall load progressively (virtualization and server-side paging) and keep responsiveness within the targets of document 2, GR-01.

## 18. Coverage against common ERP administration practice

This checklist compares this specification with administration features commonly expected from mature ERP products (for example SAP, Oracle, Microsoft Dynamics, NetSuite, Odoo and Zoho). It is a planning aid, not a comparison of specific products.

| Capability | Covered by | Wave |
| --- | --- | --- |
| Roles, permissions, data scopes, field security | ROL | A |
| Segregation of duties and access reviews | ROL-08, ROL-10 | A, B |
| SSO, MFA, directory sync, SCIM | USR-22, USR-23, IAM-06 | A |
| Audit trail with field-level history | OPS-02 | A |
| Custom fields, objects, layouts, status models | STU | A |
| Visual workflow designer, simulator, SLAs | STU-40 to STU-45 | A thin, B deep |
| Rules and decision tables | STU-50 to STU-53 | A |
| Print and letter template designer | STU-60 | A |
| Multi-company, multi-currency, multi-language | ORG, LOC | A |
| Hijri and Gregorian calendars, RTL | LOC | A |
| Sandbox, UAT, promotion of configuration | DAT-07, PKG | A |
| Data import, export, duplicates, archival | DAT | A |
| Integration: REST, webhooks, connectors, file exchange | INT | A |
| Usage and license management | OPS-07, PLT-03 | A |
| AI controls and logging | AIC | A thin |
| Privacy and data subject requests | SEC-06 | A, B |
| Tenant lifecycle and fleet operations | PLT | A |
| Delegated administration and partner access | ONB-05, SEC-09, PKG-06 | A, B |
| Mobile administration (approvals, monitoring) | UXA-06 | B |
| Marketplace for third-party extensions | Not in scope for wave A | D |
| Low-code app builder beyond custom objects | Not in scope for wave A | D |

## 19. Acceptance and testing

- Each requirement maps to an automated end-to-end test where the designer or screen is exercised through the browser, including role-restricted variants.
- Each designer is tested for the lifecycle: draft, validate, preview, publish, revert, and dependency warnings.
- Permission tests confirm admin screens are invisible and APIs return denial for users without the corresponding permission.
- Configuration scripts for a non-developer persona (an implementation consultant) must complete the setup scenarios in sections 3, 5 and 8 within defined time targets.
- Accessibility audits run on all admin screens before each release.
- Usability review with at least three real administrators before first production release; findings tracked to closure.

## 20. Assumptions and open items

**Assumptions:** the admin consoles are part of the same web application as the business screens, with separate permissions; the Platform Console is a separate deployable restricted by network and MFA; designers store their definitions as versioned data in the kernel schemas of document 3.

**Open decisions:** visual workflow canvas library (build or adopt an open-source one); form designer technology; scope of the AI configuration assistant in wave A; SCIM provisioning in wave A or B; whether partner-authored packs are in scope for the first customers; billing system to integrate with the usage statements.

**Next documents:** 5 Finance Functional Specification, then 6 HRMIS Functional Specification (revised), then 12 Claude Code Operating Manual.

## 21. Additions from the consistency review (document 14, decisions D-20 and D-25)

### 21.1 External portals, devices, search and printing (EXT)

- **FR-ADM-EXT-01** The portal administration area shall manage portal types and realms, branding and custom domains, languages, invitations and self-registration approval, external users and their partner links, inactivity expiry and access reviews, and show portal usage against the portal.external\_users entitlement (kernel PRT).
- **FR-ADM-EXT-02** Partner administrators shall have a reduced console to manage their own external users within limits set by the tenant.
- **FR-ADM-EXT-03** The device management area shall list registered devices with user, platform, app version and last sync, allow lock, wipe and removal, enforce minimum app versions and per-user device limits, and show sync health, backlog and conflicts (kernel SYN).
- **FR-ADM-EXT-04** Sync scope administration shall let administrators configure what each role may hold offline, size limits and refresh policies per module.
- **FR-ADM-EXT-05** Search administration shall show index status per entity type, allow rebuilds, manage excluded fields and synonyms, and test search as a chosen role (kernel SRC).
- **FR-ADM-EXT-06** Collaboration settings shall configure which record types allow comments and tasks, mention and email-reply behavior, retention and legal hold (kernel COL).
- **FR-ADM-EXT-07** Printer administration shall register printers and print agents per location and type, assign routing by document type, monitor queues, retry failed jobs and review print logs (kernel DOC-09 to DOC-11).

### 21.2 Analytics and AI platform administration

- **FR-ADM-OPS-11** Administration of the semantic model, certification queue, schedules, refresh jobs, data quality results, export approvals, AI skills, provider routing, quotas and the kill switch shall be provided as specified in document 9, sections 21 (ADM) and 10 (GOV), and shall appear in the Operations and AI controls sections of the console.
