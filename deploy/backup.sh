#!/bin/sh
# One consistent backup of the DoiQ database using SQLite's online backup (safe while the app
# is running with WAL on; never copy doiq.db by hand). Keeps the newest $KEEP files.
set -eu

DB="${DOIQ_DB:-/data/doiq.db}"
DIR="${BACKUP_DIR:-$(dirname "$DB")/backups}"
KEEP="${BACKUP_KEEP:-14}"

[ -f "$DB" ] || { echo "backup: $DB not found, nothing to back up" >&2; exit 0; }
mkdir -p "$DIR"

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
tmp="$DIR/.doiq-$stamp.db.partial"
out="$DIR/doiq-$stamp.db"

sqlite3 "$DB" ".timeout 30000" ".backup '$tmp'"
sqlite3 "$tmp" "PRAGMA integrity_check;" | grep -qx ok || { rm -f "$tmp"; echo "backup: integrity check failed" >&2; exit 1; }
mv "$tmp" "$out"
echo "backup: wrote $out"

# The ASP.NET Data Protection keys (login cookies) live next to the database; keep a current copy.
KEYS="${DOIQ_KEYS:-$(dirname "$DB")/keys}"
if [ -d "$KEYS" ]; then
  rm -rf "$DIR/keys" && cp -R "$KEYS" "$DIR/keys" && echo "backup: copied $KEYS"
fi

# Prune: names sort chronologically, so drop all but the last $KEEP.
ls -1 "$DIR"/doiq-*.db | sort | head -n -"$KEEP" | while read -r old; do
  rm -f "$old" && echo "backup: removed $old"
done
