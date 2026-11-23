# Maintenance note: browser scenarios of the settings centre no longer depend on order (2026-11-23, CR-082 lane A)

No deviation from any slice, decision or requirement; no test was weakened, skipped or removed.

**Problem.** The S1.15a scenarios in `web/apps/web/e2e/settings.spec.ts` change customer-wide values on the one shared test host.
A scenario that failed half-way left them behind: CI run 45118862313 (PR #94) left the idle time at 45 minutes, which then failed
`sign-in.spec.ts` (idle warning at the idle time). The sign-in page scenario (FR-ADM-SEC-08) read the page title once, before the
branding had loaded, and failed on PR #92 and passed on re-run.

**Change.**
- Control server: `DELETE /settings?key=` removes a customer-level setting value (`server.clearSetting` in `e2e/support.ts`).
- `settings.spec.ts`: `afterEach` clears every customer-level value the file changes (idle time, minimum password length, the six
  branding values), whether the scenario passed or failed. The time zone scenario reads the inherited value from the screen
  (never assumes one) and overrides a company of its own, so it leaves no customer-wide value.
- FR-ADM-SEC-08: the title check is `toHaveTitle` (retries until the branding has loaded) instead of a single read. Same expectation.
- `custom-fields.spec.ts`: `afterEach` removes the "Wings" label override (English and Urdu) the first scenario sets.
- `numbering-routes`, `org-chart`, `organization`: checked, no customer-wide value changed (all data carries the run suffix); no change.

**Evidence.** See the pull request: full WebE2E suite twice, in default order and with the settings and labels files last.
