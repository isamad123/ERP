# Dependency register

Only permissive licences (MIT, Apache-2.0, BSD, ISC, PostgreSQL, Zlib). Anything else needs a decision.

| Package | Version | Licence | Used by | Added in slice |
| --- | --- | --- | --- | --- |
| xunit.v3 | 4.0.3 | Apache-2.0 | all test projects (test framework) | S1.01 |
| NetArchTest.Rules | 1.3.2 | MIT (no licence in package metadata; verified from repository LICENSE, recorded as gate override) | Erp.ArchitectureTests | S1.01 |
| Mono.Cecil | 0.11.6 | MIT | Erp.ArchitectureTests (IL scan for clock calls and tenant_id members) | S1.01 |
| Npgsql | 10.0.3 | PostgreSQL | Erp.IntegrationTests; tools/Erp.CodeGen (`dictionary` command reads the live database; S1.02, recorded by CR-052) | S1.01 |
| Testcontainers.PostgreSql | 4.16.0 | MIT | Erp.IntegrationTests (used only when ERP_TEST_PG is unset) | S1.01 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL | Erp.BuildingBlocks.Infrastructure (EF Core mapping of generated entities); brings Microsoft.EntityFrameworkCore, .Relational, .Abstractions, .Analyzers 10.0.4 (MIT) and Microsoft.Extensions.* 10.0.4 (MIT) | S1.02 |
| YamlDotNet | 16.3.0 | MIT | tools/Erp.CodeGen (reads docs/data-design/*.yaml) | S1.02 |
| Microsoft.AspNetCore.TestHost | 10.0.4 | MIT | Erp.IntegrationTests (in-process API tests of the generated endpoints) | S1.02 |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.4 | MIT | Erp.Host.Api (validates signed tokens; the token's customer must match the host, FR-KRN-TEN-04); brings Microsoft.IdentityModel.* and System.IdentityModel.Tokens.Jwt 8.0.1 (MIT) | S1.03 |
| NSec.Cryptography | 26.4.0 | MIT | Erp.Kernel.Infrastructure (Argon2id password and recovery-code hashing, S1.05 build item 1); brings libsodium 1.0.22 (ISC, native library) | S1.05 |
| Microsoft.IdentityModel.JsonWebTokens | 8.0.1 | MIT | Erp.Kernel.Infrastructure (issues ES256 access tokens, S1.05); same version JwtBearer already brought in S1.03 | S1.05 |
| MailKit | 4.18.2 | MIT | Erp.Kernel.Infrastructure (SMTP adapter of e-mail notifications and IMAP reading of the bounce mailbox, S1.11b2, ruling Q10); brings MimeKit 4.18.2 (MIT), BouncyCastle.Cryptography 2.7.0 (MIT) and System.Security.Cryptography.Pkcs 10.0.13 (MIT) | S1.11b2 |
| AWSSDK.S3 | 4.1.0 | Apache-2.0 | Erp.Kernel.Infrastructure (S3-compatible file store, S1.11c1, ruling Q20); brings AWSSDK.Core (Apache-2.0) | S1.11c1 |
| Sylvan.Data.Csv | 1.4.4 | MIT | Erp.Kernel.Infrastructure (streaming CSV read and write for imports and exports, S1.16a, ruling Q3) | S1.16a |
| Sylvan.Data.Excel | 0.5.8 | MIT | Erp.Kernel.Infrastructure (streaming .xlsx read and write for imports and exports, S1.16a, ruling Q3) | S1.16a |
| Scriban | 7.6.1 | BSD-2-Clause | Erp.Kernel.Infrastructure (sandboxed document templates, S1.11c2, KRN-DOC-05); no dependencies | S1.11c2 |
| PuppeteerSharp | 25.18.1 | MIT | Erp.Kernel.Infrastructure (drives the server's headless Chromium to print HTML to PDF, S1.11c2, ruling Q17); brings Microsoft.Bcl.AsyncInterfaces and Microsoft.Extensions.Logging.Abstractions (MIT) | S1.11c2 |
| QRCoder | 1.8.1 | MIT | Erp.Kernel.Infrastructure (QR codes in documents as SVG, S1.11c2); no dependencies | S1.11c2 |
| Noto Sans, Noto Nastaliq Urdu (font files) | 2017-2022 releases | SIL OFL 1.1 | Embedded in Erp.Kernel.Infrastructure for PDF output (S1.11c2, ruling Q18, CR-045: OFL allowed for fonts only); licence text beside the files | S1.11c2 |
| Chromium (server tool, not a package) | any current stable | BSD-3-Clause with bundled open licences | Installed on API and worker servers like pg_dump; not built into the product (S1.11c2, ruling Q17; `docs/runbooks/pdf-rendering.md`) | S1.11c2 |
| TypeScript (npm `typescript`) | 6.0.2 | Apache-2.0 | CI only: type-checks web/generated (`npx --yes typescript@6.0.2 tsc`); not shipped | S1.02 |
| gitleaks | 8.28.0 | MIT | CI only: secret scan (`.github/workflows/ci.yml`, config `.gitleaks.toml`); downloaded as a release binary and verified by sha256; not shipped | S1.11f1 |

Transitive packages (S1.01, all test-only; full list printed by `build/licence-gate.py`): xunit.v3.* and xunit.analyzers
(Apache-2.0); Microsoft.Testing.Platform, .MSBuild, .Extensions.Telemetry, .Extensions.TrxReport.Abstractions, Microsoft.ApplicationInsights,
Microsoft.Bcl.AsyncInterfaces, Microsoft.Extensions.*.Abstractions, Microsoft.Win32.Registry, System.Security.AccessControl,
Testcontainers, Docker.DotNet.Enhanced.*, BouncyCastle.Cryptography, SSH.NET, SharpZipLib (all MIT). Test-platform
telemetry is switched off in CI with `TESTINGPLATFORM_TELEMETRY_OPTOUT=1`.

Product projects use only Npgsql.EntityFrameworkCore.PostgreSQL and its dependencies (S1.02), Microsoft.AspNetCore.Authentication.JwtBearer
and its dependencies (S1.03), NSec.Cryptography and Microsoft.IdentityModel.JsonWebTokens (S1.05), MailKit (S1.11b2), AWSSDK.S3 (S1.11c1), Scriban, PuppeteerSharp and QRCoder (S1.11c2), plus the shared .NET and ASP.NET Core frameworks (MIT). Encryption and signatures (AES-GCM, ECDSA P-256) use
the .NET base library only. Backups call the PostgreSQL client tools pg_dump and pg_restore (PostgreSQL licence) installed on the server. The Api projects reference the ASP.NET Core shared framework for minimal APIs.

## Web packages (slice S1.12a; product owner 2026-11-20, rulings Q1 and Q20, CR-064)
Pinned exactly in `web/package.json` files and `web/pnpm-lock.yaml`; versions are the newest release at least 14 days old
on 2026-11-20 unless the note says otherwise. The npm licence gate (`python3 build/licence-gate.py --npm web`) checks every
package, transitive ones included: packages shipped to browsers against the D-09 list, development-only packages against
the D-09 list, each with only the exceptions named below.

| Package | Version | Licence | Use | Shipped to browsers |
| --- | --- | --- | --- | --- |
| react, react-dom | 19.3.1 | MIT | Web framework | Yes |
| react-router | 8.5.1 | MIT | Screens and addresses | Yes |
| @tanstack/react-query (with @tanstack/query-core) | 5.107.1 (newest at least two weeks old on 2026-11-21; approved in S1.12 Q1) | MIT | Server data and caching of the web shell (S1.12b): session context, capabilities, bell, notification centre | Yes |
| @tanstack/react-virtual (with @tanstack/virtual-core 3.19.11) | 3.14.26 (newest at least two weeks old on 2026-11-22; approved in S1.12 Q1) | MIT | Row virtualisation of the grid (S1.12c, `web/packages/engine`): only the rows in view are drawn from 100 rows on | Yes |
| react-aria-components | 1.22.1 | Apache-2.0 | Accessible primitives (brings react-aria, react-stately, @internationalized/* , Apache-2.0) | Yes |
| @internationalized/date | 3.12.4 | Apache-2.0 | Date fields | Yes |
| react-intl | 12.1.6 | BSD-3-Clause | ICU message catalogues (brings @formatjs/*, MIT, and intl-messageformat, BSD-3-Clause) | Yes |
| lucide-react | 1.70.0 | ISC | Icons | Yes |
| uqr | 0.1.3 | MIT | QR code of authenticator enrolment (approved 2026-11-20) | Yes |
| @fontsource/noto-sans, @fontsource/noto-naskh-arabic | 5.3.0 | OFL-1.1 (fonts only, CR-045; named in the gate) | Screen fonts, WOFF2 split by script (approved 2026-11-20) | Yes |
| tslib | 2.9.1 (transitive, React Aria) | 0BSD (CR-064, named in the gate) | Helper of React Aria | Yes |
| vite | 8.3.6 | MIT | Build (brings lightningcss, MPL-2.0, named in the gate) | No |
| @vitejs/plugin-react | 6.2.1 | MIT | Build | No |
| typescript | 6.0.3 (not 7.0.2: typescript-eslint supports up to 6.0; approved 2026-11-20) | Apache-2.0 | Type check | No |
| pnpm | 10.34.9 | MIT | Package manager (`packageManager` field, through corepack) | No |
| vitest | 5.1.0 | MIT | Unit and component tests | No |
| jsdom | 31.0.0 | MIT | Test DOM | No |
| @testing-library/react, @testing-library/dom, @testing-library/user-event | 16.3.3, 10.4.2, 14.6.8 | MIT | Component tests (@testing-library/dom approved 2026-11-20) | No |
| @playwright/test | 1.63.0 (not 1.65.0: must match the browser on the build machine; approved 2026-11-20) | Apache-2.0 | Browser end-to-end tests and screenshots | No |
| eslint, @eslint/js | 9.39.5 (not 10: eslint-plugin-jsx-a11y supports up to 9; approved 2026-11-20) | MIT | Lint (brings argparse, Python-2.0 / PSF-2.0, named in the gate, CR-064) | No |
| typescript-eslint | 8.72.1 | MIT | Lint | No |
| eslint-plugin-jsx-a11y | 6.10.2 | MIT | Accessibility lint | No |
| eslint-plugin-react-hooks | 7.1.1 | MIT | Lint | No |
| globals | 17.14.0 | MIT | Lint configuration (approved 2026-11-20) | No |
| stylelint | 18.0.0 | MIT | Style lint (brings @csstools/* MIT-0, lru-cache and minimatch BlueOak-1.0.0, mdn-data CC0-1.0, named in the gate, CR-064) | No |
| @types/react, @types/react-dom | 19.3.0 | MIT | Type definitions (approved 2026-11-20) | No |
| @types/node | 22.20.5 | MIT | Type definitions (approved 2026-11-20) | No |
| axe-core, @axe-core/playwright | 4.14.0 | MPL-2.0 (development only, S1.12 Q20; named in the gate) | Accessibility checks in browser tests | No |
| caniuse-lite (transitive) | lockfile | CC-BY-4.0 (data, development only, Q20; named in the gate) | Browser support data of build tools | No |
| language-subtag-registry (transitive) | lockfile | CC0-1.0 (data, development only, Q20; named in the gate) | Data of the accessibility lint | No |

Approved but not used yet (added with their part): @tanstack/react-table,
@tanstack/react-virtual, react-hook-form, zod, @hookform/resolvers (S1.12c); all MIT.

S1.12c note (2026-11-22, deviation r of CR-063): of the packages approved in ruling Q1, `@tanstack/react-table`, `react-hook-form`,
`zod` and `@hookform/resolvers` are not added. Sorting and filtering are done by the server, so the grid keeps its own
column state (order, width, shown) in about a hundred lines; the newest TanStack Table at least two weeks old is a new
major version (9). The form engine checks values straight from the generated validation schema
(`web/generated/validation/<table>.json`), so there is no second schema language to keep in step. Either can still be
added later under the Q1 approval.

