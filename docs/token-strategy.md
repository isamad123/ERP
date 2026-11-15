# Token and cost strategy

Binding for every build session. Goal: lowest token use without lowering quality on high-risk work.

## 1. Let code write code (largest saving)
- Opus builds a **code generator** once, early in Release 1.0. It reads the approved data design (one YAML file per
  entity) and produces the repetitive parts: database migration, entity, repository, API endpoints, permission
  registration, list and form screens, translations keys, basic tests.
- Generated code costs **zero model tokens**. Models only write what the generator cannot: business rules, calculations,
  workflows, special screens.
- Rule: if the same kind of code is needed three times, extend the generator instead of asking a model to write it again.

## 2. Model choice by risk class
| Class | Work | Model and effort |
| --- | --- | --- |
| A | Data design, contracts, security model, payroll and tax calculation design, generator design | Opus 5.5, high effort |
| B | Tenancy, permissions, payroll engine, statutory deductions, attendance locking, final settlement, audit, licensing | Opus 5.5, high effort; fresh-session review of the diff only |
| C | Business logic in modules once a pattern exists | Sonnet 5.5 first; escalate to Opus after two failed attempts |
| D | Screens from generated code, validations, reports, seed data, translations, routine tests, docs | Sonnet 5.5, low or medium effort |

Default to Sonnet. Opus only when the slice file says class A or B.

## 3. Batch work by model
- Do not switch models slice by slice. Plan **Opus batches** (foundation, engines, patterns) and **Sonnet batches**
  (screens, reports, tests built on those patterns).
- The product owner switches the model once per batch with `/model`. No helper agents by default: a helper starts
  with no context and re-reads files, which costs more than it saves for small slices.

## 4. One slice, one fresh session
- Every slice starts a **new session** (`/clear` or a new task). Long conversations re-send their whole history on
  every step; that is the single biggest hidden cost.
- A session loads only: `CLAUDE.md`, `docs/architecture-reference.md`, the slice file, and the files it touches.
  Never load whole specifications (`docs/specs` is about 1.1 million characters). Open only the cited section.
- End every session with a handoff note of no more than 15 lines.

## 5. Small, exact slice briefs
- Each slice file lists: requirement IDs, tables and files to touch, acceptance tests, and what is out of scope.
- A brief that answers the questions in advance prevents the model exploring the repository to find out.
- Slices are sized to finish in one session: one feature, roughly one screen or one engine part.

## 6. Keep output and checks quiet
- Run builds and tests in quiet mode and show only failures (`dotnet test --nologo -v q`, `--reporter=dot`).
  Full logs go to a file; read only the failing part.
- Run only the tests for the area changed while working; the full suite once at the end of the slice and in CI.
- Do not paste large files or logs into the conversation.

## 7. Review only where it matters
- Class B slices: one fresh Opus review of the **diff and the slice brief**, not the whole repository.
- Class C and D: automated checks only; Opus samples one slice in five at milestones.
- Automated gates (build, tests, architecture tests, isolation tests, licence scan) run in GitHub Actions on every
  push, so checking does not consume model tokens.

## 8. Stop rules
- Two failed attempts at the same problem: stop, write what was tried, and escalate (Sonnet → Opus → product owner).
- If a slice turns out larger than its brief, stop and split it rather than continuing.
- Never "improve" code outside the slice.

## 9. Use proven libraries
- Prefer an established, permissively licensed library over custom code (identity, scheduling, PDF, Excel, validation).
  Writing less code is the cheapest code.

## 10. Measure and adjust
- Record per slice: model, class, tokens or usage, first-time pass. Review after the first 20 slices.
- If Sonnet passes class C work reliably, move more work to it. If a slice type keeps failing, improve the generator
  or the brief, not the model.

## 11. Keep always-loaded files short
- `CLAUDE.md` and `docs/architecture-reference.md` are read in every session; keep them under about 1,500 words each
  and remove anything that no longer applies.
