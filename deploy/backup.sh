#!/bin/sh
# A backup of the live site (or the test copy) into its backups volume (J3, D-210): the database by SQLite's online
# backup, the uploaded files and the session keys in one archive; the oldest beyond Backup:Keep (14) are deleted.
# Safe while the site runs. Called by the deploy before migrations and by the host's cron, e.g. daily at 04:00:
#   0 4 * * * /srv/ivent/deploy/backup.sh >> /srv/ivent/backup.log 2>&1
# Usage: deploy/backup.sh [site|staging] [--verify] [--copy-to <host folder>]
#   --verify   check the new archive restores (backup-verify in the image)
#   --copy-to  also copy it out of the volume, e.g. for the external storage (J3: set up with the owner at the end)
set -eu

service=site
verify=no
copy_to=
while [ $# -gt 0 ]; do
  case "$1" in
    site | staging) service=$1 ;;
    --verify) verify=yes ;;
    --copy-to)
      [ $# -ge 2 ] || { echo "--copy-to needs a folder" >&2; exit 2; }
      copy_to=$2
      shift
      ;;
    *) echo "Usage: $0 [site|staging] [--verify] [--copy-to <host folder>]" >&2; exit 2 ;;
  esac
  shift
done

here=$(cd "$(dirname "$0")" && pwd)
env_file=${ENV_FILE:-$here/../.env}
set -- -f "$here/docker-compose.yml"
[ -f "$env_file" ] && set -- "$@" --env-file "$env_file"
[ "$service" = staging ] && set -- "$@" --profile staging

# In the running container when there is one; otherwise a one-off container of the same image and volumes
if [ -n "$(docker compose "$@" ps --status running -q "$service")" ]; then
  output=$(docker compose "$@" exec -T "$service" dotnet GameEvent.Web.dll backup)
else
  output=$(docker compose "$@" run --rm --no-deps -T "$service" backup)
fi
echo "$output"

archive=$(echo "$output" | sed -n 's|^Backup: \(/backups/[^ ]*\.zip\) .*|\1|p')
[ -n "$archive" ] || { echo "No archive name in the output: the backup failed." >&2; exit 1; }

if [ "$verify" = yes ]; then
  docker compose "$@" run --rm --no-deps -T "$service" backup-verify "$archive"
fi

if [ -n "$copy_to" ]; then
  # docker cp reads a stopped container too; one removed by `down` has to be created again first (`up --no-start`)
  mkdir -p "$copy_to"
  container=$(docker compose "$@" ps -a -q "$service" | head -n 1)
  [ -n "$container" ] || { echo "No $service container to copy from: docker compose up --no-start $service" >&2; exit 1; }
  docker cp "$container:$archive" "$copy_to/"
  chmod 600 "$copy_to/$(basename "$archive")"
  echo "Copied to $copy_to/$(basename "$archive")"
fi
