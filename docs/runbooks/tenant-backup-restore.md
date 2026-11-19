# Runbook: per-customer backup and restore drill (S1.03, FR-ADM-DAT-08)

Each customer has its own database, so each is backed up and restored on its own. Backups are encrypted with AES-256-GCM
(key `ERP_BACKUP_KEY`, base64 of 32 random bytes, kept in the operations vault, never with the backup files). Losing the key
means losing the backups.

Requirements: the settings of tenant-operations.md, and `pg_dump` and `pg_restore` of the SAME major version as the database
server (16 in Release 1.0) on `PATH`, or `ERP_PG_BIN` pointing at their folder. A newer tool writes archives the server cannot
restore, so the commands refuse a version mismatch before doing anything. `ERP_MIGRATOR` points at `Erp.Host.Migrator.dll` if it is not the local Release build.

## Back up the secret store too (S1.05, risk R-006)
A database backup alone is not enough. The secret store file (`ERP_SECRETS_FILE`) holds, for every customer, the
database logins, the field-encryption key (`tenants/<code>/field-key-v1`, needed to read authenticator-app secrets) and
the platform's access-token signing keys (`platform/token-signing/*`). Without it a restored database works, but every
enrolled authenticator app is unreadable (each user then needs an MFA reset) and the logins must be re-created.
- Copy `ERP_SECRETS_FILE` to the operations vault after every change: after `M migrate`, `M tenant provision`,
  `M cluster add` and `M auth-key rotate`. The file is already encrypted with `ERP_SECRETS_KEY`; keep that key in the vault
  as well, but never in the same place as the file copies or the database backups.
- A restore drill should also check that the restored customer's authenticator secrets can be read with the restored
  secret store (sign in with a test user that has an authenticator app enrolled).

## Back up
`scripts/tenant-backup.sh acme /backups/acme-2026-11-15.dump.enc`

Runs `pg_dump` as the customer's own backup login (read-only), encrypts the stream into the file, and records
`last_backup_at` on `cp.tenant_databases` and a `tenant.backup` entry in `cp.platform_audit`. A failed backup leaves no file.
The output file must not exist: the command refuses an existing file and never changes or deletes it (S1.03 review finding 1).

In front of the dump the file holds a **manifest** (S1.03 review finding 2): the customer code and, for every table, its row
count and content checksum, taken in the same database snapshot that `pg_dump` dumps. The manifest is encrypted and
authenticated like the dump. The customer may keep working during the backup.

## Restore drill (do this regularly; the result is shown as "last restore test")
`scripts/tenant-restore.sh acme /backups/acme-2026-11-15.dump.enc erp_r_acme`

1. Creates the new database `erp_r_acme` (a restore never overwrites an existing database), owned by the customer's owner login.
   The name must not start with `erp_t_`, which is reserved for customer databases.
2. Decrypts and restores the file into it. A damaged, truncated or wrongly keyed file, a file of another customer, or a file
   without a manifest (made before 2026-11-19) is refused, and the new database is removed.
3. Compares every table of the copy with the backup's manifest (row count and content checksum) and prints the result.
4. If all tables match, records `last_restore_test_at` and a `tenant.restore_test` audit entry. Exit code 4 means a difference.

The drill compares with the backup as it was taken, not with the live database, so it can run at any time on a busy
customer. Drop the drill database when done: `drop database erp_r_acme with (force)`.

## Evidence from the S1.03 drill (2026-11-15, local PostgreSQL 16)
```
acme: backup written to .../acme-2026-11-15.dump.enc (2339558 bytes before encryption)
acme: restored into erp_r_acme; 215 tables match the source
error: Database erp_r_acme already exists; a restore never overwrites a database.
```

## Not in Release 1.0
Scheduled backups, retention, point-in-time recovery and moving a customer between servers (FR-KRN-TEN-06, Release 1.1).
On the hosting side the server-level backups (base backups and WAL archiving, RPO 15 minutes) remain the operations team's job.
