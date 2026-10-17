#!/usr/bin/env bash
# Print a Kairos database's newest migration and the row count of every table,
# one line each, sorted. Run it on a backup and on the restored copy: identical
# output means the restore brought back every row.
#
#   scripts/restore-check.sh path/to/kairos.db
#
# Needs only Docker. The database is mounted read-only and copied inside the
# container before sqlite3 opens it, because opening a WAL database writes its
# -shm file; the file being checked is never touched.
set -euo pipefail

readonly ALPINE_IMAGE="alpine:3.22@sha256:5291449c3df73caf6ed85e649dec1b9e818b39a5d8c871e97afc13e9cd5e8fa8"
readonly SQLITE_PACKAGE="sqlite=3.49.2-r1"
readonly USAGE_EXIT=2

if [ "$#" -ne 1 ] || [ ! -f "$1" ]; then
  echo "usage: $0 <path to kairos.db>" >&2
  exit "$USAGE_EXIT"
fi

dir="$(cd "$(dirname "$1")" && pwd)"
name="$(basename "$1")"

docker run --rm -i \
  -v "$dir:/in:ro" \
  -e DB_NAME="$name" \
  -e SQLITE_PACKAGE="$SQLITE_PACKAGE" \
  "$ALPINE_IMAGE" sh -s <<'IN_CONTAINER'
set -eu
set -o pipefail
apk add --no-cache "$SQLITE_PACKAGE" >/dev/null
cp "/in/$DB_NAME" /tmp/kairos.db
# A copy taken while Kairos runs may have committed rows still in the WAL.
[ -f "/in/$DB_NAME-wal" ] && cp "/in/$DB_NAME-wal" /tmp/kairos.db-wal
db=/tmp/kairos.db
migration="$(sqlite3 "$db" 'SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;')"
echo "migration=$migration"
sqlite3 "$db" "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;" |
  while read -r table; do
    count="$(sqlite3 "$db" "SELECT COUNT(*) FROM \"$table\";")"
    echo "$table=$count"
  done
IN_CONTAINER
