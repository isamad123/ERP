# HRMIS: Functional Specification (v0.2, revised)

Document 6 of the planning set | Prepared for Ibrahim | 10 November 2026 | Revises the HR MIS Product Functional Specification v0.1 (14 October 2026) and aligns it with the ERP platform, Finance and admin specifications

## 1. Introduction

### 1.1 Purpose

This document defines what the HRMIS module shall do, in testable functional terms, as a module of the ERP platform. It keeps the substance of the v0.1 specification (18 HR modules, Pakistan and United States readiness) and changes its foundations: platform capabilities now come from the kernel (documents 1 to 4), accounting comes from Finance (document 5) or an accounting export, and a Saudi Arabia pack is added.

### 1.2 What changed from v0.1

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

### 1.3 Scope and sequence

| Wave | Content | Exit criteria |
| --- | --- | --- |
| A1 | Policy framework, employee records, organization and positions, documents and letters, attendance, leave, basic onboarding and offboarding, self-service and mobile, basic dashboards | Pilot customer runs a full month of attendance, leave and self-service |
| A2 | Payroll, loans and advances, expenses, compensation and benefits and statutory compliance (Pakistan) with postings to Finance | Two payroll cycles run in parallel with the customer's process and reconcile |
| B | Recruitment, advanced onboarding, performance, learning, employee relations, asset custody, Saudi Arabia pack | One hiring cycle and one appraisal cycle completed by pilot customers |
| C | United States readiness (partner payroll and benefits) | First US tenant completes a payroll cycle and new-hire compliance flow |
| D | Advanced analytics, workforce planning and succession, extensions | AI features pass bias, privacy and accuracy review |

Wave A1 and A2 are the first release together with Finance. Wave B and later waves are specified now so the data model and policy framework do not need rework.

### 1.4 Conventions

- Requirement IDs follow FR-HRM-\<area>-\<number>. Priority (M), (S), (C); unmarked is (M). Wave is stated in each section heading; unmarked requirements belong to the section's wave.
- Requirements added in this revision are marked (new).
- Every configurable behavior is reachable through the admin UI and the public API with the same permissions (document 2).
- Statutory values (rates, formats, rules) are pack content, verified by qualified local practitioners before release; nothing statutory is hard-coded.

### 1.5 Out of scope

- General-ledger accounting (provided by Finance or an external system).
- Authoring of learning content (the module links to external content).
- An in-house United States tax calculation and filing engine (partner integration in wave C).
- Legal advice; each customer remains responsible for confirming statutory settings.

## 2. Actors and roles

| Actor | Description |
| --- | --- |
| Tenant Admin | Configures the tenant (kernel and admin console) |
| HR Admin | Maintains employee data and runs HR processes |
| HR Business Partner | Advises units; data access scoped to assigned units |
| Payroll Officer and Payroll Approver | Prepare and approve payroll (maker-checker) |
| Line Manager | Approves requests, reviews team, runs appraisals |
| Employee | Uses self-service |
| Recruiter, Interviewer, Candidate | Run and take part in hiring |
| Finance Approver | Approves loans, expenses and payroll postings |
| Auditor | Read-only access to data and audit logs |
| Implementation Partner | Configures on behalf of customers under delegated permissions |

## 3. HRMIS within the platform

### 3.1 Kernel capabilities used

| Kernel capability | Use by HRMIS |
| --- | --- |
| Organization | Companies (legal entities), branches, locations, organization units and hierarchies; HR adds positions and grades |
| Identity and access | Users link optionally to employees; roles, data scopes (own, team, subtree, company), field security for salary and personal data |
| Settings, rules, workflow | Policy parameters, formulas and eligibility, approvals for every request type |
| Metadata | Custom fields, layouts, status models, custom objects on employees, positions, requests |
| Documents and templates | Employee document vault, letters, payslips, certificates |
| Notifications | Reminders, approvals, alerts across channels |
| Localization | Urdu, Arabic, English; Hijri and Gregorian dates; holiday calendars |
| Parties | Employees as payees are referenced by employee id; banks and government authorities as partners |
| Reporting and analytics | Semantic layer, dashboards, scheduled reports |
| Integration, jobs, entitlements, AI | Connectors, imports, scheduled processing, active-employee metering, AI skills |

### 3.2 Interaction with other modules

