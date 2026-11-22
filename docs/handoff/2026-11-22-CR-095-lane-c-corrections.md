# 2026-11-22 CR-095: corrections to the lane C slice files (planning only, no code)
- Built: two lane C sessions wrote S1.27 to S1.35 at the same time; PR #75 merged first, and PR #76 (same CR numbers) was closed unmerged by the product owner's ruling.
  Its three findings move into the merged files under CR-095: S1.27 part c (API clients and keys, FR-ADM-SEC-02, FR-KRN-INT-07); S1.32 item 14 (transfer carry-over,
  FR-XM-30); S1.31 reopening only by cancelling the locking run before it is locked (data design). S1.27 is placed in Wave 2, lane C.
- Files to open next: the slice file of the part being built. S1.27 is built in the order a, c, b.
- Tests added: none (planning only).
- Decisions and numbers: CR-095 approved by the product owner; risk R-208. The S1.20 wording on backdated changes into locked periods is corrected separately by CR-096 (PR #78).
- Open risks: R-208.
- Still open for the product owner: S1.26 Q4 (S1.27 Q2 was ruled in PR #80: selfie and geo-fence move to 1.1); S1.26 Q4 (Arabic text of SSP-08 to Later is a scope change, CR-083 does not cover it);
  statutory values for the professional (AS-4).
- Measures: model Opus 5.5, one planning session, five files changed.
