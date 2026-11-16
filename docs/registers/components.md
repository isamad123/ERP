# Component register

| Component or pattern | Location | Owner class (A-D) | First built in slice | Notes |
| --- | --- | --- | --- | --- |
| Access engine (permissions, data scopes, field security; one engine for every caller) | `src/Platform/Erp.Kernel.Infrastructure/Access/AccessEngine.cs` | B | S1.06 | Reads the user's roles fresh per request (CR-022 D) |
| Access administration (roles, assignments, groups, maker-checker, previews) | `src/Platform/Erp.Kernel.Infrastructure/Access/AccessAdministrationService*.cs`, endpoints `src/Platform/Erp.Kernel.Api/Access/AccessEndpoints.cs` | B | S1.06 | Replaces the generated writes closed by CR-020, CR-021 |
| Permission registry and baseline roles (module access manifests) | `src/Platform/Erp.Kernel.Infrastructure/Access/AccessSetup.cs`, `*/Access/baseline-roles.json` | B | S1.06 | Run by provisioning and `migrate`; idempotent |
| Field shaping and scope targets for generated code | `src/BuildingBlocks/Erp.BuildingBlocks.Application/Security/FieldSecurity.cs`, `AccessAbstractions.cs` | B | S1.06 | Emitted per table by the generator (`<Entity>Access`) |
