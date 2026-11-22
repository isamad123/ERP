# Release 1.0 build plan

Status: approved 15 November 2026 (D-50). Waves, lanes and model changes: `acceleration-plan.md` (CR-082). Slice files (`S1.n-*.md`) are written one batch ahead.

Each slice is one fresh session (or one fresh session per part where its slice file splits it). **Model** is what to select with `/model` before starting the batch. Classes follow
`docs/token-strategy.md`: A and B use Opus 5.5 at high effort, C and D use Sonnet 5.5 unless stated.

## Batch 1 — Foundation (Opus)
| Slice | Work | Class | Model |
| --- | --- | --- | --- |
| S1.01 | Repository skeleton: solution, projects, CI on GitHub Actions, licence gate, architecture tests | A | Opus |
| S1.02 | Code generator: reads `docs/data-design/*.yaml`, emits migrations, entities, repositories, APIs, permission registration, list and form screens, tests | A | Opus |
| S1.03 | Control plane, tenant resolution, database-per-customer provisioning, migrator | B | Opus |
| S1.04 | Tenant-isolation test suite running against every endpoint | B | Opus |
| S1.05 | Identity: users, passwords, MFA, sessions, lockout | B | Opus |
| S1.06 | Roles, permissions, data scopes, field security, maker-checker | B | Opus |
| S1.07 | Audit log with hash chain, access log, per-person keys | B | Opus |
| S1.08 | Numbering (gap-free and gap-allowed) | B | Opus |
| S1.09 | Settings, feature flags, entitlements and licence states (built as S1.09a settings and flags, then S1.09b entitlements; ruling Q0) | B | Opus |
| S1.10 | Workflow engine: linear and conditional approvals, delegation, escalation, inbox (built as S1.10a engine and decisions, then S1.10b delegation, escalation, inbox and administration; ruling Q0) | B | Opus |
| S1.11 | Jobs (chunked, resumable, run-once), notifications (in-app, email), documents and templates (PDF) (built as S1.11a jobs and events, then S1.11b notifications and identity e-mails, then S1.11c files and templates; ruling Q0) | C | Opus (S1.11a); later parts per CR-047 (see the S1.11 slice file) |
| S1.12 | Web shell and design system: login, navigation, first list screen, first form screen, first approval screen (the patterns Sonnet copies) (built as S1.12a sign-in and foundation, S1.12b shell, S1.12c lists and forms, S1.12d approvals; ruling Q0; fresh Opus review of S1.12a) | C | Opus |

## Batch 2 — Foundation screens (Sonnet)
| Slice | Work | Class | Model |
| --- | --- | --- | --- |
| S1.13 | Admin console: users, groups, roles, permissions, data scopes, field security screens (parts S1.13a users, S1.13b access; slice file Q0) | D | Sonnet |
| S1.14 | Organization screens: companies, branches, locations, units, hierarchies, org chart, holiday calendars (parts S1.14a, S1.14b; fresh Opus review of the bank-account part, Q4) | D | Sonnet |
| S1.15 | Settings centre, labels, custom fields, option lists, numbering screens, workflow route step-list editor (S1.10 ruling Q2), notification and document template editor (S1.11 ruling Q15) (parts S1.15a settings and branding, S1.15b labels and custom fields at high effort, S1.15c numbering and routes, S1.15d templates) | D | Sonnet |
| S1.16 | Import and export centre, job centre, notification log, system health, licence page (parts S1.16a imports with a fresh Opus review (uploaded files), S1.16b exports, S1.16c operations; slice file Q0) | C (a, b), D (c) | Sonnet |
| S1.17 | Vendor platform console: platform sign-in and roles, customer list, provisioning wizard, entitlements (parts: S1.17a platform sign-in on Opus with a fresh review, S1.17b screens on Sonnet, CR-073) | C | Sonnet (Opus for S1.17a, CR-073) |
| S1.18 | First-run wizard and readiness checklist (parts S1.18a, S1.18b) | C | Sonnet |
| S1.19 | English and Urdu translations, right-to-left checks (parts S1.19a, S1.19b with a native Urdu review between them) | D | Sonnet |

## Batch 3 — HR core (mixed)
| Slice | Work | Class | Model |
| --- | --- | --- | --- |
| S1.20 | Person and employment model, effective-dated assignments, events with reason codes, transfers between companies (parts S1.20a persons, S1.20b employments, S1.20c transfers and terms, S1.20d bank and statutory; a fresh Opus review per part) | B | Opus |
| S1.21 | Employee screens: profile sections, timeline, bulk import, duplicate check (parts S1.21a, S1.21b) | D | Sonnet |
| S1.22 | Positions, grades, headcount, org chart by position (parts S1.22a, S1.22b) | C | Sonnet |
| S1.23 | Policy framework: types, versions, attribute-rule assignment, resolution view, Pakistan pack loader (parts S1.23a policies and S1.23b pack loader on Opus with fresh reviews, S1.23c first Pakistan pack content on Sonnet) | B | Opus (S1.23c Sonnet, CR-079) |
| S1.24 | Documents vault, letters from templates with approval, policy acknowledgements (parts S1.24a vault with a fresh Opus review (uploads), S1.24b letters, S1.24c publications) | C | Sonnet |
| S1.25 | Onboarding and offboarding checklists, separations, clearance (parts S1.25a, S1.25b) | C | Sonnet |
| S1.26 | Self-service web: profile change requests, service requests, home page (two parts; slice file approved, CR-084) | D | Sonnet |

