# Dependency register

Only permissive licences (MIT, Apache-2.0, BSD, ISC, PostgreSQL, Zlib). Anything else needs a decision.

| Package | Version | Licence | Used by | Added in slice |
| --- | --- | --- | --- | --- |
| xunit.v3 | 4.0.3 | Apache-2.0 | all test projects (test framework) | S1.01 |
| NetArchTest.Rules | 1.3.2 | MIT (no licence in package metadata; verified from repository LICENSE, recorded as gate override) | Erp.ArchitectureTests | S1.01 |
| Mono.Cecil | 0.11.6 | MIT | Erp.ArchitectureTests (IL scan for clock calls and tenant_id members) | S1.01 |
| Npgsql | 10.0.3 | PostgreSQL | Erp.IntegrationTests | S1.01 |
| Testcontainers.PostgreSql | 4.16.0 | MIT | Erp.IntegrationTests (used only when ERP_TEST_PG is unset) | S1.01 |

Transitive packages (S1.01, all test-only; full list printed by `build/licence-gate.py`): xunit.v3.* and xunit.analyzers
(Apache-2.0); Microsoft.Testing.Platform, .MSBuild, .Extensions.Telemetry, .Extensions.TrxReport.Abstractions, Microsoft.ApplicationInsights,
Microsoft.Bcl.AsyncInterfaces, Microsoft.Extensions.*.Abstractions, Microsoft.Win32.Registry, System.Security.AccessControl,
Testcontainers, Docker.DotNet.Enhanced.*, BouncyCastle.Cryptography, SSH.NET, SharpZipLib (all MIT). Test-platform
telemetry is switched off in CI with `TESTINGPLATFORM_TELEMETRY_OPTOUT=1`.

Product (non-test) projects use no NuGet packages yet; hosts use only the shared .NET and ASP.NET Core frameworks (MIT).
