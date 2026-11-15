# Risks, overrides and change requests

| ID | Date | Type (risk, override, deviation, change request) | Description | Raised by | Status | Decision |
| --- | --- | --- | --- | --- | --- | --- |
| R-001 | 2026-11-15 | Risk | No human security reviewer engaged yet; required before first customer go-live (D-46) | Baseline 1.1 | Open | |
| R-002 | 2026-11-15 | Risk | Pakistan payroll and tax rules not yet written or validated (gap G-05); blocks payroll slices | Baseline 1.1 | Open | |
| CR-001 | 2026-11-15 | Change request | Spec 01 section 13 names the HR module folder `Modules/Hrms`; slice S1.01, schema `hrm` and the data design use `Hrm` | S1.01 | Closed | Product owner (2026-11-15): HRM, HRMS and HRMIS are the same module. The folder is `Hrm`; wherever documentation says `Hrms`, read it as `Hrm`. No specification edit needed |
| CR-002 | 2026-11-15 | Deviation (recorded) | The D-47 `tenant_id` guard rail exempts only control-plane code (namespace or folder `ControlPlane`, schema `cp`), because the approved control-plane design (`docs/data-design/control-plane.yaml`) uses `tenant_id`. S1.03 must place control-plane code and SQL in a `ControlPlane` folder and namespace; tenant-resolution types elsewhere must not use a member named TenantId | S1.01 | Open | Product owner: exempt the control plane only |
