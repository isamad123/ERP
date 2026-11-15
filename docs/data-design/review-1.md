# Independent review 1 (15 November 2026)

A fresh Opus session that had not seen the design reviewed it (D-45, class A). It reported 30 defects: 7 critical,
13 high, 10 medium. All 30 are resolved in the YAML files and the README; the design grew from 199 to 204 tables.

| # | Severity | Finding | Resolution |
| --- | --- | --- | --- |
| 1 | Critical | Locked payroll results editable; reversal flag conflicted with the lock | Lock triggers on runs, run employees, results, lines, traces; new append-only `hrm.pay_result_reversals` |
| 2 | Critical | Same result could be paid in two batches | Partial unique key on payment lines; lines must sum to net; one live batch per run, mode and account |
| 3 | Critical | Two regular runs per period; overlapping coverage on pay-group change or final settlement | Partial unique key per pay group and period; exclusion on employee and period dates across runs |
| 4 | Critical | Expenses, overtime, retro adjustments could enter payroll twice | Unique source keys on pay inputs; single overtime path; one-time application of retro items; original period stored |
| 5 | Critical | Attendance lock had gaps (optional period, leave and overtime unlocked, reopen allowed) | Period required; lock covers attendance, leave days, overtime, regularization; no reopen after run lock; one period per pay period, no overlaps |
| 6 | Critical | Accounting event unique key impossible on a partitioned table; append-only table with status | Not partitioned; export state moved to `itg.accounting_event_exports` |
| 7 | Critical | Leave could be deducted twice; ledger retries double-posted; transfer left balance on both employments | Daily total check; ledger idempotency and single reversal; `transfer_out` entry |
| 8 | High | Effective-dating keys referenced missing columns | Generated key columns; org units no longer effective-dated; keys added |
| 9 | High | Nullable columns in unique keys allowed duplicates | `NULLS NOT DISTINCT` rule for all such keys |
| 10 | High | Tax slabs editable after use; overlapping slabs possible | Rows locked once effective or used; bounds check; non-overlap exclusion |
| 11 | High | Component formulas not versioned; cycles possible | New `hrm.pay_component_versions` (effective-dated); ids not codes; cycle check |
| 12 | High | References could cross companies | Composite company foreign keys rule |
| 13 | High | Only one social security registration per company; no province | Registration key includes authority; `region_code` on locations; new `hrm.employment_statutory_registrations` |
| 14 | High | Device user numbers reused over time | Device maps effective-dated; resolve by punch date |
| 15 | High | Loan recoveries could orphan or exceed balance | Posted at run lock only; unique source key; balance check; installments frozen |
| 16 | High | Final settlement editable after approval; no currency; could be paid outside a run | Locked from approval; currency added; run required before paid |
| 17 | High | Liability accruals could not be corrected | Sequence and reversal columns |
| 18 | High | Two published rosters could clash | One published line per employee and day |
| 19 | High | No record of the account actually paid | Account, IBAN, cheque number snapshotted on payment lines; value date and checksum on batches |
| 20 | High | Wallet and account numbers not checkable for duplicates | Blind index on account number |
| 21 | Medium | Money without currency | Currency added to the listed tables or their headers |
| 22 | Medium | Pay periods could overlap; inputs into closed periods | Unique keys, exclusion, date checks, closed-period trigger |
| 23 | Medium | Volume traps at 50,000 employees | Attendance days partitioned; denormalized keys and indexes on result lines and results; outbox key fixed |
| 24 | Medium | Held salary could be recalculated and paid twice | Release pays the original result; batches may have no run; source run on each line |
| 25 | Medium | Identifier renewal blocked by unique key | Identifiers no longer effective-dated; history in audit |
| 26 | Medium | Draft policy versions blocked by exclusion | Exclusion excludes drafts; dates set on activation |
| 27 | Medium | Secrets and identifiers in plaintext json | Device secrets in the secrets store; snapshots hold ids and amounts only |
| 28 | Medium | One nominee share for all schemes | New `hrm.nominations` per scheme, shares sum to 1 |
| 29 | Medium | Hierarchy cycles; active version could belong to another definition | Cycle triggers; composite version foreign keys |
| 30 | Medium | Single-row rules unenforced (primary employment, active entitlement, provident fund on transfer) | Partial unique keys; transfer entries |
