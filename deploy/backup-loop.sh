#!/bin/sh
# Runs backup.sh every night at $BACKUP_HOUR (container time, UTC unless TZ is set; default 03:00).
set -u
HOUR="${BACKUP_HOUR:-3}"

while true; do
  now=$(date +%s)
  today=$(date +%Y-%m-%d)
  target=$(date -d "$today $(printf '%02d' "$HOUR"):00:00" +%s)
  [ "$target" -le "$now" ] && target=$((target + 86400))
  echo "backup: next run in $((target - now))s"
  sleep $((target - now))
  /usr/local/bin/backup.sh || echo "backup: FAILED" >&2
done
