#!/usr/bin/env bash
# Encrypted backup of ONE customer database (S1.03, FR-ADM-DAT-08). See docs/runbooks/tenant-backup-restore.md.
# Usage: scripts/tenant-backup.sh <customer-code> <output-file>
# Needs: ERP_CONTROL_PLANE, ERP_SECRETS_FILE, ERP_SECRETS_KEY, ERP_BACKUP_KEY, ERP_PLATFORM_ACTOR (or ERP_PLATFORM_ADMIN_EMAIL);
# pg_dump 16 or newer on PATH (or ERP_PG_BIN); ERP_MIGRATOR (path to Erp.Host.Migrator.dll, default: the local build).
set -euo pipefail
[ $# -eq 2 ] || { echo "usage: $0 <customer-code> <output-file>" >&2; exit 1; }
MIGRATOR="${ERP_MIGRATOR:-$(dirname "$0")/../src/Host/Erp.Host.Migrator/bin/Release/net10.0/Erp.Host.Migrator.dll}"
exec dotnet "$MIGRATOR" tenant backup --code "$1" --out "$2"
