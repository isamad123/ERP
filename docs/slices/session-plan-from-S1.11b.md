# Session plan from S1.11b onward (proposal, 2026-11-18)
Status: APPROVED by the product owner (2026-11-18, "approve and accept all"), recorded as CR-047. Points 1 to 3 apply from
S1.11b1; the `Model:` line of the S1.11 slice file follows point 1.

## Why
S1.11a used far more tokens than earlier slices, for four reasons:
- It ran on Opus.
- It was one long session; every step resends the whole conversation.
- It was a very large build: about 25 new files and 40 tests.
- The session restarted three times and ran several full test runs.

The fixes below keep quality the same and cut cost.

## 1. Model per part
| Part | Class | Model now | Proposed | Reason |
| --- | --- | --- | --- | --- |
| S1.11b1 notifications core | C | Opus | **Sonnet** | Builds on the S1.11a contracts (`IEventConsumer`, alert events); no integrity-critical engine left |
| S1.11b2 e-mail and identity e-mails | C, identity parts B | Opus | **Opus for item 16 only**, Sonnet for 12, 14, 15 | Password reset and the e-mail second factor are security-critical |
| S1.11c1 file store, attachments, retention | C | Opus | **Sonnet**, Opus review | Encryption per customer reuses the S1.05 key service |
| S1.11c2 templates and PDF | C | Opus | **Sonnet** | Rendering work; new packages need the licence check only |

The list of Opus slices in the build plan stays as it is: class B slices and the fresh-session review of each Opus diff.

## 2. Smaller sessions
Each session should add at most about 8 new files and about 15 tests, and use one fresh session per part.
- **S1.11b1:** event catalogue, notification templates, audience and subscriptions, in-app centre, preferences and quiet
  hours (Build items 8 to 11 and 13). Ends with in-app items for workflow, licence and alert events.
- **S1.11b2:** SMTP adapter, queue, retry, delivery log and resend, delivery audit, senders, identity e-mails (items 12 and
  14 to 16). Adds MailKit after the licence check.
- **S1.11c1:** file store (local folder and S3), encryption, scan hook, attachments, versions, retention, legal hold,
  expiry reminders (items 17 to 19).
- **S1.11c2:** Scriban templates, Chromium PDF, letterhead, QR and barcodes, fonts (item 20).

The later slices that are larger than this rule are split the same way when their slice file is written:
- S1.20 person and employment;
- S1.29 attendance engine;
- S1.32 leave engine;
- S1.35 payroll engine.

## 3. How each session works
1. Start from the handoff note's "For the next part" list and the slice section only. Do not explore the code base widely:
   the handoff names the files to open.
2. Write a short plan of about one screen: files, tables, endpoints, tests, open points. Detail goes in the code and tests.
3. While building, run only the new test classes, and read only the names and messages of failed tests, never whole logs.
4. Run the full suite once before the pull request, in the background.
5. Commit and push at each milestone, so a restart loses nothing and needs no re-reading.
6. Test rigs share one customer per test class where the tests allow it, so the test server does not run out of
   databases.

## 4. Ruling (2026-11-18)
All three points accepted: Sonnet for S1.11b1, S1.11c1 and S1.11c2 and for S1.11b2 items 12, 14 and 15, Opus for S1.11b2 item 16;
four parts; the working rules of section 3 apply to every later slice.
