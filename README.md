# ERP Platform

A configurable, multi-company ERP product for organizations from 5 to 50,000+ employees, sold as SaaS or on-premises,
module by module. Markets: Pakistan first, then Saudi Arabia, then the United States.

## Status
Pre-development. Phase 1 (HRMIS) is being prepared: requirement scope tagging, then data design. No application code yet.

## Phases (decisions D-41, D-48)
| Phase | Modules | Release |
| --- | --- | --- |
| 1.0 | Minimum platform kernel + HRMIS web (HR, attendance, leave, Pakistan payroll, self-service web) | 1.0 HR and Payroll |
| 1.1 | Android ESS app, offline sync, geo-attendance, field tasks, workspaces, recruitment, performance, learning, employee relations | 1.1 |
| 2 | Inventory and Asset Management | 2.0 |
| 3 | Finance | 3.0 |
| 4 | Procurement, Supply Chain, core Sales | 4.0 |

## Where to start
- `CLAUDE.md` — rules for AI coding sessions.
- `docs/architecture-reference.md` — the short architecture reference.
- `docs/decisions/decision-register.md` — every decision and its status.
- `docs/specs/16-erp-baseline.md` — review, cross-module rules, phase plan, governance (section 14 is current).
- `docs/specs/` — the full planning set. From now on these files, not the online drafts, are the source of truth.

## Technology
.NET 10 (ASP.NET Core, EF Core, Dapper), PostgreSQL (one database per customer), Valkey, OpenIddict,
React 19 + TypeScript + Vite, React Native + Expo. Permissive open-source licences only.
