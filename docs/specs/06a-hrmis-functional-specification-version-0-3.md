# HRMIS Functional Specification: Version 0.3

Nov 14, 2026 · @Ibrahim

Version 0.3 extends the HRMIS specification with geo-attendance, employee task and field activity management, and a complete Android-first mobile workspace for employees, managers, HR and senior management, while keeping every v0.2 requirement.

## Document control

| Version | Date | Description |
| --- | --- | --- |
| 0.1 | 14 October 2026 | First HR MIS functional specification: platform layer and 18 HR modules for Pakistan and the United States |
| 0.2 | 10 November 2026 | Revised and aligned with the ERP platform: kernel boundaries, Finance integration, Saudi Arabia pack, wave plan, FR-HRM identifiers (document 6 of the planning set) |
| 0.3 | 14 November 2026 | This version: geo-attendance, task and field activity management, complete mobile employee self-service, manager, supervisor, HR and senior management workspaces, offline operation, mobile security and privacy, APIs, data model, roles, workflows, reporting, non-functional requirements, acceptance scenarios and an open decisions register |

Baseline: HRMIS Functional Specification v0.2 remains unchanged as a separate document and is the authoritative baseline for existing requirements. Version 0.3 is a single consolidated document that contains all v0.2 content with the new capabilities integrated into the matching sections. Status: draft for review. Companion documents: Architecture and Foundation (1), Platform Kernel (2), Data Architecture (3), Admin Console (4), Finance (5), Procurement (7), Inventory and Asset Management (8), Analytics and AI (9), Supply Chain (10), Sales (11), Claude Code Operating Manual (12), UX and Design System (13), Consistency Review (14), Editions and Starter Edition (15).

## Executive summary of version 0.3

Version 0.3 adds four capabilities and integrates them into the existing structure rather than appending separate addenda:

1. **Advanced geo-attendance** (section 9.6 to 9.11): managed attendance locations with circular and polygon geo-fences, four configurable location policy modes (strict, exception, unrestricted capture, field duty), full event data capture with capture time versus server receipt time, exception handling, live dashboards and maps, and governance of location data.
2. **Employee Task and Field Activity Management** (section 9A): task creation and assignment, a ten-state lifecycle, employee response through self-service and mobile, event-based GPS and evidence capture where configured, supervisor review, visibility and reporting.
3. **A complete mobile employee self-service application** (sections 12A to 12G): Android first and iOS later, on the same HRMIS backend, business rules, workflows and authorization model as the web portal; secure authentication and device management; employee features across attendance, leave, profile, documents, payroll, tasks, requests, learning, performance, directory and exit.
4. **Role workspaces for managers, supervisors, HR and senior management** (section 12B), with notifications (12C), offline-first operation (12D), mobile security and privacy (12E), navigation (12F) and APIs (12G).

Supporting updates: the logical data model (27.2), roles and data scopes (28.2), integration and API catalogue (29.2), events (30.2), workflow catalogue (30A), reporting (13A), non-functional requirements (31.2), privacy and compliance (34), release roadmap M1 to M4 (35), acceptance scenarios (36), open decisions and assumptions register (37), requirement traceability (38), quality review and change summary (39) and the impact on other planning documents (40).

Design commitments that run through the document:

- The mobile application is a client of HRMIS, not a second HR system: web and mobile use the same APIs, rules and Kernel authorization.
- Location capture is event-based and tied to configured business activity; continuous background tracking is not provided by default and is listed as Won't have in this release.
- Shared platform capabilities stay in the Platform Kernel. Eight extension requests to the Kernel are stated in section 3.3; HRMIS does not rebuild identity, workflow, notification, localization, reporting or tenancy services.
- Country-specific rules (privacy, working time, statutory) live in packs and are validated by local experts; none is hardcoded.
- All v0.2 requirement identifiers are retained. Requirements that were extended are marked (extended v0.3); new ones (new v0.3). Thresholds and policies not given by the baseline are proposals recorded in the register of section 37, not approved commitments.

## 1. Introduction

### 1.1 Purpose

This document defines what the HRMIS module shall do, in testable functional terms, as a module of the ERP platform. It keeps the substance of the v0.1 specification (18 HR modules, Pakistan and United States readiness), the v0.2 alignment (platform capabilities come from the kernel, accounting comes from Finance or an accounting export, a Saudi Arabia pack) and, in v0.3, adds location-aware workforce operations and a complete mobile experience.

### 1.2 What changed from v0.1 to v0.2

| Area | v0.1 | v0.2 |
| --- | --- | --- |
| Platform layer (Phase 0) | Tenancy, metadata, workflow, rules, security, templates, notifications, reporting and so on defined inside the HR spec | Removed. Defined once in the Platform Kernel Specification and Admin Console Specification; HRMIS only states how it uses them |
| Product positioning | Standalone HR product | Module of the ERP, sellable alone, with other modules, or with an external accounting system |
| Accounting | Out of scope; journal file export | Payroll, loans, advances and expense events post to Finance through posting rules, or go to the Accounting Export connector when Finance is not licensed |
| Assets and facilities (M14) | Full asset register inside HR | HR keeps custody, issue and return workflows; the asset register belongs to the Assets module (wave B) with a lightweight HR register until then |
| Expenses and travel | Inside payroll phase | HRMIS owns employee claims and advances; Finance owns payables and payments |
| Phases | P0 to P5 | Waves aligned to the ERP: A1 core HR, A2 payroll (Pakistan), B talent and Saudi pack, C United States, D intelligence and ecosystem |
| Markets | Pakistan, then United States | Pakistan, United States, Saudi Arabia |
| Hosting | On-premise undecided | On-premise at launch; one build for SaaS and on-premise |
| Mobile | Native or cross-platform undecided | Cross-platform with offline support (Architecture section 4.9) |
| IDs | FR-P1-EMP-01 | FR-HRM-EMP-01; numbering kept for traceability; new requirements are marked (new) |

### 1.3 What changed from v0.2 to v0.3

| Area | Change | Where |
| --- | --- | --- |
| Attendance | ATT-01, ATT-04, ATT-05, ATT-18 and ATT-19 extended; new locations, policy modes, event capture, exceptions, dashboards, maps and location data governance (ATT-23 to ATT-82) | Section 9 |
| Tasks and field activity | New capability TFA (TFA-01 to TFA-70) | Section 9A |
| Self-service | SSP-04 extended; new SSP-09 to SSP-14; complete mobile ESS (MOB) | Sections 12 and 12A |
| Workspaces | New manager, supervisor, HR and senior management workspaces (WSP) | Section 12B |
| Notifications, offline, security, UX, APIs | New areas NTF, OFF, MSEC, MUX, API | Sections 12C to 12G |
| Reporting | RPT-05 to RPT-10 and a reporting catalogue | Sections 13 and 13A |
| Global requirements | GHR-02 extended; GHR-11 to GHR-16 added | Section 4 |
| Platform boundary | Eight Kernel extension requests (KER-01 to KER-08) | Section 3.3 |
| Model, roles, integrations, events, workflows | Extended with new entities, permissions, APIs, events, workflows | Sections 27 to 30A |
| NFR, privacy, roadmap, tests, decisions, traceability | New sections and additions | Sections 31.2, 34 to 40 |

### 1.4 Scope and sequence

| Wave | Content | Exit criteria |
| --- | --- | --- |
| A1 | Policy framework, employee records, organization and positions, documents and letters, attendance, leave, basic onboarding and offboarding, self-service and mobile, basic dashboards | Pilot customer runs a full month of attendance, leave and self-service |
| A2 | Payroll, loans and advances, expenses, compensation and benefits and statutory compliance (Pakistan) with postings to Finance | Two payroll cycles run in parallel with the customer's process and reconcile |
| B | Recruitment, advanced onboarding, performance, learning, employee relations, asset custody, Saudi Arabia pack | One hiring cycle and one appraisal cycle completed by pilot customers |
| C | United States readiness (partner payroll and benefits) | First US tenant completes a payroll cycle and new-hire compliance flow |
| D | Advanced analytics, workforce planning and succession, extensions | AI features pass bias, privacy and accuracy review |

Mobile releases map onto these waves as a proposal pending approval of the roadmap (section 35):

| Release | Content | Wave alignment | Depends on |
| --- | --- | --- | --- |
| M1 Android ESS foundation | Secure login and device registration, home, profile, attendance, leave, notifications, basic manager approvals | With wave A1 | Kernel identity, device registry and sync (SYN), notifications; EMP, ATT, LVE |
| M2 Advanced workforce operations | Geo-attendance, attendance exceptions, field duty, tasks with GPS and evidence, supervisor workspace, location dashboards | After the A1 core (A1 extension) | KER-01, KER-02, KER-03, KER-05, KER-08; workflow; POL |
| M3 Complete Android ESS | Payslips and payroll self-service, HR helpdesk, documents, learning and performance where licensed, executive dashboards, offline enhancements | With wave A2 and wave B | PAY (A2); LND and PRF (B); analytics |
| M4 iOS application | Equivalent core features on iOS | After M3 | Shared APIs; platform security parity |

Mobile delivery cannot precede the backend services it depends on.

### 1.5 Conventions

- Requirement IDs follow FR-HRM-\<area>-\<number>. Existing IDs are preserved. New areas use blocks of numbers by sub-area so that IDs stay unique and readable (for example MOB-20 to MOB-29 for authentication and devices).
- Priority: (M) Must have, (S) Should have, (C) Could have, (W) Won't have in this release, recorded to prevent scope creep. Unmarked requirements are (M).
- Change classification: (extended v0.3) a v0.2 requirement that was widened without losing content; (modified v0.3) a v0.2 requirement whose text changed; (new v0.3) added in this version. The tag (new) kept from v0.2 means added in v0.2; unmarked means unchanged since v0.2.
- Dependencies are stated as references to requirement IDs, Kernel extension requests (KER-nn), or other planning documents.
- Every configurable behavior is reachable through the admin UI and the public API with the same permissions (document 2).
- Statutory values and country rules are pack content, verified by qualified local practitioners before release; nothing statutory is hard-coded.
- Thresholds and policies not provided by the baseline are proposals, marked as such and recorded in the open decisions register (section 37).

### 1.6 Out of scope