| Module | Interaction |
| --- | --- |
| Finance (optional) | Consumes PayrollRunFinalized, FinalSettlementApproved, ExpenseClaimApproved, LoanDisbursed (which covers loans and advances), LeaveLiabilityCalculated and GratuityAccrualCalculated events; publishes nothing it depends on; sends payment status back to HRMIS; budget and cost center references are optional |
| Accounting Export connector (when Finance is absent) | Maps the same events to journal files or an external accounting format |
| Procurement | Employee purchase requests remain in Procurement; HRMIS owns only expense claims |
| Assets (wave B) | Asset issue and return from HR creates custody records; unreturned assets feed offboarding clearance |
| Inventory | Uniform and consumable issuance may use Inventory when licensed |
| Analytics | HR semantic models and standard dashboards |

HRMIS hard-depends on the kernel only (decision D-10). Every Finance, Assets or Inventory integration degrades gracefully when the other module is not licensed.

## 4. HR global requirements

| ID | Requirement |
| --- | --- |
| GHR-01 | The module shall support at least 50,000 employees per tenant, with payroll for 10,000 employees completing in under 15 minutes and attendance processing for 500 employees on three shifts for a month in under 5 minutes |
| GHR-02 | Data shall be classified as general personal, sensitive personal (national ID, bank account, salary, medical, disciplinary, union, self-identification) and restricted (cases, investigations); sensitive and restricted fields shall use field-level encryption, masking by default, restricted roles, and access logging |
| GHR-03 | Every employee-related record that changes over time shall be effective-dated; as-of queries and timelines shall be available throughout |
| GHR-04 | A backdated change shall show an impact analysis (attendance, leave, payroll, benefits) before saving and flag affected periods for recalculation (new) |
| GHR-05 | Privacy tools shall support access, correction, export and erasure or anonymization within retention rules, legal holds, and statutory record-keeping periods defined by packs (new) |
| GHR-06 | Salary and compensation data shall be hidden from every role except those explicitly granted, including in reports, exports, search results, AI and notifications |
| GHR-07 | Multiple legal entities and countries shall be supported in one tenant with different calendars, policies and country packs |
| GHR-08 | Statutory formats and rules shall change only through staged pack releases with impact preview |
| GHR-09 | All HR actions on behalf of another person (HR applying leave for an employee, manager marking attendance) shall be audited with the acting user |
| GHR-10 | Employee self-service and approvals shall work on mobile and low-bandwidth connections |

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

## 9. Time and attendance (ATT), wave A1

**Purpose:** capture work time accurately across offices, shifts, sites and remote work, and compute attendance outcomes under configurable rules. **Actors:** Employee, Line Manager, HR Admin, Payroll Officer.

**Capture**

- **FR-HRM-ATT-01** Attendance shall be captured from web and mobile check-in and check-out, geo-fence validation, selfie capture, QR code, kiosk mode, biometric devices (fingerprint, face), card readers, manual entry and file import.
- **FR-HRM-ATT-02** A device management console shall register devices by site, store connection settings, monitor online status, and show last sync time and errors.
- **FR-HRM-ATT-03** Biometric integration shall support push and pull sync, offline buffering, duplicate removal and mapping of device user IDs to employees; a driver interface shall allow adding device models without core changes.
- **FR-HRM-ATT-04** Mobile capture shall work offline, store punches locally with device time and location, and sync when connected, with tamper checks (device clock, mock-location detection). (S)
- **FR-HRM-ATT-05** Geo-fences shall be defined per site with radius or polygon and assigned to employees or groups; out-of-fence punches shall be flagged or blocked per policy.

**Shifts and rosters**

- **FR-HRM-ATT-06** Shift types shall include fixed, flexible (core hours), rotating, split, night and continuous 24-hour; weekly-off patterns shall be configurable (fixed day, rotating, alternating weeks).
- **FR-HRM-ATT-07** A roster builder shall assign shifts to employees or groups for a period, with copy-forward, conflict and rest-hour checks, and publication to employees.
- **FR-HRM-ATT-08** Employees shall request shift swaps and changes through a workflow. (S)

**Rules and processing**