## Batch 4 — Time and leave (mixed)
| Slice | Work | Class | Model |
| --- | --- | --- | --- |
| S1.27 | Biometric device connector and import (one reference device type first); API clients and keys (FR-ADM-SEC-02) (S1.13 ruling Q2, CR-069) (parts S1.27a API clients with a fresh Opus review, S1.27b devices and drivers, S1.27c web, kiosk, HR entry and file import; slice file approved, CR-088) | C | Sonnet (CR-082; was Opus) |
| S1.28 | Shifts, rosters, Ramzan timings (two parts; slice file approved, CR-085) | D | Sonnet |
| S1.29 | Attendance engine: punches to daily results with explanation, exceptions, overtime (parts S1.29a processing, S1.29b exceptions, overtime, late and early rules; fresh Opus review of each; slice file approved, CR-089) | B | Opus |
| S1.30 | Regularization and duty requests, attendance dashboard and reports (parts S1.30a requests and queues, S1.30b dashboard and reports; slice file approved, CR-090) | D | Sonnet |
| S1.31 | Attendance period lock and retro adjustments (FR-XM-26) (parts S1.31a lock and reopening, S1.31b changes after lock; fresh Opus review of each; slice file approved, CR-091) | B | Opus |
| S1.32 | Leave engine: ledger, accrual, carry-forward, encashment, sandwich rules, year-end (two parts, fresh Opus review of each; slice file approved, CR-086) | B | Opus |
| S1.33 | Leave screens: types, requests, calendars, balances (parts S1.33a employee and manager, S1.33b HR administration; slice file approved, CR-092) | D | Sonnet |

## Batch 5 — Payroll (Opus, then Sonnet) — needs the validated Pakistan rules
| Slice | Work | Class | Model |
| --- | --- | --- | --- |
| S1.34 | Pay set-up: groups, periods, components, structures, compensation assignments (two parts, fresh Opus review of part b; slice file approved, CR-087) | C | Sonnet |
| S1.35 | Payroll engine: snapshot, calculation, trace, chunked job, determinism tests (parts S1.35a evaluator and pro-rata, S1.35b runs, inputs and snapshot, S1.35c calculation, trace and performance; fresh Opus review of each; slice file approved, CR-093) | B | Opus |
| S1.36 | Pakistan income tax (salaried slabs, irregular income, declarations, previous employer) | B | Opus |
| S1.37 | Statutory contributions: EOBI, provincial social security, WWF, provident fund, gratuity accrual | B | Opus |
| S1.38 | Run stages, validation flags, variance report, maker-checker approval, lock and reopen | B | Opus |
| S1.39 | Arrears and retro corrections, off-cycle runs, salary hold | B | Opus |
| S1.40 | Payslips (bilingual PDF), bank salary files, payment status import | C | Sonnet |
| S1.41 | Loans and advances with payroll recovery | C | Sonnet |
| S1.42 | Expense claims with policy checks and reimbursement | C | Sonnet |
| S1.43 | Final settlement with clearance blocking (FR-XM-29) | B | Opus |
| S1.44 | Compensation: bands, increment cycles, benefits, provident fund ledger, liabilities | C | Sonnet |
| S1.45 | Journal export (accounting events, account mapping, file formats) | C | Sonnet |
| S1.46 | Compliance calendar, statutory returns, tax certificates | C | Sonnet |
| S1.47 | Parallel-run comparison tool (moved to Release 1.1 by CR-082) | C | Sonnet |

## Batch 6 — Performance and learning (Sonnet)
| Slice | Work | Class | Model |
| --- | --- | --- | --- |
| S1.48 | Form designer and responses | C | Sonnet |
| S1.49 | Review cycles, goals, reviews, sign-off | C | Sonnet |
| S1.50 | Courses, sessions, enrolments, learning records, mandatory training, bonds | D | Sonnet |

## Batch 7 — Hardening (Opus)
| Slice | Work | Class | Model |
| --- | --- | --- | --- |
| S1.51 | Standard reports and dashboards (HR, attendance, leave, payroll) | D | Sonnet |
| S1.52 | Load test: payroll for 50,000 employees, attendance volume, isolation under load | B | Opus |
| S1.53 | Security review checklist, backup and restore drill, release packaging | B | Opus |

## Count
53 slices: 26 on Opus (mostly foundation, engines and payroll), 27 on Sonnet (screens, set-up, reports, routine modules).
Once the code generator (S1.02) exists, many Sonnet slices are short, because the generator writes the repetitive part.
