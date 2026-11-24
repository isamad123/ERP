# Acceleration plan: waves, parallel lanes, batch approvals (CR-082)
Status: APPROVED by the product owner (2026-11-22, "Approve all"). Aim: shorter calendar time at lower token cost, same quality gates.

## 1. What is already in place (nothing to redo)
Lean sessions, quiet tests, generator first, handoff notes, Sonnet by default and fresh Opus review for class B only are already
rules (`docs/token-strategy.md` sections 1 to 7, CR-047, CR-052). This plan adds parallel lanes, batch approvals, the model
changes below and the scope trims. Done on `main` (2026-11-22): S1.01 to S1.12d, S1.13a; slice files S1.13 to S1.25 approved.

## 2. Model tiering (cost)
- Opus only for class A and B parts: S1.20a to d, S1.23a and b, S1.29, S1.31, S1.32 (engine), S1.35 to S1.39, S1.43, S1.52, S1.53.
- Everything else Sonnet, including S1.27 (biometric connector) and the screen, set-up and reporting slices.
- Fresh Opus review only for class B parts and the exceptions already ruled (S1.24a uploads, S1.11b2, S1.11c1). Other slices:
  automated checks, with an Opus sample of one slice in five.

## 3. Waves and lanes (time)
At most **three** build sessions run at once, each on its own branch from the latest `main`, each owning one module area.
| Wave | Lanes (run together) | Needs first |
| --- | --- | --- |
| 1 (now) | A: S1.13b onward, then S1.14 to S1.19 (screens) - B: S1.20a to d (Opus) - C: slice files for Wave 2 and 3 (planning only) | S1.12 (done) |
| 2 | A: S1.21, S1.22, S1.23c - B: S1.23a, b then S1.32 engine (Opus) - C: S1.28 shifts, S1.34 pay set-up, then S1.27a, c, b (attendance capture and API clients; CR-095) | S1.20b |
| 3 | A: S1.24, S1.25, S1.26 - B: S1.29 attendance engine (Opus), then S1.31 - C: S1.30, S1.33, S1.35 payroll engine (Opus) | S1.20, S1.28, S1.32 |
| 4 | A: S1.36 and S1.37 (Opus; the mechanism is built with test tables now, the pack content and any customer payroll wait for the tax professional, CR-109) - B: S1.38, S1.39 (Opus; S1.38c screens Sonnet) - C: S1.40, S1.41, S1.42 | S1.35 |
| 5 | S1.43 (Opus), S1.44, S1.45, S1.46 (calendar only), S1.48 to S1.51 | S1.38 |
| 6 | S1.52, S1.53 (Opus) | everything |
Longest chain: S1.20 > S1.28/S1.32 > S1.29/S1.35 > S1.36/S1.37 > S1.38 > S1.43 > S1.52 > S1.53.

## 4. Lane rules (also in CLAUDE.md)
1. One slice part per session; a lane never edits another lane's module folders or tables.
2. Shared files (generator, permission and settings manifests, `web/apps/web/src/modules/*.ts`, registers, CLAUDE.md) are edited
   in small separate commits; rebase on `main` before the pull request; a conflict that is not mechanical goes to the product owner.
3. Reserve CR and R numbers in `docs/registers/risks-and-changes.md` before starting (existing rule), so lanes never collide.
4. Never more than three open build pull requests. Merge as CI turns green (standing instruction); update from `main` first.
5. Migrations: timestamps come from the slice's reserved range so two lanes never write the same file name.

6. One coordinator (product owner, 2026-11-23): only the coordinator session starts build or review sessions; other sessions report to it.
   About eight sessions ran at once on 2026-11-23 and the account's 5-hour usage limit stopped all work from about 05:52 to 08:20 UTC.

## 5. Batch approvals
- Slice files are written a wave ahead and approved together, in one sitting.
- Each question carries a recommendation; **silence on an item means the recommendation stands** (recorded as the ruling).
- The approved slice file is the plan: a build session starts from it without a separate plan-mode approval. It still stops
  and asks when the specification is silent or contradictory (CLAUDE.md rule, unchanged).
- A wave report replaces a stop between slices; handoff notes follow the half-page template in section 7.

## 6. Scope trims (approved)
- FR-HRM-PAY-35 (parallel-run comparison tool) moves from 1.0 to 1.1; S1.47 leaves Release 1.0. Until then a customer compares
  the first payrolls outside the product (R-136).
- S1.46 builds the compliance calendar only (FR-HRM-CPL-01, CPL-04). Returns (CPL-02, generation) and certificates (CPL-05)
  stay in 1.0 for now: CPL-02 has a data part in S1.20d and a tax certificate is needed at year end. The product owner may retag
  them later; this needs a ruling, not a guess.
- S1.48 form designer stays but is built minimal (fixed sections, rating and text fields) because probation and appraisal forms
  (ONB-12, PRF-03) need it (R-020). Dashboards beyond the core HR set in S1.51 move to 1.1 when its slice file is written.

## 7. Handoff template (half a page)
Built (3 lines) - Files to open next - Tests added (names) - Decisions and CR/R numbers - Open risks - Measures: model, parts, wall-clock, first-time CI pass.

## 8. Measuring
Every handoff records the measures. After Wave 1 the product owner and Claude compare against the S1.11 parts and adjust this plan.