- **FR-HRM-ATT-09** An attendance policy shall define grace periods, late and early-leaving thresholds, half-day and absence thresholds, break and lunch handling, minimum hours for a full day, and rounding rules.
- **FR-HRM-ATT-10** The engine shall process raw punches into daily attendance records (present, absent, late, half-day, on leave, holiday, weekly off, on duty) with an explanation of each result.
- **FR-HRM-ATT-11** Missing or incorrect punches shall appear in an exception queue; employees raise regularization requests with reason and proof; managers approve.
- **FR-HRM-ATT-12** Overtime rules shall be defined by day type (working day, rest day, holiday), with configurable rate multipliers, daily and monthly caps, pre-approval or post-approval, and an option to convert to compensatory off.
- **FR-HRM-ATT-13** Late-coming and early-leaving rules shall support automatic deduction as leave, pay deduction or warning, with waiver by an approver.
- **FR-HRM-ATT-14** On-duty, official visit, work-from-home and field-duty requests shall be supported with approvals and count as present per policy.
- **FR-HRM-ATT-15** Holiday calendars shall be defined per entity and location with Gregorian and Hijri dates, optional holidays and employee choice of optional holidays.
- **FR-HRM-ATT-16** Ramzan (Ramadan) and seasonal timing policies shall apply by date range and employee group.
- **FR-HRM-ATT-17** Attendance periods shall be lockable by cut-off date; changes after lock require approval and trigger a recalculation flag to payroll.

**Views and reports**

- **FR-HRM-ATT-18** A live dashboard shall show present, absent, late, on leave and not yet punched, by unit and location.
- **FR-HRM-ATT-19** Standard reports shall include daily and monthly muster, late and early, overtime, absenteeism, punch log, device sync and exception ageing.

**Additions**

- **FR-HRM-ATT-20** Biometric data shall be handled under explicit consent: templates stay on devices or are stored encrypted with a retention period, and employees can request deletion on leaving; collection can be disabled per entity. (new)
- **FR-HRM-ATT-21** Locking an attendance period shall publish an AttendanceLocked event used by payroll, and reopening shall publish AttendanceReopened with the affected employees. (new)
- **FR-HRM-ATT-22** Project, client and task time entry with billable flag, approval and lock shall be available as timesheets (required for United States tenants and professional-services customers). (new, wave B)
- **BR-05** A punch within the grace window does not count as late; a day is classified by the policy applicable on that date, not today's policy.
- **BR-06** If two overlapping approved requests exist (for example leave and on-duty), the system blocks the second or applies the configured precedence.
- **AC:** 500 employees on three rotating shifts across two sites get correct daily outcomes, with a missing punch regularized and the month locked, in under 5 minutes of processing.

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

## 12. Self-service portals and mobile (SSP), wave A1

- **FR-HRM-SSP-01** The employee portal shall provide profile, documents, payslips and tax documents (wave A2), attendance, leave, requests and their status, letters, directory, organization chart, announcements and policies.
- **FR-HRM-SSP-02** The manager portal shall provide an approvals inbox (kernel workflow inbox), team dashboard (attendance, leave, birthdays, probation and contract dates), team calendar and request-on-behalf actions.
- **FR-HRM-SSP-03** HR and payroll workspaces shall provide task queues, exceptions, deadlines and quick actions.
- **FR-HRM-SSP-04** Mobile apps shall provide attendance capture, leave and request submission, approvals, payslips, directory and push notifications, with biometric unlock.
- **FR-HRM-SSP-05** Chat-based self-service through WhatsApp shall support leave balance, leave application, attendance summary and payslip request, authenticated by registered number and a one-time code. (S)
- **FR-HRM-SSP-06** Tenants shall configure home page widgets, quick links, welcome content and language defaults per role.
- **FR-HRM-SSP-07** Employees and managers shall set delegation and out-of-office from self-service, applied to approvals through the kernel delegation feature. (new)
- **FR-HRM-SSP-08** Self-service shall be available in English, Urdu and Arabic with right-to-left layout and Hijri date display. (new)
- **AC:** an employee applies for leave and checks the balance on mobile in Urdu; the manager approves from a push notification without logging into the web portal.

## 13. Dashboards and reports, basic (RPT), wave A1

HR reporting uses the kernel semantic layer and builders (document 9); this section defines standard content.

- **FR-HRM-RPT-01** Dashboards shall include headcount, joiners and leavers, demographics, attrition, attendance, leave liability, and contract and probation expiries.
- **FR-HRM-RPT-02** Standard reports shall include employee master, strength by unit, grade and location, movement report, birthdays and anniversaries, document expiry, leave balance, leave utilization and attendance summaries.
- **FR-HRM-RPT-03** All standard reports shall be cloneable and editable through the report builder.
- **FR-HRM-RPT-04** HR semantic models shall respect data scopes and hide compensation fields unless the user has salary permission. (new)

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

