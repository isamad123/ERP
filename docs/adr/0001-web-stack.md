# 0001 Web stack and browser sign-in

Status: accepted (product owner 2026-11-20, slice S1.12 rulings Q1 to Q3, Q7, Q19, Q20; CR-063, CR-064). Implemented in S1.12a.

## Context
Spec 13 section 24 asks for React with TypeScript, a query library, accessible open-source primitives, a grid wrapped
by the product's own API, ICU messages, bundled fonts and icons, and decisions recorded as ADRs. D-09 allows permissive
licences only. The web application is the first client of the sign-in API, whose refresh token was readable by scripts.

## Decision
- **Workspace:** pnpm workspace `web/` with `apps/web`, `packages/ui` (design system), `packages/i18n` and the generated
  client; Vite builds, strict TypeScript, ESLint and Stylelint enforce spec 13 section 24 (no literal text, logical
  properties, token values only, accessibility rules).
- **Accessible primitives:** React Aria Components (Apache-2.0), chosen over Radix UI for its built-in right-to-left,
  date and internationalisation support. Grid (S1.12c): TanStack Table behind the product's grid API, chosen over AG Grid
  Community because the features needed are not in a paid edition and the look stays the product's own.
- **Messages:** react-intl (ICU), English and Urdu; server texts are collected at build time and looked up as plain text.
- **Fonts:** Noto Sans and Noto Naskh Arabic from the Fontsource packages (OFL-1.1, WOFF2 split by script).
- **Hosting:** the API host serves the build at the customer's own address (`Web:Root`), so the customer is resolved
  from the host name as before and cookies stay on that address. A strict content security policy allows only the
  host's own scripts, styles, fonts and connections.
- **Browser session:** Identity API version 3. Browsers (header `X-Erp-Client: web`) get the refresh token only in an
  HttpOnly, Secure, SameSite=Strict cookie for `/api/iam/auth`; the access token lives in memory only. One tab refreshes at
  a time (Web Lock) and shares the token (BroadcastChannel; tokens ordered by the server's expiry, times passed as ages);
  refresh happens only after activity, so the server's idle timeout still ends forgotten sessions.
- **Checks:** the token contrast gate, the npm licence gate with named exceptions, size budgets, and Playwright browser
  tests with axe-core against the real API host and PostgreSQL in the CI job `web`.

## Consequences
- Later screen slices add only their routes, menus and screens; they use the design system and catalogues.
- React Aria's own inline styles must stay compatible with the content security policy (R-066).
- The `web` CI job should become a required check of `main` (R-067).
