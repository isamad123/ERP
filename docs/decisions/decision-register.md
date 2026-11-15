# Decision register

Status as of 15 November 2026. Full text and rationale: `docs/specs/14-consistency-review-and-decision-log.md`
(sections 6 and 16) and `docs/specs/16-erp-baseline.md` (sections 4 and 14). A change to any approved decision
needs a change request approved by the product owner (D-39).

Status values: **Approved**, **Superseded** (by the decision named), **Rejected**.

| ID | Decision | Status |
| --- | --- | --- |
| D-01 | Modular monolith on .NET, PostgreSQL, React; Clean Architecture inside each module | Approved |
| D-02 | Database per tenant behind a tenant-resolution layer | Approved; strengthened by D-47 |
| D-03 | Industry-neutral core; industries through packs | Approved |
| D-04 | Country packs for Pakistan, United States, Saudi Arabia | Approved |
| D-05 | One entitlement service for licence files and subscriptions | Approved 15 Nov 2026 |
| D-06 | Built-in analytics; Superset optional later | Approved |
| D-07 | AI as a kernel service (AI Gateway); not built in Phase 1 | Approved 15 Nov 2026 |
| D-08 | First build: foundation, Finance and HRMIS | Superseded by D-41 |
| D-09 | Permissive open-source licences only, enforced in CI | Approved 15 Nov 2026 |
| D-10 | Modules depend only on the kernel; other modules are optional integrations | Approved 15 Nov 2026 |
| D-11 | Items, partners and units of measure live in the kernel | Approved 15 Nov 2026 |
| D-12 | Order-related supplier invoices matched in Procurement, handed to Finance AP | Approved 13 Nov 2026 |
| D-13 | Inventory stores replenishment parameters; Supply Chain proposes changes | Approved 13 Nov 2026 |
| D-14 | Quality inspection owned by Inventory | Approved 13 Nov 2026 |
| D-15 | Receiving, issuing and returns are synchronous commands; owners publish events | Approved 13 Nov 2026 |
| D-16 | Sales issues customer invoices when licensed, otherwise Finance AR | Approved 13 Nov 2026 |
| D-17 | Return authorizations owned by Sales | Approved 13 Nov 2026 |
| D-18 | DeliveryConfirmed published by Supply Chain, otherwise Sales | Approved 13 Nov 2026 |
| D-19 | Projects are a Finance dimension until a Projects module exists | Approved 13 Nov 2026 |
| D-20 | Kernel adds portal, offline sync, global search, collaboration, print service | Approved 13 Nov 2026; phasing by D-33 and D-42 |
| D-21 | Event conventions and canonical event catalog (spec 14 section 4) | Approved 13 Nov 2026 |
| D-22 | Canonical module names, codes and requirement prefixes | Approved 13 Nov 2026 |
| D-23 | Kernel partner onboarding workflow template | Approved 13 Nov 2026 |
| D-24 | Entitlement key catalog (spec 14 section 8) is the packaging starting point | Approved 13 Nov 2026 |
| D-25 | Analytics API and external reporting database role | Approved 13 Nov 2026 |
| D-26 | Three editions (Starter, Business, Enterprise) as entitlement templates | Approved 15 Nov 2026 |
| D-27 | Small tenants on the same architecture | Superseded by D-47 |
| D-28 | Trading Starter without Sales | Superseded by D-41 |
| D-29 | Simple mode is configuration, never a code branch | Approved 15 Nov 2026 |
| D-30 | Hybrid tenancy with pooled mode | Superseded by D-47 |
| D-31 | Atomic ledger posting by default | Deferred to Phase 3 by D-43 |
| D-32 | Four phases with Sales core in Phase 3 | Superseded by D-41 |
| D-33 | Minimum Viable Kernel; services scheduled by phase | Approved 15 Nov 2026; narrowed by D-42 |
| D-34 | Release scope tagging (R, L, X) at each phase entry, approved by the product owner | Approved 15 Nov 2026 |
| D-35 | Module data design gate before any module migration | Approved 15 Nov 2026 |
| D-36 | Geospatial computed in the application; no database geospatial extension | Approved 15 Nov 2026 |
| D-37 | HRMIS v0.3.1 is the HRMIS build baseline; kernel accepts KER-01 to KER-08 | Approved 15 Nov 2026 |
| D-38 | Three-model allocation including Fable | Superseded by D-45 |
| D-39 | Change control for baseline items; repository registers | Approved 15 Nov 2026 |
| D-40 | Four size profiles as configuration presets | Approved 15 Nov 2026 |
| D-41 | Phase order: HRMIS → Inventory and Assets → Finance → Procurement, Supply Chain (and core Sales) | Approved 15 Nov 2026 |
| D-42 | Phase 1 = minimum kernel + HRMIS core + Pakistan payroll + Android M1 | Approved 15 Nov 2026 |
| D-43 | Until Finance exists, financial effects are accounting events and journal exports; Finance starts at a cutover | Approved 15 Nov 2026 |
| D-44 | Database per tenant with `tenant_id` kept on tables | Rejected 15 Nov 2026 |
| D-45 | Opus 5.5 for design and integrity-critical work with fresh-session review; Sonnet 5.5 for routine; no Fable | Approved 15 Nov 2026 |
| D-46 | Conditions to start Phase 1 (spec 16 section 14.7) | Approved 15 Nov 2026 |
| D-48 | Phase 1 splits into Release 1.0 (HRMIS web only: core HR, attendance, leave, Pakistan payroll, self-service web) and Release 1.1 (Android ESS app, offline sync, geo-attendance, field tasks, workspaces, and the remaining HRMIS features: recruitment, performance, learning, employee relations, asset custody, Should-have items), both before Phase 2. Saudi and US packs, AI and analytics stay later | Approved 15 Nov 2026 (product owner) |
| D-49 | Performance management (PRF) and learning and development (LND) move from Release 1.1 to Release 1.0 | Approved 15 Nov 2026 (product owner) |
| D-47 | Separate database for every customer; no `tenant_id` on business tables; no shared-database hosting | Approved 15 Nov 2026 |

## Working assumptions (not yet confirmed)
| Item | Assumption |
| --- | --- |
| Sales placement | Core Sales is built in Phase 4 with Procurement and Supply Chain |
| AS-1 | First customers are in Pakistan; Saudi Arabia next; US later via partners |
| AS-2 | Hosted SaaS first; on-premises later |
| AS-4 | A practising Pakistani payroll and tax professional validates the pack before any customer payroll |
| AS-6 | Largest single tenant: 50,000 employees, 2,000 concurrent users, 50 legal entities |
