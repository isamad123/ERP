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
| `ERP_TEMPLATES_ROOT` | Optional; folder of the edition templates for `entitlement draft` (defaults to `EntitlementTemplates` next to the executable) |

The API host reads `ConnectionStrings__ControlPlane`, `ERP_SECRETS_FILE`, `ERP_SECRETS_KEY`, `Auth__Issuer`, `Auth__Audience`
and optionally `Tenancy__StatementTimeoutSeconds` (30), `Tenancy__HostCacheSeconds` (30) and `Tenancy__MaxPoolSizePerTenant` (20).
Every `Tenancy__HostCacheSeconds` the API reloads the list of registered host names; a request for any other host is answered
404 without a database query (S1.03 review finding 3). A host added by provisioning is served within that time.

**Control-plane login of the API (S1.03 review finding 11).** Do not give the API the migrator's login. `M migrate` creates
the group role `cp_api` (no login). It may connect to the control-plane database and read `cp.tenants`,
`cp.tenant_domains`, `cp.tenant_databases`, `cp.db_clusters`, `cp.entitlements`, `cp.tenant_module_versions` and
`cp.usage_meters`, and nothing else. Create the API's login once, as a server administrator, and use it in
`ConnectionStrings__ControlPlane`:
`create role erp_cp_api login password '<from the vault>' in role cp_api;`
If the migrator's login cannot create roles, `migrate` skips the role. Then create it as an administrator:
`create role cp_api nologin;` and run `M migrate` again, which grants the rights.
Since S1.05 the API signs and checks access tokens with the ES256 keys in the secret store (created by `M migrate`); it
refuses to start when no key exists. `Auth__SigningPublicKeys__0` may add a further trusted public key (tests only).

`M` below stands for `dotnet Erp.Host.Migrator.dll`.

## One-time set-up
1. Create an empty control-plane database, then `M migrate` (applies the control-plane scripts, closes the database to
   other logins, creates the platform admin and the module catalogue). Since S1.11f2 each applied script is recorded with
   its checksum in `cp.schema_history`; a later run applies only new scripts and stops if a recorded one was changed. A
   control plane set up before S1.11f2 records all its scripts on its next `M migrate` (no action needed).
2. Register each PostgreSQL server: `ERP_CLUSTER_ADMIN_PASSWORD=... M cluster add --name pk-main --region pk-1 --host db1 --port 5432 --max-databases 200 --admin-user postgres`.
   The administrator login must be able to create roles and databases (a superuser in Release 1.0).
   Running `cluster add` again with the same name updates the credential or capacity. It refuses to change the host or port
   of a cluster that holds customer databases, because that moves all of them. After they have really been moved, repeat
   with `--change-address yes`, then restart the API hosts (S1.03 review finding 6).
3. Register the public key that signs entitlements: `M signing-key add --key-id ent-2026-1 --public-key ent-public.pem`.
   The private key stays with the issuer, never on the platform.
4. `M migrate` also creates the first access-token signing key in the secret store (S1.05). Rotate it with
   `M auth-key rotate`, then restart the API hosts; the previous two keys keep validating tokens already issued
   (access tokens live 5 minutes, so a key can be dropped soon after a rotation).
   With more than one API host, restart them all within a few minutes: a host still on the old key list refuses tokens
   signed with the new key until it restarts (users may see a sign-in prompt in that window). Do rotations at a quiet
   time; a two-phase rotation without restarts is planned with the hosting slice (S1.05 review finding 8).

## HTTPS is required for browser sign-in (S1.12a review finding 6, R-070)
Serve every customer host over HTTPS, with a certificate the browsers trust. Reasons:
- The browser sign-in keeps its refresh token in a `Secure` cookie (`__Secure-erp_rt`, and `__Secure-erp_td` for a remembered browser).
  A browser drops a `Secure` cookie that arrives over plain HTTP (only `localhost` is exempt), so over plain HTTP a person can sign in
  but is signed out again after 5 minutes, when the first access token runs out.
- The host sends `Strict-Transport-Security` only on a request it sees as HTTPS. The host does not yet read forwarded headers, so when a
  proxy ends TLS in front of it the host sees plain HTTP and sends no such header. Until a hosting setting for trusted proxies exists
  (open question to the product owner, to be decided with the first on-premises installation work), the customer's proxy must add
  `Strict-Transport-Security: max-age=31536000` itself, and must pass the original host name through (the customer is found from it).

## Provision a customer (FR-KRN-TEN-02)
1. Issuer signs the entitlement (payload: `tenant`, `version`, `edition`, `effective_from`, `expires_at`, `claims`; the claims
   format is in `docs/registers/contracts.md`, S1.09b; `M entitlement draft` writes a payload to start from):
   `M entitlement sign --payload acme-payload.json --key-id ent-2026-1 --private-key ent-private.pem --out acme-entitlement.json`