- General-ledger accounting (provided by Finance or an external system).
- Authoring of learning content (the module links to external content).
- An in-house United States tax calculation and filing engine (partner integration in wave C).
- Legal advice; each customer remains responsible for confirming statutory settings.
- Continuous background location tracking of employees and any monitoring beyond configured business events (Won't have, section 34).
- Storage of biometric templates in HRMIS; facial recognition for attendance or identity decisions.
- A separate mobile HR system: the mobile application uses the HRMIS backend.
- Vehicle and asset tracking by telematics (belongs to Supply Chain and Assets).
- Automated disciplinary or compensation decisions based on location or task data.

## 2. Actors and roles

| Actor | Description |
| --- | --- |
| Tenant Admin | Configures the tenant (kernel and admin console) |
| System Administrator | Operates the installation (on-premise IT or platform operations): infrastructure, updates, backups, diagnostics; no business data access by default |
| HR Admin | Maintains employee data and runs HR processes |
| HR Business Partner | Advises units; data access scoped to assigned units |
| Payroll Officer and Payroll Approver | Prepare and approve payroll (maker-checker) |
| Employee | Uses self-service on web and mobile |
| Field Employee | An employee assigned to field duty or field tasks, with the location policy of the Field Duty mode |
| Line Manager | Approves requests, reviews team, runs appraisals, assigns tasks (new v0.3 scope) |
| Supervisor | Oversees attendance and tasks of a team or site; reviews exceptions and task submissions (new v0.3) |
| Department Head | Oversees a department; reviews at department level (new v0.3) |
| Regional Manager | Oversees several locations or branches in a region (new v0.3) |
| Senior Management | Executives with dashboards and authorized drill-down (new v0.3) |
| Recruiter, Interviewer, Candidate | Run and take part in hiring |
| Finance Approver | Approves loans, expenses and payroll postings |
| Auditor | Read-only access to data and audit logs |
| Implementation Partner | Configures on behalf of customers under delegated permissions |

Mobile and web roles are the same roles: a user's permissions and data scope do not depend on the client used (GHR-12).

## 3. HRMIS within the platform

### 3.1 Kernel capabilities used

| Kernel capability | Use by HRMIS |
| --- | --- |
| Organization | Companies (legal entities), branches, locations, organization units and hierarchies; HR adds positions and grades; attendance locations extend kernel locations |
| Identity and access | Users link optionally to employees; roles, data scopes (own, team, subtree, company), field security for salary and personal data; mobile sessions and device trust (KER-03, KER-07) |
| Settings, rules, workflow | Policy parameters, formulas and eligibility, approvals for every request type, exception routing and task review |
| Metadata | Custom fields, layouts, status models, custom objects on employees, positions, requests and tasks |
| Documents and templates | Employee document vault, letters, payslips, certificates, task evidence storage |
| Notifications | Reminders, approvals, alerts across channels including push |
| Localization | Urdu, Arabic, English; Hijri and Gregorian dates; holiday calendars |
| Parties | Employees as payees are referenced by employee id; banks and government authorities as partners |
| Reporting and analytics | Semantic layer, dashboards, scheduled reports, map components |
| Integration, jobs, entitlements, AI | Connectors, imports, scheduled processing, active-employee metering, AI skills |
| Offline sync framework (SYN) | Device registry, sync protocol, outbox, conflict rules, remote wipe (document 2, sections 27 to 31) |
| Collaboration (COL) | Comments on tasks and requests |
| Geospatial service (requested) | Geo-fence storage and validation (KER-01) |

### 3.2 Interaction with other modules

| Module | Interaction |
| --- | --- |
| Finance (optional) | Consumes PayrollRunFinalized, FinalSettlementApproved, ExpenseClaimApproved, LoanDisbursed (which covers loans and advances), LeaveLiabilityCalculated and GratuityAccrualCalculated events; publishes nothing it depends on; sends payment status back to HRMIS; budget and cost center references are optional |
| Accounting Export connector (when Finance is absent) | Maps the same events to journal files or an external accounting format |
| Procurement | Employee purchase requests remain in Procurement; HRMIS owns only expense claims |
| Assets (wave B) | Asset issue and return from HR creates custody records; unreturned assets feed offboarding clearance |
| Inventory | Uniform and consumable issuance may use Inventory when licensed |
| Analytics | HR semantic models and standard dashboards |
| Supply Chain | Field tasks may reference delivery or site activities; drivers are HRMIS employees; vehicle telematics stay outside HRMIS |

HRMIS hard-depends on the kernel only (decision D-10). Every Finance, Assets or Inventory integration degrades gracefully when the other module is not licensed.

### 3.3 Kernel extension requests (new v0.3)

The capabilities below are shared platform needs. HRMIS consumes them through defined interfaces; they are requested for the Platform Kernel (document 2) and are not rebuilt inside HRMIS.

| ID | Request | Kernel area | Used by |
| --- | --- | --- | --- |
| KER-01 | Geospatial service: store geo-fences (circle and polygon), compute distance and point-in-boundary tests with accuracy awareness, validate boundaries | New geospatial service | ATT-23 to ATT-34, TFA-37 to TFA-44 |
| KER-02 | Map component in the design system: markers, clusters, geo-fence overlays, accuracy circles, filters, bundled boundary data and optional tile providers | Design system (document 13, DSH-14) | ATT-67 to ATT-76, TFA-45 to TFA-54 |
| KER-03 | Device registry extension: trust state, attestation and risk signals (rooted or jailbroken device, mock-location indicators), per-user device limits, remote revoke | Offline sync, SYN-01 | MOB-20 to MOB-29, MSEC |
| KER-04 | Push notification device-token management and provider adapters | Notifications, NTF-01 | NTF-01 to NTF-16 |
| KER-05 | Data classification and retention rules for location data and evidence, with purge jobs, legal hold and audit | Documents DOC-04, Admin SEC-05 | ATT-77 to ATT-82, PRV |
| KER-06 | Mobile API conventions: versioning, idempotency keys, sync endpoints, error model | Integration, SYN-02 | API-01 to API-14 |
| KER-07 | Mobile session management: refresh-token rotation, device-bound sessions, remote logout, step-up authentication | Identity, IAM-04 | MOB-20 to MOB-29 |
| KER-08 | Large media pipeline: chunked and resumable upload, scanning, image compression, thumbnails, retention | Documents DOC-02, SYN-07 | TFA evidence, ATT evidence, MOB |

Status of each request: requested; to be accepted into document 2 (see section 40).

## 4. HR global requirements

| ID | Requirement |
| --- | --- |
| GHR-01 | The module shall support at least 50,000 employees per tenant, with payroll for 10,000 employees completing in under 15 minutes and attendance processing for 500 employees on three shifts for a month in under 5 minutes |
| GHR-02 | Data shall be classified as general personal, sensitive personal (national ID, bank account, salary, medical, disciplinary, union, self-identification, and, extended v0.3, precise location, device identifiers and task evidence such as photographs) and restricted (cases, investigations); sensitive and restricted fields shall use field-level encryption, masking by default, restricted roles, and access logging |
| GHR-03 | Every employee-related record that changes over time shall be effective-dated; as-of queries and timelines shall be available throughout |
| GHR-04 | A backdated change shall show an impact analysis (attendance, leave, payroll, benefits) before saving and flag affected periods for recalculation (new) |
| GHR-05 | Privacy tools shall support access, correction, export and erasure or anonymization within retention rules, legal holds, and statutory record-keeping periods defined by packs (new) |
| GHR-06 | Salary and compensation data shall be hidden from every role except those explicitly granted, including in reports, exports, search results, AI and notifications |
| GHR-07 | Multiple legal entities and countries shall be supported in one tenant with different calendars, policies and country packs |
| GHR-08 | Statutory formats and rules shall change only through staged pack releases with impact preview |
| GHR-09 | All HR actions on behalf of another person (HR applying leave for an employee, manager marking attendance) shall be audited with the acting user |
| GHR-10 | Employee self-service and approvals shall work on mobile and low-bandwidth connections |
| GHR-11 | Precise location, device identifiers and task evidence shall be collected only at configured business events, for documented purposes (purpose limitation and data minimization); continuous background tracking shall not be provided by default (new v0.3) |
| GHR-12 | Web and mobile clients shall use the same HRMIS APIs, business rules, workflows and Kernel authorization; no mobile-only authorization model or business rule shall exist (new v0.3) |
| GHR-13 | Every location-bearing record shall carry the event capture time, the server receipt time and the validation result, so offline and late submissions are distinguishable (new v0.3) |
| GHR-14 | Access to location history and evidence shall be restricted by role and data scope, logged, retention-limited and disclosed to employees through a notice and acknowledgement (new v0.3) |
| GHR-15 | Country-specific privacy, employment, working-time and statutory rules shall be configured through packs and validated by qualified local experts before release; none shall be hard-coded (new v0.3) |
| GHR-16 | Mobile capabilities that depend on optional modules (payroll, performance, learning, recruitment, asset custody) shall appear only when those modules are licensed and degrade gracefully otherwise (new v0.3) |

## 5. Policy framework (POL), wave A1

**Purpose:** assign the right rules to the right employees, and explain why. **Actors:** Tenant Admin, HR Admin.

- **FR-HRM-POL-01** Policy types shall include leave, attendance, shift, overtime, pay, expense, loan, benefits and performance; new types shall be definable. (S)
- **FR-HRM-POL-02** Policies shall be assigned by attribute rules (entity, location, grade, employee type, department, custom field) with precedence and effective dates.
- **FR-HRM-POL-03** A policy resolution view shall show which policy applies to an employee and why, including the rule and layer responsible.
- **FR-HRM-POL-04** A country pack shall contain holidays, leave statutes, working-time limits, statutory deductions, tax tables, forms, reports and field requirements.
- **FR-HRM-POL-05** Pack updates shall be staged: preview which employees' results would change, approve, and activate on an effective date.
- **FR-HRM-POL-06** Tenants shall override any pack value; overrides survive pack updates and are listed in a diff view.
- **FR-HRM-POL-07** Policy changes shall be simulated against sample or current employees before activation and shall use the kernel rules engine for conditions and formulas. (new)
- **AC:** installing a pack update shows which employees' results would change and requires approval before activation.

In v0.3 the policy framework also carries the attendance location policy modes (section 9.7), task location policies (section 9A.4) and mobile device and session policies (section 12A), resolved by the same attribute-rule mechanism.

## 6. Employee records (EMP), wave A1

**Purpose:** a single, effective-dated source of truth for every person employed. **Actors:** HR Admin, Employee, Line Manager, Auditor.

- **FR-HRM-EMP-01** The module shall maintain an employee master with personal, contact, address, family and dependents, emergency contacts, education, experience, skills, languages and certifications; all sections configurable per tenant.
- **FR-HRM-EMP-02** The module shall maintain an employment record: employee type (permanent, contract, probation, intern, daily wage, consultant, part-time), status, grade, position, manager, cost center, location, joining date, confirmation date and expected end date.
- **FR-HRM-EMP-03** Every change to employment data shall be effective-dated with a reason code (hire, promotion, transfer, demotion, grade change, acting charge, secondment, rehire, termination).
- **FR-HRM-EMP-04** Employee IDs shall be generated from configurable patterns per entity through kernel numbering, with manual override permission.
- **FR-HRM-EMP-05** National identifiers shall be captured with country-pack validation: Pakistan (CNIC 13 digits with format check and expiry, NTN, EOBI number, social security number, domicile), Saudi Arabia (national ID or iqama, GOSI number, nationality, sponsor), United States (SSN encrypted and masked, work authorization); duplicate identifiers within a tenant shall be flagged.
- **FR-HRM-EMP-06** Bank accounts per employee shall support multiple accounts, IBAN validation, split salary by percentage or amount, effective dates, and maker-checker on changes (aligned with kernel rules for partner bank accounts).
- **FR-HRM-EMP-07** HR shall create employees individually, by bulk import, or by conversion from an accepted offer (wave B).
- **FR-HRM-EMP-08** Employees shall submit data change requests through self-service; changes route through configurable workflow and apply on approval.
- **FR-HRM-EMP-09** Contract management shall cover start and end dates, renewal reminders and workflow, contract history and letters.
- **FR-HRM-EMP-10** Probation tracking shall follow policy: period, extension, early confirmation, reminders to manager and HR.
- **FR-HRM-EMP-11** The profile shall show a timeline of all changes, filterable by type, with an as-of date view.
- **FR-HRM-EMP-12** Bulk update shall change a field across a selected set with preview, approval and rollback.
- **FR-HRM-EMP-13** An employee shall be able to hold multiple concurrent assignments (for example two entities) with a primary flag. (S)
- **FR-HRM-EMP-14** Photo, ID card data and QR code generation shall be supported for ID cards. (S)
- **FR-HRM-EMP-15** The model shall separate Person (one per human, with a person ID) from Employment (one or more per person), so rehires, concurrent engagements and former employees link to one person without duplicate records. (new)
- **FR-HRM-EMP-16** Identifier types, their validation and visibility shall be configurable per country through packs and custom fields rather than fixed columns. (new)
- **FR-HRM-EMP-17** Employees shall optionally link to a kernel user; linking, unlinking and automatic deprovisioning on termination shall be configurable. (new)
- **FR-HRM-EMP-18** Duplicate detection on create and import shall match by identifier, name and date of birth, and bank account blind index, with a review queue and merge audit. (new)
- **FR-HRM-EMP-19** Employee data shall be exportable by the employee in a portable format on request, subject to approval rules. (new, S)
- **BR-01** A rehired employee links to the earlier person record and gets a new employment record when policy requires.
- **BR-02** Confirmation date cannot precede joining date; termination date cannot precede the last attendance or a payroll-locked period without an approved override.
- **BR-03** Dependents and emergency contacts are stored as separate records so benefits and notifications can use them.
- **AC:** hire an employee, promote with a future date, correct the promotion date backward, and see the as-of view, audit entries and the impact analysis flagging affected attendance or payroll at every step.

## 7. Organization and position management (POS), wave A1

**Purpose:** control structure, sanctioned strength and vacancies. Organization units, hierarchies and companies belong to the kernel (document 2, section 4); HRMIS adds positions, grades and jobs.

- **FR-HRM-POS-01** The tenant shall choose a model per entity: position-based (each employee fills a position) or headcount-based (budget per unit and job).
- **FR-HRM-POS-02** Positions shall carry title, grade, unit, reporting position, location, cost center reference, budgeted FTE, status (planned, open, filled, frozen, closed) and effective dates.
- **FR-HRM-POS-03** The module shall report sanctioned, filled and vacant strength per unit, grade and location.
- **FR-HRM-POS-04** An interactive organization chart shall display reporting lines with filters, search, zoom, print and export, including as-of and future-dated views.
- **FR-HRM-POS-05** A restructure tool shall stage multiple changes (move, merge, split, reassign) as a draft, show impact, and publish atomically on an effective date.
- **FR-HRM-POS-06** Grades and bands shall define pay ranges, leave entitlements and policy defaults used by the policy framework.
- **FR-HRM-POS-07** A job catalogue shall store descriptions, responsibilities, qualifications and competencies, versioned.
- **FR-HRM-POS-08** Organization units used by HRMIS shall be the kernel's units; restructures shall be coordinated so a unit change updates positions, employees and approvers consistently, and notify Finance of cost center mapping changes through events. (new)
- **FR-HRM-POS-09** When Finance is licensed, headcount cost plans shall be comparable to budgets by cost center. (new, S, wave B)
- **BR-04** Filling a position beyond its budgeted FTE is blocked unless an exception approval is recorded.
- **AC:** a department merge staged for the first of next month shows affected employees, positions and managers, and applies atomically on that date.

## 8. Documents and letters (DOC), wave A1

Document storage, templates and retention use the kernel Documents component; this section defines HR behavior.

- **FR-HRM-DOC-01** The document vault shall hold files per employee with categories, document types, issue and expiry dates, and access rules by role.
- **FR-HRM-DOC-02** HR shall request documents from employees with due dates; employees upload through self-service; HR verifies or rejects with reason.
- **FR-HRM-DOC-03** Expiring documents (national ID, visa, residence permit, licenses, certifications) shall trigger notifications at configurable intervals.
- **FR-HRM-DOC-04** Letters shall be generated from templates (offer, appointment, confirmation, increment, promotion, transfer, warning, show-cause, experience, salary certificate, NOC, resignation acceptance, relieving) with merge fields from any object, in the employee's language.
- **FR-HRM-DOC-05** Letters shall pass through an optional approval workflow, then be issued, stored, emailed and acknowledged by the employee.
- **FR-HRM-DOC-06** Policy documents shall be published to selected populations with versioning and acknowledgement tracking.
- **FR-HRM-DOC-07** Employees shall request letters (for example salary certificate) through self-service with automatic generation where the template allows.
- **FR-HRM-DOC-08** Salary-bearing letters and documents shall be generated only by roles with salary permission and shall be logged in the access log. (new)
- **AC:** a policy update published to one entity is acknowledged by 90 percent of employees within a defined period, with automated reminders and a report of non-acknowledgers.

## 9. Time and attendance (ATT), wave A1, with geo-attendance extensions in v0.3

**Purpose:** capture work time accurately across offices, shifts, sites and remote work, compute attendance outcomes under configurable rules, and, from v0.3, validate where work was recorded. **Actors:** Employee, Field Employee, Line Manager, Supervisor, HR Admin, Payroll Officer, Tenant Admin.

### 9.1 Capture

- **FR-HRM-ATT-01** Attendance shall be captured from web and mobile check-in and check-out, geo-fence validation, selfie capture, QR code, kiosk mode, biometric devices (fingerprint, face), card readers, manual entry and file import. (extended v0.3: mobile and web capture follows the location policy modes of section 9.7 and records the event data of section 9.8)
- **FR-HRM-ATT-02** A device management console shall register devices by site, store connection settings, monitor online status, and show last sync time and errors.
- **FR-HRM-ATT-03** Biometric integration shall support push and pull sync, offline buffering, duplicate removal and mapping of device user IDs to employees; a driver interface shall allow adding device models without core changes.
- **FR-HRM-ATT-04** Mobile capture shall work offline, store punches locally with device time and location, and sync when connected, with tamper checks (device clock, mock-location detection). (S) (extended v0.3: offline capture is permitted per location policy mode (MD-08), follows the offline rules of section 12D, keeps capture time distinct from server receipt time, and is validated by the server after synchronization)
- **FR-HRM-ATT-05** Geo-fences shall be defined per site with radius or polygon and assigned to employees or groups; out-of-fence punches shall be flagged or blocked per policy. (extended v0.3: managed attendance locations, boundaries, applicability and lifecycle are specified in section 9.6; the policy outcome follows section 9.7)

### 9.2 Shifts and rosters

- **FR-HRM-ATT-06** Shift types shall include fixed, flexible (core hours), rotating, split, night and continuous 24-hour; weekly-off patterns shall be configurable (fixed day, rotating, alternating weeks).
- **FR-HRM-ATT-07** A roster builder shall assign shifts to employees or groups for a period, with copy-forward, conflict and rest-hour checks, and publication to employees.
- **FR-HRM-ATT-08** Employees shall request shift swaps and changes through a workflow. (S)

### 9.3 Rules and processing

- **FR-HRM-ATT-09** An attendance policy shall define grace periods, late and early-leaving thresholds, half-day and absence thresholds, break and lunch handling, minimum hours for a full day, and rounding rules.
- **FR-HRM-ATT-10** The engine shall process raw punches into daily attendance records (present, absent, late, half-day, on leave, holiday, weekly off, on duty) with an explanation of each result.
- **FR-HRM-ATT-11** Missing or incorrect punches shall appear in an exception queue; employees raise regularization requests with reason and proof; managers approve. (extended v0.3: exception types, routing, review and history are specified in section 9.9)
- **FR-HRM-ATT-12** Overtime rules shall be defined by day type (working day, rest day, holiday), with configurable rate multipliers, daily and monthly caps, pre-approval or post-approval, and an option to convert to compensatory off.
- **FR-HRM-ATT-13** Late-coming and early-leaving rules shall support automatic deduction as leave, pay deduction or warning, with waiver by an approver.
- **FR-HRM-ATT-14** On-duty, official visit, work-from-home and field-duty requests shall be supported with approvals and count as present per policy. (extended v0.3: an approved field-duty request creates or updates the Field Duty Assignment used by Mode D, section 9.7)
- **FR-HRM-ATT-15** Holiday calendars shall be defined per entity and location with Gregorian and Hijri dates, optional holidays and employee choice of optional holidays.
- **FR-HRM-ATT-16** Ramzan (Ramadan) and seasonal timing policies shall apply by date range and employee group.
- **FR-HRM-ATT-17** Attendance periods shall be lockable by cut-off date; changes after lock require approval and trigger a recalculation flag to payroll.

### 9.4 Views and reports

- **FR-HRM-ATT-18** A live dashboard shall show present, absent, late, on leave and not yet punched, by unit and location. (extended v0.3: remote, field duty, exceptions and pending approval indicators, an interactive map and drill-down are specified in section 9.10)
- **FR-HRM-ATT-19** Standard reports shall include daily and monthly muster, late and early, overtime, absenteeism, punch log, device sync and exception ageing. (extended v0.3: geo-fence exception, regularization, field-duty and location distribution reports are listed in section 13A)

### 9.5 Additions made in v0.2

- **FR-HRM-ATT-20** Biometric data shall be handled under explicit consent: templates stay on devices or are stored encrypted with a retention period, and employees can request deletion on leaving; collection can be disabled per entity. (new) Scope note (v0.3): this requirement governs biometric attendance devices; biometric unlock of the mobile application uses the operating system's credential and stores no template in HRMIS (section 12E).
- **FR-HRM-ATT-21** Locking an attendance period shall publish an AttendanceLocked event used by payroll, and reopening shall publish AttendanceReopened with the affected employees. (new)
- **FR-HRM-ATT-22** Project, client and task time entry with billable flag, approval and lock shall be available as timesheets (required for United States tenants and professional-services customers). (new, wave B)
- **BR-05** A punch within the grace window does not count as late; a day is classified by the policy applicable on that date, not today's policy.
- **BR-06** If two overlapping approved requests exist (for example leave and on-duty), the system blocks the second or applies the configured precedence.
- **AC:** 500 employees on three rotating shifts across two sites get correct daily outcomes, with a missing punch regularized and the month locked, in under 5 minutes of processing.

### 9.6 Attendance locations and geo-fences (new v0.3)

**Purpose:** define where attendance is valid, as managed, versioned data. **Actors:** Tenant Admin, HR Admin, authorized location administrators, Supervisor (temporary locations). **Preconditions:** kernel Organization locations exist; the geospatial service is available (KER-01).

- **FR-HRM-ATT-23** The system shall maintain attendance locations of types office, branch, factory, warehouse, project site, customer premises, construction site, temporary field location and other authorized work location. Attendance locations shall extend the kernel Organization locations; a location shall not be duplicated as a second master record. (M, new v0.3; dep KER-01)
- **FR-HRM-ATT-24** Each attendance location shall carry a name, unique identifier, address, geographical coordinates, status, effective start and end dates, applicable employees or rules, and the authorized administrators. (M, new v0.3)
- **FR-HRM-ATT-25** Each location shall support one or more geo-fence boundaries: circular (center and radius) and polygon (three or more vertices, validated as a non-self-intersecting area). (M for circular, S for polygon, new v0.3; dep KER-01)
- **FR-HRM-ATT-26** Radius and boundary values shall be configurable within allowed minimum and maximum limits set by tenant policy; the default radius is a proposal (100 meters, MD-05). (M, new v0.3)
- **FR-HRM-ATT-27** Applicability shall be assignable to employees, departments, teams, shifts and employee categories by attribute rules with effective dates and precedence, using the kernel data scopes and the policy framework (POL-02). (M, new v0.3)
- **FR-HRM-ATT-28** Employees may hold a primary and secondary attendance locations; a roster line may specify the location for a given day (links ATT-07). (S, new v0.3)
- **FR-HRM-ATT-29** Temporary field locations shall be creatable by authorized users for a defined period, optionally through an approval workflow, and shall expire automatically; expired locations cannot validate new events. (M, new v0.3)
- **FR-HRM-ATT-30** Locations shall have the lifecycle draft, active, inactive and archived; boundary or applicability changes shall be effective-dated and versioned so past attendance results are never altered by later changes. (M, new v0.3)
- **FR-HRM-ATT-31** A map-based editor shall support address search, placing a point, drawing a circle or polygon, and previewing which employees the location applies to; import of boundaries from standard geographic files shall be available. (editor M, import S, new v0.3; dep KER-02)
- **FR-HRM-ATT-32** Boundary data shall be validated: valid coordinate ranges, minimum radius, no self-intersecting polygons, and warnings for overlapping geo-fences of different locations. (M, new v0.3)
- **FR-HRM-ATT-33** Every change to a location, geo-fence or assignment shall be audited with before and after values and the acting user; managing geo-fences shall require its own permission (section 28.2). (M, new v0.3)
- **FR-HRM-ATT-34** Bulk import, export and bulk assignment of locations and applicability shall be supported with validation and preview. (S, new v0.3)
- **BR-ATT-A** Distance and boundary tests shall use the Kernel geospatial service with a documented method, and shall take the reported GPS accuracy into account according to the policy of section 9.7.
- **AC:** an administrator draws a polygon around a factory, applies it to the production department from the first of next month, and attendance recorded before that date is unaffected.

### 9.7 Attendance location policy modes (new v0.3)

**Purpose:** let each organization decide how strictly location is enforced. **Actors:** Tenant Admin, HR Admin. **Preconditions:** locations (9.6) and applicability rules exist.

- **FR-HRM-ATT-35** The system shall provide four configurable location policy modes, set per organization, employee category, department, location and other policy dimensions with precedence and effective dates (POL-02): Mode A strict geo-fence; Mode B geo-fence with exception; Mode C unrestricted location capture; Mode D field duty or mobile workforce. (M, new v0.3)
- **FR-HRM-ATT-36** Mode A: attendance shall be accepted only when the verified device location falls inside an authorized geo-fence under the accuracy rule; otherwise the submission shall be rejected with a clear reason and the employee may submit a regularization request where policy allows. (M, new v0.3)
- **FR-HRM-ATT-37** Mode B: attendance outside the geo-fence shall be accepted as a submission, the actual coordinates captured, the distance from the assigned location recorded, the record flagged as an exception, the employee's reason captured, and the record routed for review by policy. (M, new v0.3)
- **FR-HRM-ATT-38** Mode C: attendance may be recorded from any location; coordinates shall be captured and be available to authorized supervisors and HR; no geo-fence rejection applies. (M, new v0.3)
- **FR-HRM-ATT-39** Mode D: employees with a field-duty assignment (dates, allowed areas, approved customer sites or linked tasks) may record attendance at approved field locations or other permitted locations; events outside the assignment shall be flagged for review. (M, new v0.3; dep TFA)
- **FR-HRM-ATT-40** Policy parameters shall include: accuracy threshold (proposal 50 meters, MD-04); default radius (proposal 100 meters, MD-05); boundary tolerance; allowed event types; whether offline capture is permitted for the mode (MD-08); whether a photograph is required; whether a reason is mandatory; and how suspected mock location is handled (flag or block). (M, new v0.3)
- **FR-HRM-ATT-41** The policy resolution view of POL-03 shall explain which mode applies to an employee on a date and why. (M, new v0.3)
- **FR-HRM-ATT-42** Policy changes shall be effective-dated and simulated against recent events to show which results would change before activation (POL-07). (S, new v0.3)
- **FR-HRM-ATT-43** Before an employee's first location-bearing capture, the application shall show the purpose notice for location capture and record the acknowledgement, in the employee's language, configurable per country (section 34). (M, new v0.3)
- **FR-HRM-ATT-44** Employees approved to work remotely shall be assigned Mode B or C; home coordinates shall not be stored unless the tenant explicitly configures it (MD-24). (M, new v0.3)
- **BR-ATT-B** Server-side validation decides the outcome. The client may display a preliminary result, but acceptance, rejection and flags are determined by the server under the policy effective at the event time.
- **AC:** in Mode A an employee 400 meters from the site is rejected with distance shown; in Mode B the same attempt is accepted as a flagged exception with the reason recorded; in Mode C it is accepted with coordinates visible to the supervisor.

### 9.8 Attendance event capture (new v0.3)

**Purpose:** record each attendance event with enough evidence to explain it, and nothing more. **Actors:** Employee, Field Employee, system.

- **FR-HRM-ATT-45** Event types shall include check-in, check-out, break start, break end and other configured types. (M, new v0.3)
- **FR-HRM-ATT-46** For each applicable event the system shall capture: employee ID; event type; event timestamp; server receipt timestamp; device timestamp; GPS latitude and longitude; GPS accuracy where available; resolved address or location description where available; assigned workplace or geo-fence; distance from the authorized location; geo-fence validation result; device identifier and registration reference; application and platform information; network status at capture; validation and exception flags; employee reason where applicable; approval or regularization status. (M, new v0.3)
- **FR-HRM-ATT-47** The event timestamp (capture time) and the server receipt timestamp shall be stored separately; attendance days shall be computed from the capture time within the allowed backdating window for offline events (proposal 72 hours, MD-19), while the server time stays authoritative for audit. (M, new v0.3)
- **FR-HRM-ATT-48** Device clock tampering shall be detected by comparing device and server time at connection and by monotonic-clock offsets; events with skew above a threshold (proposal 5 minutes) shall be flagged. (M, new v0.3)
- **FR-HRM-ATT-49** The geo-fence validation result shall use defined codes: inside, outside, accuracy insufficient, location unavailable, location services disabled, mock suspected, unverified offline, unrecognized location. (M, new v0.3)
- **FR-HRM-ATT-50** Reverse geocoding shall be optional and informational, through a configurable provider adapter (MD-21); no provider shall be hard-coded. (S, new v0.3)
- **FR-HRM-ATT-51** Duplicate prevention shall use an idempotency key per event and a duplicate window; sequence rules (for example no check-out without check-in) shall be configurable. (M, new v0.3)
- **FR-HRM-ATT-52** A photograph shall be optional by policy and stored as evidence with its own retention; automated face matching is not provided (W for this release; MD-25). (S for photo, W for face matching, new v0.3)
- **FR-HRM-ATT-53** Raw location evidence shall be stored separately from the attendance result (Attendance Location Evidence, section 27.2) with stricter access than the attendance record. (M, new v0.3)
- **FR-HRM-ATT-54** Accepted events are immutable; corrections occur through regularization with full history, never by editing the captured event. (M, new v0.3)
- **AC:** an offline check-in made at 08:58 and synchronized at 11:20 is stored with capture time 08:58 and receipt time 11:20, validated by the server, and shown to the supervisor as synchronized later.

### 9.9 Attendance exception management (new v0.3)

**Purpose:** handle deviations fairly, quickly and with a complete history. **Actors:** Employee, Supervisor, Line Manager, HR Admin. **Preconditions:** a captured or missing event produces a validation flag.

- **FR-HRM-ATT-55** Configurable exception types shall include: outside geo-fence; GPS unavailable; poor GPS accuracy; location services disabled; mock or suspicious location; missing check-in or check-out; duplicate events; late arrival and early departure; offline synchronization; unrecognized or unauthorized location; attendance at an incorrect work location. Each type has severity, routing and optional auto-resolution rules. (M, new v0.3)
- **FR-HRM-ATT-56** Where policy permits, employees shall submit explanations and regularization requests with reason and attachments within a time limit, through the mobile app or web. (M, new v0.3)
- **FR-HRM-ATT-57** Supervisors, managers and HR shall review, approve, reject or return exceptions for correction according to their authority, determined by data scope and by thresholds (for example distance beyond a configured value requires the manager, beyond a higher value HR), through the kernel workflow engine. (M, new v0.3)
- **FR-HRM-ATT-58** Reminders and escalation to the next approver shall follow configurable timing; automatic approval is not a default (W); any timeout outcome requires explicit policy and HR approval (MD-20). (M for reminders and escalation, new v0.3)
- **FR-HRM-ATT-59** A complete, immutable decision history shall record who decided, when, comments, and previous and new values. (M, new v0.3)
- **FR-HRM-ATT-60** Regularization shall change the attendance outcome only; the original captured event and its evidence remain unchanged. (M, new v0.3)
- **FR-HRM-ATT-61** Reviewers shall have bulk tools with filters and grouping by location, team and exception type, and one-tap actions on mobile. (S, new v0.3)
- **FR-HRM-ATT-62** Repeated exceptions by an employee or at a location shall be surfaced to HR as advisory indicators; no disciplinary or pay action shall be triggered automatically (AIA-05). (S, new v0.3)
- **FR-HRM-ATT-63** Employees shall see their own exceptions, status and decisions, with notifications. (M, new v0.3)
- **FR-HRM-ATT-64** Unresolved exceptions shall be shown to payroll at attendance lock (ATT-17, BR-09) and block or warn according to policy. (M, new v0.3)
- **FR-HRM-ATT-65** Mock or suspicious location indicators (developer mock provider, impossible movement, device risk signals from KER-03) shall be treated as signals routed to review, not as proof; Mode A policy may block the submission. (M, new v0.3; dep KER-03)
- **FR-HRM-ATT-66** Distance and severity thresholds for routing shall be configurable by policy. (M, new v0.3)
- **BR-ATT-C** An employee cannot approve their own exception; the approver must be within the employee's reporting line or an authorized HR role.
- **AC:** an out-of-fence Mode B check-in routes to the supervisor with distance and reason; the supervisor returns it for correction; the employee adds an attachment; the manager approves; the history shows all four steps and the original event remains unchanged.

### 9.10 Attendance dashboards and location maps (new v0.3)

**Purpose:** give authorized users a current picture of attendance and where events occurred. **Actors:** Supervisor, Line Manager, Department Head, Regional Manager, HR Admin, HR Business Partner, Senior Management.

- **FR-HRM-ATT-67** A live dashboard shall show employees present, absent, late, on leave, working remotely, on field duty, with attendance exceptions and pending approvals, with distribution by location, department, branch and organization, limited to the viewer's data scope. (M, new v0.3)
- **FR-HRM-ATT-68** Dashboard data shall be near real time (proposal: visible within 60 seconds of server receipt, MD-18); events synchronized later shall appear with their capture time and a synchronized-later indicator. (M, new v0.3)
- **FR-HRM-ATT-69** An interactive map shall show attendance event markers (clustered), geo-fence overlays and accuracy circles, filterable by date, time, department, employee, location, team and status. (M, new v0.3; dep KER-02)
- **FR-HRM-ATT-70** Selecting a marker shall show event details: recorded GPS position, assigned workplace, inside or outside, distance, device and application information, evidence photo where permitted, and exception state. (M, new v0.3)
- **FR-HRM-ATT-71** Users shall drill down to an employee's attendance history and see the event points of a chosen period; the map shows event points only, never a continuous trail. (M, new v0.3)
- **FR-HRM-ATT-72** Map and evidence visibility shall respect the viewer's data scope and a separate permission to view GPS evidence; users without it see only inside or outside and the exception status. (M, new v0.3)
- **FR-HRM-ATT-73** Map and dashboard queries shall use the semantic layer with query limits, and exporting coordinates shall follow export controls and approval (document 9, GOV-04). (M, new v0.3)
- **FR-HRM-ATT-74** Continuous, real-time movement tracking of individuals between events is not provided and is Won't have in this release; any safety-motivated exception (for example lone workers) requires a separate decision, explicit consent and legal review (MD-23). (W, new v0.3)
- **FR-HRM-ATT-75** The dashboard and map shall be available on web and mobile workspaces (sections 12B and 12F). (M, new v0.3)
- **FR-HRM-ATT-76** Executive map access shall be configurable and disabled unless the tenant policy enables it for senior management (MD-17). (M, new v0.3)
- **AC:** an HR user in scope filters the map to one branch and the late arrivals of today, opens a marker to see the inside-or-outside result and distance, and cannot open the same employee's marker from another company.

### 9.11 Location data governance within attendance (new v0.3)

- **FR-HRM-ATT-77** Retention of raw location evidence shall be configurable per tenant and entity and independent of the retention of attendance results; the default is a proposal (MD-06); purging shall run as an audited job honoring legal holds (KER-05). (M, new v0.3)
- **FR-HRM-ATT-78** Every view or export of location evidence shall be written to the access log with user, scope and purpose where required. (M, new v0.3)
- **FR-HRM-ATT-79** Employees shall be able to see their own recorded events and locations and the location notice. (M, new v0.3)
- **FR-HRM-ATT-80** Precision shall be reducible by policy (for example coarse location for senior roles or for some countries) and only configured events shall capture location. (S, new v0.3)
- **FR-HRM-ATT-81** Aggregated analytics shall be preferred to individual-level location data where the purpose does not need individuals. (S, new v0.3)
- **FR-HRM-ATT-82** An entity or country flag shall disable location capture entirely; events then record time and device information only. (M, new v0.3)

## 9A. Employee Task and Field Activity Management (TFA), wave A1 extension (new v0.3)

**Purpose:** let authorized users assign work to employees, receive responses with evidence through self-service and mobile, and review it, for both office and field staff. **Actors:** Supervisor, Line Manager, Department Head, HR Admin (assigners and reviewers); Employee and Field Employee (assignees); Senior Management (visibility). **Preconditions:** employees, organization hierarchy and data scopes exist; for location-enabled tasks, locations and the geospatial service exist (KER-01).

**Boundary with the platform.** Tasks are HRMIS domain objects owned by this module. They reuse the kernel workflow engine (review and approval), Documents (evidence storage, KER-08), Notifications, Collaboration comments (COL), Identity data scopes and the geospatial service. They do not reuse the lightweight kernel tasks and reminders of COL-02, which have no evidence, checkpoints or location rules (assumption recorded as MD-22). TFA is not a project management product.

### 9A.1 Task creation and assignment

- **FR-HRM-TFA-01** Authorized users shall create individual tasks with title, description, instructions and expected deliverables. (M, new v0.3)
- **FR-HRM-TFA-02** A task shall be assignable to one employee, several employees, a team or a department; assignment to a team or department creates one assignment per member at the time of assignment. (M for one and several, S for team and department, new v0.3)
- **FR-HRM-TFA-03** A task shall carry priority, start date, due date, expected duration, assigned supervisor or reviewer, and an optional association with a project (a Finance dimension value when Finance is licensed), department, location or business activity. (M, new v0.3)
- **FR-HRM-TFA-04** A task shall define whether location capture is required and at which events (section 9A.4), whether photographs, documents or other evidence are required, the required checkpoints or milestones, and the completion criteria. (M, new v0.3)
- **FR-HRM-TFA-05** Reference documents and instructions shall be attachable and shown to assignees. (M, new v0.3)
- **FR-HRM-TFA-06** Tasks may depend on other tasks (finish-to-start); a dependent task becomes assignable or startable only when its predecessor is completed. (S, new v0.3)
- **FR-HRM-TFA-07** Task types shall be configurable (for example site visit, inspection, survey, installation, collection, delivery visit, office assignment), each with defaults for evidence, location policy, checkpoints, review rules and a checklist form built with the kernel metadata engine. (M for types, S for checklist forms, new v0.3)
- **FR-HRM-TFA-08** Task templates shall store type, instructions, checkpoints, evidence and review rules for reuse; recurring task definitions shall generate tasks on a schedule with assignment rules. (templates S, recurrence C, new v0.3)
- **FR-HRM-TFA-09** A user may assign tasks only to employees within their data scope; assignment outside the scope requires a specific permission and is audited. (M, new v0.3; dep section 28.2)
- **FR-HRM-TFA-10** When assigning, the system shall warn about conflicts with approved leave, rest days and shift hours of the assignee, and about workload above a configured limit. (S, new v0.3)
- **FR-HRM-TFA-11** For multiple assignees the task shall define a completion mode: each assignee completes and is reviewed separately (default), or one shared submission completes the task for the group. (M, new v0.3)
- **FR-HRM-TFA-12** Bulk assignment and import of tasks from a spreadsheet shall be supported with validation and preview. (S, new v0.3)
- **FR-HRM-TFA-13** Assignment shall notify assignees and reviewers through the notification service (section 12C). (M, new v0.3)
- **FR-HRM-TFA-14** Reassignment of an open task shall be possible by the assigner or manager with reason, keeping history. (M, new v0.3)

### 9A.2 Task lifecycle

- **FR-HRM-TFA-15** The task lifecycle shall have ten statuses: Draft, Assigned, Acknowledged, In Progress, On Hold, Submitted for Review, Returned for Rework, Completed or Approved, Rejected, and Cancelled or Closed (closure reasons: cancelled, completed and archived, rejected and closed). (M, new v0.3)
- **FR-HRM-TFA-16** Valid transitions, permitted actors and conditions shall be as in the table below and enforced by the status model (kernel MET-30); invalid transitions shall be refused. (M, new v0.3)
- **FR-HRM-TFA-17** Every status change shall record actor, time, reason (mandatory for On Hold, Returned for Rework, Rejected and Cancelled) and optional comment, in an immutable activity history. (M, new v0.3)
- **FR-HRM-TFA-18** An unacknowledged task shall trigger reminders and escalation after a configurable period. (S, new v0.3)
- **FR-HRM-TFA-19** Submission for review shall be blocked until required checkpoints, evidence and mandatory fields are complete. (M, new v0.3)
- **FR-HRM-TFA-20** The number of rework cycles shall be recorded and a configurable limit may escalate the task to the next reviewer. (S, new v0.3)
- **FR-HRM-TFA-21** Completed tasks shall close automatically after a configurable period and may be reopened by an authorized manager with a reason. (S, new v0.3)
- **FR-HRM-TFA-22** Overdue status shall be derived from due date and status, not stored as a separate status, and shall drive reminders and reports. (M, new v0.3)
- **FR-HRM-TFA-23** When an assignee leaves the organization or becomes inactive, open tasks shall be flagged for reassignment (links ONB offboarding). (M, new v0.3)
- **FR-HRM-TFA-24** Review and approval of submissions shall use the kernel workflow engine so reviewer resolution, delegation, escalation and out-of-office rules apply. (M, new v0.3)

| From | To | Actor | Conditions |
| --- | --- | --- | --- |
| Draft | Assigned | Assigner | At least one assignee, due date and reviewer set |
| Draft | Cancelled or Closed | Creator | Reason optional |
| Assigned | Acknowledged | Assignee | None |
| Assigned, Acknowledged | On Hold | Assigner or manager | Reason required |
| Assigned, Acknowledged, In Progress, On Hold | Cancelled or Closed | Assigner or manager | Reason required; before approval only |
| Acknowledged | In Progress | Assignee | Start event; location captured if required |
| In Progress | On Hold | Assignee or manager | Reason required |
| On Hold | In Progress | Assignee or manager | None |
| In Progress | Submitted for Review | Assignee | Checkpoints, evidence and mandatory fields complete |
| Submitted for Review | Completed or Approved | Reviewer | Completion criteria met |
| Submitted for Review | Returned for Rework | Reviewer | Comment required |
| Submitted for Review | Rejected | Reviewer | Reason required; no further rework |
| Returned for Rework | In Progress | Assignee | Resubmission follows the same checks |
| Rejected | Cancelled or Closed | Manager | Closure with reason |
| Completed or Approved | Cancelled or Closed | System or manager | Automatic after the configured period |
| Completed or Approved | In Progress | Authorized manager | Reopen with reason |

### 9A.3 Employee response through self-service and mobile

- **FR-HRM-TFA-25** Employees shall see their assigned tasks (list, calendar and map where location-enabled) on web and mobile, filtered by status, priority and due date. (M, new v0.3)
- **FR-HRM-TFA-26** Employees shall review instructions, deadlines and reference documents and acknowledge the assignment. (M, new v0.3)
- **FR-HRM-TFA-27** Employees shall start a task and submit progress updates with comments. (M, new v0.3)
- **FR-HRM-TFA-28** Employees shall record work performed (time spent, quantities, checklist answers) according to the task type. (M, new v0.3)
- **FR-HRM-TFA-29** Employees shall attach photographs, documents and, where configured, a recipient signature as evidence, with size and type limits and compression on mobile. (M for photos and documents, S for signature, new v0.3; dep KER-08)
- **FR-HRM-TFA-30** Employees shall complete required checkpoints in the configured order or freely, each with optional location and evidence. (M, new v0.3)
- **FR-HRM-TFA-31** Employees shall submit the task for review with completion details. (M, new v0.3)
- **FR-HRM-TFA-32** Employees shall respond to reviewer feedback, add comments and resubmit after rework. (M, new v0.3)
- **FR-HRM-TFA-33** Employees shall view the history and status of their tasks. (M, new v0.3)
- **FR-HRM-TFA-34** All employee actions shall work offline within the rules of section 12D, with queued submissions and clear status. (M, new v0.3)
- **FR-HRM-TFA-35** Employees shall be told before location is captured for a task event and shall see which events capture location. (M, new v0.3)
- **FR-HRM-TFA-36** Employees shall be able to report that a task cannot be performed with a reason from a configured list, which routes to the assigner. (S, new v0.3)

### 9A.4 GPS capture for task activities

- **FR-HRM-TFA-37** Location capture shall be configurable per task type and per task at these events: task start, progress update, checkpoint completion, task submission, and other configured field activity events. The default for a task type without a location policy is no location capture. (M, new v0.3)
- **FR-HRM-TFA-38** The system shall not assume every task requires GPS. (M, new v0.3)
- **FR-HRM-TFA-39** For each location-enabled event the system shall capture: employee ID; task ID; event type; event timestamp; server receipt timestamp; GPS latitude and longitude; GPS accuracy; resolved address where available; device and application information; location validation status; and associated photograph or document evidence. (M, new v0.3)
- **FR-HRM-TFA-40** A task may specify an assigned location (an attendance location, a geo-fence or a point with radius); validation compares the event location to it using the accuracy rules of section 9.7 and yields the result codes of ATT-49. (M, new v0.3; dep KER-01)
- **FR-HRM-TFA-41** Policy shall define whether an out-of-area event is blocked, accepted with a flag and reason, or accepted without validation; the default is accepted with a flag and reason. (M, new v0.3)
- **FR-HRM-TFA-42** Mock-location signals and device risk signals shall be flagged as for attendance (ATT-65). (M, new v0.3; dep KER-03)
- **FR-HRM-TFA-43** Events captured offline shall keep capture time and server receipt time and shall be validated by the server after synchronization (section 12D). (M, new v0.3)
- **FR-HRM-TFA-44** Location evidence of tasks follows the retention, access and audit rules of ATT-77 to ATT-82 and section 34, with the task evidence retention proposal in MD-09. (M, new v0.3; dep KER-05)

### 9A.5 Supervisor and senior management visibility

- **FR-HRM-TFA-45** Supervisors, managers, department heads and authorized senior management shall see assigned tasks, employee responses, progress and completion status within their data scope. (M, new v0.3)
- **FR-HRM-TFA-46** Reviewers shall view submitted photographs, documents and checklist answers. (M, new v0.3)
- **FR-HRM-TFA-47** Authorized users shall see task event locations on an interactive map and compare them to the assigned location or geo-fence, with inside or outside and distance. (M, new v0.3; dep KER-02)
- **FR-HRM-TFA-48** Reviewers shall approve, reject or return task submissions with comments and instructions through the workflow engine, from web or mobile. (M, new v0.3)
- **FR-HRM-TFA-49** Users shall see an employee's activity history for tasks within their scope. (M, new v0.3)
- **FR-HRM-TFA-50** Overdue and delayed tasks shall be highlighted with reminders to assignees and escalations to supervisors. (M, new v0.3)
- **FR-HRM-TFA-51** Team and departmental task performance shall be shown on dashboards (section 13A). (M, new v0.3)
- **FR-HRM-TFA-52** Map visibility shall respect role, data scope and the separate permission to view GPS evidence; users without it see only validation results. (M, new v0.3)
- **FR-HRM-TFA-53** Comments between reviewer and assignee shall use kernel collaboration comments with the task's visibility rules. (M, new v0.3; dep COL)
- **FR-HRM-TFA-54** Senior management map views shall be disabled unless the tenant policy enables them (MD-17). (M, new v0.3)

### 9A.6 Task reporting and analytics

- **FR-HRM-TFA-55** Reports and dashboards shall cover: tasks assigned by employee, team, department and manager; completed; pending; overdue; average completion time; rejected and returned; employee task activity; field activity by geographical area; performance by department and location; completion trends; and supervisor review and approval turnaround. (M, new v0.3)
- **FR-HRM-TFA-56** GPS presence and the number of task events shall not be presented as productivity; productivity indicators shall be configurable, explainable business measures (for example completion against due date, rework rate) and shall show their definitions. (M, new v0.3)
- **FR-HRM-TFA-57** Individual-level task analytics shall be restricted to the employee, their reporting line and authorized HR; aggregated views shall be the default for senior management. (M, new v0.3)
- **FR-HRM-TFA-58** Reports shall use the kernel semantic layer, respect data scopes and privacy controls, and be exportable under export controls. (M, new v0.3)
- **FR-HRM-TFA-59** Predictive or ranking features based on task or location data are Won't have in this release; any future use requires bias and privacy review (document 9, SAF). (W, new v0.3)
- **FR-HRM-TFA-60** Task data shall be available to the AI assistant only within the requesting user's permissions and shall not drive automated performance, pay or disciplinary decisions (AIA-05). (M, new v0.3)

### 9A.7 Governance, notifications and audit

- **FR-HRM-TFA-61** Notifications shall cover assignment, acknowledgement reminders, deadline reminders, overdue alerts, review comments, approval, rejection and rework. (M, new v0.3)
- **FR-HRM-TFA-62** All task and evidence actions, including views of location and evidence, shall be audited. (M, new v0.3)
- **FR-HRM-TFA-63** Evidence retention periods shall be configurable per task type and entity, with a default proposal (MD-09); purging shall honor legal holds. (M, new v0.3; dep KER-05)
- **FR-HRM-TFA-64** Photographs shall be scanned, compressed and stored encrypted; embedded location metadata of images shall be handled according to policy (kept for validation, hidden from viewers without permission). (M, new v0.3; dep KER-08)
- **FR-HRM-TFA-65** Task custom fields, labels and checklists shall be configurable through the kernel metadata engine without code. (M, new v0.3)
- **FR-HRM-TFA-66** Task types and templates shall be exportable and importable as configuration packages. (S, new v0.3)
- **FR-HRM-TFA-67** Tasks shall be searchable by title, assignee, status and location within the user's scope. (S, new v0.3)
- **FR-HRM-TFA-68** The employee shall be able to see which fields and events a supervisor can see for their tasks. (S, new v0.3)
- **FR-HRM-TFA-69** Task assignment across legal entities requires explicit permission and respects entity-level policies. (S, new v0.3)
- **FR-HRM-TFA-70** The module shall expose its task events to integrations through the integration framework (section 30.2) without exposing location data unless configured. (S, new v0.3)

**Business rules.**

- **BR-TFA-A** An assignee can only act on their own assignment; supervisors cannot complete tasks on behalf of an assignee except by an audited, permissioned action.
- **BR-TFA-B** A reviewer cannot be the assignee of the same task assignment.
- **BR-TFA-C** Location capture occurs only at the events configured on the task; absence of GPS on an event that requires it produces a flag, never a silent pass.
- **BR-TFA-D** Task data of an employee on approved leave or outside working hours shall not trigger overdue penalties; due date calculations use the employee's calendar when configured.

**Acceptance criteria.**

- A supervisor creates a site-visit task with start and completion locations and two photographs required and assigns it to two employees; each employee receives a notification, acknowledges, starts the task with a location captured inside the site, uploads photographs and submits; the supervisor sees both submissions on the map with inside results and approves one and returns the other with a comment.
- A task without a location policy records no coordinates, and the interface shows no location prompt.
- A user outside the assignee's data scope cannot see the task, its evidence or its map points.

## 10. Leave and absence (LVE), wave A1

- **FR-HRM-LVE-01** Leave types shall be unlimited and configurable (annual, casual, sick, maternity, paternity, marriage, Hajj, Umrah, bereavement, study, unpaid, compensatory off and custom), each with paid or unpaid flag, calendar or working-day counting and document requirement.
- **FR-HRM-LVE-02** Entitlement models shall include fixed yearly grant, monthly accrual, accrual per days or hours worked, tenure-based tiers, grade-based tiers and manual grant, with configurable pro-rata for joiners and leavers.
- **FR-HRM-LVE-03** Rules per leave type shall include carry-forward with cap and expiry, encashment with cap and rate basis, negative balance allowance, minimum notice, maximum consecutive days, gender and tenure eligibility, probation eligibility, sandwich rules for holiday and weekend adjacency, and combining restrictions.
- **FR-HRM-LVE-04** Employees shall apply for full-day, half-day and hourly leave with attachments and see the balance impact before submitting.
- **FR-HRM-LVE-05** Requests shall route through the kernel workflow engine; managers see the team calendar and coverage warnings; HR can apply leave on behalf of an employee.
- **FR-HRM-LVE-06** Cancellation and modification of approved leave shall be supported, with approvals where policy requires and automatic balance restoration.
- **FR-HRM-LVE-07** Leave without pay shall flow to payroll as an attendance-based deduction (wave A2).
- **FR-HRM-LVE-08** Compensatory off shall be earned from approved overtime or holiday work, with expiry rules.
- **FR-HRM-LVE-09** Blackout periods and maximum concurrent leave by team shall be configurable.
- **FR-HRM-LVE-10** A leave ledger per employee shall show opening balance, accruals, usage, adjustments, encashment, lapse and closing balance with source documents.
- **FR-HRM-LVE-11** Year-end processing shall carry forward, lapse and encash per policy, with preview and rollback until confirmed.
- **FR-HRM-LVE-12** Country packs shall preload statutory leave minimums and special leave (for example maternity and paternity provisions in Pakistan, Hajj leave and statutory leave in Saudi Arabia) as editable defaults.
- **FR-HRM-LVE-13** A leave liability calculation (days and value by employee and entity) shall be produced each period and, when Finance is licensed, published for accrual posting by posting rules. (new, wave A2)
- **FR-HRM-LVE-14** Leave balances and entitlements shall be recomputable from the ledger and policies for audit, and every recomputation shall explain differences. (new)
- **FR-HRM-LVE-15** Managers shall see team leave calendars and availability (on leave, working remotely, on field duty) within their data scope, with coverage warnings, on web and mobile. (M, new v0.3)
- **FR-HRM-LVE-16** Employees shall be able to add supporting documents when applying, including photographs from the mobile camera, with type and size limits and scanning. (M, new v0.3; dep KER-08)
- **FR-HRM-LVE-17** Managers shall approve or reject leave with comments from the mobile workspace using the same workflow, rules and authority as on the web (GHR-12). (M, new v0.3)
- **BR-07** Balances are never changed without a ledger entry; every adjustment needs a reason and an approver where policy requires.
- **BR-08** Leave across a holiday or weekly-off counts only as the leave type's counting rule dictates.
- **AC:** an employee joining mid-year receives a pro-rated annual leave grant, applies for two days around a public holiday under a sandwich rule, and sees the exact deduction explained before submitting.

## 11. Onboarding and offboarding (ONB)

### 11.1 Basic, wave A1

- **FR-HRM-ONB-01** Onboarding checklists shall be built per employee type, entity, location or role with tasks, owners (HR, IT, admin, manager, employee), due dates relative to joining date, and reminders.
- **FR-HRM-ONB-02** A pre-boarding portal shall let the new hire complete forms, upload documents, accept policies and provide bank details before day one.
- **FR-HRM-ONB-03** Resignation shall be submitted by the employee or recorded by HR, with notice period determined by policy, acceptance workflow and last working day calculation.
- **FR-HRM-ONB-04** Offboarding checklists shall include clearance by department, asset return, access removal and knowledge handover, with outstanding items shown to payroll for final settlement.
- **FR-HRM-ONB-05** Exit interviews shall use configurable forms visible only to authorized HR roles.
- **FR-HRM-ONB-06** Termination types (resignation, termination, end of contract, retirement, death, absconding) shall carry different workflows and letters.
- **AC:** triggering offboarding generates all clearance tasks, notifies owners and blocks final settlement approval until mandatory clearances are complete.

### 11.2 Advanced, wave B

- **FR-HRM-ONB-10** Onboarding shall include buddy assignment, 30-60-90 day plans, scheduled check-ins and new-hire surveys.
- **FR-HRM-ONB-11** Provisioning tasks shall call integrations (email account, directory, ticketing) to create or disable accounts automatically. (S)
- **FR-HRM-ONB-12** Probation reviews shall be configured as forms with manager and HR sign-off, producing confirmation, extension or termination outcomes.
- **FR-HRM-ONB-13** Offboarding shall capture exit reasons in a standard taxonomy for attrition analysis, and keep alumni records with rehire eligibility flags.

### 11.3 Mobile separation and exit (new v0.3)

- **FR-HRM-ONB-14** Employees shall submit a resignation, see its status and notice period, and receive the acceptance decision on mobile and web. (M, new v0.3)
- **FR-HRM-ONB-15** Employees and clearance owners shall see and complete their exit checklist and handover tasks on mobile, with evidence where required. (M, new v0.3)
- **FR-HRM-ONB-16** Exit interview scheduling and, where configured, completion of the exit form shall be available on mobile; responses remain visible only to authorized HR roles (ONB-05). (S, new v0.3)
- **FR-HRM-ONB-17** Employees shall see the status of the final settlement and their separation documents; settlement amounts are shown only to the employee and authorized HR and payroll roles. (M, new v0.3; dep PAY-26)

## 12. Self-service portals and mobile (SSP), wave A1

- **FR-HRM-SSP-01** The employee portal shall provide profile, documents, payslips and tax documents (wave A2), attendance, leave, requests and their status, letters, directory, organization chart, announcements and policies.
- **FR-HRM-SSP-02** The manager portal shall provide an approvals inbox (kernel workflow inbox), team dashboard (attendance, leave, birthdays, probation and contract dates), team calendar and request-on-behalf actions.
- **FR-HRM-SSP-03** HR and payroll workspaces shall provide task queues, exceptions, deadlines and quick actions.
- **FR-HRM-SSP-04** Mobile apps shall provide attendance capture, leave and request submission, approvals, payslips, directory and push notifications, with biometric unlock. (extended v0.3: the complete mobile ESS scope, roles, offline behavior, security and navigation are specified in sections 12A to 12G)
- **FR-HRM-SSP-05** Chat-based self-service through WhatsApp shall support leave balance, leave application, attendance summary and payslip request, authenticated by registered number and a one-time code. (S)
- **FR-HRM-SSP-06** Tenants shall configure home page widgets, quick links, welcome content and language defaults per role.
- **FR-HRM-SSP-07** Employees and managers shall set delegation and out-of-office from self-service, applied to approvals through the kernel delegation feature. (new)
- **FR-HRM-SSP-08** Self-service shall be available in English, Urdu and Arabic with right-to-left layout and Hijri date display. (new)
- **FR-HRM-SSP-09** The web portal and the mobile applications shall be clients of the same HRMIS services; a function offered on both shall behave identically, with the same validation, workflow and authorization (GHR-12). (M, new v0.3)
- **FR-HRM-SSP-10** Availability of each self-service function per client (web, Android, iOS) shall be configurable by the tenant without code, and a function shall be offered only when the module behind it is licensed (GHR-16). (M, new v0.3)
- **FR-HRM-SSP-11** Home widgets shall be role-aware and configurable per role and tenant on web and mobile (MOB-30 to MOB-39). (M, new v0.3)
- **FR-HRM-SSP-12** Profile change requests, HR service requests and document requests shall be available on both clients through the same workflows (EMP-08, DOC-07, MOB-100 to MOB-109). (M, new v0.3)
- **FR-HRM-SSP-13** Self-service actions started offline on mobile shall follow the offline rules of section 12D and shall never bypass server-side validation or approval. (M, new v0.3)
- **FR-HRM-SSP-14** Self-service on both clients shall meet the accessibility, localization and performance rules of document 13. (M, new v0.3)
- **AC:** an employee applies for leave and checks the balance on mobile in Urdu; the manager approves from a push notification without logging into the web portal.

## 12A. Mobile employee self-service application (MOB), new v0.3

**Purpose:** a mobile-first employee experience that extends the web self-service portal (SSP) on the same backend. **Actors:** Employee, Field Employee, and the manager, HR and executive roles of section 12B. **Preconditions:** the user has an active kernel account linked to an employee; the mobile client is enabled for the tenant.

### 12A.1 Mobile strategy and platform

- **FR-HRM-MOB-01** The mobile application shall be delivered Android first (release M1 onwards) and iOS subsequently (release M4), on one shared backend, one set of APIs, one set of business rules and one authorization model. (M, new v0.3)
- **FR-HRM-MOB-02** The mobile application is a client of the HRMIS platform and not a separate HR system: it shall hold no business rules that are not enforced by the server and shall not own master data. (M, new v0.3; GHR-12)
- **FR-HRM-MOB-03** Functionality shall be consistent across Android and iOS, subject to platform-specific capabilities (for example notification and biometric behavior), and every difference shall be documented. (M, new v0.3)
- **FR-HRM-MOB-04** The mobile architecture shall allow reuse of mobile UI components and logic across platforms where technically appropriate. (S, new v0.3)
- **FR-HRM-MOB-05** The application shall run on lower-end Android devices within the performance budgets of document 13 (section 22) and the supported operating system versions set by decision MD-03. (M, new v0.3)
- **FR-HRM-MOB-06** The application shall be distributable through the channels chosen by decision MD-02 (public store, private distribution or enterprise device management) without code changes. (M, new v0.3)
- **FR-HRM-MOB-07** The application shall expose only functions of licensed modules and enabled tenant features (GHR-16, SSP-10). (M, new v0.3)
- **FR-HRM-MOB-08** Minimum supported application versions shall be enforceable by the server, with update prompts and backward-compatible APIs for the previous version (KER-06). (M, new v0.3)
- **FR-HRM-MOB-09** The assistant of AIA-01 to AIA-05 shall be available inside the application when enabled, within the same permissions and safety rules. (S, new v0.3)
- **FR-HRM-MOB-10** Tenant branding (logo, colors, name) shall be applied to the application through configuration (document 13, CUI-07). (S, new v0.3)

**Architecture options and recommendation (not a requirement).** The Architecture document (section 4.9) already recommends a cross-platform client with offline storage, which suits an Android-first plan followed by iOS because it reuses code and keeps behavior consistent. Native clients for each platform give the deepest device integration at higher cost. The final choice is decision MD-01; the functional requirements above do not depend on it.

### 12A.2 Mobile user roles

- **FR-HRM-MOB-11** The application shall provide experiences for the roles Employee, Manager, Supervisor, Department Head, HR Administrator, HR Business Partner, Regional Manager, Senior Management, and other configurable roles, selected from the user's Kernel roles and data scopes. (M, new v0.3)
- **FR-HRM-MOB-12** Mobile permissions shall equal web permissions: a user with several roles sees the union of functions allowed by those roles, and nothing more because they use a mobile device. (M, new v0.3; GHR-12)
- **FR-HRM-MOB-13** Role-specific workspaces shall be exposed as additional areas of the same application (section 12B), not as separate applications. (M, new v0.3)
- **FR-HRM-MOB-14** Role and data scope changes shall take effect on the next request or sync without reinstalling or re-registering the application. (M, new v0.3)

### 12A.3 Authentication and device management

- **FR-HRM-MOB-20** Sign-in shall support employee ID, username or email according to tenant configuration, and password authentication. (M, new v0.3)
- **FR-HRM-MOB-21** Single sign-on shall be supported where the tenant configures an identity provider (kernel IAM-03). (S, new v0.3)
- **FR-HRM-MOB-22** Multi-factor authentication shall be supported, including one-time codes by authenticator, SMS or email according to tenant policy, with enforcement by role or user (kernel IAM-02). (M, new v0.3)
- **FR-HRM-MOB-23** Biometric unlock shall use device-supported authentication to unlock an application session that was already authenticated by the server; it shall not replace server-side authentication or authorization, and HRMIS shall not store biometric templates. Policy (MD-13) sets whether biometric unlock is optional or required. (M, new v0.3)
- **FR-HRM-MOB-24** Each device shall be registered securely on first sign-in with a device identifier, platform, application version and user binding; a user's registered devices are limited by policy (MD-14). (M, new v0.3; dep KER-03)
- **FR-HRM-MOB-25** Trusted-device policies shall determine which devices may access which functions; unrecognized devices shall require additional verification or administrator approval according to policy. (M, new v0.3)
- **FR-HRM-MOB-26** Session timeout, inactivity lock and session revocation shall be configurable; tokens shall be device-bound and rotate on use (KER-07). (M, new v0.3)
- **FR-HRM-MOB-27** Password reset and account recovery shall be available through verified channels without revealing whether an account exists. (M, new v0.3)
- **FR-HRM-MOB-28** Authorized administrators shall revoke a device or remotely sign out a user, wipe locally stored application data on the next connection, and see the revocation in the audit log. (M, new v0.3)
- **FR-HRM-MOB-29** Configurable limits for failed sign-ins and suspicious sign-in patterns (new device, impossible travel, repeated failures) shall lock or challenge the account and alert the user and administrators. (M, new v0.3)

### 12A.4 Home dashboard

- **FR-HRM-MOB-30** The home screen shall be role-aware and configurable and shall show relevant widgets from: today's attendance status, check-in and check-out actions, assigned shift, attendance exceptions, leave balance, upcoming holidays, pending leave requests, assigned tasks, task deadlines, pending approvals (for approvers), payslip availability, announcements, notifications and reminders, HR service requests, and important employee information. (M, new v0.3)
- **FR-HRM-MOB-31** Widgets shall appear only for functions the user may use and modules licensed (GHR-16). (M, new v0.3)
- **FR-HRM-MOB-32** Tenants shall configure widget availability and order per role; users may reorder within allowed limits. (S, new v0.3)
- **FR-HRM-MOB-33** The home screen shall load from cached data immediately and refresh in the background, showing data freshness. (M, new v0.3)
- **FR-HRM-MOB-34** Primary daily actions (check in or out, apply for leave, view tasks) shall be reachable in one tap from home. (M, new v0.3)

### 12A.5 Attendance on mobile

- **FR-HRM-MOB-40** Employees shall check in, check out, and start and end breaks under the location policy mode that applies to them (section 9.7), with the event data of section 9.8. (M, new v0.3)
- **FR-HRM-MOB-41** The application shall show attendance history, a daily summary and a monthly calendar view, the assigned shift, and overtime information where applicable. (M, new v0.3)
- **FR-HRM-MOB-42** Employees shall submit regularization requests, missing attendance submissions and exception explanations with attachments where policy permits (ATT-56). (M, new v0.3)
- **FR-HRM-MOB-43** Field-duty employees shall record attendance at approved field locations or other permitted locations under Mode D (ATT-39). (M, new v0.3)
- **FR-HRM-MOB-44** Before capture the application shall check location permission and service state, guide the employee to enable them, and explain the purpose notice (ATT-43); if location cannot be obtained, behavior follows policy (reject, accept with flag, or accept time only). (M, new v0.3)
- **FR-HRM-MOB-45** The application shall show geo-fence feedback: distance from the assigned location, whether inside or outside, and the accuracy reached. (M, new v0.3)
- **FR-HRM-MOB-46** After each submission the application shall show a clear status: accepted, rejected with reason, or submitted for review; and for offline captures, locally captured and not yet accepted by the server (section 12D). (M, new v0.3)
- **FR-HRM-MOB-47** Offline attendance capture and synchronization shall follow section 12D and the per-mode permission of MD-08. (M, new v0.3)

### 12A.6 Leave on mobile

- **FR-HRM-MOB-50** Employees shall view leave balances, leave types and applicable policies, leave history and the organizational holiday calendar. (M, new v0.3)
- **FR-HRM-MOB-51** Employees shall apply for leave with dates, duration, reason and supporting documents, see the balance effect before submitting, and cancel or amend applications where policy permits. (M, new v0.3; LVE-04, LVE-06, LVE-16)
- **FR-HRM-MOB-52** Employees shall track application status and see approval or rejection comments, with notifications. (M, new v0.3)
- **FR-HRM-MOB-53** Managers shall review team leave requests and team availability within their data scope (LVE-15, LVE-17). (M, new v0.3)

### 12A.7 Profile and personal information

- **FR-HRM-MOB-60** Employees shall view personal details, contact information, employment information, job title and designation, department and reporting manager, work location, emergency contacts, qualifications and skills, employment documents and profile photograph, within field security. (M, new v0.3)
- **FR-HRM-MOB-61** Bank details shall be shown only where permitted, masked by default, with reveal protected by step-up authentication and logged. (M, new v0.3)
- **FR-HRM-MOB-62** Employees shall submit profile change requests; sensitive changes (bank accounts and other controlled fields) shall follow the verification and approval workflows of EMP-06 and EMP-08, including maker-checker. (M, new v0.3)
- **FR-HRM-MOB-63** Profile photographs shall be captured or chosen with size and quality checks and routed for approval where policy requires. (S, new v0.3)

### 12A.8 Employee documents

- **FR-HRM-MOB-70** Employees shall access authorized documents: employment and appointment letters, contracts, salary certificates, payslips, tax documents, policy documents, performance documents and training certificates, and other employee-specific documents. (M, new v0.3)
- **FR-HRM-MOB-71** Secure viewing and downloading shall follow document permissions, retention rules and tenant policy; downloads of sensitive types may be disabled or watermarked. (M, new v0.3)
- **FR-HRM-MOB-72** Sensitive documents shall not be cached on the device unless the tenant allows it, in which case they are encrypted and removed on sign-out or revocation. (M, new v0.3)
- **FR-HRM-MOB-73** Employees shall request letters and certificates and upload requested documents (DOC-02, DOC-07). (M, new v0.3)

### 12A.9 Payroll and compensation self-service

Available only where payroll (section 14) is licensed and enabled.

- **FR-HRM-MOB-80** Employees shall view monthly payslips, salary history, earnings and deductions, allowances, bonuses and incentives, tax deductions, loans and advances, reimbursements and expenses, year-to-date information, and tax statements and certificates where applicable. (M, new v0.3; dep PAY, CPL-05)
- **FR-HRM-MOB-81** Access to payroll screens shall be restricted by role and field security, may require step-up authentication, and shall mask amounts when the screen is idle or when the app is in the background, according to policy. (M, new v0.3)
- **FR-HRM-MOB-82** Payslip availability shall be notified without including amounts in the notification. (M, new v0.3)
- **FR-HRM-MOB-83** Employees shall submit expense claims and loan requests from mobile where those modules are enabled (LON, EXP), with receipt capture by camera. (S, new v0.3)

### 12A.10 Tasks and field activities on mobile

Functions are those of sections 9A.3 and 9A.4.

- **FR-HRM-MOB-90** Employees shall view, acknowledge, start and update tasks, record progress and checkpoints, upload photographs and supporting evidence, submit completion reports, respond to review comments, resubmit and view their task history (TFA-25 to TFA-36). (M, new v0.3)
- **FR-HRM-MOB-91** Location shall be captured at the configured task events only, with the notice of TFA-35 and the offline behavior of section 12D. (M, new v0.3)
- **FR-HRM-MOB-92** A map view shall show location-enabled tasks and the employee's own recorded event points where appropriate. (S, new v0.3)
- **FR-HRM-MOB-93** The camera flow shall support multiple photographs per task event, compression, retake and caption, within the limits of the task type. (M, new v0.3)

### 12A.11 HR helpdesk and employee requests

- **FR-HRM-MOB-100** Employees shall submit and track requests in configurable categories including general HR inquiries, employment verification, salary certificate, documents, payroll queries, attendance complaints, leave queries, profile corrections, and integrated IT or administrative requests. (M, new v0.3)
- **FR-HRM-MOB-101** Requests shall allow attachments, show status and responses, and notify the employee at each step. (M, new v0.3)
- **FR-HRM-MOB-102** Requests shall use the kernel workflow and service-request facilities and the helpdesk of ERL-05 where licensed; no separate request engine shall be built. (M, new v0.3)

### 12A.12 Recruitment and internal opportunities

Available only where recruitment (section 18) is licensed.

- **FR-HRM-MOB-110** Employees shall view internal job opportunities, apply internally, follow application status, refer candidates and receive related communications. (S, new v0.3; dep REC)
- **FR-HRM-MOB-111** The feature shall use the recruitment module's postings, applications and notifications; no separate recruitment store shall be created. (M, new v0.3)

### 12A.13 Learning and development

Available only where learning (section 20) is licensed or integrated.

- **FR-HRM-MOB-120** Employees shall see assigned training, schedules, course details, learning materials, progress, completion status and certificates, and receive training reminders. (S, new v0.3; dep LND)

### 12A.14 Performance management

Available only where performance (section 19) is licensed.

- **FR-HRM-MOB-130** Employees shall view objectives and key indicators, review schedules, complete self-assessment, give and receive feedback, see review status and development plans, and track goals and progress. (S, new v0.3; dep PRF)
- **FR-HRM-MOB-131** Managers shall complete authorized reviews and feedback from their mobile workspace. (S, new v0.3)

### 12A.15 Announcements, directory and organization

- **FR-HRM-MOB-140** The application shall show company announcements, HR notices, policy updates and circulars, with acknowledgement tracking where required (DOC-06). (M, new v0.3)
- **FR-HRM-MOB-141** An employee directory shall be shown according to privacy settings (fields visible, populations visible), with the organization chart, department information, manager and reporting relationships, and office and branch information. (M, new v0.3)

### 12A.16 Employee separation and exit

- **FR-HRM-MOB-150** Employees shall submit resignation, see its status, follow the exit checklist and clearance tasks, schedule the exit interview, see handover requirements, final settlement status and separation documents, as specified in ONB-14 to ONB-17, with access to sensitive settlement information limited to authorized roles. (M, new v0.3)

**Acceptance criteria for the mobile ESS (summary; full scenarios in section 36).** An employee signs in with MFA, unlocks later with biometrics, checks in under the applicable mode, applies for leave, views a payslip and submits an HR request; the same actions through the web portal give identical results under identical rules; a user outside a data scope cannot reach other employees' data from the application.

## 12B. Manager, supervisor, HR and senior management workspaces (WSP), new v0.3

**Purpose:** role-specific areas of the same mobile application (and equally available on the web) for people who act on others' data. **Actors:** Line Manager, Supervisor, Department Head, Regional Manager, HR Admin, HR Business Partner, Senior Management. **Rule for all workspaces:** the data scope of the Kernel applies to every list, count, map and drill-down; mobile never widens it (GHR-12).

### 12B.1 Manager and supervisor workspace

- **FR-HRM-WSP-01** Managers and supervisors shall see their direct reports and, according to scope, the wider team, with current status (present, absent, on leave, on field duty, remote). (M, new v0.3)
- **FR-HRM-WSP-02** They shall see authorized team attendance, late arrivals and absences for the day and period, and review attendance exceptions and regularization requests (ATT-57). (M, new v0.3)
- **FR-HRM-WSP-03** They shall approve or reject leave applications and see team leave calendars and availability (LVE-15, LVE-17). (M, new v0.3)
- **FR-HRM-WSP-04** They shall create and assign tasks within their data scope, review progress and submissions, view task-related evidence and location results, and approve, reject or return task submissions (TFA-09, TFA-45 to TFA-54). (M, new v0.3)
- **FR-HRM-WSP-05** They shall add comments and instructions on tasks and exceptions and receive reminders for pending approvals. (M, new v0.3)
- **FR-HRM-WSP-06** A single approvals inbox shall list pending decisions across modules (leave, attendance, tasks, requests, expenses) with context, filters, and quick approve and reject, using the kernel workflow inbox. (M, new v0.3)
- **FR-HRM-WSP-07** Bulk approval shall be available where policy permits, with a confirmation that states the number of items. (S, new v0.3)
- **FR-HRM-WSP-08** Delegation and out-of-office settings (SSP-07) shall be available from the workspace and applied to approvals. (M, new v0.3)
- **FR-HRM-WSP-09** A team dashboard shall show attendance, leave, task status, overdue tasks, probation and contract dates. (M, new v0.3)
- **FR-HRM-WSP-10** Employee contact details shall be shown only as allowed by privacy settings; calling or messaging a team member from the application shall use the device's own facilities. (S, new v0.3)
- **FR-HRM-WSP-11** Reports to the manager shall be limited to their own team unless a wider scope is granted. (M, new v0.3)

### 12B.2 HR workspace

- **FR-HRM-WSP-15** Authorized HR users shall view employee information within their assigned scope and field security. (M, new v0.3)
- **FR-HRM-WSP-16** They shall review attendance exceptions and regularization requests and leave requests assigned to HR. (M, new v0.3)
- **FR-HRM-WSP-17** They shall monitor employee requests and HR helpdesk cases, with ageing and service levels. (M, new v0.3)
- **FR-HRM-WSP-18** They shall see workforce dashboards for their scope (headcount, attendance, leave, exceptions, requests). (M, new v0.3)
- **FR-HRM-WSP-19** They shall review task and field activity information where authorized, without evidence or location unless the GPS evidence permission is held (ATT-72). (M, new v0.3)
- **FR-HRM-WSP-20** They shall monitor and decide employee data change requests, with verification steps for sensitive fields. (M, new v0.3)
- **FR-HRM-WSP-21** They shall receive workflow alerts and escalations for items past their service levels. (M, new v0.3)
- **FR-HRM-WSP-22** HR users shall see whether an employee has a registered device and the application version, for support purposes, without access to the employee's location history; revoking a device requires the device management permission (MOB-28). (S, new v0.3)
- **FR-HRM-WSP-23** HR Business Partners shall see only the units assigned to them. (M, new v0.3)

### 12B.3 Senior management workspace

- **FR-HRM-WSP-27** An executive dashboard shall show configurable high-level indicators: total headcount; active and inactive employees; attendance overview; absenteeism and late arrivals; leave utilization; workforce distribution; departmental headcount; task completion and overdue tasks; field workforce activity; workforce trends; and other approved HR indicators. (M, new v0.3)
- **FR-HRM-WSP-28** Indicators shall be aggregated by default, with drill-down to departments, locations and, only where authorized, individuals. (M, new v0.3)
- **FR-HRM-WSP-29** Regional managers shall see their regions; senior management shall see the scopes granted to their roles; comparisons across departments and regions shall be available within scope. (M, new v0.3)
- **FR-HRM-WSP-30** Map views for senior management shall be controlled by permission and by the tenant's location privacy policy, and shall show aggregated or event-level data as configured (MD-17). (M, new v0.3)
- **FR-HRM-WSP-31** Executive indicator definitions shall be shown on demand so the figures are explainable (document 9, GOV-11). (M, new v0.3)
- **FR-HRM-WSP-32** The last synchronized dashboard shall remain viewable offline with its timestamp, and sensitive drill-downs shall require connection (section 12D). (S, new v0.3)
- **FR-HRM-WSP-33** Compensation figures shall appear only for roles granted salary permission (GHR-06). (M, new v0.3)
- **FR-HRM-WSP-34** Narrative summaries of changes may be offered by the AI assistant for review, within permissions (document 9, section 13). (C, new v0.3)

## 12C. Notifications and communication (NTF), new v0.3

HRMIS consumes the kernel notification service (document 2, NTF) and the push provider adapters of KER-04; it does not build its own messaging layer.

- **FR-HRM-NTF-01** Channels shall be in-app, mobile push, email, SMS and, where an approved integration exists, WhatsApp, each selectable per event type by tenant policy and per user within allowed limits. (M for in-app, push and email; S for SMS and WhatsApp, new v0.3)
- **FR-HRM-NTF-02** Events shall include: attendance reminders; attendance submission accepted or failed; attendance exceptions; leave submission, approval and rejection; task assignment, deadline reminders, review comments, approval and rejection; pending manager approvals; HR service-request updates; payslip availability; company announcements; policy updates; and security alerts. (M, new v0.3)
- **FR-HRM-NTF-03** Templates shall be configurable per event, channel and language (English, Urdu, Arabic) with placeholders, preview and test send (kernel NTF-02). (M, new v0.3)
- **FR-HRM-NTF-04** Users shall set preferences by event group and channel and quiet hours, except for mandatory notifications such as security alerts. (M, new v0.3)
- **FR-HRM-NTF-05** Delivery status (queued, sent, delivered where known, failed), retries with back-off and a delivery log shall be kept and be auditable. (M, new v0.3)
- **FR-HRM-NTF-06** Push and message content shall not include sensitive data such as salary amounts, location coordinates or settlement figures; details are shown after authentication. (M, new v0.3)
- **FR-HRM-NTF-07** Notifications shall carry deep links that open the relevant screen with the user's permissions checked. (M, new v0.3)
- **FR-HRM-NTF-08** Third-party providers shall be reached only through the kernel's provider adapters; no provider shall be referenced in HRMIS business logic (MD-10, MD-11). (M, new v0.3)
- **FR-HRM-NTF-09** A notification center in the application shall list notifications with read state, retention and search. (M, new v0.3)
- **FR-HRM-NTF-10** Reminders (attendance, deadlines, pending approvals) shall be schedulable by policy, respect working calendars and leave, and not be sent to employees on approved leave unless mandatory. (S, new v0.3)
- **FR-HRM-NTF-11** Escalation notifications shall follow workflow escalation rules (kernel WFL-06). (M, new v0.3)
- **FR-HRM-NTF-12** Rate limits and deduplication shall prevent notification floods; digests shall be available for noisy events. (S, new v0.3)
- **FR-HRM-NTF-13** Critical announcements and policy updates may require acknowledgement and shall track it (DOC-06). (S, new v0.3)
- **FR-HRM-NTF-14** Notifications shall be localized to the recipient's language, calendar and number formats. (M, new v0.3)
- **FR-HRM-NTF-15** The device token lifecycle (registration, refresh, removal on revocation or sign-out) shall be managed with the device registry (KER-03, KER-04). (M, new v0.3)
- **FR-HRM-NTF-16** All notifications that carry decisions or deadlines shall be written to the audit trail with time and delivery result. (M, new v0.3)

## 12D. Offline-first mobile capabilities (OFF), new v0.3

**Purpose:** practical operation on low bandwidth or intermittent connectivity without weakening control. The mechanics (device registry, sync protocol, outbox, conflicts, wipe) are the Kernel offline sync framework (document 2, section 28); this section states HRMIS rules on top of it. **Rule:** offline operation never bypasses authorization, approval or server-side business rules.

### 12D.1 What can be done offline

| Operation | Offline | Rule |
| --- | --- | --- |
| View previously synchronized employee information | Yes, read only | Limited to the user's scope; stored encrypted; sensitive fields not synchronized unless allowed |
| View cached task assignments and instructions | Yes | Includes reference documents marked for offline use |
| View policies and instructions | Yes | Cached with version and date |
| Capture attendance events | By mode (MD-08) | Proposal: Modes B, C and D permitted; Mode A not permitted by default; every offline event is unverified until the server validates it |
| Record task progress, checkpoints and completion details | Yes | Queued with capture time and location where configured |
| Capture photographs and documents | Yes | Stored encrypted and queued; uploaded in chunks when online |
| Create HR requests and profile change drafts | Draft yes, submission queued for requests | Profile changes with sensitive fields require connection to submit |
| Submit leave application | Draft only | Balance and policy checks need the server; submission requires connection |
| Approve or reject requests (managers) | No | Decisions need current state and authority checks |
| View payslips and payroll data | No by default | A tenant may allow encrypted caching for a limited time (MD-27) |
| Sign in, MFA, device registration and revocation | No | Biometric unlock of a cached session is allowed within the offline grace period |
| Directory and organization chart | Cached subset | Subject to privacy settings |

### 12D.2 Requirements

- **FR-HRM-OFF-01** The application shall define, per feature and per tenant policy, whether it works offline, and shall show the user clearly which actions are available without a connection. (M, new v0.3)
- **FR-HRM-OFF-02** Local data shall be limited to what the user needs, stored in an encrypted local database and encrypted files, with keys bound to the user's session and device (KER-03, KER-07). (M, new v0.3)
- **FR-HRM-OFF-03** Pending actions shall be held in an offline event queue with idempotency keys, capture timestamp, device timestamp, location and evidence references, and be sent when connectivity returns. (M, new v0.3)
- **FR-HRM-OFF-04** The server shall validate every queued event on receipt against the policies, authority and data scope effective at the capture time, and may accept, reject or route it to review; offline capture never implies acceptance. (M, new v0.3)
- **FR-HRM-OFF-05** The application shall distinguish locally captured, uploading, accepted by the server, rejected and in review for every event, and never show a local event as accepted before the server confirms. (M, new v0.3)
- **FR-HRM-OFF-06** Duplicate events shall be prevented by idempotency keys and duplicate windows; replays shall not create new records (KER-06). (M, new v0.3)
- **FR-HRM-OFF-07** Conflicts (for example a task cancelled or reassigned while offline, a location deactivated, a policy changed) shall be resolved by declared rules per entity: the server version wins by default, and the employee sees a clear message and the way to resubmit or explain. (M, new v0.3)
- **FR-HRM-OFF-08** Capture time and server receipt time shall be stored separately and shown separately (GHR-13); events older than the backdating window (proposal 72 hours, MD-19) shall be rejected or routed to review by policy. (M, new v0.3)
- **FR-HRM-OFF-09** Failed synchronization shall be retried with back-off, shown in a synchronization status screen with reasons, and escalated to the user after a configurable number of failures. (M, new v0.3)
- **FR-HRM-OFF-10** The user shall see overall synchronization status (up to date, syncing, pending items count, last sync time) at all times and can trigger a manual sync. (M, new v0.3)
- **FR-HRM-OFF-11** A maximum offline period shall be configurable; after it, sensitive cached data is locked until the next successful sync. (M, new v0.3)
- **FR-HRM-OFF-12** On sign-out, device revocation, deprovisioning or a wipe command, locally stored data shall be erased, and unsynchronized events shall be handled by policy (kept until synced where permitted, otherwise removed with a warning). (M, new v0.3)
- **FR-HRM-OFF-13** Offline photographs and attachments shall be size limited, compressed and retried as chunks, with progress and retry controls. (M, new v0.3; KER-08)
- **FR-HRM-OFF-14** Device clock changes made while offline shall be detected on reconnection and flagged (ATT-48). (M, new v0.3)
- **FR-HRM-OFF-15** Selective synchronization limits (for example number of tasks, days of history, reference data size) shall be configurable per role and tenant. (S, new v0.3)
- **FR-HRM-OFF-16** Offline behavior shall be covered by tests that simulate loss and return of connectivity, interrupted uploads and device restarts. (M, new v0.3)

## 12E. Mobile security and privacy (MSEC), new v0.3

The general security architecture is in document 1 (section 10) and the kernel; this section lists the mobile-specific controls and the privacy rules for location and activity data. Section 34 lists country and data-governance items.

- **FR-HRM-MSEC-01** All API traffic shall use TLS with certificate validation; certificate pinning shall be available as a tenant option. (M for TLS, S for pinning, new v0.3)
- **FR-HRM-MSEC-02** Tokens and secrets shall be kept in the operating system's secure storage; no credentials shall be stored in plain files or logs. (M, new v0.3)
- **FR-HRM-MSEC-03** The local database and cached files shall be encrypted; keys shall be protected by device secure hardware where available. (M, new v0.3)
- **FR-HRM-MSEC-04** Device registration, trust state, session expiry, revocation, remote logout and data wipe shall work as in MOB-24 to MOB-28 and be audited. (M, new v0.3)
- **FR-HRM-MSEC-05** Rooted Android and jailbroken iOS devices shall be detected through device risk signals (KER-03) and handled by policy: allow with flag, restrict sensitive functions, or block. (M, new v0.3)
- **FR-HRM-MSEC-06** Mock-location and location manipulation shall be detected where technically feasible and handled as signals for review (ATT-65, TFA-42); detection quality and limits shall be documented. (M, new v0.3)
- **FR-HRM-MSEC-07** Application integrity controls (signature checks, tamper detection, and, where available, platform attestation) shall be applied; the server may refuse unrecognized builds. (S, new v0.3)
- **FR-HRM-MSEC-08** APIs shall be protected by rate limiting, abuse detection and authorization checks on every request; the client is never trusted for authorization. (M, new v0.3)
- **FR-HRM-MSEC-09** Sensitive data (salary, bank account, national identifiers, settlement figures, location evidence) shall be masked by default, with reveal protected by step-up authentication and logged. (M, new v0.3)
- **FR-HRM-MSEC-10** Sensitive screens shall hide content in the task switcher and may block screenshots and screen recording according to tenant policy. (S, new v0.3)
- **FR-HRM-MSEC-11** Clipboard use of sensitive values shall be restricted by policy. (S, new v0.3)
- **FR-HRM-MSEC-12** Secure document access shall follow FR-HRM-MOB-70 to MOB-72; downloaded files shall be encrypted and removable on sign-out. (M, new v0.3)
- **FR-HRM-MSEC-13** Mobile events shall be written to the audit log: sign-in and failures, device registration and revocation, attendance and task captures, location evidence views, exports, and administrative actions. (M, new v0.3)
- **FR-HRM-MSEC-14** Device and session policies (device limits, trusted-device rules, timeouts, biometric requirement, root or jailbreak handling, screenshot rules) shall be configurable by tenant, entity and role. (M, new v0.3)
- **FR-HRM-MSEC-15** The application shall be compatible with enterprise mobile device and application management solutions where the customer requires them (MD-12), including managed configuration and remote wipe. (S, new v0.3)
- **FR-HRM-MSEC-16** A penetration test and a mobile application security review shall be completed before each major release (document 12, section 10). (M, new v0.3)
- **FR-HRM-MSEC-17** Dependencies of the mobile application shall pass the license and vulnerability gates of document 12. (M, new v0.3)
- **FR-HRM-MSEC-18** Location and employee activity data shall be collected only for the documented business purposes of section 34; the purposes, the events that trigger capture, the roles that can see it, the retention periods, the employee notice and the audit of access shall be configurable and documented per tenant and country. (M, new v0.3)
- **FR-HRM-MSEC-19** Continuous background location tracking is not designed as a default and is Won't have in this release (ATT-74). (W, new v0.3)

## 12F. Mobile user experience and navigation (MUX), new v0.3

The mobile application follows the design system and accessibility rules of document 13 (UX and Design System Principles); this section adds the mobile information architecture.

### 12F.1 Requirements

- **FR-HRM-MUX-01** The primary navigation shall have five destinations: Home, Attendance, Tasks, Leave and Profile. Other features shall be reached from the home dashboard, contextual navigation or a Services menu. (M, new v0.3)
- **FR-HRM-MUX-02** Users with manager, HR or executive roles shall switch between My view and a role workspace (Team, HR queues, Executive) from the home screen; the primary navigation stays the same so routine employee actions are never harder. (M, new v0.3)
- **FR-HRM-MUX-03** Frequent actions (check in or out, apply for leave, open a task, approve a pending item) shall take at most two taps from home. (M, new v0.3)
- **FR-HRM-MUX-04** Navigation items shall appear only for licensed modules, enabled functions and permitted roles (SSP-10, GHR-16). (M, new v0.3)
- **FR-HRM-MUX-05** Attendance and task statuses shall use consistent status chips with text and icon, never color alone (document 13, TOK-03). (M, new v0.3)
- **FR-HRM-MUX-06** Controls shall meet the touch target, contrast and text scaling rules of document 13, work one-handed for primary actions, and support light, dark and high-contrast themes. (M, new v0.3)
- **FR-HRM-MUX-07** The application shall support English, Urdu and Arabic, right-to-left layout for Urdu and Arabic, localized dates, numbers, currencies and times, and Gregorian and Hijri calendars where country policy requires them, all through the shared Kernel localization services. (M, new v0.3)
- **FR-HRM-MUX-08** Validation and error messages shall say what happened and how to fix it, in the user's language, and offline and sync states shall be explained (document 13, STA). (M, new v0.3)
- **FR-HRM-MUX-09** The application shall be optimized for low bandwidth: small payloads, deferred images, compressed uploads, and a usable offline home. (M, new v0.3)
- **FR-HRM-MUX-10** Notification taps and deep links shall open the exact screen after authentication and permission checks. (M, new v0.3)
- **FR-HRM-MUX-11** Global search within the user's scope shall find people, tasks, requests and documents the user may see (kernel SRC). (S, new v0.3)
- **FR-HRM-MUX-12** Settings shall include language, notifications, security (biometric unlock, devices), privacy notice, help and sign-out. (M, new v0.3)
- **FR-HRM-MUX-13** Clickable prototypes of attendance, task, approval and executive dashboards shall be tested with representative users, including Urdu and Arabic speakers and field staff, before build (document 13, GOV-05, GOV-06). (S, new v0.3)

### 12F.2 Navigation hierarchy

| Level 1 | Level 2 and main screens | Notes |
| --- | --- | --- |
| Home | Role-aware widgets; notifications and sync status; Services menu; workspace switcher | One-tap actions for daily tasks |
| Attendance | Today (check in, check out, breaks); history (calendar and list); exceptions and regularization; field duty; location help | Mode and location feedback shown here |
| Tasks | My tasks (list, calendar, map); task detail (instructions, checkpoints, evidence, activity, comments); task review queue for reviewers | Assignment screens appear in the Team workspace |
| Leave | Balances; apply; my requests; holidays; team calendar for managers | Approvals appear in the Team workspace and the inbox |
| Profile | Personal and employment details; documents; settings; privacy notice; devices; help and support; sign out | Sensitive fields masked |
| Services menu | Payslips and compensation; documents; HR requests; directory and organization chart; announcements; learning; performance; internal jobs; exit | Items shown only when licensed and enabled |
| Team workspace (managers, supervisors) | Team overview; team attendance and exceptions; approvals inbox; task assignment and review; leave calendar; map | Data scope applies |
| HR workspace | Queues for exceptions, requests and data changes; employee lookup; workforce dashboards | Evidence and location per permission |
| Executive workspace | Executive dashboard; drill-down; map where enabled | Aggregated by default |

### 12F.3 Main screens and their functions

| Screen | Primary functions | Roles | Offline |
| --- | --- | --- | --- |
| Home | Widgets, quick actions, notifications, sync status | All | Cached |
| Attendance today | Check in or out, break, shift, location feedback, status | Employee roles | Per mode |
| Attendance history | Calendar, list, daily summary, overtime | Employee roles | Cached |
| Exception and regularization | Explain, attach, submit, track | Employee roles | Draft |
| Task list and detail | View, acknowledge, start, progress, checkpoints, evidence, submit, rework | Employee roles | Yes |
| Task assignment | Create, assign, set evidence and location rules | Assigners | Draft |
| Task review | Evidence, map result, approve, reject, return | Reviewers | No |
| Leave balances and apply | Balances, apply, cancel, track | Employee roles | Draft |
| Approvals inbox | Approve, reject, comment | Approvers | No |
| Team overview | Team status, availability, exceptions | Managers | Cached |
| Payslips | List, view, download | Employee roles with payroll | No by default |
| Documents | List, view, request | Employee roles | Selected only |
| HR requests | Create, track, respond | Employee roles | Draft and queue |
| Directory | Search people and units | Employee roles | Cached subset |
| Executive dashboard | Indicators, drill-down, map | Senior management | Last snapshot |
| Devices and security | Registered devices, biometric setting, sign-out everywhere | All | No |

### 12F.4 User journeys

1. First sign-in: install, sign in with ID and password, complete MFA, register the device, accept the location and privacy notice, enable biometric unlock, land on home.
2. Daily check-in under Mode B: open Attendance, check in, see inside or outside and distance, add a reason when outside, see status accepted or submitted for review.
3. Field day offline: open the task list from cache, start a task offline, capture photographs and checkpoints, check out, see queued items, reconnect, watch synchronization and the server's accept or reject results.
4. Leave request: open Leave, choose type and dates, see the balance effect, attach a document, submit, receive the manager's decision as a push notification.
5. Supervisor task cycle: assign a task with evidence and location rules, receive the submission, view evidence and map result, return for rework with a comment, approve the resubmission.
6. Manager approval: open the approvals inbox from a notification, review context, approve or reject.
7. HR exception review: open the HR queue, filter exceptions by location, review each with distance and reason, decide, escalate if needed.
8. Executive check: open the executive workspace, read indicators, drill to a department, view an aggregated field activity map where enabled.

### 12F.5 Role-specific variations

| Role | Additional functions beyond the employee experience |
| --- | --- |
| Line Manager, Supervisor | Team workspace: team attendance and exceptions, leave and task approvals, task assignment, evidence and map review |
| Department Head | Department view of the same, with department dashboards |
| HR Administrator, HR Business Partner | HR workspace queues and dashboards within assigned scope |
| Regional Manager | Region view of dashboards and queues |
| Senior Management | Executive workspace with aggregated indicators and controlled drill-down |
| Tenant Administrator | Device management and mobile policies are on the web console; the application shows only the user's own devices |

Dependencies that shape navigation: Tasks require the TFA capability to be enabled; payslips require payroll to be licensed; learning, performance and internal jobs require their modules; the map requires the geospatial service and the GPS evidence permission; executive indicators require the semantic layer.

## 12G. Mobile APIs and integration architecture (API), new v0.3

The mobile application uses secure, versioned HRMIS APIs built on the kernel API conventions (document 2, INT; KER-06). Detailed contracts belong to the technical design; this section fixes the functional behavior those contracts must provide.

### 12G.1 API groups

| Group | Purpose and main operations | Authorization and validation | Idempotency and offline | Audit |
| --- | --- | --- | --- | --- |
| Authentication and session | Sign in, MFA, token refresh, sign out, recovery | Credentials, MFA, device binding, lockout rules | Not offline | Sign-ins, failures, revocations |
| Device registration | Register, list, revoke, update push token | User and device policy; administrator for revoke | Not offline | Registration and revocation events |
| Employee profile | Read profile, submit change requests, read allowed fields | Field security, data scope; sensitive changes via workflow | Read cached; change drafts queued | Profile views of sensitive fields, change requests |
| Attendance events | Submit check-in, check-out, breaks, with location and device data | Policy mode, accuracy rules, sequence rules, server validation | Idempotency key per event; offline queue with capture time | Every event and result |
| Attendance history | Daily results, calendar, exceptions, regularization | Own records or team within scope | Cached read | Access to location evidence |
| Geo-fence validation | Evaluate a position against assigned locations; read applicable locations | Policy; no evidence beyond what the user may see | Optional preliminary check online; final result by server | Validation results |
| Leave | Balances, types, apply, cancel, track, team calendar | Policy, balance and eligibility checks, data scope | Draft only offline | Applications and decisions |
| Employee documents | List, view, download, request, upload | Document permissions and retention | Selected files cached encrypted | Views and downloads of sensitive documents |
| Payroll and payslips | Payslips, salary history, earnings and deductions, tax statements | Payroll permission, step-up authentication | Not offline by default | Every view |
| Tasks | List, read, acknowledge, start, update, submit, rework, review | Assignment scope, status model, reviewer authority | Idempotent updates; offline queue | Task activity and reviews |
| GPS event capture for tasks | Submit location events linked to task events | Task location policy, validation | Idempotent with capture time | Location events and results |
| Evidence and attachments | Chunked upload, scan status, thumbnails, download | Type and size limits, scanning, permissions | Resumable chunks; retry | Uploads and views |
| Notifications | List, read state, preferences, test | Own notifications | Cached list | Preference changes |
| Manager approvals | Inbox, approve, reject, return, comment | Workflow authority and data scope | Online only | Decisions with reasons |
| HR service requests | Create, attach, track, respond | Category permissions, workflow | Draft and queued creation | Requests and responses |
| Mobile dashboards | Home widgets, team, HR and executive indicators | Role, data scope, privacy settings | Last snapshot cached | Dashboard access to sensitive drill-downs |
| Offline synchronization | Pull changes by scope, push queued events, report conflicts and results | Scope definitions, per-event validation | Delta tokens; idempotent push | Sync runs and conflicts |

### 12G.2 Requirements common to all groups

- **FR-HRM-API-01** All mobile APIs shall be versioned; the server shall support the current and previous major version of the application API for at least one release (KER-06). (M, new v0.3)
- **FR-HRM-API-02** Errors shall use the kernel problem-details model with stable error codes the application can translate; validation errors shall identify fields. (M, new v0.3)
- **FR-HRM-API-03** Every write that can be retried shall accept an idempotency key and produce one result for repeated requests (OFF-06). (M, new v0.3)
- **FR-HRM-API-04** Requests shall carry both the capture time and the device time where relevant; responses shall return the server receipt time and the validation result (GHR-13). (M, new v0.3)
- **FR-HRM-API-05** Authorization shall be enforced on the server for every request, using the same Kernel permissions and data scopes as the web (GHR-12); responses shall contain only fields the user may see. (M, new v0.3)
- **FR-HRM-API-06** Input validation shall be strict (types, ranges, coordinate validity, file types and sizes) and independent of client checks. (M, new v0.3)
- **FR-HRM-API-07** Rate limiting and abuse detection shall apply per user, device and tenant with clear responses and retry hints. (M, new v0.3)
- **FR-HRM-API-08** List endpoints shall use cursor pagination and support conditional requests so the application can refresh cheaply. (M, new v0.3)
- **FR-HRM-API-09** Synchronization endpoints shall support delta retrieval by scope, resumable upload of queued events and attachments, and per-item results (accepted, rejected with reason, in review). (M, new v0.3)
- **FR-HRM-API-10** Audit records shall be written for the operations listed in the table, with correlation identifiers that tie mobile requests to server actions. (M, new v0.3)
- **FR-HRM-API-11** Public documentation (interface description, examples, error catalogue, versioning and deprecation policy) shall be published per version, with a sandbox per tenant environment for partners. (S, new v0.3)
- **FR-HRM-API-12** Contract tests shall verify the application against the API on every release, including the previous application version. (M, new v0.3)
- **FR-HRM-API-13** Logs and traces shall exclude sensitive values (credentials, tokens, salary, coordinates unless explicitly enabled for diagnostics with expiry). (M, new v0.3)
- **FR-HRM-API-14** The mobile APIs shall be the same APIs available to other approved clients and integrations, subject to client permissions; no hidden mobile-only business endpoints shall bypass rules. (M, new v0.3)

## 13. Dashboards and reports, basic (RPT), wave A1, extended in v0.3

HR reporting uses the kernel semantic layer and builders (document 9); this section defines standard content.

- **FR-HRM-RPT-01** Dashboards shall include headcount, joiners and leavers, demographics, attrition, attendance, leave liability, and contract and probation expiries.
- **FR-HRM-RPT-02** Standard reports shall include employee master, strength by unit, grade and location, movement report, birthdays and anniversaries, document expiry, leave balance, leave utilization and attendance summaries.
- **FR-HRM-RPT-03** All standard reports shall be cloneable and editable through the report builder.
- **FR-HRM-RPT-04** HR semantic models shall respect data scopes and hide compensation fields unless the user has salary permission. (new)
- **FR-HRM-RPT-05** Attendance and location reports and dashboards listed in section 13A shall be standard content of the module. (M, new v0.3)
- **FR-HRM-RPT-06** Task and field activity reports and dashboards listed in section 13A shall be standard content of the module (TFA-55 to TFA-58). (M, new v0.3)
- **FR-HRM-RPT-07** Mobile application reports for administrators (devices, usage, authentication, synchronization, notifications) listed in section 13A shall be standard content. (M, new v0.3)
- **FR-HRM-RPT-08** Executive reporting listed in section 13A shall be available on web and in the senior management workspace (WSP-27 to WSP-34). (M, new v0.3)
- **FR-HRM-RPT-09** Reports and maps that use location data shall run through the semantic layer, apply the GPS evidence permission and data scopes, write access to the access log, and follow export controls and approval (ATT-72, ATT-73, ATT-78). (M, new v0.3)
- **FR-HRM-RPT-10** Location and task reporting shall be aggregated by default; individual-level views shall be restricted to the employee, their reporting line and authorized HR, and shall never present GPS presence or event counts as productivity (TFA-56). (M, new v0.3)

## 13A. Reporting catalogue for v0.3 (new v0.3)

Every item below respects data scopes, field security, location privacy controls and export controls; each is cloneable through the report builder and described by a visible definition of its measures.

### 13A.1 Attendance and location

| Report or dashboard | Content | Main audience |
| --- | --- | --- |
| Attendance by date and location | Present, absent, late, remote and field duty counts per day and location, with trends | Supervisors, managers, HR |
| Attendance by department and employee | Attendance status per department and per employee for a period | Managers, HR |
| Late arrivals and early departures | Counts, minutes, repeat patterns, reasons | Managers, HR |
| Geo-fence exceptions | Exceptions by type, location, distance band, status and age | Supervisors, HR |
| Attendance regularization | Requests, decisions, turnaround, approvers | HR, managers |
| Field-duty attendance | Field-duty attendance by assignment, area and approval status | Supervisors, regional managers |
| Attendance event location distribution | Aggregated event distribution by location with inside and outside shares; event-level map for authorized users | HR, managers with evidence permission |

### 13A.2 Tasks and field activities

| Report or dashboard | Content | Main audience |
| --- | --- | --- |
| Task assignment and completion | Tasks assigned and completed by employee, team, department and manager | Managers, HR |
| Overdue tasks | Open tasks past due with age, owner and reviewer | Supervisors, managers |
| Employee task activity | Task history and status for an employee, within scope | Employee, line manager, HR |
| Departmental task performance | Completion against due date, rework rate and turnaround by department and location | Department heads, senior management |
| Field activity by geographical area | Task counts and results by region, branch or area; event-level map for authorized users | Regional managers, senior management |
| Task completion turnaround | Time from assignment to completion by task type | Managers |
| Review and approval turnaround | Time reviewers take to decide, rework cycles | Managers, HR |

### 13A.3 Mobile application

| Report or dashboard | Content | Main audience |
| --- | --- | --- |
| Active registered devices | Devices by platform, version, status and last use; revoked devices | Tenant administrators |
| Application usage | Active users by function and role, adoption by entity | HR, tenant administrators |
| Sign-in and authentication trends | Sign-ins, failures, MFA outcomes, lockouts, suspicious patterns | Tenant administrators, security |
| Offline synchronization status | Pending items, success rate, sync age, conflicts | Tenant administrators, support |
| Failed synchronization events | Failed events with reason, age and employee impact | Support, HR |
| Notification delivery status | Sent, delivered, failed by channel and event | Tenant administrators |

### 13A.4 Executive reporting

| Report or dashboard | Content | Main audience |
| --- | --- | --- |
| Workforce attendance | Attendance rate, absenteeism and late arrivals by period, department and region | Senior management |
| Workforce distribution | Headcount by department, location, grade and employee type | Senior management |
| Employee availability | Present, on leave, on field duty and remote now and next week | Senior management, regional managers |
| Leave utilization | Leave taken against entitlement by type and department | HR, senior management |
| Field workforce activity | Aggregated task and attendance activity of field staff by area | Regional managers, senior management |
| Task completion and overdue indicators | Completion rate and overdue counts by department | Senior management |
| Departmental and regional comparisons | Comparative indicators with targets, within scope | Senior management |

## 14. Payroll (PAY), wave A2

**Purpose:** calculate, approve and pay salaries accurately under configurable structures and statutory rules. **Actors:** Payroll Officer, Payroll Approver, Finance Approver, Employee.

**Setup**

- **FR-HRM-PAY-01** Pay groups shall define frequency (monthly, semi-monthly, bi-weekly, weekly), calendar, cut-off dates, payment dates, currency and entity; an employee belongs to exactly one pay group at a time.
- **FR-HRM-PAY-02** Pay components shall be unlimited and typed as earning, deduction, employer contribution, reimbursement or informational, each with taxable, pensionable, EOBI-able and other statutory-base flags configured by pack.
- **FR-HRM-PAY-03** Component calculation methods shall include fixed amount, percentage of another component, formula, slab or table lookup, per-day or per-hour rate, attendance-linked, and manual input.
- **FR-HRM-PAY-04** Salary structures shall bundle components with order of evaluation, assignable by grade, employee type or individually, effective-dated.
- **FR-HRM-PAY-05** Pro-rata rules (calendar days, working days, fixed 30 days, custom) shall be configurable per component for joiners, leavers, unpaid leave and mid-month changes.
- **FR-HRM-PAY-06** Salary revisions shall record old and new structure, effective date and reason; backdated revisions shall compute arrears automatically over affected periods.

**Inputs**

- **FR-HRM-PAY-07** Payroll inputs shall come from locked attendance, leave without pay, overtime, loans and advances, approved expense reimbursements, bonuses, incentives and one-time adjustments, and shall be importable from files.
- **FR-HRM-PAY-08** Bonuses (Eid, annual, performance, ad hoc) shall be defined with eligibility, basis and tax treatment and processed in regular or off-cycle runs.
- **FR-HRM-PAY-09** Payroll shall support holding an employee's salary with reason and releasing it in a later run.

**Run lifecycle**

- **FR-HRM-PAY-10** Run stages shall be create, import inputs, calculate, validate, review, approve, lock, generate bank files and payslips, post to accounting, and close; each stage records user and time.
- **FR-HRM-PAY-11** Validation shall flag negative net pay, missing bank data, missing tax IDs, changes above a configurable threshold versus the last run, duplicate payments, and employees without attendance data.
- **FR-HRM-PAY-12** A variance report shall compare current and previous runs by employee and component with drill-down to the cause.
- **FR-HRM-PAY-13** Payroll shall support dual approval (maker-checker) with thresholds by total amount or variance.
- **FR-HRM-PAY-14** A run may be recalculated for a subset of employees; a locked run may be reopened only with elevated permission and an audit reason.
- **FR-HRM-PAY-15** Off-cycle runs (bonus, correction, final settlement, advance) shall be supported without disturbing the regular run.
- **FR-HRM-PAY-16** Retroactive changes discovered after lock shall produce correction entries in the next open run and never overwrite a closed run.

**Tax and statutory (country pack)**

- **FR-HRM-PAY-17** Income tax shall be computed from effective-dated slab tables by tax year (July to June for Pakistan), including rebates and surcharge, using projected annual income with monthly adjustment and year-end true-up.
- **FR-HRM-PAY-18** Tax on bonuses, arrears, leave encashment, gratuity and other irregular payments shall follow configurable treatment (taxed in the month, averaged, or separate rate).
- **FR-HRM-PAY-19** Tax exemptions and credits shall be capturable through employee declarations with proof upload and HR verification.
- **FR-HRM-PAY-20** Statutory contributions shall be calculated from configurable bases and rates (Pakistan: EOBI employee and employer, provincial social security, Workers Welfare Fund, Workers Profit Participation Fund, provident fund, gratuity accrual).
- **FR-HRM-PAY-21** Rates, ceilings and bases shall come from pack tables, overridable per tenant; no statutory rate shall be hard-coded.
- **FR-HRM-PAY-22** Previous-employer income and tax paid shall be captured for mid-year joiners and included in tax computation.

**Disbursement**

- **FR-HRM-PAY-23** Salary payment files shall be generated per bank format (configurable templates), including bulk and inter-bank formats, validated before download.
- **FR-HRM-PAY-24** Payment modes shall include bank transfer, cheque, cash and mobile wallet, per employee, with split by percentage or amount.
- **FR-HRM-PAY-25** Payment status shall be updatable by import of bank files (paid, failed), with failed payments queued for correction and re-payment.

**Final settlement**

- **FR-HRM-PAY-26** Final settlement shall compute pending salary, leave encashment, gratuity or end-of-service benefit, notice-period shortfall or payment, loan and advance recovery, asset recovery, bonus and tax adjustment.
- **FR-HRM-PAY-27** Settlement shall depend on offboarding clearance status and produce a settlement statement and a full-and-final letter for acknowledgement.

**Outputs**

- **FR-HRM-PAY-28** Payslips shall be generated from designable bilingual templates, delivered as password-protected PDF by email and shown in self-service, with year-to-date totals.
- **FR-HRM-PAY-29** Payroll reports shall include payroll register, component summary, bank advice, department and cost-center costing, statutory summaries, tax summary, employer cost, and headcount-to-payroll reconciliation.
- **FR-HRM-PAY-30** Accounting integration shall publish a PayrollRunFinalized event with totals by component, cost center, department, project and company; Finance posting rules (or the Accounting Export connector) create journals; employee-level detail remains in HRMIS and is reachable by drill-down.

**Engine design and control (new)**

- **FR-HRM-PAY-31** The calculation engine shall be deterministic: the run freezes a snapshot of inputs (employee data, structures, attendance, tables, rules versions), so a recalculation with the same snapshot gives identical results. (new)
- **FR-HRM-PAY-32** Each result shall carry a calculation trace (inputs, formulas, rule versions, rounding) viewable by authorized users. (new)
- **FR-HRM-PAY-33** Results of a locked run shall be immutable; corrections are new entries in later runs referencing the original. (new)
- **FR-HRM-PAY-34** Calculation shall run as a chunked, resumable job with progress, meeting GHR-01 (10,000 employees in under 15 minutes). (new)
- **FR-HRM-PAY-35** A parallel-run tool shall import a legacy system's results, compare by employee and component against this system, classify differences, and produce a tolerance report used for go-live decisions. (new)
- **FR-HRM-PAY-36** A what-if simulation shall estimate the payroll effect of a salary revision, policy or tax change before activation. (new, S)
- **FR-HRM-PAY-37** Multi-entity and multi-currency payroll shall be supported, each entity with its own calendar, pay groups, bank accounts and posting dimensions. (new)
- **FR-HRM-PAY-38** A payroll calendar with cut-off reminders and a readiness checklist (attendance locked, loans updated, claims approved, bank data complete) shall guide each run. (new)
- **FR-HRM-PAY-39** Payroll records shall be retained and archived according to pack-defined legal periods, with restricted access and audit. (new)
- **FR-HRM-PAY-40** Payslips and payroll information shall be available through the mobile application under the controls of MOB-80 to MOB-83 and MSEC-09, with the same permissions and masking as on the web. (M, new v0.3)
- **BR-09** A payroll period cannot be processed if attendance is not locked, unless the Payroll Approver overrides with a reason.
- **BR-10** Net pay is non-negative; recoveries exceeding net pay carry forward by a configured rule.
- **BR-11** Any change to a component, structure or statutory table applies from its effective date only and never modifies closed runs.
- **AC:** a 1,000-employee run with three salary structures, overtime, a mid-month joiner, an arrears case and a bonus produces payslips, a bank file and a Finance journal that reconcile exactly, and every figure traces to its inputs and formulas.

## 15. Loans, advances and expenses (LON, EXP), wave A2

### 15.1 Loans and advances

- **FR-HRM-LON-01** Loan and advance types shall be configurable with eligibility (tenure, grade, existing loans), maximum amount (fixed or multiple of salary), interest (none, flat, reducing balance) and maximum tenure.
- **FR-HRM-LON-02** Applications shall route through approval workflows with finance review and optional guarantor.
- **FR-HRM-LON-03** Approved loans shall generate an installment schedule; recovery shall flow automatically into payroll.
- **FR-HRM-LON-04** Employees shall request early settlement, skip or restructuring; HR can hold or adjust installments with audit.
- **FR-HRM-LON-05** Outstanding loans shall appear in final settlement and offboarding clearance.
- **FR-HRM-LON-06** Perquisite tax on interest-free or concessional loans shall be configurable by country pack. (S)
- **FR-HRM-LON-07** Disbursement shall publish LoanDisbursed for Finance (employee receivable and payment) and installments recovered through payroll shall reduce it; the loan ledger shall reconcile to the Finance control account when licensed. (new)

### 15.2 Expenses and travel

- **FR-HRM-EXP-01** Expense categories shall carry limits by grade, per-diem and mileage rules, receipt requirements and tax treatment.
- **FR-HRM-EXP-02** Employees shall submit claims with receipts (camera capture on mobile, OCR to prefill amounts), multi-currency, project and cost center coding.
- **FR-HRM-EXP-03** Policy checks at submission shall flag over limit, duplicate receipt, missing receipt and out-of-period items for approvers.
- **FR-HRM-EXP-04** Travel requests shall support itinerary, advance and estimate, approval and settlement against actual expenses.
- **FR-HRM-EXP-05** Approved claims shall be reimbursed through payroll or a separate payment batch.
- **FR-HRM-EXP-06** Approved claims shall publish ExpenseClaimApproved so Finance posts the expense and employee payable, and payment status shall return to the claim. (new)
- **FR-HRM-EXP-07** When Finance is licensed, budget availability shall be checked on submission or approval per the budget policy (document 5, BUD-06). (new, wave B)
- **FR-HRM-EXP-08** Corporate card statements shall import and match to claims by reference, amount and date. (new, S)

## 16. Compensation and benefits (CMP), wave A2

- **FR-HRM-CMP-01** Salary bands per grade shall be maintained with minimum, midpoint and maximum; compa-ratio and range penetration shall be reported.
- **FR-HRM-CMP-02** Increment cycles shall be created with budget, eligibility rules, manager recommendations, HR review, approval and bulk application with arrears.
- **FR-HRM-CMP-03** Allowances and perquisites (vehicle, fuel, mobile, housing, medical, education) shall be assignable by grade or individually with caps, taxable treatment and proof requirements.
- **FR-HRM-CMP-04** Provident fund shall maintain employee and employer contributions, profit allocation, loans against the fund, withdrawals and statements; gratuity shall maintain accrual and payout by policy.
- **FR-HRM-CMP-05** Medical and insurance shall record plans, covered dependents, premiums, claims and employee deductions.
- **FR-HRM-CMP-06** Total rewards statements shall summarize pay, benefits and employer contributions per employee, generated as documents.
- **FR-HRM-CMP-07** Gratuity, leave encashment and similar liabilities shall be computed per period and published for Finance accrual when licensed. (new)

## 17. Statutory compliance and government reporting (CPL), wave A2 for Pakistan

- **FR-HRM-CPL-01** A compliance calendar shall list statutory deadlines per entity with owners, reminders and completion status.
- **FR-HRM-CPL-02** The module shall generate registers and returns in prescribed formats (Pakistan: muster roll, wage register, EOBI contribution lists, social security returns, withholding tax statements, annual employee tax statements and tax certificates).
- **FR-HRM-CPL-03** A compliance risk dashboard shall flag minimum-wage breaches, missing statutory IDs, expired contracts, missing registrations and overdue filings.
- **FR-HRM-CPL-04** Statutory formats shall be maintained in country packs and updated through staged pack releases (FR-HRM-POL-05).
- **FR-HRM-CPL-05** Employee tax certificates and annual statements shall be available in self-service. (new)
- **AC:** after a budget-year tax slab change is activated, a test run on 20 sample employees shows the difference per employee before approval, and the next payroll uses the new slabs from the effective month.

## 18. Recruitment and applicant tracking (REC), wave B

**Purpose:** fill positions from requisition to hire with a configurable pipeline. **Actors:** Hiring Manager, Recruiter, Interviewer, Candidate, HR Admin, Finance Approver.

- **FR-HRM-REC-01** Job requisitions shall be raised against a position or budgeted headcount, routed through approval, and blocked if no budget or vacancy exists unless an exception is approved.
- **FR-HRM-REC-02** Job postings shall use templates, custom fields and screening questions, and publish to the careers page and job boards through connectors.
- **FR-HRM-REC-03** A careers page builder shall provide branding, search, filters, language options and mobile-friendly application with resume upload and optional social or email login.
- **FR-HRM-REC-04** Resume parsing shall extract contact, education, experience and skills into the candidate profile and flag duplicates by email, phone and national ID.
- **FR-HRM-REC-05** Pipelines shall be configurable per job type with stages, stage owners, required actions, auto-rejection rules and templated communications.
- **FR-HRM-REC-06** Interview scheduling shall find panel availability, send invitations, support virtual links and record reschedules.
- **FR-HRM-REC-07** Interviewers shall complete structured scorecards with competencies and ratings; the system aggregates scores and shows a consensus view.
- **FR-HRM-REC-08** Assessments shall be attachable by stage through connectors or uploaded results. (S)
- **FR-HRM-REC-09** Reference and background checks shall be tracked with consent capture and results stored with restricted access.
- **FR-HRM-REC-10** Offers shall be generated from templates with compensation components, approved, sent for e-signature, and tracked through negotiation to acceptance.
- **FR-HRM-REC-11** An accepted offer shall convert into an employee record with prefilled data and trigger onboarding tasks.
- **FR-HRM-REC-12** Talent pools and rediscovery search over past candidates shall be available with retention and consent rules.
- **FR-HRM-REC-13** Government hiring option: merit scoring formulas, quota and reserved-seat rules, shortlist generation and public notification templates. (S)
- **FR-HRM-REC-14** Recruitment analytics shall report time-to-fill, time-in-stage, source effectiveness, cost-per-hire, offer acceptance rate and pipeline conversion.
- **FR-HRM-REC-15** Internal job postings and internal applications shall be available to employees through the mobile application, using this module's postings and applications (MOB-110, MOB-111). (S, new v0.3)
- **BR-12** Candidate data is erased or anonymized after the configured retention period unless consent for talent-pool storage exists.
- **BR-13** Where blind scoring is enabled, an interviewer cannot see other interviewers' scores before submitting their own.
- **AC:** a requisition approved on Monday publishes to the careers page the same day; 50 applicants move through a four-stage pipeline with scorecards, and the accepted candidate becomes an employee with onboarding tasks created.

## 19. Performance management (PRF), wave B

- **FR-HRM-PRF-01** Performance cycles shall be defined with type (annual, half-yearly, quarterly, continuous), participants by rule, phases and deadlines.
- **FR-HRM-PRF-02** Goal frameworks shall support KPIs, OKRs and balanced scorecards, with weights, targets, cascading from parent goals and progress updates.
- **FR-HRM-PRF-03** Appraisal forms shall be built with the form designer: sections, competencies, rating scales, comments and mandatory fields.
- **FR-HRM-PRF-04** Review types shall include self, manager, skip-level, peer and 360 with anonymity options and minimum respondent thresholds.
- **FR-HRM-PRF-05** Calibration sessions shall display rating distributions by unit and manager, allow adjustment with justification, and optionally enforce distribution guidelines.
- **FR-HRM-PRF-06** A 9-box grid (performance versus potential) shall be generated from review data with manual adjustments.
- **FR-HRM-PRF-07** Continuous feedback, check-in notes, one-on-one agendas and recognition shall be available between cycles.
- **FR-HRM-PRF-08** Performance improvement plans shall carry objectives, milestones, review dates and outcomes with HR oversight.
- **FR-HRM-PRF-09** Outcomes shall feed increment, bonus and promotion proposals in compensation cycles (FR-HRM-CMP-02) with eligibility rules.
- **FR-HRM-PRF-10** Government option: confidential report routing through reporting, countersigning and reviewing officers with controlled visibility. (S)
- **FR-HRM-PRF-11** Employee and manager performance activities shall be available on mobile within the permissions and rules of this module (MOB-130, MOB-131). Task and location data shall not feed performance ratings automatically (TFA-56, TFA-59). (S, new v0.3)
- **AC:** a half-yearly cycle runs for 300 employees with self, manager and peer reviews; calibration changes are recorded with reasons; final ratings drive an increment proposal list.

## 20. Learning and development (LND), wave B

- **FR-HRM-LND-01** A catalog of courses (internal, external, online) shall hold description, provider, duration, cost, prerequisites and competencies addressed.
- **FR-HRM-LND-02** Sessions shall have schedule, venue, trainer, capacity and waitlist; employees enroll or are nominated with approval and budget check.
- **FR-HRM-LND-03** Attendance, assessments, certificates and feedback shall be recorded in the employee learning record.
- **FR-HRM-LND-04** Mandatory training by role, location or rule shall have due dates, reminders and compliance reports.
- **FR-HRM-LND-05** Certifications and licenses shall have expiry tracking with renewal reminders and links to required training.
- **FR-HRM-LND-06** Training bonds shall record obligation period and recovery terms, flowing recovery into final settlement.
- **FR-HRM-LND-07** Training needs analysis shall suggest courses from appraisal gaps and skill matrices.
- **FR-HRM-LND-08** Learning content connectors (LMS, video libraries) shall provide single sign-on and completion sync. (S)
- **FR-HRM-LND-09** Training cost reporting shall compare budget and actual by unit, course and period.
- **FR-HRM-LND-10** Assigned training, schedules, progress, completion status, certificates and reminders shall be available on mobile (MOB-120). (S, new v0.3)

## 21. Employee relations, discipline and helpdesk (ERL), wave B

- **FR-HRM-ERL-01** Case management shall support grievances, complaints and incidents with confidential handling, restricted case teams and tamper-evident notes.
- **FR-HRM-ERL-02** The disciplinary process shall be stage-configurable: allegation, show-cause notice, reply deadline, inquiry, hearing, decision and appeal, each with templates and legal timelines.
- **FR-HRM-ERL-03** Harassment and sensitive cases shall use dedicated routing, committee assignment and anonymous reporting where enabled.
- **FR-HRM-ERL-04** Union membership and collective agreement terms shall be recordable and applicable to relevant policies where the tenant uses them. (C)
- **FR-HRM-ERL-05** The HR helpdesk shall provide categories, SLAs, routing, knowledge base, canned responses, satisfaction surveys and reports.
- **FR-HRM-ERL-06** Surveys (pulse, engagement, exit) shall support anonymity, segmentation, scheduling, reminders and result dashboards with minimum group size.
- **FR-HRM-ERL-07** Announcements, polls and the employee directory shall be targetable by entity, unit, location and role.
- **FR-HRM-ERL-08** The HR helpdesk shall be available on mobile and shall use the same case engine, categories and service levels as the web (MOB-100 to MOB-102); grievance and disciplinary cases shall not be created or viewed by case teams through unrestricted mobile lists. (M, new v0.3)
- **BR-14** Case records are visible only to assigned case members and designated auditors; access is logged and exports are restricted.

## 22. Asset custody (AST), wave B

The asset register, acquisition, depreciation and maintenance belong to the Assets module (document 8). HRMIS owns the employee side.

- **FR-HRM-AST-01** HR shall issue assets to employees and record return with acknowledgement, using the Assets module register when licensed, or a lightweight HR register (type, serial number, status, condition) otherwise.
- **FR-HRM-AST-02** Unreturned assets shall appear in offboarding clearance and final settlement with recovery value.
- **FR-HRM-AST-03** Access cards, parking, lockers, desk booking and transport routes shall be manageable as bookable or assignable resources. (S)
- **FR-HRM-AST-04** Uniform and consumable issuance shall be tracked with entitlement cycles, using Inventory when licensed. (S)
- **FR-HRM-AST-05** Migrating the lightweight register to the Assets module shall be supported by an import template and link repair. (new)
- **FR-HRM-AST-06** Employees shall see the assets assigned to them and acknowledge receipt or request return on mobile and web (document 8, CST-04). (S, new v0.3)

## 23. Workforce planning and succession (WFP), wave D

- **FR-HRM-WFP-01** Headcount plans shall be created per unit with budget, growth assumptions and scenarios, linked to positions and requisitions (and to Finance budgets when licensed).
- **FR-HRM-WFP-02** Succession plans shall identify critical roles, successors, readiness ratings and vacancy risk, with talent review sessions.
- **FR-HRM-WFP-03** A skills inventory shall be maintained from profiles, learning and reviews, with internal mobility matching to open roles.

## 24. Saudi Arabia pack and requirements (KSA), wave B

All items are planning inputs to be verified by a qualified Saudi labor and payroll practitioner before design is frozen. Statutory values live in the pack, not in code. (new)

- **FR-HRM-KSA-01** Employee records shall support national ID or residence permit (iqama) numbers with expiry, passport, work permit and visa data, sponsor or establishment, nationality, and GOSI number.
- **FR-HRM-KSA-02** Social insurance contributions shall be computed from pack tables that differ by nationality, with contribution base rules, caps and monthly return files.
- **FR-HRM-KSA-03** A wage protection file shall be generated in the required bank and platform format, with submission status, reconciliation and a compliance dashboard.
- **FR-HRM-KSA-04** Saudization (national workforce classification) reporting shall compute ratios by entity and activity, with alerts and what-if hiring analysis.
- **FR-HRM-KSA-05** End-of-service benefit calculation shall follow pack rules by termination type, contract type and tenure, feeding final settlement and Finance accrual.
- **FR-HRM-KSA-06** Working-time and leave rules (weekend pattern, Ramadan hours, annual and sick leave tiers, Hajj, maternity and other statutory leave) shall be pack defaults with Hijri holiday calendars.
- **FR-HRM-KSA-07** Contract types, probation limits, notice periods and termination reasons shall be configured by pack, with export or connector support for contract registration with national platforms.
- **FR-HRM-KSA-08** Residence permit, visa, exit and re-entry and renewal tracking shall support workflows, reminders, fees and cost allocation per employee.
- **FR-HRM-KSA-09** Medical insurance coverage for employees and dependents shall be tracked against regulatory minimums with expiry alerts.
- **FR-HRM-KSA-10** Payslips, letters and certificates shall be bilingual (Arabic and English) with right-to-left layout and Hijri or Gregorian dates by company setting.
- **FR-HRM-KSA-11** Payroll periods shall support Gregorian or Hijri month basis by company, and proration rules for each.
- **FR-HRM-KSA-12** Hosting, personal-data handling and cross-border transfer settings shall follow the Saudi data protection requirements confirmed with local counsel, including regional hosting options.
- **FR-HRM-KSA-13** Mobile attendance, location capture and task evidence for Saudi entities shall follow the data protection and labor requirements confirmed with local counsel (MD-15), including the ability to disable location capture (ATT-82), Arabic and right-to-left mobile screens, and Hijri date display. (M, new v0.3)
- **AC:** a Saudi entity with Saudi and non-Saudi employees runs payroll with correct social insurance by nationality, produces the wage protection file, and shows the Saudization ratio and an end-of-service calculation for a terminated employee that a local practitioner confirms.

## 25. United States readiness (USD, USL, USP, USB, USC, USS), wave C

**Principle:** United States requirements are delivered as a country pack plus specific modules. Where a statutory engine is complex (tax calculation and filing, benefits carriers), the product integrates a certified partner through the connector framework rather than rebuilding it.

### 25.1 Entity and employee data

- **FR-HRM-USD-01** US legal entities shall capture EIN, state registrations, unemployment insurance accounts, workers compensation and payroll calendars.
- **FR-HRM-USD-02** US employee data shall include SSN (encrypted, masked, restricted view), work state and residence state, work authorization, federal W-4 and state withholding forms, and emergency contacts.
- **FR-HRM-USD-03** Worker classification shall distinguish W-2 employees and 1099 contractors, exempt and non-exempt status, and union status, with rules for each.
- **FR-HRM-USD-04** Employees working in multiple states shall be supported, with allocation of work days for tax and policy purposes.
- **FR-HRM-USD-05** Pronouns and voluntary self-identification fields (EEO categories, veteran and disability status) shall be separated from general HR views and available only for compliance reporting.

### 25.2 Time, leave and absence

- **FR-HRM-USL-01** FLSA rules shall be configurable: overtime after 40 hours per week, daily overtime and double-time by state, meal and rest break rules, and premium pay.
- **FR-HRM-USL-02** PTO, vacation, sick and personal leave policies shall support accrual by hours worked, caps, carry-over and payout rules.
- **FR-HRM-USL-03** The US pack shall include state and local paid sick leave rules (accrual, usage, carry-over, reasons) and update through pack releases.
- **FR-HRM-USL-04** FMLA tracking shall determine eligibility, track entitlement, designate leave, generate required notices and support intermittent leave.
- **FR-HRM-USL-05** Jury duty, military leave and bereavement shall be supported with policy templates.
- **FR-HRM-USL-06** Timesheets shall support project, client and task coding, billable flag, approvals and lock for payroll (see FR-HRM-ATT-22).

### 25.3 Payroll through a partner

- **FR-HRM-USP-01** The system shall send employee, compensation, time and deduction data to the payroll partner and receive calculated results, taxes, deductions and net pay for display and reporting.
- **FR-HRM-USP-02** Federal, state and local tax calculation, filing, deposit and year-end forms (941, W-2, W-3, 1099-NEC) shall be performed by the partner; the product shows status and documents.
- **FR-HRM-USP-03** Pre-tax and post-tax deductions, garnishments, child support orders and benefit deductions shall be maintained and synced.
- **FR-HRM-USP-04** Payment methods shall include direct deposit (ACH), paper check and pay card, with split deposits.
- **FR-HRM-USP-05** Off-cycle runs, bonuses, supplemental wages, tip reporting and reimbursements shall be supported through the partner.
- **FR-HRM-USP-06** A payroll reconciliation view shall compare partner output with product inputs and flag discrepancies.
- **FR-HRM-USP-07** The partner abstraction shall allow switching partners per tenant without changing HR data models. (S)
- **FR-HRM-USP-08** Partner payroll results shall publish PayrollRunFinalized in the same form as native payroll so Finance posting rules work unchanged. (new)

### 25.4 Benefits administration

- **FR-HRM-USB-01** Plans (medical, dental, vision, life, disability, FSA, HSA, 401(k)) shall be configurable with eligibility, waiting periods, coverage tiers, rates and employer contributions.
- **FR-HRM-USB-02** Open enrollment shall run by period with decision support, dependents, beneficiary designation, confirmation statements and audit.
- **FR-HRM-USB-03** Qualifying life events shall open special enrollment windows with documents and deadlines.
- **FR-HRM-USB-04** Carrier file feeds shall transmit enrollments and changes in carrier formats; reconciliation reports highlight mismatches.
- **FR-HRM-USB-05** 401(k) contributions shall flow to the plan administrator with deferral limits and match calculations.
- **FR-HRM-USB-06** COBRA qualifying events shall be detected, notices generated and tracked.
- **FR-HRM-USB-07** ACA measurement, offer tracking and 1094-C and 1095-C data preparation shall be supported.

### 25.5 Compliance, privacy and hosting

- **FR-HRM-USC-01** Form I-9 shall be completed in Section 1 by the employee and Section 2 by an authorized representative with document capture, reverification dates and retention.
- **FR-HRM-USC-02** E-Verify submissions and results shall integrate through a connector. (S)
- **FR-HRM-USC-03** EEO-1 component data and reports shall be generated from self-identification and job category data.
- **FR-HRM-USC-04** New-hire reporting to state agencies shall be generated or transmitted through the partner.
- **FR-HRM-USC-05** OSHA injury and illness logs (300, 300A, 301) shall be maintained with reporting deadlines.
- **FR-HRM-USC-06** Required postings, policy acknowledgements and harassment training tracking by state shall be supported through learning compliance assignments.
- **FR-HRM-USC-07** Pay transparency, salary history and pay equity reports shall be configurable for states with such requirements.
- **FR-HRM-USS-01** US tenants shall be hosted in US regions with US-only support access options.
- **FR-HRM-USS-02** Controls shall align with SOC 2 Type II criteria; evidence collection and reports shall be supported.
- **FR-HRM-USS-03** State privacy requirements shall be supported through data access, correction and deletion workflows within legal retention rules.
- **FR-HRM-USS-04** Candidate and employee use of automated decision tools shall follow applicable bias-audit and notice requirements; such features are disabled by default where laws require review.
- **FR-HRM-USS-05** State and local requirements for notice and consent to electronic monitoring and location capture of employees shall be configurable by pack (notice text, acknowledgement, retention) and validated by counsel before US mobile attendance or task location features are enabled (MD-15). (M, new v0.3)
- **AC:** a US tenant with employees in three states runs payroll through the partner, shows state paid sick leave balances correct to the applicable local rule, completes I-9 for a new hire and produces EEO-1 data.

## 26. Intelligence and extensions (ANA, AIA, EXT), wave D with early AI in wave A

AI features run through the kernel AI Gateway (document 2, section 22) and follow its controls.

- **FR-HRM-ANA-01** Predictive models shall estimate attrition risk, absenteeism trends, time-to-fill and payroll cost projection, trained per tenant with transparency on features used. (wave D)
- **FR-HRM-ANA-02** Every prediction shall show contributing factors and be restricted to authorized roles; individual-level predictions are off by default.
- **FR-HRM-ANA-03** Pay equity analysis shall compare compensation across groups controlling for grade, tenure and location, with remediation planning.
- **FR-HRM-ANA-04** Natural-language query over permitted data shall return charts and tables respecting the user's data scope (through the kernel semantic layer). (wave A to B)
- **FR-HRM-AIA-01** An assistant shall answer employee questions on balances, policies, payslips and process steps using the employee's own data and the tenant's policy documents, with source references. (wave A)
- **FR-HRM-AIA-02** HR users shall draft letters, job descriptions, policy summaries and review summaries with the assistant and edit before use. (wave A to B)
- **FR-HRM-AIA-03** The assistant shall perform actions (apply for leave, raise a request) only after explicit user confirmation, through the same workflows and permissions as the UI.
- **FR-HRM-AIA-04** Tenants shall disable AI per module, restrict data used, and review logs of AI interactions; payroll and compensation data can be excluded entirely.
- **FR-HRM-AIA-05** AI shall not make or finalize hiring, disciplinary, termination or pay decisions; outputs in these areas are advisory with human review recorded.
- **FR-HRM-AIA-06** AI features, including those available in the mobile application, shall not use location history or task evidence to infer behavior, rank employees or trigger actions, and location data shall be excluded from AI context unless a tenant explicitly enables a named use and the user holds the GPS evidence permission. (M, new v0.3)
- **FR-HRM-EXT-01** Extension points (custom tabs, widgets, actions, scripts, marketplace) are platform capabilities defined in the kernel and later waves; HRMIS exposes its objects and events to them. (wave D)

## 27. Data model overview

### 27.1 Baseline model (v0.2, retained)

Tables live in the hrm schema (kernel references as in document 3). Sensitive columns are encrypted; effective-dated tables use exclusion constraints; ledgers and results are append-only.

| Area | Main tables | Notes |
| --- | --- | --- |
| People | persons, person\_identifiers (encrypted, blind index), person\_contacts, person\_addresses, dependents, emergency\_contacts, education, experience, skills, certifications | One person, many employments |
| Employment | employments, employment\_assignments (effective-dated), employment\_events, contracts, probation\_records | Reason codes on each change |
| Structure | positions, position\_versions, grades, bands, jobs, job\_versions | Org units are kernel tables |
| Pay assignment | compensation\_assignments (effective-dated), employee\_bank\_accounts (effective-dated, encrypted) | Salary visibility by field policy |
| Policies | policies, policy\_versions, policy\_assignments | Resolved through kernel rules |
| Attendance | devices, geo\_fences, shifts, rosters, roster\_lines, punches (partitioned, append-only), attendance\_days, regularization\_requests, overtime\_entries, timesheets | Extended in section 27.2 (v0.3) |
| Leave | leave\_types, leave\_policies, leave\_ledger (append-only), leave\_requests, comp\_off\_entries | Balances derive from the ledger |
| Payroll | pay\_groups, pay\_components, salary\_structures, pay\_calendars, pay\_runs, pay\_run\_snapshots, pay\_inputs, pay\_results, pay\_result\_lines, pay\_traces, payment\_batches, final\_settlements | Results immutable after lock |
| Loans and expenses | loan\_types, loans, loan\_installments, expense\_categories, expense\_claims, claim\_lines, travel\_requests |  |
| Benefits and compliance | benefit\_plans, enrollments, provident\_fund\_ledger, gratuity\_accruals, compliance\_calendar, statutory\_returns |  |
| Talent | requisitions, candidates, applications, interview\_scores, offers, review\_cycles, goals, reviews, courses, sessions, enrollments\_learning, training\_bonds |  |
| Relations and custody | cases, case\_notes, tickets, surveys, asset\_custody | Restricted access |
| Outbox and inbox | outbox\_messages, inbox\_messages | Events of section 30 |

Note: the v0.2 table geo\_fences and the punches table are extended, not replaced, by section 27.2; the v0.2 name of the attendance event table (punches) is kept and described as Attendance Event.

### 27.2 New and extended logical entities (new v0.3)

Common rules for every entity below: the primary identifier is a time-ordered unique id (document 3 standards); every row carries the tenant database scope, the company, and the standard audit columns; organizational scope is derived from the company, organization unit and location of the employee or location concerned; sensitive coordinates and evidence follow GHR-02; no entity duplicates employee, organization, location, user, device, document or workflow master data owned by the Kernel or by existing HRMIS tables. Ownership column: New (HRMIS), Extends (existing HRMIS entity), Reuse (Kernel entity or service).

**Geo-attendance entities**

| Entity | Business purpose | Key attributes | Relationships | Ownership and reuse | Access, audit, retention |
| --- | --- | --- | --- | --- | --- |
| Attendance Location | Where attendance is valid | location id, type, name, status, effective dates, applicability rule reference | Extends kernel location (one to one); has geo-fences, assignments | Extends kernel Organization location; HRMIS adds a table | Location administrators; all changes audited; kept with effective-dated versions |
| Geo-fence | A validity boundary for a location | geo-fence id, shape type, radius or polygon reference, tolerance, effective dates | Belongs to a location; references a boundary | New (HRMIS); extends v0.2 geo\_fences | Same as location; versioned, never altered retroactively |
| Geo-fence Boundary | Stored geometry of a fence | boundary id, geometry, vertex count, validated flag | Referenced by geo-fences | Reuse: Kernel geospatial service (KER-01) | Same as location; versioned |
| Employee Location Assignment | Which employees may use which locations | employee or rule reference, location, primary flag, effective dates | Employee, location, policy | New (HRMIS) | HR and location administrators; audited; kept with employment history |
| Attendance Policy (location mode) | Mode A to D and parameters | policy type, mode, accuracy threshold, radius default, offline permission, reason and photo rules, effective dates | Extends policy framework; assigned by attribute rules | Extends v0.2 policies and policy\_versions | Tenant administrators and HR; audited; versions kept permanently |
| Field Duty Assignment | Mode D allowance for field work | employee, dates, allowed areas or sites, linked tasks, approver | Employee, locations, tasks | New (HRMIS) | Supervisors and HR; audited; kept with attendance records |
| Attendance Event | One captured attendance event with result | event id, employee, type, capture time, server receipt time, device time, validation result code, flags, status, idempotency key, device reference | Employee, location, policy version; one evidence record; zero or more exceptions | Extends v0.2 punches (partitioned, append-only) | Own, team and HR within scope; immutable once accepted; retained with attendance records |
| Attendance Location Evidence | Raw location and device facts of an event | evidence id, event, latitude, longitude, accuracy, resolved address, distance, network status, device and application data, photograph reference | One to one with an event | New (HRMIS); photograph via kernel documents (KER-08) | GPS evidence permission only; every view logged; separate shorter retention (KER-05, MD-06) |
| Attendance Exception | A flagged deviation needing review | exception id, event or day, type, severity, status, routed to, reason, thresholds met | Event, employee, workflow instance | New (HRMIS) | Employee, reviewers, HR; decisions immutable; retained with attendance records |
| Attendance Regularization | Employee correction request | request id, exception or day, requested outcome, reason, attachments, status | Extends v0.2 regularization\_requests | Extends v0.2 | Employee and approvers; audited; retained with attendance records |
| Attendance Approval History | Decisions on exceptions and regularizations | step, actor, decision, comment, previous and new values | Workflow instance, exception or request | Reuse: Kernel workflow history, referenced by id | Immutable; retained with the record |
| Location Validation Result | Outcome of geo-fence validation | result code, distance, accuracy used, rule applied | Value stored on the event and on task location events; detail in evidence | Value object, no separate table (avoids duplication) | Follows the event or evidence rules |
| Location Notice Acknowledgement | Proof the employee saw the location notice | employee, notice version, language, timestamp, device | Employee, notice document | New (HRMIS) | Employee and HR; kept for the life of the employment plus statutory period |

**Task and field activity entities**

| Entity | Business purpose | Key attributes | Relationships | Ownership and reuse | Access, audit, retention |
| --- | --- | --- | --- | --- | --- |
| Task Type | Defaults per kind of task | code, name, location policy, evidence rules, checklist form reference, review rule | Has templates and tasks | New (HRMIS) | Tenant administrators and HR; versioned |
| Task Template | Reusable task definition | type, instructions, checkpoints, evidence rules, recurrence | Task type | New (HRMIS) | Assigners; versioned |
| Task | The unit of work | task id, title, description, priority, dates, status, reviewer, location rule, project or activity reference, completion mode | Type, template, assignments, checkpoints, reviews | New (HRMIS) | Assigners, reviewers, assignees, within scope; audited; retained per MD-09 policy |
| Task Assignment | The act of assigning | assignment id, task, assigned by, scope (individual, team, department), time | Task, assignees | New (HRMIS) | Assigners and HR; audited |
| Task Assignee | One employee's instance of a task | assignee id, task, employee, status, acknowledged time, started time, submitted time | Task, employee, progress, evidence, location events | New (HRMIS) | The employee, reviewers, supervisors in scope; audited |
| Task Status History | Every status change | from, to, actor, reason, time | Task assignee | New (HRMIS), append-only | Same as task; immutable |
| Task Activity | Chronological activity feed | activity type, actor, summary, time | Task | New (HRMIS), append-only | Same as task; immutable |
| Task Progress Update | Employee progress reports | text, quantities, time spent, checklist answers | Task assignee | New (HRMIS) | Employee and reviewers; audited |
| Task Checkpoint | Milestones within a task | sequence, name, required evidence, completion time, result | Task, assignee completion records | New (HRMIS) | Same as task |
| Task Evidence | Photographs, documents, signatures | evidence id, event reference, document reference, type, size, scan status | Task assignee or checkpoint; kernel file | New link table; file in kernel Documents (KER-08) | Reviewers and employee; views logged for location-bearing media; retention per MD-09 |
| Task Location Event | GPS event at a configured task event | event type, capture time, receipt time, latitude, longitude, accuracy, validation result, device data | Task assignee, checkpoint | New (HRMIS), append-only | GPS evidence permission; every view logged; retention per KER-05 |
| Task Review | Reviewer decision | reviewer, decision, comment, time | Task assignee | New (HRMIS) | Reviewers and assignee |
| Task Comment | Discussion on a task | author, text, time, visibility | Task | Reuse: Kernel collaboration comments | Per task visibility; audited |
| Task Approval History | Workflow trail of review and approval | step, actor, decision, time | Workflow instance | Reuse: Kernel workflow history | Immutable |

**Mobile application entities**

| Entity | Business purpose | Key attributes | Relationships | Ownership and reuse | Access, audit, retention |
| --- | --- | --- | --- | --- | --- |
| Mobile Device and Device Registration | Registered device of a user | device id, user, platform, application version, trust state, risk signals, last seen, revoked flag | User; used by events and sync | Reuse: Kernel device registry (KER-03); HRMIS references it by id | Tenant administrators; registration and revocation audited; kept while the user is active plus an audit period |
| Mobile Session | Authenticated application session | session id, device, expiry, last use | Device, user | Reuse: Kernel identity sessions (KER-07) | Security administrators; audited |
| Mobile Notification Preference | Per-user notification choices | event group, channel, quiet hours | User | Reuse: Kernel notification preferences | The user and administrators |
| Mobile Synchronization Record | Result of a sync run | run id, device, started, finished, counts, conflicts | Device | Reuse: Kernel sync records | Support and administrators; short retention |
| Offline Event Reference | Links an offline-queued event to its server outcome | idempotency key, device, capture time, receipt time, resulting record reference, status, reason | Attendance events, task events, requests | New (HRMIS) | Employee, support; kept for a defined troubleshooting period |
| Mobile Application Audit Event | Audit of mobile activity | event type, actor, device, target, time | User, device, records | Reuse: Kernel audit and access logs | Auditors; retained per audit policy |

### 27.3 Data integrity rules (new v0.3)

- **FR-HRM-DAT-01** Attendance events, location evidence, task location events, status histories and decision histories are append-only; corrections are new records that reference the original. (M, new v0.3)
- **FR-HRM-DAT-02** Every event stores capture time and server receipt time separately, with the idempotency key and the policy version used for validation. (M, new v0.3)
- **FR-HRM-DAT-03** Location evidence and task location events are held apart from attendance results and task records, with stricter access and their own retention. (M, new v0.3)
- **FR-HRM-DAT-04** Evidence and event tables shall be partitioned by time with retention and purge jobs (document 3, section 8). (M, new v0.3)
- **FR-HRM-DAT-05** Employees, organization units, locations, users, devices, documents and workflow records shall be referenced by id and never copied (except immutable snapshots needed for audit). (M, new v0.3)
- **FR-HRM-DAT-06** Precise coordinates are classified sensitive; the encryption approach for coordinates versus aggregated analytics is decision MD-28; aggregated analytics shall use rounded or gridded locations. (M, new v0.3)
- **FR-HRM-DAT-07** Every new table shall pass the module table checklist of document 3, section 7.6. (M, new v0.3)

## 28. Baseline roles and permissions

### 28.1 Baseline matrix (v0.2, retained)

| Capability | Employee | Manager | HR Admin | Payroll Officer | Tenant Admin | Auditor |
| --- | --- | --- | --- | --- | --- | --- |
| View own profile and payslips | Yes | Yes | Yes | Yes | Yes | No |
| View team profiles | No | Yes, no salary | Scope | No | Scope | Read-only |
| Edit employment data | No | Request | Yes | No | Yes | No |
| Approve leave and attendance | No | Team | Override | No | No | No |
| View salary data | Own | No | Scope | Yes | No | Read-only |
| Run payroll | No | No | No | Yes | No | No |
| Approve payroll | No | No | No | Approver role | No | No |
| Configure workflows, fields, policies | No | No | Limited | No | Yes | No |
| View audit logs | No | No | Limited | Limited | Yes | Yes |
| Export bulk data | No | No | Approval | Approval | Approval | Approval |

Every cell is changeable through kernel permission sets (document 2, IAM-11).

### 28.2 Expanded permission matrix (new v0.3)

The Kernel data-scope model applies: own records, direct reports, team, organizational subtree, department, branch or location, company or entity, tenant-wide. A mobile client confers no additional access (GHR-12). Legend: Own = own records only; Team = direct reports and team; Dept = department subtree; Region = assigned locations or region; Scope = the scope granted to the role; Entity = company; Config = configurable by the tenant; blank dash = no access. Permission codes are proposals for the Kernel permission registry.

| Permission (proposed code) | Employee | Line Manager | Supervisor | Dept Head | HR Admin | HR Business Partner | Regional Manager | Senior Management | System Admin | Tenant Admin |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| View attendance (hrm.attendance.view) | Own | Team | Team | Dept | Scope | Scope | Region | Scope, aggregated | - | Scope |
| Record attendance (hrm.attendance.record) | Own | Own | Own | Own | Own | Own | Own | Own | - | Own |
| View GPS evidence (hrm.attendance.evidence.view) | Own | Config | Config | Config | Config | Config | Config | Config, off by default | - | Config |
| Manage locations and geo-fences (hrm.location.manage) | - | - | Temporary locations | - | Yes | - | - | - | - | Yes |
| Review attendance exceptions (hrm.attendance.exception.review) | - | Team | Team | Dept | Scope | Scope | Region | - | - | - |
| Approve regularization (hrm.attendance.regularize.approve) | - | Team | Team | Dept | Scope | Scope | Region | - | - | - |
| Create tasks (hrm.task.create) | Config | Team | Team | Dept | Scope | Scope | Region | - | - | - |
| Assign tasks (hrm.task.assign) | - | Team | Team | Dept | Scope | Scope | Region | - | - | - |
| View assigned tasks (hrm.task.view) | Own | Team | Team | Dept | Scope | Scope | Region | Scope, aggregated | - | Scope |
| Update tasks (hrm.task.update) | Own | Own | Own | Own | Own | Own | Own | Own | - | - |
| Review task submissions (hrm.task.review) | - | Team | Team | Dept | Scope | Scope | Region | - | - | - |
| View task locations on maps (hrm.task.map.view) | Own | Config | Config | Config | Config | Config | Config | Config, off by default | - | Config |
| View employee information (hrm.employee.view) | Own | Team, no salary | Team, no salary | Dept, no salary | Scope | Scope | Region, no salary | Aggregated | - | Scope |
| View payroll information (hrm.payroll.view) | Own | - | - | - | Scope if granted | - | - | Aggregated cost only | - | - |
| Approve leave (hrm.leave.approve) | - | Team | Team | Dept | Override | Scope | Region | - | - | - |
| Manage mobile devices (hrm.mobile.device.manage) | Own devices only | - | - | - | View | - | - | - | Infrastructure only | Yes |
| Manage mobile access and policies (hrm.mobile.policy.manage) | - | - | - | - | - | - | - | - | - | Yes |
| Access executive dashboards (hrm.dashboard.executive.view) | - | - | - | Department | Scope | Scope | Region | Yes | - | Config |

- **FR-HRM-ROL-01** All permissions of section 28.2 shall be registered in the Kernel permission registry and be assignable to roles through permission sets; baseline roles ship as editable defaults (document 2, IAM-10, IAM-11). (M, new v0.3)
- **FR-HRM-ROL-02** The same permission and data scope shall govern web, mobile, API and AI access to the same data (GHR-12, AIA-03). (M, new v0.3)
- **FR-HRM-ROL-03** Viewing GPS evidence and task locations shall be a distinct permission from viewing attendance or task status, off by default for senior management, and every use shall be logged. (M, new v0.3)
- **FR-HRM-ROL-04** Segregation of duties shall prevent a user from approving their own exceptions, regularizations, tasks or leave, and from reviewing a task they are assigned to (BR-ATT-C, BR-TFA-B). (M, new v0.3)
- **FR-HRM-ROL-05** Delegated approval and out-of-office substitution shall carry only the delegator's permissions within the delegation period and be audited. (M, new v0.3)
- **FR-HRM-ROL-06** A System Administrator shall have no access to business data by default; infrastructure actions on devices are limited to the mobile platform's operational functions. (M, new v0.3)

## 29. Integration catalogue

### 29.1 Baseline catalogue (v0.2, retained)

| Integration | Purpose | Wave | Method |
| --- | --- | --- | --- |
| Biometric devices and card readers | Attendance capture | A1 | Device drivers, push and pull |
| Email, SMS, WhatsApp gateways | Notifications and chat self-service | A1 | API |
| SSO and directory | Authentication, deprovisioning | A1 | OIDC, SAML, LDAP, SCIM |
| Banks (Pakistan, Saudi Arabia) | Salary files and status | A2, B | File formats, bank APIs |
| Finance module or accounting systems | Journals and payments | A2 | Events, Accounting Export connector |
| National ID verification | Where an authorized service exists | A2 | API |
| Government platforms (Saudi wage protection, social insurance, labor platforms) | Compliance filings | B | Files, connectors |
| Job boards, e-signature, calendar and video tools | Recruiting and offers | B | API |
| Directory and IT service desk | Account provisioning | B | SCIM, API |
| LMS and content libraries | Learning completion | B | LTI, API |
| US payroll and tax partner, benefits carriers, background checks, E-Verify | US readiness | C | Partner APIs, EDI |
| Collaboration tools | Notifications, approvals | A1 to C | API |

### 29.2 Additions for geo-attendance, tasks and mobile (new v0.3)

The mobile API groups themselves are listed in section 12G.1. The external and platform integrations below are used by those APIs.

| Integration | Purpose | Release | Method | Owner |
| --- | --- | --- | --- | --- |
| Geospatial service | Boundary storage, distance and boundary tests | M2 | Kernel service (KER-01) | Kernel |
| Map and geocoding providers | Address search and reverse geocoding (optional) | M2 | Provider adapters, configurable (MD-21) | Kernel integration framework |
| Push notification providers | Mobile push delivery | M1 | Provider adapters and device tokens (KER-04, MD-10) | Kernel notifications |
| SMS and WhatsApp gateways | Alerts and chat self-service on mobile | M1 to M3 | Provider adapters (MD-11) | Kernel notifications |
| Identity providers | Single sign-on for mobile sign-in | M1 | OIDC and SAML through Kernel identity | Kernel identity |
| Device risk and attestation signals | Rooted or jailbroken detection, application integrity | M2 | Device registry signals (KER-03) | Kernel sync framework |
| Enterprise mobile device and application management | Managed distribution, configuration, remote wipe | M1 onward | Managed configuration and wipe standards (MD-12) | Customer and Kernel |
| Map component and semantic layer | Dashboards and maps | M2 | Design system and analytics (KER-02) | Kernel analytics |
| Media pipeline | Evidence upload, scanning, compression, thumbnails | M2 | Documents service (KER-08) | Kernel documents |
| Document and policy retrieval for the assistant | AI answers with citations | M3 | AI Gateway and retrieval (document 9) | Kernel AI |

## 30. Events

### 30.1 Baseline events (v0.2, retained)

| Event | Direction | Consumers or sources |
| --- | --- | --- |
| PayrollRunFinalized, FinalSettlementApproved | Published | Finance or Accounting Export |
| ExpenseClaimApproved, LoanDisbursed | Published | Finance or Accounting Export |
| LeaveLiabilityCalculated, GratuityAccrualCalculated | Published | Finance (accruals) |
| AttendanceLocked, AttendanceReopened | Published | Payroll (internal), notifications |
| EmployeeHired, EmployeeChanged, EmployeeTerminated | Published | Kernel identity (provisioning), Assets, Finance (cost center), notifications |
| AssetIssued, AssetReturned | Consumed | Assets module |
| PaymentIssued, PaymentVoided | Consumed | Finance (updates payroll and claim payment status) |
| PeriodClosed, PeriodReopened | Consumed | Finance (blocks or allows posting) |
| UserDisabled | Consumed | Kernel identity (flags employee link) |

### 30.2 New events (new v0.3)

Events carry identifiers, statuses and result codes. They never carry coordinates, photographs or evidence content; a consumer that needs evidence calls a permissioned API (GHR-14).

| Event | Direction | Consumers or sources | Notes |
| --- | --- | --- | --- |
| AttendanceEventAccepted | Published | Attendance engine, reporting, notifications | Result code and flags only |
| AttendanceExceptionRaised, AttendanceExceptionResolved | Published | Workflow, notifications, reporting | Type, severity and decision |
| AttendanceRegularized | Published | Payroll (recalculation flag when the period is locked), notifications | New outcome only |
| FieldDutyAssigned | Published | Notifications, attendance policy resolution | Dates and approver |
| LocationNoticeAcknowledged | Published | Compliance reporting, audit | Notice version |
| TaskAssigned, TaskAcknowledged, TaskStarted, TaskSubmitted, TaskReturned, TaskCompleted, TaskRejected, TaskCancelled, TaskOverdue | Published | Notifications, workflow, reporting; optional integrations by opt-in | Status transitions of section 9A.2 |
| TaskLocationEventRecorded | Published | Reporting and validation only | Validation result and distance band, no coordinates |
| DeviceRegistered, DeviceRevoked | Consumed | Kernel device registry (KER-03) | HRMIS updates device status and stops notifications |
| MobileSyncCompleted, MobileSyncFailed | Consumed | Kernel sync framework | Used by support dashboards |

## 30A. Workflow catalogue (new v0.3)

All workflows run on the Kernel workflow engine (document 2, WFL) with its approver resolution, delegation, escalation and decision history; HRMIS defines the definitions, not the engine. Status names refer to the status models of sections 9 and 9A.

### 30A.1 Attendance workflows

| Workflow | Initiator and preconditions | Steps and responsible roles | Validation and approval rules | Notifications and status transitions | Exception paths and escalation | Audit events |
| --- | --- | --- | --- | --- | --- | --- |
| WF-ATT-01 Normal geo-fenced check-in | Employee; active employment, applicable location and policy, location notice acknowledged | Employee checks in; app captures location and device data; server validates | Accuracy, sequence, duplicate and device trust checks; no approval | Confirmation to employee; Submitted to Accepted | Outside fence goes to WF-ATT-02; location unavailable raises an exception | Event recorded with result code and times |
| WF-ATT-02 Check-in outside geo-fence | Employee in Mode A or B; event outside the fence | Mode A: server rejects with reason. Mode B: server accepts as flagged; employee gives reason; supervisor reviews | Distance bands decide reviewer level; reason mandatory in Mode B | Supervisor notified; Submitted to Flagged to Approved, Rejected or Returned | Escalate to manager then HR after the service level; employee may regularize | Event, flag, reason, decisions |
| WF-ATT-03 Attendance exception submission | Employee, or system-created exception; exception open | Employee explains and attaches; supervisor reviews; manager or HR if thresholds require | Authority by data scope and thresholds; no self-approval | Reviewer notified; Open to Explained to Approved, Rejected or Returned | Reminder and escalation; unresolved shown at attendance lock | Explanation, attachments, each decision |
| WF-ATT-04 Attendance regularization | Employee or supervisor on behalf; within window, period not locked or HR override | Request; supervisor approves; HR approves where policy requires | Reason and proof; approval chain by policy | Draft to Submitted to Approved, Rejected or Returned; payroll flag if locked | Returned for correction; escalation on delay | Request, decisions, outcome change |
| WF-ATT-05 Offline attendance synchronization | Employee earlier offline; offline allowed for the mode (MD-08) | App queues; sync sends; server validates at capture time; employee notified | Backdating window, clock skew, duplicate and device checks | Locally captured to Uploading to Accepted, Flagged or Rejected | Outside window goes to review; conflicts resolved server-first; retries with back-off | Capture time, receipt time, outcome |
| WF-ATT-06 Field-duty attendance | Supervisor assigns field duty; employee in Mode D | Supervisor creates assignment; employee records attendance at approved sites; server validates; out-of-assignment flagged and reviewed | Allowed areas and dates; flagged events need review | Assignment Assigned to Active to Ended; events accepted or flagged | Review by supervisor then HR; end-of-duty reminder | Assignment, events, decisions |
| WF-ATT-07 Missing check-in or check-out correction | System at day close; employee has an incomplete day | Employee notified; submits correction and reason; supervisor approves | Policy window; proof where required | Missing to Corrected or Rejected | Reminder, escalation; payroll flag if locked | Detection, correction, decision |

### 30A.2 Task workflows

| Workflow | Initiator and preconditions | Steps and responsible roles | Validation and approval rules | Notifications and status transitions | Exception paths and escalation | Audit events |
| --- | --- | --- | --- | --- | --- | --- |
| WF-TSK-01 Task creation and assignment | Assigner within data scope | Assigner creates draft, sets rules, assigns to employees | Required fields; scope; leave and workload warnings | Assignees and reviewer notified; Draft to Assigned | Scope exceeded needs permission; edits before assignment only | Creation, assignment, reassignment |
| WF-TSK-02 Employee acknowledgement | Assignee; task Assigned | Assignee reads instructions and acknowledges | Within a set period | Assigned to Acknowledged | Reminder, escalation to assigner; cannot-perform report routes to assigner | Acknowledgement time |
| WF-TSK-03 Task start and progress updates | Assignee; task Acknowledged | Assignee starts; submits updates; may hold with reason | Location at start if configured; hold reason required | In Progress, On Hold, back to In Progress; supervisor optional alerts | Overdue alerts; assigner may hold or cancel | Start, updates, holds |
| WF-TSK-04 Location-enabled task event | Assignee; configured event reached | App captures location; server validates against assigned location | Accuracy, area and mock-signal checks | Event Recorded, Flagged or Rejected per policy | Out-of-area accepted with flag and reason by default; offline queued | Location event, result, views of evidence |
| WF-TSK-05 Evidence submission | Assignee; evidence required | Capture, compress, upload, scan | Types, counts, size, scan result | Evidence Uploaded or Failed; status unchanged until submission | Retry and resume; blocked submission if mandatory evidence missing | Upload, scan result |
| WF-TSK-06 Completion submission | Assignee; required checkpoints and evidence complete | Assignee submits with completion details | Checkpoints and mandatory fields complete | In Progress to Submitted for Review; reviewer notified | Blocked with reasons if incomplete | Submission |
| WF-TSK-07 Supervisor review | Reviewer within scope | Reviewer views evidence and map result; approves, rejects or returns | Comment mandatory for return and reject; reviewer cannot be assignee | To Completed, Rejected or Returned for Rework; assignee notified | Escalate to next reviewer after the service level | Decision, comment, reviewer |
| WF-TSK-08 Rejection or rework | Assignee after return or rejection | Returned: assignee reworks and resubmits. Rejected: manager closes the task or creates a new assignment | Rework limit may escalate | Returned for Rework to In Progress; Rejected to Closed | Escalation after the limit; a new assignment needs the assign permission | Rework cycles, closure |
| WF-TSK-09 Final approval and closure | Reviewer, then system | Approval completes the task; closure after the configured period; reopening by authorized manager | Completion criteria met | Completed or Approved to Cancelled or Closed | Reopen with reason | Completion, closure, reopening |

### 30A.3 Mobile workflows

| Workflow | Initiator and preconditions | Steps and responsible roles | Validation and approval rules | Notifications and status transitions | Exception paths and escalation | Audit events |
| --- | --- | --- | --- | --- | --- | --- |
| WF-MOB-01 Device registration | Employee at first sign-in; valid credentials | Sign in and MFA; register device; accept notices; trust policy evaluated; administrator approves where required | Device limit (MD-14); risk signals | Security notice to user; Pending to Trusted or Blocked | Admin approval queue; blocked devices get a clear message | Registration, decision |
| WF-MOB-02 Login and MFA | Employee, manager or other user | Identifier and password or SSO; MFA challenge; session issued | Lockout rules; suspicious sign-in checks | Failure alerts; Signed out to Signed in | Lockout and recovery; alert to user and administrators | Sign-ins, failures, challenges |
| WF-MOB-03 Biometric application unlock | User with a live session and biometric unlock enabled | Device authenticates user; application session unlocks | Device-level only; no template stored; policy MD-13 | Locked to Unlocked | After failures, fall back to password or MFA | Setting changes and fallbacks |
| WF-MOB-04 Device revocation | Tenant Admin, or user for own device | Revoke; tokens invalidated; push token removed; wipe on next connection | Permission hrm.mobile.device.manage | User and administrators notified; Trusted to Revoked | Pending wipe until reconnection | Revocation, wipe confirmation |
| WF-MOB-05 Offline event synchronization | App with queued events | Sync; server validates each event; results returned | Idempotency, authority and policy at capture time | Local to Accepted, Flagged, Rejected or In review | Retry, conflict rules, user messages | Sync run, per-event result |
| WF-MOB-06 Profile change request | Employee | Submit change; HR verifies; sensitive fields need maker-checker | Field security; evidence for sensitive fields | Submitted to Approved or Rejected | Return for correction; escalation to HR lead | Request, verification, decision |
| WF-MOB-07 Mobile HR service request | Employee | Create with attachments; routed to category owner; responses; closure | Category permissions; service levels | Open to Answered to Closed | Escalation on delay; reopen by employee | Request, responses |
| WF-MOB-08 Manager mobile approval | Approver with pending item | Open notification or inbox; review context; decide | Authority and scope checked on the server; delegation applies | Pending to Approved or Rejected; requester notified | Escalation and reassignment; offline not allowed | Decision with reason |

## 31. Non-functional requirements, acceptance and testing

### 31.1 Baseline (v0.2, retained)

- **Performance and scale:** as in GHR-01; verified at 1.5 times expected load.
- **Security:** field-level encryption of sensitive data, permission-bypass tests for every salary and case screen, tenant isolation tests in every pipeline run, penetration test before the first production customer.
- **Test levels:** unit, integration, system, performance, security and user acceptance; every requirement maps to at least one test and a traceability matrix is generated per release.
- **Statutory validation:** payroll and compliance packs are validated against independent calculations of at least 50 representative employee scenarios per pack release, reviewed by a qualified practitioner in the country.
- **Configuration testing:** each configurable capability is exercised through the admin UI by a non-developer persona.
- **Data migration tests:** import of 10,000 employees from spreadsheet and from a competitor export, with error handling checks, plus opening leave balances and loan balances with tie-out.
- **Parallel payroll:** at least two cycles in parallel with the customer's process using the tool in FR-HRM-PAY-35.
- **Integration tests with Finance:** payroll, loan and claim events produce expected journals; with Finance absent the Accounting Export output matches.
- **UAT:** each wave closes with UAT by pilot customers using signed-off scripts; exit criteria are zero open severity-1 and severity-2 defects.

**Release exit criteria**

| Wave | Exit criteria |
| --- | --- |
| A1 | A pilot customer runs daily attendance, leave and self-service for a full month without workarounds |
| A2 | Two payroll cycles run in parallel and reconcile; journals reach Finance (or the export) and tie to the payroll register |
| B | One full hiring cycle and one appraisal cycle completed; Saudi pack validated by a local practitioner |
| C | First US tenant completes a payroll cycle through the partner and a new-hire compliance flow |
| D | AI features pass bias, privacy and accuracy review |

### 31.2 Non-functional requirements added in v0.3 (new v0.3)

Established means the target exists in the planning documents; Proposed means it is a suggested target recorded in the register of section 37 (MD-18) and not an approved commitment.

| ID | Area | Requirement | Basis |
| --- | --- | --- | --- |
| NFR-01 | Performance | Attendance submission round trip under 3 seconds at the 95th percentile on a typical 4G connection; home screen shown from cache under 2 seconds; map with 1,000 markers under 3 seconds; synchronization of 100 queued events under 60 seconds | Proposed |
| NFR-02 | Scalability | Support peaks at shift start of 10,000 attendance events within 15 minutes per tenant and 50,000 employees per tenant (GHR-01); mobile API capacity sized from these peaks | Employees: established; peaks: proposed |
| NFR-03 | Availability | HRMIS and mobile APIs available 99.9 percent monthly in SaaS (document 1, section 3.7); on-premise availability defined by the customer architecture; the mobile application degrades to offline operation when APIs are unreachable | Established for SaaS |
| NFR-04 | Reliability | No accepted or queued event shall be lost: at-least-once delivery with idempotency; queued offline events survive application restart and device reboot | Proposed |
| NFR-05 | Security | Mobile security controls of section 12E; penetration test and mobile security review before each major release; alignment with a recognized mobile application security verification standard chosen by ADR | Proposed standard |
| NFR-06 | Privacy | Controls of section 34; employee notice before location capture; retention enforced by jobs | Established principles; periods proposed |
| NFR-07 | Auditability | Audit of sign-ins, device actions, attendance and task captures, evidence views, exports, decisions and administrative actions with correlation identifiers | Established |
| NFR-08 | Data integrity | Append-only events and evidence; capture time and receipt time stored separately; no silent overwrite (section 27.3) | Established |
| NFR-09 | Multi-tenancy isolation | Tenant isolation tests cover mobile APIs, sync, evidence storage, maps and notifications on every pipeline run | Established (document 2) |
| NFR-10 | Offline synchronization | Conflict rules, retries, duplicate prevention and clear status as in section 12D; tested with simulated network loss | Established |
| NFR-11 | Mobile responsiveness | Cold start under 3 seconds and screen transitions under 300 milliseconds on a mid-range Android phone; list scrolling at 60 frames per second | Document 13 budgets, proposed for devices |
| NFR-12 | Accessibility | WCAG 2.1 AA for the mobile application; screen reader and large text support; tested with Urdu and Arabic content | Established (document 13) |
| NFR-13 | Localization | English, Urdu and Arabic; right-to-left layout; Gregorian and Hijri calendars; localized formats through Kernel services | Established |
| NFR-14 | Maintainability | Modular mobile code, generated API clients, contract tests, documented module boundaries | Proposed |
| NFR-15 | API versioning | Current and previous major API version supported for at least one release (KER-06) | Proposed |
| NFR-16 | Integration resilience | Provider adapters with retries, circuit breakers and fallbacks for push, SMS, WhatsApp and geocoding; failures never block attendance or task capture | Proposed |
| NFR-17 | Backup and disaster recovery | Recovery point 15 minutes and recovery time 4 hours in SaaS (document 1, section 3.7); evidence files included in backups and restores | Established for SaaS |
| NFR-18 | Monitoring and observability | Metrics, logs and alerts for event ingest latency, synchronization success rate, device registrations, notification delivery, map query time and error rates, per tenant | Proposed |
| NFR-19 | Data retention | Configurable retention for location evidence, task evidence, device records and access logs, enforced by audited jobs; defaults proposed | Mechanism established; periods proposed |
| NFR-20 | Deployment portability | The same HRMIS build serves on-premise and SaaS; the mobile application targets a tenant-specific endpoint; no mandatory cloud dependency except optional provider adapters | Established |
| NFR-21 | On-premise operation | On-premise installations work without internet access except for optional provider adapters; where push delivery needs an external provider, in-app polling and SMS gateways are the documented fallbacks | Established principle; fallbacks proposed |
| NFR-22 | Future SaaS readiness | Tenant-specific endpoints, branding, policies and quotas; no per-customer code branches | Established |

## 32. Assumptions, dependencies and open items

**Assumptions:** statutory content is delivered as pack configuration and verified before each release; US payroll tax is delivered by a certified partner; biometric support starts with the most common local device models; English, Urdu and Arabic are launch languages. Added in v0.3: the mobile application uses the HRMIS backend and Kernel services only; Android is the first mobile platform; location capture is event-based; Kernel extension requests KER-01 to KER-08 will be accepted into document 2.

**Dependencies:** bank file format specifications for the first customers' banks; legal and statutory reviewers for pack validation in Pakistan and Saudi Arabia; partner agreements for US payroll, benefits feeds and background checks before wave C design is frozen; Finance posting rules for payroll available in wave A2. Added in v0.3: the Kernel geospatial service, device registry extension, offline sync framework and media pipeline before release M2; payroll before payslips on mobile (M3).

**Open decisions:** beachhead customer segment, which decides the order inside wave A1 and the starter packs; which devices and banks are supported first; scripting and extension scope for partners; pricing and metering definitions for active employees and add-ons; whether employee claims need a corporate card integration at launch. The open decisions introduced by v0.3 are in the register of section 37.

**Resolved since v0.1:** on-premise at launch (yes, same build); mobile approach (cross-platform with offline); product positioning (module of the ERP).

## 33. Mapping from v0.1 identifiers

| v0.1 | v0.2 and v0.3 |
| --- | --- |
| FR-P0-\* (platform) | Kernel and admin specifications, documents 2 and 4; policy requirements retained as FR-HRM-POL |
| FR-P1-EMP, POS, DOC, ATT, LVE, ONB, SSP, RPT | FR-HRM-EMP, POS, DOC, ATT, LVE, ONB (basic 01 to 06), SSP, RPT with the same numbers |
| FR-P2-PAY, LON, EXP, CMP | FR-HRM-PAY, LON, EXP, CMP with the same numbers; compliance items CMP-07 to CMP-10 became CPL-01 to CPL-04 |
| FR-P3-REC, ONB (advanced), PRF, LND, ERL, AST | FR-HRM-REC, ONB-10 to ONB-13, PRF, LND, ERL, AST |
| FR-P4-USD, USL, USP, USB, USC, USS | FR-HRM-USD, USL, USP, USB, USC, USS |
| FR-P5-ANA, AIA, WFP, EXT | FR-HRM-ANA, AIA, WFP; EXT moved to the platform |

Version 0.3 keeps every v0.2 identifier unchanged. New identifiers added in v0.3 are listed by area and range in section 38.

## 34. Privacy, compliance and data governance (new v0.3)

Employee information, GPS coordinates, payroll data, personal documents and activity records are sensitive. This section sets the governance that applies to all of them. Country entries are areas that qualified local experts must validate; nothing here is legal advice or confirmed compliance.

### 34.1 Principles

- **FR-HRM-PRV-01** Purpose limitation: each category of location and activity data shall have a documented business purpose, and data shall not be used for other purposes. (M, new v0.3)
- **FR-HRM-PRV-02** Data minimization: only the configured events capture location; precision and retention are the minimum that serves the purpose; aggregated analytics are preferred to individual-level data. (M, new v0.3)
- **FR-HRM-PRV-03** Role-based access: location history and evidence are visible only to roles with the GPS evidence permission within their data scope; senior management access is off by default (ROL-03). (M, new v0.3)
- **FR-HRM-PRV-04** Retention, archival and deletion: retention periods are configurable per data category, tenant and entity with country defaults from packs; expiry triggers audited purge or anonymization, honoring legal holds (KER-05). (M, new v0.3)
- **FR-HRM-PRV-05** Audit: every view, export and administrative change involving location evidence, task evidence, payroll data and personal documents is logged, and unusual patterns of evidence access raise alerts to the tenant's security contacts. (M, new v0.3)
- **FR-HRM-PRV-06** Employee transparency: before first capture, and when purposes or retention change, employees receive a plain-language notice in their language and acknowledge it; employees can see their own recorded events and locations (ATT-43, ATT-79). (M, new v0.3)
- **FR-HRM-PRV-07** Secure storage and transmission: encrypted in transit and at rest; evidence files in the Kernel document store under tenant keys; secure document access as in MOB-70 to MOB-72. (M, new v0.3)
- **FR-HRM-PRV-08** A purpose register per tenant shall list each data category, purpose, legal or policy basis chosen by the customer, events, access roles and retention, and be exportable for audits and employee representatives. (M, new v0.3)
- **FR-HRM-PRV-09** A data protection impact assessment template shall be provided for enabling location capture, geo-fence modes and task location capture, and the features shall be enabled per entity only after the tenant confirms it. (S, new v0.3)
- **FR-HRM-PRV-10** Data subject requests (access, correction, export, erasure or restriction) shall cover location evidence, task evidence and device data, within legal retention and holds (GHR-05). (M, new v0.3)
- **FR-HRM-PRV-11** The list of sub-processors and providers (push, SMS, WhatsApp, geocoding, maps) shall be documented per tenant with the data each receives; location data shall not be sent to a provider unless the tenant enables it. (M, new v0.3)
- **FR-HRM-PRV-12** Data residency shall follow the entity's hosting region; location evidence and media shall stay in that region (document 1, section 3.6). (M, new v0.3)
- **FR-HRM-PRV-13** Supervisors and reviewers shall receive guidance and acknowledge acceptable use before receiving the GPS evidence permission; misuse is auditable. (S, new v0.3)
- **FR-HRM-PRV-14** Documentation for employee representatives or works councils, where customers need it, shall be generated from the purpose register. (C, new v0.3)

### 34.2 Location and activity data register (proposal)

| Data item | Purpose | Captured when | Who may access | Retention (proposal, MD-06 and MD-09) |
| --- | --- | --- | --- | --- |
| Attendance event time and result | Attendance and payroll | Each attendance event | Employee, reporting line, HR, payroll within scope | Statutory attendance record period from the country pack |
| Attendance location evidence (coordinates, accuracy, address, distance) | Verify the place of attendance and resolve exceptions | Attendance events under Modes A, B and D; Mode C by policy | GPS evidence permission within scope; the employee for their own | Shorter than attendance results; proposal 180 days, then purge or coarse cell only |
| Task location events | Verify field activity against the assigned area | Configured task events only | GPS evidence permission within scope; the employee | Proposal 12 months after task closure or as the contract or law requires |
| Task photographs and documents | Evidence of completed work | Configured events and submission | Reviewers, supervisors, the employee | Proposal 12 months after closure; configurable per task type |
| Device identifiers and risk signals | Device trust and security | Registration and sign-in | Tenant administrators, security | While registered plus an audit period |
| Location notice acknowledgements | Proof of transparency | First capture and on notice change | Employee, HR, auditors | Employment plus statutory period |
| Evidence access log | Detect misuse | Each view or export | Security administrators, auditors | Per audit policy, proposal 24 months |
| Offline event references | Troubleshoot synchronization | Each offline event | Support, employee | Short troubleshooting period, proposal 90 days |

### 34.3 Country areas requiring validation by local experts

| Country | Areas to validate (not legal advice) |
| --- | --- |
| Pakistan | Current status and requirements of personal data protection rules; employee monitoring and consent practice; working-hours and attendance record rules under federal and provincial labor laws; record retention for statutory registers; any rules on cross-border processing of personal data |
| Saudi Arabia | The Personal Data Protection Law and its executive regulations (consent, purpose, notice, retention, cross-border transfer, data subject rights); labor law on working hours and records; cybersecurity controls for hosted services; requirements on location and photograph data |
| United States | State laws on employee electronic monitoring and location notices and consents; state biometric privacy laws if any photograph-based or biometric feature is added; state privacy laws covering employee data; wage and hour recordkeeping; unionized workplace considerations |

Packs carry country defaults for notices, retention and feature switches; the customer's counsel confirms them before enabling features (MD-07, MD-15).

## 35. Mobile and functional release roadmap (new v0.3)

This is a proposed delivery structure, not an approved commitment. Releases map onto the waves of section 1.4.

### 35.1 Releases

| Release | Scope | Backend and platform dependencies | Exit criteria |
| --- | --- | --- | --- |
| M1 Android ESS foundation | Secure login and device registration; employee home dashboard; employee profile; attendance check-in and check-out; attendance history; leave applications and status; notifications; basic manager approvals | Kernel identity, device registry and sync (SYN, KER-03, KER-07); notifications (KER-04); EMP, ATT basic, LVE; workflow inbox | Pilot employees complete a month of attendance and leave on the application with no workarounds; security review passed |
| M2 Advanced workforce operations | Geo-fence attendance; attendance exceptions; field-duty attendance; employee task assignment and response; GPS-enabled task activities; photograph and document evidence; supervisor task review; manager and supervisor workspace; location-based dashboards | KER-01, KER-02, KER-05, KER-08; location policy modes; TFA engine; semantic layer map support | Pilot field team runs tasks with evidence for a month; exception review turnaround and false-flag rate measured; privacy notice and retention jobs active |
| M3 Complete Android ESS | Payroll and payslips; HR helpdesk; employee documents; learning and performance features where licensed; advanced HR and executive dashboards; offline-first enhancements; extended employee services | PAY and CPL (wave A2); ERL helpdesk, LND, PRF (wave B); analytics workspaces; sync enhancements | Two payroll cycles of payslips delivered on mobile; helpdesk requests handled end to end; executive dashboards validated |
| M4 iOS application | Equivalent core ESS features; platform-specific authentication and security; offline synchronization; notifications; feature parity per the approved roadmap | Shared APIs and sync unchanged; platform notification and biometric support | The M1 to M3 acceptance scenarios pass on iOS; security review passed; parity table signed off |

### 35.2 Dependencies between mobile features and modules

| Mobile feature | Requires | Earliest release |
| --- | --- | --- |
| Sign-in, device registration | Kernel identity and device registry | M1 |
| Attendance and history | ATT, POL, EMP | M1 |
| Leave | LVE, workflow | M1 |
| Manager approvals | Workflow inbox, data scopes | M1 |
| Geo-fence attendance and exceptions | KER-01, location policy, workflow | M2 |
| Tasks with GPS and evidence | TFA, KER-01, KER-08 | M2 |
| Map dashboards | KER-02, GPS evidence permission | M2 |
| Payslips and payroll self-service | PAY (A2), CPL-05 | M3 |
| HR helpdesk | ERL-05, workflow | M3 |
| Learning and performance | LND, PRF (wave B) | M3 |
| Executive workspace | Semantic layer, executive indicators | M3 |
| iOS client | Everything above stable on Android | M4 |

- **FR-HRM-ROA-01** No mobile feature shall be released before the backend services, Kernel extensions and policies it depends on are available and tested. (M, new v0.3)
- **FR-HRM-ROA-02** Each release shall include tests of the previous application version against the current API (API-12). (M, new v0.3)
- **FR-HRM-ROA-03** Release M4 shall not add business functions that Android lacks, and any platform difference shall be documented (MOB-03). (M, new v0.3)
- **FR-HRM-ROA-04** A release shall not ship with open severity-1 or severity-2 defects, open privacy findings on location features, or an unfinished retention job. (M, new v0.3)

## 36. Acceptance criteria and test scenarios for v0.3 (new v0.3)

Each scenario is a functional and integration test for QA. Given and when steps are the setup and action; the expected result is the acceptance criterion. Requirement references show traceability.

### 36.1 Geo-attendance

| ID | Scenario | Given and when | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| AT-G01 | Check-in within an authorized geo-fence | Employee in Mode A, notice acknowledged, device inside the fence with accuracy within the threshold; employee checks in | Event accepted; capture time and receipt time stored; distance and result inside recorded; the app shows accepted | ATT-36, ATT-46, MOB-46 |
| AT-G02 | Attempt outside a strict geo-fence | Employee in Mode A, 400 meters from the site; employee checks in | Submission rejected with the reason and distance shown; no accepted event; the app offers a regularization request | ATT-36, MOB-45 |
| AT-G03 | Outside the fence in exception mode | Employee in Mode B, outside the fence; checks in and gives a reason | Event accepted as flagged; coordinates, distance and reason stored; routed to the supervisor | ATT-37, ATT-57 |
| AT-G04 | Unrestricted location | Employee in Mode C anywhere; checks in | Event accepted with coordinates; authorized supervisor and HR can see them; no rejection | ATT-38 |
| AT-G05 | Field-duty attendance | Employee in Mode D with an assignment; checks in at an approved customer site, then outside the assignment | First event accepted; second flagged for review | ATT-39, WF-ATT-06 |
| AT-G06 | GPS unavailable or inaccurate | Location services off, or accuracy above the threshold | Behavior follows the mode (reject, flag with reason, or time only); the app explains and guides to enable location; exception type recorded | ATT-40, ATT-49, MOB-44 |
| AT-G07 | Suspected location manipulation | A mock location provider is active | Event flagged as a signal and routed to review (blocked only if Mode A policy says so); employee receives a neutral message; reviewer sees the signals | ATT-65, MSEC-06 |
| AT-G08 | Offline attendance | Device offline, mode permits offline capture | App stores the event with capture time, shows locally captured and not yet accepted; no acceptance is shown | OFF-03, OFF-05, ATT-47 |
| AT-G09 | Offline attendance synchronized later | Event captured at 08:58, synchronized at 11:20; retry occurs | One event stored with both times; validated under the policy at capture time; shown as synchronized later; no duplicate | OFF-04, OFF-06, ATT-47, ATT-68 |
| AT-G10 | Supervisor reviews an exception | Flagged event in the supervisor's scope | Supervisor sees distance and reason, returns for correction, employee adds an attachment, manager approves; history shows each step; original event unchanged | ATT-57 to ATT-60 |
| AT-G11 | HR views locations on the map | HR user with evidence permission filters by branch and date | Markers within scope, with inside or outside, distance and details; drill-down to history; every view logged | ATT-69 to ATT-72, ATT-78 |
| AT-G12 | Unauthorized location access | User outside scope, or without evidence permission, requests another employee's location | Access denied or only inside and outside shown; attempt logged; no coordinates returned | ATT-72, ROL-03 |

### 36.2 Task and field activity

| ID | Scenario | Given and when | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| AT-T01 | Create and assign a task | Supervisor creates a site-visit task with location and two photographs required, assigns it to two employees | Two assignee records; notifications sent; scope enforced; status Assigned | TFA-01 to TFA-04, TFA-09, TFA-13 |
| AT-T02 | Task notification | Employees receive the assignment | Push without sensitive content; deep link opens the task after authentication | NTF-06, NTF-07 |
| AT-T03 | Acknowledge | Employee opens and acknowledges | Status Acknowledged; time recorded | TFA-26 |
| AT-T04 | Start a location-enabled task | Employee starts the task at the site | Location notice shown the first time; location captured; result inside; status In Progress | TFA-35, TFA-37, TFA-40 |
| AT-T05 | Progress with GPS | Employee submits an update at a checkpoint, online and then offline | Location events recorded with capture and receipt times and results; offline update shows queued | TFA-39, TFA-43 |
| AT-T06 | Upload evidence | Employee uploads photographs and a document; connection drops once | Files scanned and compressed; upload resumes; required counts enforced | TFA-29, OFF-13 |
| AT-T07 | Submit for review | Employee submits with and without complete checkpoints | Blocked with reasons when incomplete; when complete, Submitted for Review and reviewer notified | TFA-19 |
| AT-T08 | Supervisor approves | Reviewer reviews evidence and map result | Status Completed or Approved; history and reports updated | TFA-48, TFA-24 |
| AT-T09 | Return for rework | Reviewer returns with a comment | Comment mandatory; status Returned for Rework; assignee notified | TFA-17, TFA-48 |
| AT-T10 | Resubmit | Employee corrects and resubmits | Rework count recorded; status Submitted; reviewer reviews again | TFA-20, TFA-32 |
| AT-T11 | Senior management map | Senior management user opens the field activity map | Aggregated by default; event-level only if policy and permission allow; within scope | TFA-54, WSP-28, WSP-30 |
| AT-T12 | Unauthorized task access | User outside the assignee's scope opens the task, evidence or map directly | Denied at the API; nothing shown; attempt logged | TFA-45, TFA-52 |

### 36.3 Mobile application

| ID | Scenario | Given and when | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| AT-M01 | Login | Employee signs in with the configured identifier and password | Session issued; device registered or recognized; home shown | MOB-20, MOB-24 |
| AT-M02 | MFA | Tenant enforces MFA | Code challenge completed; failures counted; lockout rules apply | MOB-22, MOB-29 |
| AT-M03 | Biometric unlock | Employee with a live session enables biometric unlock | Unlock uses the device credential; no template stored; fallback to password and MFA after failures | MOB-23 |
| AT-M04 | Record attendance | Employee checks in on mobile | Same result as the web under the same policy; status clear | MOB-40, MOB-46, GHR-12 |
| AT-M05 | Leave application | Employee applies with a document | Balance effect shown; status trackable; manager notified | MOB-51, LVE-04 |
| AT-M06 | View a payslip | Employee opens a payslip | Step-up authentication if configured; amounts masked when backgrounded; notification carried no amounts | MOB-80 to MOB-82, MSEC-09 |
| AT-M07 | HR service request | Employee submits a request with an attachment | Routed by category; trackable; response notified | MOB-100 to MOB-102 |
| AT-M08 | Receive a task assignment | Supervisor assigns a task | Push and in-app item received | TFA-13, NTF-02 |
| AT-M09 | Offline task update | Device offline, employee records progress and a photograph | Stored encrypted; queued; sync status visible | OFF-02, OFF-03, TFA-34 |
| AT-M10 | Offline synchronization | Connectivity returns | Events accepted, flagged or rejected with reasons; no duplicates; statuses updated | OFF-04 to OFF-10 |
| AT-M11 | Manager approves leave | Manager opens the inbox from a notification | Decision recorded with scope and authority checks; employee notified | WSP-03, WSP-06 |
| AT-M12 | Supervisor reviews a task | Supervisor opens a submission | Evidence and map result shown within scope; decision recorded | WSP-04, TFA-48 |
| AT-M13 | Device revocation | Tenant Admin revokes a registered device | Tokens invalid; push token removed; local data wiped on next connection; audit entry | MOB-28, WF-MOB-04 |
| AT-M14 | Access outside scope | User requests another employee's data from the application | Denied by the server; logged; no data returned | GHR-12, API-05 |
| AT-M15 | Urdu or Arabic use | Employee switches language | Right-to-left layout, localized dates and numbers, Hijri where configured; core flows work | MUX-07, NFR-13 |
| AT-M16 | iOS parity | iOS release available | The scenarios AT-M01 to AT-M15 pass on iOS with documented differences only | MOB-03, ROA-03 |

## 37. Open decisions and assumptions register (new v0.3)

Status: Open (not decided), Proposed (a recommendation exists, awaiting approval), Approved, Rejected. Nothing here is approved yet. Recommendations are suggestions, not requirements.

### 37.1 Open decisions

| ID | Topic | Description and options | Recommended approach | Impact | Owner | Status |
| --- | --- | --- | --- | --- | --- | --- |
| MD-01 | Mobile technology stack | Cross-platform shared codebase, native per platform, or hybrid | Cross-platform with shared code (document 1, section 4.9), confirmed by a spike on offline database, background sync, camera and secure storage | Delivery speed and device integration depth | Product owner, architect | Proposed |
| MD-02 | Distribution model | Public store listing, private distribution, enterprise device management | Support all through a tenant endpoint configured by code or managed configuration | On-premise endpoints, customer IT policies | Product owner | Open |
| MD-03 | Supported operating system versions | Fixed minimum versus rolling window | Set from a pilot device survey; propose a rolling window of recent major versions | Reach on low-end devices versus maintenance cost | Product owner | Open |
| MD-04 | GPS accuracy threshold | 30, 50 or 100 meters, per location type | 50 meters default, overridable per location | False rejections versus spoofing exposure | HR, operations | Proposed |
| MD-05 | Default geo-fence radius | 50, 100 or 200 meters | 100 meters default, overridable per location | Usability versus precision | HR, operations | Proposed |
| MD-06 | Location evidence retention | 90, 180 or 365 days; country-specific | 180 days for raw evidence, shorter than attendance results; confirm per country | Privacy exposure versus dispute handling | HR, legal | Open |
| MD-07 | Employee location privacy policy and consent model | Notice and acknowledgement versus explicit consent, per country | Notice and acknowledgement as default, with country overrides validated by counsel | Legal compliance, employee trust | Legal, HR | Open |
| MD-08 | Offline attendance by mode | Permit or forbid offline capture in each of modes A to D | Permit in B, C and D; Mode A not by default; all unverified until the server validates | Field usability versus strict control | HR, operations | Proposed |
| MD-09 | Task evidence retention | Months after closure, per task type | 12 months after closure, configurable per type | Storage cost, dispute handling, privacy | HR, legal | Proposed |
| MD-10 | Push notification provider | Platform-native providers behind adapters; customer-supplied | Adapter pattern with provider-neutral business logic; polling and SMS fallback for on-premise | Delivery reliability, on-premise independence | Architect | Open |
| MD-11 | SMS and WhatsApp providers | Providers per country behind adapters | Adapters per country; WhatsApp only with an approved business integration | Cost, deliverability, compliance | Product owner | Open |
| MD-12 | Device management requirements | Which customers require MDM or MAM, and which products | Support managed configuration and remote wipe generically; confirm per customer | Enterprise sales readiness | Product owner | Open |
| MD-13 | Biometric authentication policy | Optional, required for sensitive roles, or required for all | Optional by default, tenant can require for approvers and payroll access | Security versus convenience | Security, product owner | Proposed |
| MD-14 | Devices per employee | 1, 2 or configurable | 2 active devices by default, configurable | Support load, security | Security | Proposed |
| MD-15 | Country-specific statutory requirements | Privacy, labor and monitoring rules for Pakistan, Saudi Arabia and the United States | Validate with qualified local counsel before enabling location features per country | Legal exposure, feature availability | Legal | Open |
| MD-16 | Payroll and tax functionality by country on mobile | Payslips and statements per pack and wave | Pakistan in wave A2, Saudi Arabia in wave B, United States through the partner in wave C | Mobile scope in M3 | Product owner | Open |
| MD-17 | Executive access to employee location | Allowed, aggregated only, or disabled | Disabled by default; aggregated views only; event-level by explicit tenant policy | Privacy risk, executive expectations | HR, legal | Proposed |
| MD-18 | Performance and availability targets | Targets of section 31.2 and attendance dashboard freshness | Adopt the proposed targets after pilot load tests | Sizing and cost | Architect | Open |
| MD-19 | Offline backdating window | 24, 48, 72 hours or more | 72 hours, after which events go to review | Payroll cut-offs, fraud exposure | HR, payroll | Proposed |
| MD-20 | Exception timeout outcome | Escalate only, auto-approve or auto-reject after a period | Escalate only; no automatic approval | Fairness, control | HR | Proposed |
| MD-21 | Reverse geocoding and map providers | Optional provider adapters or bundled data only | Optional adapters; bundled boundary data for on-premise | Address text quality, external data sharing | Architect | Open |
| MD-22 | Task boundary with Kernel tasks | Reuse Kernel tasks and reminders, or HRMIS-owned tasks | HRMIS-owned tasks reusing Kernel comments, workflow, documents, notifications | Avoids overloading light tasks with evidence and location | Architect | Proposed |
| MD-23 | Lone-worker or safety tracking | Provide continuous tracking in safety cases | Out of scope; needs a separate decision, consent and legal review | Privacy, duty of care | Legal, HR | Open |
| MD-24 | Home and remote work location | Store home coordinates or not | Do not store home coordinates by default | Privacy | HR | Proposed |
| MD-25 | Selfie evidence and face matching | Photograph only, or automated matching | Optional photograph; no face matching in this release | Privacy, accuracy, bias | Legal, HR | Proposed |
| MD-26 | iOS timing | After M3, parallel with M3, or earlier for specific customers | After M3 on stable shared APIs | Cost and customer needs | Product owner | Open |
| MD-27 | Offline caching of payroll data | Never, short period, tenant option | Never by default; tenant option with encrypted short-lived cache | Security versus convenience | Security | Proposed |
| MD-28 | Encryption of precise coordinates | Database encryption at rest, column encryption, gridded copies for analytics | Database encryption at rest, optional column encryption, rounded copy for analytics | Query performance versus protection | Architect, security | Open |

### 37.2 Assumptions

| ID | Assumption | Impact if wrong | Owner |
| --- | --- | --- | --- |
| A-01 | The Platform Kernel will accept KER-01 to KER-08 | HRMIS would have to build shared services itself or drop features | Architect |
| A-02 | Employees have personal or company smartphones; shared devices use kiosk mode | Mobile adoption lower; kiosk requirements grow | Product owner |
| A-03 | Employees acknowledge the location notice before first capture | Legal exposure; policy must block capture until acknowledgement | HR, legal |
| A-04 | A pilot customer exists for M1 and M2 | Releases lack real validation | Product owner |
| A-05 | Existing biometric and card attendance devices continue to be supported | Customers with devices need extra work | Product owner |
| A-06 | Customer site boundaries can be defined by address and drawing; imports from other systems are optional | More manual setup | Implementation |
| A-07 | Connectivity in the field is intermittent, not absent for long periods | Offline limits and storage may need revision | Operations |
| A-08 | Local counsel validates location features before they are enabled per country | Features may need to stay disabled in a country | Legal |
| A-09 | The mobile application and web portal remain functionally aligned through shared APIs | Divergent behavior and double maintenance | Architect |

## 38. Requirement traceability and change register (new v0.3)

### 38.1 New identifiers by area

Ranges are blocks; numbers inside a block that are not used are reserved to keep identifiers stable (for example WSP-12 to WSP-14). No existing identifier was reused or renumbered.

| Area | Identifiers | Count | Section |
| --- | --- | --- | --- |
| Attendance and geo-attendance | ATT-23 to ATT-82 | 60 | 9.6 to 9.11 |
| Task and field activity | TFA-01 to TFA-70 | 70 | 9A |
| Mobile ESS application | MOB-01 to MOB-10, 11 to 14, 20 to 29, 30 to 34, 40 to 47, 50 to 53, 60 to 63, 70 to 73, 80 to 83, 90 to 93, 100 to 102, 110 to 111, 120, 130 to 131, 140 to 141, 150 | 74 | 12A |
| Role workspaces | WSP-01 to 11, 15 to 23, 27 to 34 | 28 | 12B |
| Notifications | NTF-01 to NTF-16 | 16 | 12C |
| Offline operation | OFF-01 to OFF-16 | 16 | 12D |
| Mobile security | MSEC-01 to MSEC-19 | 19 | 12E |
| Mobile experience | MUX-01 to MUX-13 | 13 | 12F |
| Mobile APIs | API-01 to API-14 | 14 | 12G |
| Reporting | RPT-05 to RPT-10 | 6 | 13 |
| Payroll, leave, onboarding | PAY-40; LVE-15 to 17; ONB-14 to 17 | 8 | 14, 10, 11 |
| Self-service | SSP-09 to SSP-14 | 6 | 12 |
| Other module additions | REC-15, PRF-11, LND-10, ERL-08, AST-06, KSA-13, USS-05, AIA-06 | 8 | 18 to 26 |
| Global requirements | GHR-11 to GHR-16 | 6 | 4 |
| Data integrity, roles | DAT-01 to DAT-07; ROL-01 to ROL-06 | 13 | 27.3, 28.2 |
| Privacy and roadmap | PRV-01 to PRV-14; ROA-01 to ROA-04 | 18 | 34, 35 |
| Kernel extension requests | KER-01 to KER-08 | 8 | 3.3 |
| Non-functional requirements | NFR-01 to NFR-22 | 22 | 31.2 |
| Total |  | 405 |  |

New business rules: BR-ATT-A, BR-ATT-B, BR-ATT-C, BR-TFA-A, BR-TFA-B, BR-TFA-C, BR-TFA-D. New acceptance scenarios: AT-G01 to AT-G12, AT-T01 to AT-T12, AT-M01 to AT-M16. New workflows: WF-ATT-01 to 07, WF-TSK-01 to 09, WF-MOB-01 to 08. New decisions and assumptions: MD-01 to MD-28, A-01 to A-09.

### 38.2 Existing requirements extended (none modified, none removed)

| ID | What was extended | Where |
| --- | --- | --- |
| FR-HRM-ATT-01 | Capture follows location policy modes and event data | 9.1 |
| FR-HRM-ATT-04 | Offline capture rules, per-mode permission, server validation | 9.1 |
| FR-HRM-ATT-05 | Managed locations, boundaries, applicability, lifecycle | 9.1 |
| FR-HRM-ATT-11 | Exception types, routing, review and history | 9.3 |
| FR-HRM-ATT-14 | Field-duty requests create the Mode D assignment | 9.3 |
| FR-HRM-ATT-18 | Remote, field duty, exception and pending indicators; map; drill-down | 9.4 |
| FR-HRM-ATT-19 | Geo-fence, regularization, field-duty and location reports | 9.4 |
| FR-HRM-ATT-20 | Scope note: biometric devices only; app unlock stores no template | 9.5 |
| FR-HRM-SSP-04 | Complete mobile scope in sections 12A to 12G | 12 |
| FR-HRM-GHR-02 | Precise location, device identifiers and task evidence classified sensitive | 4 |

### 38.3 Traceability from the requested capabilities to this document

| Capability requested for v0.3 | Main sections | Identifier ranges |
| --- | --- | --- |
| Geo-attendance and location-based workforce management | 9.6 to 9.11, 13A.1 | ATT-23 to ATT-82 |
| Employee task assignment and field activity | 9A, 13A.2, 30A.2 | TFA-01 to TFA-70 |
| Complete ESS mobile application | 12, 12A, 12F | SSP-09 to SSP-14, MOB |
| Manager, supervisor, HR and senior management workspaces | 12B | WSP |
| Android first, iOS later | 1.4, 12A.1, 35 | MOB-01 to MOB-03, ROA |
| Notifications and communication | 12C | NTF |
| Offline-first capabilities | 12D | OFF |
| Mobile security and privacy | 12E, 34 | MSEC, PRV |
| User experience, accessibility, localization, navigation | 12F, 31.2 | MUX, NFR-11 to NFR-13 |
| Mobile APIs and integrations | 12G, 29.2 | API, KER-06 |
| Data model | 27.2, 27.3 | DAT |
| Roles, permissions, data scopes | 28.2 | ROL |
| Workflows and business rules | 30A | WF-ATT, WF-TSK, WF-MOB |
| Reporting and analytics | 13, 13A | RPT-05 to RPT-10 |
| Non-functional requirements | 31.2 | NFR-01 to NFR-22 |
| Release roadmap | 1.4, 35 | ROA |
| Acceptance criteria and test scenarios | 36 | AT-G, AT-T, AT-M |
| Privacy, compliance, data governance | 34 | PRV, GHR-11 to GHR-16 |
| Open decisions and assumptions | 37 | MD, A |

## 39. Quality review and change summary (new v0.3)

### 39.1 Consistency review

The review below was made by reading the document against the checks you listed. Two limits apply: it was a manual review, not an automated one, and I have not been able to run a script over the exported text. A script check is recommended (section 39.3).

| No. | Check | Result | Notes |
| --- | --- | --- | --- |
| 1 | No valid original requirement removed | Passed by review | Every v0.2 section was carried over in order; extended requirements are marked |
| 2 | Existing identifiers preserved | Passed by review | No renumbering; the mapping of section 33 is unchanged |
| 3 | New identifiers unique | Passed by review | Identifiers are allocated in blocks per area; reserved gaps are listed in 38.1 |
| 4 | No duplication of Platform Kernel capabilities | Passed with one assumption | Eight extension requests (KER) instead of local services; the task boundary with Kernel tasks is assumption MD-22 |
| 5 | Geo-attendance consistent with existing attendance workflows | Passed | ATT-11, ATT-14, ATT-17 and ATT-20 were extended with explicit notes |
| 6 | Task management integrated with self-service | Passed | TFA-25 to TFA-36, MOB-90 to MOB-93, WSP-04 |
| 7 | GPS capture is event-based and policy-governed | Passed | GHR-11, ATT-74, TFA-37, TFA-38, MSEC-19 |
| 8 | Mobile and web share rules and authorization | Passed | GHR-12, SSP-09, API-05, ROL-02 |
| 9 | Manager, HR, supervisor and senior management access scoped | Passed | 28.2, WSP rules, TFA-52, ATT-72 |
| 10 | Offline behavior explicit | Passed | 12D table and OFF-01 to OFF-16 |
| 11 | Mobile security and privacy addressed | Passed | 12E and 34 |
| 12 | Android and iOS roadmap consistent | Passed with a noted change | v0.2 placed mobile in wave A1; only M1 stays in A1 and M2 to M4 follow (section 1.4) |
| 13 | Database entities support the functionality | Passed | 27.2 covers every area; Location Validation Result is a value object, not a table |
| 14 | APIs and integrations support the journeys | Passed | Each journey of 12F.4 maps to the API groups of 12G.1 |
| 15 | Workflows, statuses, approvals and notifications consistent | Passed after two corrections | The rejected-task reopening wording in WF-TSK-08 was aligned with the transition table |
| 16 | Acceptance criteria testable | Passed | 40 scenarios with expected results and references |
| 17 | Open decisions identified | Passed | MD-01 to MD-28 |
| 18 | No contradictory or duplicated requirements | Passed with noted overlaps | ATT-56 and MOB-42 restate regularization from the web and mobile sides by design; MOB-12 and ROL-02 restate the same authorization rule |

### 39.2 Change summary

| Item | Summary |
| --- | --- |
| Sections added | Document control and executive summary; 3.3; 9.6 to 9.11; 9A; 11.3; 12A to 12G; 13A; 27.2 and 27.3; 28.2; 29.2; 30.2; 30A; 31.2; 34; 35; 36; 37; 38; 39; 40 |
| Sections expanded | 1 (what changed, scope and sequence, conventions, out of scope); 2 (roles); 3.1 and 3.2; 4; 5 (policy note); 9; 10; 11; 12; 13; 14 to 26 (cross-references only); 31; 32; 33 |
| Existing requirements extended | 10 (section 38.2); none modified or removed |
| New requirement identifiers | 405 (section 38.1), including 8 Kernel extension requests and 22 non-functional requirements |
| New logical entities | 18 new HRMIS entities, 5 extended v0.2 entities, 10 reused Kernel entities or value objects (section 27.2) |
| New workflows | 24 (7 attendance, 9 task, 8 mobile) |
| New integrations and APIs | 17 mobile API groups; 10 external and platform integrations; 20 new or consumed events |
| Outstanding business decisions | 28 (MD-01 to MD-28) and 9 assumptions |

**Conflicts and limitations found in v0.2**

- v0.2 placed self-service and mobile entirely in wave A1; the extended mobile scope cannot all fit there because it depends on payroll (A2), talent modules (B) and new Kernel services. The release structure of section 1.4 and 35 resolves this as a proposal.
- v0.2 listed a geo\_fences table without defining locations, applicability, versions or evidence; v0.3 defines them and keeps the name.
- v0.2 had no device, notice acknowledgement or offline reference entities; they are now defined or reused from the Kernel.
- v0.2 ATT-04 treated offline capture as optional; v0.3 keeps its priority but ties it to the per-mode decision MD-08.
- v0.2 did not distinguish biometric attendance devices from biometric unlock of an application; ATT-20 now carries a scope note.
- v0.2 assumed shared Kernel services that did not yet include geospatial, mobile session, push token and media pipeline capabilities; these are requested as KER-01 to KER-08.

### 39.3 Recommended automated checks

Export v0.2 and v0.3 as Markdown, extract identifiers matching FR-HRM-\<AREA>-\<number> and the plain forms (BR-nn, GHR-nn), and confirm that every v0.2 identifier appears in v0.3, that no identifier appears twice as a definition, and that every cross-reference (section numbers, MD, KER, WF and AT identifiers) resolves to a defined item. This check should run as part of the documentation checks of document 12 before the specification is frozen.

## 40. Impact on other planning documents (new v0.3)

None of the following has been changed yet; they are the follow-on updates implied by this version. Document 6 (v0.2) remains the baseline.

| Document | Section | Change needed |
| --- | --- | --- |
| 1 Architecture | 4.9 mobile; 4.6 kernel components; 3.6 residency | State Android first and iOS in release M4; add the geospatial service and device-risk signals to the kernel component list; confirm that evidence and media stay in the entity's region |
| 2 Platform Kernel | New requirements; IAM; NTF; DOC; INT; SYN | Accept KER-01 to KER-08: geospatial service, device registry extension, push token management, data classification and retention for location data, mobile API conventions, mobile sessions, media pipeline; register the permissions of section 28.2 |
| 3 Data Architecture | hrm schema; geospatial types | Add the tables of section 27.2, geometry storage and indexing for boundaries, partitioning and retention for evidence and event tables, and the encryption decision MD-28 |
| 4 Admin Console | Devices, policies, locations | Screens for geo-fence and location management, location policy modes, mobile policies, device administration and notice management |
| 9 Analytics and AI | Semantic layer, maps | Semantic entities and reporting views for attendance events, exceptions and tasks; map visuals; evidence access controls; exclusion of location data from AI context (AIA-06) |
| 12 Claude Code Operating Manual | Build catalog | New slices for the geospatial engine, location policy engine, attendance event pipeline, task engine, mobile application releases M1 to M4, offline sync and evidence pipeline; Fable for the policy and sync engines and security reviews, Opus for screens |
| 13 UX and Design System | Mobile patterns | Patterns for location permission flows, map, camera evidence, offline and sync status, role switching and consent notices |
| 14 Consistency Review | Registry of prefixes and decisions | Add the new FR-HRM areas (TFA, MOB, WSP, NTF, OFF, MSEC, MUX, API, PRV, ROA, DAT, ROL) and a decision for accepting the Kernel extension requests |
| 15 Editions and Starter | People Lite | Decide whether mobile attendance and leave are part of the Starter People Lite add-on and which location modes are offered there |

## 41. Baseline 1.0 addendum (this document is now version 0.3.1)

*Added 14 November 2026 by the comprehensive review (Doc 16). Sections 1 to 40 are unchanged and no requirement ID is removed, weakened or renumbered. The previous state (version 0.3) is kept in this document's version history and in the frozen originals archive. Version 0.2 remains the untouched historical baseline. Items tied to a proposed decision take effect on its approval.*

### 41.1 Status of this document (proposed D-37)

Version 0.3 is the HRMIS baseline for build planning. Section 40 listed updates needed in other documents; they are now made: the kernel accepts KER-01 to KER-08 (Doc 2 section 32.2), data standards are extended (Doc 3 section 14), the build manual maps slices to phases (Doc 12 section 16) and the decision log records the change (Doc 14 section 16).

### 41.2 Kernel extension requests: outcome

| Request | Outcome | Kernel requirement | Phase |
| --- | --- | --- | --- |
| KER-01 Geospatial service | Accepted, computed in the application | FR-KRN-GEO-01 | 2 circle, 3 polygon |
| KER-02 Map component | Accepted as a design-system component | FR-KRN-GEO-02 | 3 |
| KER-03 Device trust registry | Accepted | FR-KRN-SYN-12 | 2 basic, 3 risk signals |
| KER-04 Push tokens and providers | Accepted | FR-KRN-NTF-08 | 2 |
| KER-05 Location data classification and retention | Accepted | FR-KRN-DOC-12 | 3 |
| KER-06 Mobile API conventions | Accepted | FR-KRN-INT-11 | 2 |
| KER-07 Mobile session management | Accepted | FR-KRN-IAM-19 | 2 |
| KER-08 Large media pipeline | Accepted | FR-KRN-DOC-13 | 3 |

### 41.3 Geospatial implementation (proposed D-36)

Wherever sections 3.3, 9.6, 27 or 37 allow or mention a database geospatial extension, the baseline is: no database extension; coordinates and accuracy in plain numeric columns; fence shapes as standard geometry text with a bounding box; distance and containment computed in the application with a permissively licensed library (Doc 3 section 14.4). The reason is decision D-09 (permissive licenses only) and source-code delivery. No functional requirement changes. Open decisions in section 37 that concern the extension are closed by D-36 if it is approved.

### 41.4 Mobile releases and waves mapped to phases (proposed D-32)

| This document | Phase (Doc 16 section 8) | Release |
| --- | --- | --- |
| Wave A1 core: POL, EMP, POS, DOC, ATT-01 to ATT-22, LVE, ONB, SSP, RPT basic | 2 | 1.0 |
| Wave A2: PAY, LON, EXP, CMP, CPL for Pakistan | 2 | 1.0 |
| Mobile M1 (Android: profile, leave, payslip, simple check-in with one circular fence) | 2 | 1.0 |
| Geo-attendance in full (ATT-23 to ATT-82), TFA, WSP, mobile M2 and M3, KSA pack, REC, AST | 3 | 2.0 |
| PRF, LND, ERL, WFP, US readiness, iOS (M4), ANA and AIA beyond the thin AI skills | 4 | 3.0 |

Recruitment (REC) may move to Phase 4 at the Phase 3 release-scope tagging if capacity requires; that is a D-34 decision at that time.

### 41.5 Binding cross-module rules

Doc 16 section 6 applies. HRMIS is the enforcing party for FR-XM-26 (attendance and leave lock when payroll is calculated; later changes are retroactive adjustments in the next open payroll), FR-XM-27 (one summarized payroll entry per legal entity and cost allocation, posted atomically through the Finance posting contract under proposed D-31; payslip reversal before payment only), FR-XM-28 (an employee who is also a supplier or customer is one party with several roles), FR-XM-29 (final settlement blocked by open advances, loans, unreturned assets, unsubmitted claims, open tasks needing handover and pending approvals) and FR-XM-30 (transfer between legal entities ends one employment and starts a linked one). Where a requirement in sections 5 to 26 is looser than these rules, the FR-XM rule applies.

### 41.6 Data notes

HRMIS tables carry `tenant_id` (proposed D-30) and transactional tables carry `branch_id` (correction C-39). Personal and sensitive fields written to the audit log are encrypted per data subject so that erasure is possible without breaking the audit chain (Doc 3 section 14.3), which supports PRV-01 to PRV-14. The HRMIS data design gate (section 27 brought to column level, with diagram) is passed at Phase 2 entry for core and payroll, and at Phase 3 entry for geo-attendance and tasks (proposed D-35).

### 41.7 Size profiles

Micro: employee list, leave, simple payroll, one office fence at most; positions, grades, shifts and approval chains exist as defaults and stay hidden. SME adds attendance, shifts and loans. Mid adds positions, grades, geo-attendance and tasks. Enterprise adds talent, workforce planning and multi-country payroll (Doc 16 section 5.2).