## 21. Employee relations, discipline and helpdesk (ERL), wave B

- **FR-HRM-ERL-01** Case management shall support grievances, complaints and incidents with confidential handling, restricted case teams and tamper-evident notes.
- **FR-HRM-ERL-02** The disciplinary process shall be stage-configurable: allegation, show-cause notice, reply deadline, inquiry, hearing, decision and appeal, each with templates and legal timelines.
- **FR-HRM-ERL-03** Harassment and sensitive cases shall use dedicated routing, committee assignment and anonymous reporting where enabled.
- **FR-HRM-ERL-04** Union membership and collective agreement terms shall be recordable and applicable to relevant policies where the tenant uses them. (C)
- **FR-HRM-ERL-05** The HR helpdesk shall provide categories, SLAs, routing, knowledge base, canned responses, satisfaction surveys and reports.
- **FR-HRM-ERL-06** Surveys (pulse, engagement, exit) shall support anonymity, segmentation, scheduling, reminders and result dashboards with minimum group size.
- **FR-HRM-ERL-07** Announcements, polls and the employee directory shall be targetable by entity, unit, location and role.
- **BR-14** Case records are visible only to assigned case members and designated auditors; access is logged and exports are restricted.

## 22. Asset custody (AST), wave B

The asset register, acquisition, depreciation and maintenance belong to the Assets module (document 8). HRMIS owns the employee side.

- **FR-HRM-AST-01** HR shall issue assets to employees and record return with acknowledgement, using the Assets module register when licensed, or a lightweight HR register (type, serial number, status, condition) otherwise.
- **FR-HRM-AST-02** Unreturned assets shall appear in offboarding clearance and final settlement with recovery value.
- **FR-HRM-AST-03** Access cards, parking, lockers, desk booking and transport routes shall be manageable as bookable or assignable resources. (S)
- **FR-HRM-AST-04** Uniform and consumable issuance shall be tracked with entitlement cycles, using Inventory when licensed. (S)
- **FR-HRM-AST-05** Migrating the lightweight register to the Assets module shall be supported by an import template and link repair. (new)

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
- **FR-HRM-EXT-01** Extension points (custom tabs, widgets, actions, scripts, marketplace) are platform capabilities defined in the kernel and later waves; HRMIS exposes its objects and events to them. (wave D)

## 27. Data model overview

Tables live in the hrm schema (kernel references as in document 3). Sensitive columns are encrypted; effective-dated tables use exclusion constraints; ledgers and results are append-only.

| Area | Main tables | Notes |
| --- | --- | --- |
| People | persons, person\_identifiers (encrypted, blind index), person\_contacts, person\_addresses, dependents, emergency\_contacts, education, experience, skills, certifications | One person, many employments |
| Employment | employments, employment\_assignments (effective-dated), employment\_events, contracts, probation\_records | Reason codes on each change |
| Structure | positions, position\_versions, grades, bands, jobs, job\_versions | Org units are kernel tables |
| Pay assignment | compensation\_assignments (effective-dated), employee\_bank\_accounts (effective-dated, encrypted) | Salary visibility by field policy |
| Policies | policies, policy\_versions, policy\_assignments | Resolved through kernel rules |
| Attendance | devices, geo\_fences, shifts, rosters, roster\_lines, punches (partitioned, append-only), attendance\_days, regularization\_requests, overtime\_entries, timesheets |  |
| Leave | leave\_types, leave\_policies, leave\_ledger (append-only), leave\_requests, comp\_off\_entries | Balances derive from the ledger |
| Payroll | pay\_groups, pay\_components, salary\_structures, pay\_calendars, pay\_runs, pay\_run\_snapshots, pay\_inputs, pay\_results, pay\_result\_lines, pay\_traces, payment\_batches, final\_settlements | Results immutable after lock |
| Loans and expenses | loan\_types, loans, loan\_installments, expense\_categories, expense\_claims, claim\_lines, travel\_requests |  |
| Benefits and compliance | benefit\_plans, enrollments, provident\_fund\_ledger, gratuity\_accruals, compliance\_calendar, statutory\_returns |  |
| Talent | requisitions, candidates, applications, interview\_scores, offers, review\_cycles, goals, reviews, courses, sessions, enrollments\_learning, training\_bonds |  |
| Relations and custody | cases, case\_notes, tickets, surveys, asset\_custody | Restricted access |
| Outbox and inbox | outbox\_messages, inbox\_messages | Events of section 30 |

