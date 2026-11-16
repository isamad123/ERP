# Contract register

| Contract or event | Owner module | Version | Consumers | Status |
| --- | --- | --- | --- | --- |
| `Erp.Kernel.Contracts.Access.IWorkforceDirectory` (employments, persons, org units of a user; direct reports; employments in org units, branches, companies; persons of employments, as of a date) | Kernel (defines), HRM (implements) | 1 (S1.06) | Kernel access engine (data scopes) | Published |
| `iam.*` access-control events in `iam.outbox_messages` (`iam.role_created`, `iam.role_renamed`, `iam.role_archived`, `iam.role_permissions_changed`, `iam.role_data_scopes_changed`, `iam.role_field_policies_changed`, `iam.role_assigned`, `iam.role_assignment_ended`, `iam.user_group_*`, `iam.maker_checker_requested/approved/rejected/expired`); actor in `headers.actor` | Kernel | 1 (S1.06) | Audit log (S1.07) | Published |
| Module access manifest: `<module>.Infrastructure/Access/permissions.g.json` (generated) and `baseline-roles.json` | Each module | 1 (S1.06) | Migrator `migrate` and provisioning (`AccessSetup`) | Published |
| `iam.platform_admin_role_repaired`, `iam.platform_admin_granted`, `iam.platform_admin_revoked` (payload: `platform_user` {id, email, role}, `reason`, `changed`, `details`; `headers.actor` = system user) | Kernel | 1 (S1.06-r1, CR-024) | Audit log (S1.07) | Published |