2. `M tenant provision --code acme --name "Acme Textiles" --host acme.example.com --region pk-1 --country PK --currency PKR --size-profile sme --cluster pk-main --legal-name "Acme Textiles (Private) Limited" --admin-email hr.admin@acme.example --admin-name "Acme HR Admin" --entitlement acme-entitlement.json`

Steps: register → database (own logins, own database) → migrate (kernel + licensed modules) → entitlement (signature checked)
→ seed (country, currency, default company and branch, permission registry and baseline roles from the access manifests (S1.06), system user, invited first admin) → smoke_test →
activate. If a step fails the customer stays `provisioning` and the error is in `cp.platform_audit`; fix the cause and run the
same command again: it resumes and never duplicates anything. On an active customer it changes nothing.
Two runs for the same customer never work at once (S1.11f2, R-031): the second prints "another provisioning run of
<code> is in progress; waiting" and waits. When the first ends, the second stops (customer active) or resumes (first
run failed). Ctrl+C stops the wait without changing anything. Different customers never wait for each other. The lock is held by
one control-plane session for the whole run, so the migrator's `ERP_CONTROL_PLANE` connection must reach PostgreSQL
directly or through session pooling, never through transaction pooling (PgBouncer `pool_mode = transaction`). If that
session is ended during a run (server restart, a killed session), the run stops before its next step with "lost its
lock"; a step that was already running when the session ended still finishes first, so wait until that run has
stopped, then run the same command again to resume.
Resume with the same `--cluster`; another cluster is refused. A database of the customer's name that provisioning did
not create (another owner) stops the run (S1.03 review finding 12). Database passwords are sent to the server only as
SCRAM verifiers, so they never appear in a server log (finding 7). Host names must be DNS names; IP addresses are refused.

When provisioning finishes, the command prints the first administrator's **activation link** (valid 72 hours by default,
setting `iam.activation.token_hours`; works once). It is a secret: hand it to the customer privately. E-mail delivery comes
with S1.11. To issue a new link (the old one stops working): `M tenant admin-activation-link --code acme`.
Provisioning and `M migrate` also register the identity and access settings (`cfg.settings`, keys `iam.*`, rulings Q5, Q6, Q10), the permission catalogue and the baseline roles (S1.06; `migrate` adds new permissions only to baseline roles the customer never edited), and
create the customer's field-encryption key in the secret store (`tenants/<code>/field-key-v1`). Back up the secret store:
without that key the customer's authenticator secrets cannot be read.
Since S1.08 they also register the numbering settings (`cfg.numbering.fiscal_year_start_month`, `org.time_zone`) and give
every company a default numbering scheme for every registered document type it has none of (person numbers once per
customer). A company added later gets its defaults at the next `migrate`, or on its first document. A scheme an
administrator retired is never re-created.

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
comes with S1.11; until then run it by hand or from the host's scheduler). A day counts as finished once the customer's
chain has an entry on a later day, or a full day after it ended (UTC), whichever comes first: until then an entry stamped
just before midnight can still land on it (review S1.07 finding 2). A day not yet finished is simply copied on a later run.

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
`M migrate` (or `M migrate --tenant acme`). First it checks that this release can still read every stored current and
scheduled entitlement (S1.09b review, CR-057): if one fails the new key catalogue, it stops before changing any customer
and names each customer, version and reason (exit code 2). Do not deploy the API hosts of that release; fix the catalogue
or issue new entitlements first, because a host that cannot read an entitlement serves that customer as Locked.
Customers are migrated one at a time. A customer whose migration fails is put in
`maintenance` (its API answers 503 `errors.tenant.unavailable`) and the others continue; exit code 3 means at least one
failed. Fix and run again: customers in maintenance are retried. Every applied file is recorded with its checksum in the
customer's `meta.schema_history`; a file changed after it was applied is refused.

## Suspend and resume (FR-KRN-TEN-09, FR-ADM-PLT-11)
`M tenant suspend --code acme --reason "..."` and `M tenant resume --code acme --reason "..."`. Nothing is deleted. Since S1.09b
a suspended customer is served exactly as a locked licence: users can sign in, see the licence page and run the audit export;
everything else gets 403 `errors.tenant.locked`. API hosts apply the change within `Tenancy__HostCacheSeconds`.

## Entitlements and licence states (S1.09b; FR-ADM-PLT-03, FR-KRN-ENT-04, FR-KRN-ENT-06)
1. Draft the next version from an edition template (every template value is a placeholder until the product owner sets it):
   `M entitlement draft --edition business --tenant acme --from 2027-01-01 --to 2027-12-31 --out acme-v2.json`. Review every value.
2. The issuer signs it off-line: `M entitlement sign --payload acme-v2.json --key-id ent-2026-1 --private-key ent-private.pem --out acme-v2-signed.json`.
   The person who signs is not the operator who applies (ruling Q3).
