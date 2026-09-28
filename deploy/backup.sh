#!/bin/sh
# A backup of the live site (or the test copy) into its backups volume (J3, D-210, D-214): the database by SQLite's
# online backup, the uploaded files and the session keys in one archive; the oldest beyond Backup:Keep (14) are deleted.
# Safe while the site runs. It runs in a one-off `backup` container of the same image — the long-running site never
# mounts the backups — and records each archive's SHA-256 outside the volume, so a restore can prove the archive is the
# one made here (restore --sha256). Called by the deploy (after maintenance is on) and by the host's cron:
#   0 4 * * * cd /srv/ivent && deploy/backup.sh --verify >> /srv/ivent/backup.log 2>&1
# Usage: deploy/backup.sh [site|staging] [--verify] [--copy-to <host folder>]
#   --verify   check the new archive restores (backup-verify in the image)
#   --copy-to  also copy it out of the volume, e.g. for the external storage (J3: set up with the owner at the end)
# Environment: ENV_FILE (default .env in the repository), BACKUP_HASHES (default var/backup-hashes.txt in the
# repository), SITE_VERSION / STAGING_VERSION — the image to run, as in docker compose.
set -eu
umask 077
# Git Bash on a Windows workstation would turn /backups/… into Windows paths; no effect on Linux
export MSYS2_ARG_CONV_EXCL='*'

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

# pwd -W: the Windows form of the path under Git Bash (nothing is converted, see above); plain pwd elsewhere
here=$(cd "$(dirname "$0")" && { pwd -W 2> /dev/null || pwd; })
root=$(cd "$here/.." && { pwd -W 2> /dev/null || pwd; })
env_file=${ENV_FILE:-$root/.env}
hashes=${BACKUP_HASHES:-$root/var/backup-hashes.txt}
tool=backup
[ "$service" = staging ] && tool=staging-backup
set -- -f "$here/docker-compose.yml"
[ -f "$env_file" ] && set -- "$@" --env-file "$env_file"
set -- "$@" --profile tools

# The image must know the backup commands: an older one would start a second site instead (D-214). The first deploy
# with backups takes its backup with the new image: SITE_VERSION=<new> deploy/backup.sh
images=$(docker compose "$@" config --images "$tool") || { echo "docker compose config failed" >&2; exit 1; }
image=$(echo "$images" | head -n 1)
label=$(docker image inspect -f '{{ index .Config.Labels "ivent.backup" }}' -- "$image" 2>/dev/null) || label=
if [ "$label" != 1 ]; then
  echo "The image $image has no backup commands (or is not here): run with the new version, SITE_VERSION=<version> $0" >&2
  exit 1
fi

# A hung SQLite lock or a full disk must not hang cron forever
output=$(timeout 30m docker compose "$@" run --rm --no-deps -T "$tool" backup 2>&1) || {
  rc=$?
  echo "$output"
  echo "The backup failed (exit $rc)." >&2
  exit "$rc"
}
echo "$output"

archive=$(echo "$output" | sed -n 's|^Backup: \(/backups/[^ ]*\.zip\) .*|\1|p')
sha256=$(echo "$output" | sed -n 's|^SHA-256: \([0-9a-f]\{64\}\)  .*|\1|p')
[ -n "$archive" ] && [ -n "$sha256" ] || { echo "No archive name or hash in the output: the backup failed." >&2; exit 1; }
name=$(basename -- "$archive")

# Outside the volume: a restore takes --sha256 from here (`sha256sum -c` form)
mkdir -p -- "$(dirname -- "$hashes")"
echo "$sha256  $name" >> "$hashes"
echo "Recorded the SHA-256 in $hashes"

if [ "$verify" = yes ]; then
  timeout 30m docker compose "$@" run --rm --no-deps -T "$tool" backup-verify "$archive" --sha256 "$sha256"
fi

if [ -n "$copy_to" ]; then
  mkdir -p -- "$copy_to"
  # Streamed out of a one-off container: the copy is the host user's own file, 0600 (umask)
  timeout 30m docker compose "$@" run --rm --no-deps -T --entrypoint cat "$tool" -- "$archive" > "$copy_to/$name"
  echo "$sha256  $name" >> "$copy_to/SHA256SUMS"
  if command -v sha256sum > /dev/null; then
    (cd -- "$copy_to" && echo "$sha256  $name" | sha256sum -c -) || { echo "The copy does not match its hash." >&2; exit 1; }
  fi
  echo "Copied to $copy_to/$name"
fi
