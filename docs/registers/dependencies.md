# Dependency register

Only permissive licences (MIT, Apache-2.0, BSD, ISC, PostgreSQL, Zlib). Anything else needs a decision.

| Package | Version | Licence | Used by | Added in slice |
| --- | --- | --- | --- | --- |
| xunit.v3 | 4.0.3 | Apache-2.0 | all test projects (test framework) | S1.01 |
| NetArchTest.Rules | 1.3.2 | MIT (no licence in package metadata; verified from repository LICENSE, recorded as gate override) | Erp.ArchitectureTests | S1.01 |
| Mono.Cecil | 0.11.6 | MIT | Erp.ArchitectureTests (IL scan for clock calls and tenant_id members) | S1.01 |
| Npgsql | 10.0.3 | PostgreSQL | Erp.IntegrationTests | S1.01 |
| Testcontainers.PostgreSql | 4.16.0 | MIT | Erp.IntegrationTests (used only when ERP_TEST_PG is unset) | S1.01 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL | Erp.BuildingBlocks.Infrastructure (EF Core mapping of generated entities); brings Microsoft.EntityFrameworkCore, .Relational, .Abstractions, .Analyzers 10.0.4 (MIT) and Microsoft.Extensions.* 10.0.4 (MIT) | S1.02 |
| YamlDotNet | 16.3.0 | MIT | tools/Erp.CodeGen (reads docs/data-design/*.yaml) | S1.02 |
| Microsoft.AspNetCore.TestHost | 10.0.4 | MIT | Erp.IntegrationTests (in-process API tests of the generated endpoints) | S1.02 |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.4 | MIT | Erp.Host.Api (validates signed tokens; the token's customer must match the host, FR-KRN-TEN-04); brings Microsoft.IdentityModel.* and System.IdentityModel.Tokens.Jwt 8.0.1 (MIT) | S1.03 |
| NSec.Cryptography | 26.4.0 | MIT | Erp.Kernel.Infrastructure (Argon2id password and recovery-code hashing, S1.05 build item 1); brings libsodium 1.0.22 (ISC, native library) | S1.05 |
| Microsoft.IdentityModel.JsonWebTokens | 8.0.1 | MIT | Erp.Kernel.Infrastructure (issues ES256 access tokens, S1.05); same version JwtBearer already brought in S1.03 | S1.05 |
| MailKit | 4.18.2 | MIT | Erp.Kernel.Infrastructure (SMTP adapter of e-mail notifications and IMAP reading of the bounce mailbox, S1.11b2, ruling Q10); brings MimeKit 4.18.2 (MIT), BouncyCastle.Cryptography 2.7.0 (MIT) and System.Security.Cryptography.Pkcs 10.0.13 (MIT) | S1.11b2 |
| TypeScript (npm `typescript`) | 6.0.2 | Apache-2.0 | CI only: type-checks web/generated (`npx --yes typescript@6.0.2 tsc`); not shipped | S1.02 |

Transitive packages (S1.01, all test-only; full list printed by `build/licence-gate.py`): xunit.v3.* and xunit.analyzers
(Apache-2.0); Microsoft.Testing.Platform, .MSBuild, .Extensions.Telemetry, .Extensions.TrxReport.Abstractions, Microsoft.ApplicationInsights,
Microsoft.Bcl.AsyncInterfaces, Microsoft.Extensions.*.Abstractions, Microsoft.Win32.Registry, System.Security.AccessControl,
Testcontainers, Docker.DotNet.Enhanced.*, BouncyCastle.Cryptography, SSH.NET, SharpZipLib (all MIT). Test-platform
telemetry is switched off in CI with `TESTINGPLATFORM_TELEMETRY_OPTOUT=1`.

Product projects use only Npgsql.EntityFrameworkCore.PostgreSQL and its dependencies (S1.02), Microsoft.AspNetCore.Authentication.JwtBearer
and its dependencies (S1.03), plus the shared .NET and ASP.NET Core frameworks (MIT). Encryption and signatures (AES-GCM, ECDSA P-256) use
the .NET base library only. Backups call the PostgreSQL client tools pg_dump and pg_restore (PostgreSQL licence) installed on the server. The Api projects reference the ASP.NET Core shared framework for minimal APIs.
