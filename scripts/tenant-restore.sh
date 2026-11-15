#!/usr/bin/env bash
# Restore ONE customer's encrypted backup into a NEW database on the same server and verify it table by table
# against the live database (S1.03 restore drill, FR-ADM-DAT-08). See docs/runbooks/tenant-backup-restore.md.
# Usage: scripts/tenant-restore.sh <customer-code> <backup-file> <new-database-name>
# A restore never overwrites an existing database. Same settings as tenant-backup.sh (pg_restore instead of pg_dump).
set -euo pipefail
[ $# -eq 3 ] || { echo "usage: $0 <customer-code> <backup-file> <new-database-name>" >&2; exit 1; }
MIGRATOR="${ERP_MIGRATOR:-$(dirname "$0")/../src/Host/Erp.Host.Migrator/bin/Release/net10.0/Erp.Host.Migrator.dll}"
exec dotnet "$MIGRATOR" tenant restore --code "$1" --file "$2" --target-database "$3"
