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
| TypeScript (npm `typescript`) | 6.0.2 | Apache-2.0 | CI only: type-checks web/generated (`npx --yes typescript@6.0.2 tsc`); not shipped | S1.02 |

Transitive packages (S1.01, all test-only; full list printed by `build/licence-gate.py`): xunit.v3.* and xunit.analyzers
(Apache-2.0); Microsoft.Testing.Platform, .MSBuild, .Extensions.Telemetry, .Extensions.TrxReport.Abstractions, Microsoft.ApplicationInsights,
Microsoft.Bcl.AsyncInterfaces, Microsoft.Extensions.*.Abstractions, Microsoft.Win32.Registry, System.Security.AccessControl,
Testcontainers, Docker.DotNet.Enhanced.*, BouncyCastle.Cryptography, SSH.NET, SharpZipLib (all MIT). Test-platform
telemetry is switched off in CI with `TESTINGPLATFORM_TELEMETRY_OPTOUT=1`.

Product projects use only Npgsql.EntityFrameworkCore.PostgreSQL and its dependencies (S1.02) plus the shared .NET and ASP.NET Core
frameworks (MIT). The Api projects reference the ASP.NET Core shared framework for minimal APIs.
