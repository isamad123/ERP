# Runbook: per-customer backup and restore drill (S1.03, FR-ADM-DAT-08)

Each customer has its own database, so each is backed up and restored on its own. Backups are encrypted with AES-256-GCM
(key `ERP_BACKUP_KEY`, base64 of 32 random bytes, kept in the operations vault, never with the backup files). Losing the key
means losing the backups.

Requirements: the settings of tenant-operations.md, and PostgreSQL 16 (or newer) `pg_dump` and `pg_restore` on `PATH`
(or `ERP_PG_BIN` pointing at their folder). `ERP_MIGRATOR` points at `Erp.Host.Migrator.dll` if it is not the local Release build.

## Back up
`scripts/tenant-backup.sh acme /backups/acme-2026-11-15.dump.enc`

Runs `pg_dump` as the customer's own backup login (read-only), encrypts the stream into the file, and records
`last_backup_at` on `cp.tenant_databases` and a `tenant.backup` entry in `cp.platform_audit`. A failed backup leaves no file.

## Restore drill (do this regularly; the result is shown as "last restore test")
`scripts/tenant-restore.sh acme /backups/acme-2026-11-15.dump.enc erp_r_acme`

1. Creates the new database `erp_r_acme` (a restore never overwrites an existing database), owned by the customer's owner login.
2. Decrypts and restores the file into it (a damaged, truncated or wrongly keyed file is refused and the new database removed).
3. Compares every table with the live customer database (row count and a content checksum) and prints the result.
4. If all tables match, records `last_restore_test_at` and a `tenant.restore_test` audit entry. Exit code 4 means a difference.

The drill compares with the live database, so run it soon after the backup or on a quiet customer; changes made between
backup and comparison show up as differences. Drop the drill database when done: `drop database erp_r_acme with (force)`.

## Evidence from the S1.03 drill (2026-11-15, local PostgreSQL 16)
```
acme: backup written to .../acme-2026-11-15.dump.enc (2339558 bytes before encryption)
acme: restored into erp_r_acme; 215 tables match the source
error: Database erp_r_acme already exists; a restore never overwrites a database.
```

## Not in Release 1.0
Scheduled backups, retention, point-in-time recovery and moving a customer between servers (FR-KRN-TEN-06, Release 1.1).
On the hosting side the server-level backups (base backups and WAL archiving, RPO 15 minutes) remain the operations team's job.
