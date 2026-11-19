# R-027 review sessions (CR-052 Q8)
Status: APPROVED by the product owner (2026-11-19, CR-052 Q8; started 2026-11-19).
Class: B (review)        Model: **Opus 5.5 (high effort)**, one fresh session per slice. All seven run in parallel
(product owner, 2026-11-19: "run all six remaining reviews in parallel"); the order is the priority for rulings:

| Order | Slice | Merged as | Handoff | Reserved numbers |
| --- | --- | --- | --- | --- |
| 1 | S1.03 tenancy, provisioning, migrator | 4f0ce51 (#3) | `docs/handoff/2026-11-15-S1.03.md` | CR-055 (taken by S1.11c2; none needed), R-037 to R-039 |
| 2 | S1.04 tenant-isolation suite | 99425ff (#4) | `docs/handoff/2026-11-15-S1.04.md` | CR-056, R-040 to R-042 |
| 3 | S1.09b entitlements and licensing | 1c7a3f6 (#24) | `docs/handoff/2026-11-17-S1.09b.md` | CR-057, R-043 to R-045 |
| 4 | S1.08 numbering | e7d4a6b (#21) | `docs/handoff/2026-11-17-S1.08.md` | CR-058, R-046 to R-048 |
| 5 | S1.10a workflow engine part 1 | ecd1df9 (#26) | `docs/handoff/2026-11-17-S1.10a.md` | CR-059, R-049 to R-051 |
| 6 | S1.10b workflow engine part 2 | 8cdb88b (#27) | `docs/handoff/2026-11-17-S1.10b.md` | CR-060, R-052 to R-054 |
| 7 | S1.09a settings and flags | fa8dd7b (#23) | `docs/handoff/2026-11-17-S1.09a.md` | CR-061, R-055 to R-057 |

S1.11b2 is reviewed alongside S1.12 (CR-052 Q8 b); S1.11c1 was reviewed on 2026-11-19.

## How each review runs (the S1.05, S1.06, S1.07 and R-028 reviews are the model)
1. Read CLAUDE.md, `docs/architecture-reference.md`, the slice file, its handoff and `git show --stat <commit>`. Review
   **the slice's diff and brief**, not the whole repository (token-strategy section 7), then the current state of
   the files it touched, because later slices changed some of them.
2. Look for correctness and security defects: tenant isolation, permissions, data scopes, concurrency and locking,
   transactions, SQL safety, secrets, audit, and tests that would pass even if the behaviour were wrong. Prove a
   finding with a test or a query where you can.
3. Write `docs/reviews/<slice>-review.md` in the shape of `docs/reviews/S1.05-review.md`: a plain-language summary for
   the product owner; findings most severe first (HIGH, MEDIUM, LOW, with file and line, and a fix); "tests that would
   pass even if the behaviour were wrong"; "checked and found correct".
4. Present the findings to the product owner and **wait** for rulings. Then fix what is ruled, with tests citing FR ids,
   raise change requests for baseline items (reserved numbers only), add an "Outcome" table to the review, and open
   the pull request. The fixes of a review are themselves class B: they merge only when CI is green and the review's
   high and medium findings are fixed or ruled on.
5. Write a handoff of at most 15 lines. Do **not** edit the R-027 row (seven sessions run at once; the coordinating
   session updates it). New register rows go at the end; if `main` moved, merge it and keep every row.

Out of scope: rewriting working code for style; any slice not named above.
