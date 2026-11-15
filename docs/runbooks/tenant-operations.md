# Runbook: control plane, customers and migrations (S1.03)

Operator commands run from the migrator host (`Erp.Host.Migrator`). There is no web console yet (slice S1.17).
Every command is recorded in `cp.platform_audit` against the platform user named by `ERP_PLATFORM_ACTOR`.

## Settings (environment variables only; never in the repository)

| Variable | Meaning |
| --- | --- |
| `ERP_CONTROL_PLANE` | Connection string of the control-plane database |
| `ERP_SECRETS_FILE`, `ERP_SECRETS_KEY` | Encrypted secret store file and its key (base64 of 32 random bytes: `openssl rand -base64 32`). Database passwords live only here |
| `ERP_PLATFORM_ADMIN_EMAIL`, `ERP_PLATFORM_ADMIN_NAME` | First platform admin, created by `migrate` if missing |
| `ERP_PLATFORM_ACTOR` | Platform user the command runs as (defaults to the admin) |
| `ERP_CLUSTER_ADMIN_PASSWORD` | Only for `cluster add` |
| `ERP_BACKUP_KEY` | Backup encryption key (base64 of 32 bytes); see tenant-backup-restore.md |
| `ERP_MIGRATIONS_ROOT` | Optional; defaults to the migration files shipped next to the executable |

The API host reads `ConnectionStrings__ControlPlane`, `ERP_SECRETS_FILE`, `ERP_SECRETS_KEY`, `Auth__Issuer`, `Auth__Audience`,
`Auth__SigningPublicKeys__0` (PEM public key of the token issuer, S1.05) and optionally `Tenancy__StatementTimeoutSeconds` (30),
`Tenancy__HostCacheSeconds` (30) and `Tenancy__MaxPoolSizePerTenant` (20).

`M` below stands for `dotnet Erp.Host.Migrator.dll`.

## One-time set-up
1. Create an empty control-plane database, then `M migrate` (applies the control-plane scripts, closes the database to
   other logins, creates the platform admin and the module catalogue).
2. Register each PostgreSQL server: `ERP_CLUSTER_ADMIN_PASSWORD=... M cluster add --name pk-main --region pk-1 --host db1 --port 5432 --max-databases 200 --admin-user postgres`.
   The administrator login must be able to create roles and databases (a superuser in Release 1.0).
3. Register the public key that signs entitlements: `M signing-key add --key-id ent-2026-1 --public-key ent-public.pem`.
   The private key stays with the issuer, never on the platform.

## Provision a customer (FR-KRN-TEN-02)
1. Issuer signs the entitlement (payload: `tenant`, `version`, `edition`, `effective_from`, `expires_at`, `claims.modules`, limits):
   `M entitlement sign --payload acme-payload.json --key-id ent-2026-1 --private-key ent-private.pem --out acme-entitlement.json`
2. `M tenant provision --code acme --name "Acme Textiles" --host acme.example.com --region pk-1 --country PK --currency PKR --size-profile sme --cluster pk-main --legal-name "Acme Textiles (Private) Limited" --admin-email hr.admin@acme.example --admin-name "Acme HR Admin" --entitlement acme-entitlement.json`

Steps: register → database (own logins, own database) → migrate (kernel + licensed modules) → entitlement (signature checked)
→ seed (country, currency, default company and branch, five baseline roles, system user, invited first admin) → smoke_test →
activate. If a step fails the customer stays `provisioning` and the error is in `cp.platform_audit`; fix the cause and run the
same command again: it resumes and never duplicates anything. On an active customer it changes nothing.

Customer codes are 2 to 30 lower-case letters or digits, starting with a letter (they become host, database and login names).

## Migrate all customers (S1.03 build item 5)
`M migrate` (or `M migrate --tenant acme`). Customers are migrated one at a time. A customer whose migration fails is put in
`maintenance` (its API answers 503 `errors.tenant.unavailable`) and the others continue; exit code 3 means at least one
failed. Fix and run again: customers in maintenance are retried. Every applied file is recorded with its checksum in the
customer's `meta.schema_history`; a file changed after it was applied is refused.

## Suspend and resume (FR-KRN-TEN-09, FR-ADM-PLT-11)
`M tenant suspend --code acme --reason "..."` and `M tenant resume --code acme --reason "..."`. Nothing is deleted. A suspended
customer gets 403 `errors.tenant.locked`. API hosts apply the change within `Tenancy__HostCacheSeconds`.

## Customer list (FR-ADM-PLT-01)
`M tenant list`: code, status, database status, region, cluster, edition, entitlement state, modules, schema version, legal
hold, last backup and last restore test.