3. Apply: `M entitlement apply --tenant acme --file acme-v2-signed.json`. Refused with the reason when the signing key is unknown,
   revoked or not valid, the signature does not match (file changed), the customer code differs, the version is not higher than
   the last one, the claims break the key catalogue, or the end date has passed. Newly licensed modules get their schema first.
   A start date of today or earlier (the customer's day) puts it in force at once, without downtime (API hosts within
   `Tenancy__HostCacheSeconds`); a future start date stores it as `scheduled` and it is in force from that day. A module no
   longer licensed keeps its schema and data; its endpoints answer 404 until it is licensed again.
4. History: `M entitlement history --tenant acme` (versions, statuses, dates, who applied).

States follow the end date (`expires_at` is the last licensed day) on the customer's day (`org.time_zone`): Active, Warning
(last 30 days), Grace (14 days after), Read-only (30 days), Locked; the periods can be set per contract in `claims.term`.
Read-only: sign-in, reading and export only. Locked: sign-in, licence page, capabilities and the audit export only. No state
deletes or hides data. The worker host's hourly job `kernel.licence_refresh` (S1.11a; see "Background jobs") records each change once (`cp.license_events`, the customer's security
events, outbox event `kernel.licence.state_changed` for the e-mails of S1.11), activates scheduled entitlements, takes the daily
usage readings and copies limit events. To run it at once: `M licence refresh [--tenant acme]` (exit code 3 if a customer failed).

Usage statement of a month: `M usage statement --tenant acme --month 202611` (each meter, its limit and percentage; the monthly
value is the highest daily reading, API calls the total).

## Approval escalation (S1.10b, FR-XM-32)
The worker host's job `kernel.workflow_escalate` runs approval escalation every minute (S1.11a) for every customer whose
access mode is full: an open approval task past its time limit (working days in the company's time zone,
weekly off days `org.weekly_off_days` and the company's public and religious holidays not counted) moves to the next level
(the approver's manager, else the step's escalation user or role), at most three levels; nothing is ever approved
automatically. A task with nobody above stays open and shows as overdue in `GET /api/wf/instances?overdue=true`. The first run
after upgrading gives open tasks created before S1.10b their time limit. To run it at once:
`M workflow escalate [--tenant acme]` (prints escalated, overdue and time limits set per customer; exit code 3 if a customer failed).
Modules that send documents for approval register their document types (and HR its approver directory) in the worker host as
in the API host; tasks of types the worker does not know are left alone.

## Background jobs and events (S1.11a, FR-KRN-JOB-01 to FR-KRN-JOB-06)
The worker host (`dotnet Erp.Host.Worker.dll`) runs the job scheduler and the event dispatcher for every active or suspended
customer whose database is ready. Run one or more workers; each run is taken under a lease, so it runs once, and a run whose
worker stops is taken over by another worker when its lease expires (it continues from its last checkpoint).
Settings (environment variables): `ERP_CONTROL_PLANE`, `ERP_SECRETS_FILE`, `ERP_SECRETS_KEY`, `ERP_PLATFORM_ACTOR` (the
platform user the daily audit-anchor copy acts as; without it that job fails with `errors.job.platform_actor_missing` and an
alert), `Jobs__Slots` (4 runs at once per worker), `Jobs__PerTenantSlots` (2 per customer), `Jobs__LeaseSeconds` (60),
`Jobs__PollMilliseconds` (2000), `Events__PollMilliseconds` (2000), `Events__BatchSize` (100), `Settings__RefreshMilliseconds`.
The old settings `Licence__IntervalMinutes` and `Workflow__EscalationIntervalSeconds` are no longer read.

Kernel jobs (times in the customer's `org.time_zone`; `migrate` and provisioning seed them in `job.definitions`):
licence refresh (hourly at :05, every mode), approval escalation (every minute, full mode), audit anchor copy (01:15), audit
check of the previous day with the clock check (02:30), monthly partitions three months ahead (00:45), expiry of
maker-checker requests (00:20), operational checks (every 5 minutes), clean-up of finished runs after `job.retention.run_days`
(30) and delivered events after 30 days (03:30). In read-only and locked mode only the licence, audit, partition, check and
clean-up jobs run; a suspended customer runs only the licence refresh. A job of an unlicensed module never runs. After a pause
only the most recent missed slot of a schedule runs. The job centre (`/api/jobs/...`) lists jobs and runs, starts a run,
reruns, cancels, and changes the schedule of the jobs marked editable (maker-checker expiry, clean-up).

Partitions: if `meta.ensure_partitions` reports `default_has_rows` (rows of a future month were written into the default
partition, for example after a wrong clock), move those rows out of `<table>_default`, then rerun `kernel.partitions_ensure`.
Health: `GET /api/jobs/health` shows queue depth, failed runs, event backlog per schema and the last time a worker started a
run; if every worker stops, no alert can be sent (R-025), so watch that number from outside.
The operator commands `licence refresh`, `workflow escalate`, `audit anchors-publish` and `audit verify` still work as before.

## Customer list (FR-ADM-PLT-01)
`M tenant list`: code, status, database status, region, cluster, edition, entitlement state, modules, schema version, legal
hold, last backup and last restore test.
