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

## Approved for slice S1.12 (product owner 2026-11-20, S1.12 rulings Q1 and Q20; added by the build parts)
Licences checked on the npm registry on 2026-11-20. Versions are fixed at build time (newest release at least two weeks
old) and this section then moves into the table above.

| Package | Licence | Use | Shipped to browsers |
| --- | --- | --- | --- |
| react, react-dom | MIT | Web framework | Yes |
| react-router | MIT | Screens and addresses | Yes |
| @tanstack/react-query | MIT | Server data and caching | Yes |
| react-aria-components, @internationalized/date | Apache-2.0 | Accessible primitives, date fields | Yes |
| @tanstack/react-table, @tanstack/react-virtual | MIT | Grid and long lists | Yes |
| react-hook-form, zod, @hookform/resolvers | MIT | Forms and validation | Yes |
| react-intl | BSD-3-Clause | ICU message catalogues | Yes |
| lucide-react | ISC | Icons | Yes |
| Noto Sans, Noto Naskh Arabic (font files) | SIL OFL 1.1 (fonts only, CR-045) | Screen fonts | Yes |
| vite, @vitejs/plugin-react, typescript, pnpm | MIT; TypeScript Apache-2.0 | Build | No |
| vitest, @testing-library/react, @testing-library/user-event, jsdom | MIT | Unit and component tests | No |
| @playwright/test (Playwright already on the build machine) | Apache-2.0 | End-to-end tests and screenshots | No |
| eslint, typescript-eslint, eslint-plugin-jsx-a11y, eslint-plugin-react-hooks, stylelint | MIT | Lint | No |
| axe-core, @axe-core/playwright | MPL-2.0 (development only, S1.12 Q20) | Accessibility checks in tests | No |