## 28. Baseline roles and permissions

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

## 29. Integration catalogue

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

## 30. Events

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

## 31. Non-functional requirements, acceptance and testing

- **Performance and scale:** as in GHR-01; verified at 1.5 times expected load.
- **Security:** field-level encryption of sensitive data, permission-bypass tests for every salary and case screen, tenant isolation tests in every pipeline run, penetration test before the first production customer.
- **Test levels:** unit, integration, system, performance, security and user acceptance; every requirement maps to at least one test and a traceability matrix is generated per release.
- **Statutory validation:** payroll and compliance packs are validated against independent calculations of at least 50 representative employee scenarios per pack release, reviewed by a qualified practitioner in the country.
- **Configuration testing:** each configurable capability is exercised through the admin UI by a non-developer persona.
- **Data migration tests:** import of 10,000 employees from spreadsheet and from a competitor export, with error handling checks, plus opening leave balances and loan balances with tie-out.
- **Parallel payroll:** at least two cycles in parallel with the customer's process using the tool in FR-HRM-PAY-35.
- **Integration tests with Finance:** payroll, loan and claim events produce expected journals; with Finance absent the Accounting Export output matches.
- **UAT:** each wave closes with UAT by pilot customers using signed-off scripts; exit criteria are zero open severity-1 and severity-2 defects.

### 31.1 Release exit criteria

| Wave | Exit criteria |
| --- | --- |
| A1 | A pilot customer runs daily attendance, leave and self-service for a full month without workarounds |
| A2 | Two payroll cycles run in parallel and reconcile; journals reach Finance (or the export) and tie to the payroll register |
| B | One full hiring cycle and one appraisal cycle completed; Saudi pack validated by a local practitioner |
| C | First US tenant completes a payroll cycle through the partner and a new-hire compliance flow |
| D | AI features pass bias, privacy and accuracy review |

## 32. Assumptions, dependencies and open items

**Assumptions:** statutory content is delivered as pack configuration and verified before each release; US payroll tax is delivered by a certified partner; biometric support starts with the most common local device models; English, Urdu and Arabic are launch languages.

**Dependencies:** bank file format specifications for the first customers' banks; legal and statutory reviewers for pack validation in Pakistan and Saudi Arabia; partner agreements for US payroll, benefits feeds and background checks before wave C design is frozen; Finance posting rules for payroll available in wave A2.

**Open decisions:** beachhead customer segment, which decides the order inside wave A1 and the starter packs; which devices and banks are supported first; scripting and extension scope for partners; pricing and metering definitions for active employees and add-ons; whether employee claims need a corporate card integration at launch.

**Resolved since v0.1:** on-premise at launch (yes, same build); mobile approach (cross-platform with offline); product positioning (module of the ERP).

## 33. Mapping from v0.1 identifiers

| v0.1 | v0.2 |
| --- | --- |
| FR-P0-\* (platform) | Kernel and admin specifications, documents 2 and 4; policy requirements retained as FR-HRM-POL |
| FR-P1-EMP, POS, DOC, ATT, LVE, ONB, SSP, RPT | FR-HRM-EMP, POS, DOC, ATT, LVE, ONB (basic 01 to 06), SSP, RPT with the same numbers |
| FR-P2-PAY, LON, EXP, CMP | FR-HRM-PAY, LON, EXP, CMP with the same numbers; compliance items CMP-07 to CMP-10 became CPL-01 to CPL-04 |
| FR-P3-REC, ONB (advanced), PRF, LND, ERL, AST | FR-HRM-REC, ONB-10 to ONB-13, PRF, LND, ERL, AST |
| FR-P4-USD, USL, USP, USB, USC, USS | FR-HRM-USD, USL, USP, USB, USC, USS |
| FR-P5-ANA, AIA, WFP, EXT | FR-HRM-ANA, AIA, WFP; EXT moved to the platform |

**Next documents:** 12 Claude Code Operating Manual, then the module specifications 7 Procurement, 8 Inventory and Asset Management, 10 Supply Chain, 11 Sales and Distribution, 9 Analytics, Reporting and AI, and 13 UX and Design System Principles.
