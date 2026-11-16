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

The API host reads `ConnectionStrings__ControlPlane`, `ERP_SECRETS_FILE`, `ERP_SECRETS_KEY`, `Auth__Issuer`, `Auth__Audience`
and optionally `Tenancy__StatementTimeoutSeconds` (30), `Tenancy__HostCacheSeconds` (30) and `Tenancy__MaxPoolSizePerTenant` (20).
Since S1.05 the API signs and checks access tokens with the ES256 keys in the secret store (created by `M migrate`); it
refuses to start when no key exists. `Auth__SigningPublicKeys__0` may add a further trusted public key (tests only).

`M` below stands for `dotnet Erp.Host.Migrator.dll`.

## One-time set-up
1. Create an empty control-plane database, then `M migrate` (applies the control-plane scripts, closes the database to
   other logins, creates the platform admin and the module catalogue).
2. Register each PostgreSQL server: `ERP_CLUSTER_ADMIN_PASSWORD=... M cluster add --name pk-main --region pk-1 --host db1 --port 5432 --max-databases 200 --admin-user postgres`.
   The administrator login must be able to create roles and databases (a superuser in Release 1.0).
3. Register the public key that signs entitlements: `M signing-key add --key-id ent-2026-1 --public-key ent-public.pem`.
   The private key stays with the issuer, never on the platform.
4. `M migrate` also creates the first access-token signing key in the secret store (S1.05). Rotate it with
   `M auth-key rotate`, then restart the API hosts; the previous two keys keep validating tokens already issued
   (access tokens live 5 minutes, so a key can be dropped soon after a rotation).
   With more than one API host, restart them all within a few minutes: a host still on the old key list refuses tokens
   signed with the new key until it restarts (users may see a sign-in prompt in that window). Do rotations at a quiet
   time; a two-phase rotation without restarts is planned with the hosting slice (S1.05 review finding 8).

## Provision a customer (FR-KRN-TEN-02)
1. Issuer signs the entitlement (payload: `tenant`, `version`, `edition`, `effective_from`, `expires_at`, `claims.modules`, limits):
   `M entitlement sign --payload acme-payload.json --key-id ent-2026-1 --private-key ent-private.pem --out acme-entitlement.json`
2. `M tenant provision --code acme --name "Acme Textiles" --host acme.example.com --region pk-1 --country PK --currency PKR --size-profile sme --cluster pk-main --legal-name "Acme Textiles (Private) Limited" --admin-email hr.admin@acme.example --admin-name "Acme HR Admin" --entitlement acme-entitlement.json`

Steps: register → database (own logins, own database) → migrate (kernel + licensed modules) → entitlement (signature checked)
→ seed (country, currency, default company and branch, permission registry and baseline roles from the access manifests (S1.06), system user, invited first admin) → smoke_test →
activate. If a step fails the customer stays `provisioning` and the error is in `cp.platform_audit`; fix the cause and run the
same command again: it resumes and never duplicates anything. On an active customer it changes nothing.

When provisioning finishes, the command prints the first administrator's **activation link** (valid 72 hours by default,
setting `iam.activation.token_hours`; works once). It is a secret: hand it to the customer privately. E-mail delivery comes
with S1.11. To issue a new link (the old one stops working): `M tenant admin-activation-link --code acme`.
Provisioning and `M migrate` also register the identity and access settings (`cfg.settings`, keys `iam.*`, rulings Q5, Q6, Q10), the permission catalogue and the baseline roles (S1.06; `migrate` adds new permissions only to baseline roles the customer never edited), and
create the customer's field-encryption key in the secret store (`tenants/<code>/field-key-v1`). Back up the secret store:
without that key the customer's authenticator secrets cannot be read.

Customer codes are 2 to 30 lower-case letters or digits, starting with a letter (they become host, database and login names).

## Tenant admin recovery and override (S1.06-r1, CR-024)
Inside a customer, the Tenant Admin role always keeps Manage Roles and Assign Roles, cannot be archived, and the last
active tenant admin cannot lose it. When a customer is locked out anyway (or an administrator account is compromised),
a **platform Super Admin** (platform user with role `platform_admin`; set `ERP_PLATFORM_ACTOR` to their e-mail) can act.
A reason is mandatory. Every run is recorded in `cp.platform_audit` and as an `iam.platform_admin_*` event in the
customer's database.
- `M tenant admin-role-repair --code acme --reason "..."`: restores the Tenant Admin role (un-archives or recreates it)
  and adds back its mandatory and shipped permissions. It never removes the customer's additions.
- `M tenant admin-grant --code acme --email person@acme.example --reason "..."`: gives that user the Tenant Admin role
  (all companies, from today). For an invited user, then issue `tenant admin-activation-link`.
- `M tenant admin-revoke --code acme --email person@acme.example --reason "..."`: ends that user's Tenant Admin role
  today and ends their sessions, even if they are the last tenant admin (it warns; then grant the role to someone
  else). Groups that still give them the role are listed: remove them from the group as well.

When a customer's Tenant Admin role lacks a mandatory permission (only a direct database edit can cause this),
`M migrate` adds it back if the customer never edited the role (the normal baseline top-up). Otherwise it prints a
WARNING and changes nothing: run `tenant admin-role-repair`.

## Audit chain: anchor copies and checks (S1.07, CR-027, rulings P3 and P5)

Every change-log entry of a customer is linked to the one before it by a SHA-256 hash; each day's last hash is that
day's anchor (`aud.chain_anchors`). Copy the anchors of finished days to the control plane once a day (a scheduled job
comes with S1.11; until then run it by hand or from the host's scheduler):

```bash
dotnet run --project src/Host/Erp.Host.Migrator -- audit anchors-publish                 # every customer
dotnet run --project src/Host/Erp.Host.Migrator -- audit anchors-publish --tenant <code> # one customer
```

A copy is written once and never changed. Exit code 3 and a line starting `CRITICAL` mean a customer's anchor differs
from the copy taken earlier: someone with database owner rights rewrote that day. It is also a critical security event
in the customer's database (`aud.anchor_copy_mismatch`) and an entry in the platform audit. Treat it as a security
incident: keep the database as it is, take a backup for evidence, and check the PostgreSQL server log.

To check one customer's chain for a period (also available to auditors as `POST /api/aud/verify`):

```bash
dotnet run --project src/Host/Erp.Host.Migrator -- audit verify --tenant <code> --from 2026-11-01 --to 2026-11-16
```

Exit code 3 names the first broken row and the problem: `row_changed` (a row's values were changed), `link_broken` (a
row before it was removed or inserted), `anchor_mismatch`, `rows_missing` or `control_plane_mismatch`.

**Clock synchronisation is required (CR-032, R-014).** Every application and database server must keep its clock
synchronised (NTP or chrony, SaaS and on-premises alike). The audit chain never goes back in time; an entry stamped more
than 5 minutes ahead of the database clock is stored at the database time and raises a warning security event
`aud.clock_ahead`. If you see that event, fix the server's clock synchronisation.

**Watch the PostgreSQL log (R-012).** Alert on `permission denied for table` or `append-only table` errors that name an
`aud.` table: someone tried to change audit rows outside the application.

What the application can and cannot see (ruling Q9): an attempt through the application to change or delete audit rows
is refused by the database and recorded as a critical security event (`aud.tamper_attempt`). An attempt made directly in
the database by its owner is not seen by the application: it shows in the PostgreSQL server log and is found by the
chain check and the anchor copies above.

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
