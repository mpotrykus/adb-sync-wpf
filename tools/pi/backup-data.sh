#!/usr/bin/env bash
# Snapshots the AdbSync server's data folder (settings, manifests, run history, the adb key, the TLS certificate and
# the paired clients) into a dated .tar.gz and keeps the newest few. The project folders aren't included; they are
# the masters and are backed up by whatever backs up the drive they live on.
#
#   backup-data.sh
#
# Settings through environment variables:
#   ADBSYNC_DATA          the data folder              (default ~/adbsync/data)
#   ADBSYNC_BACKUP_MOUNT  the drive that must be mounted (default /mnt/usb4); an unmounted drive leaves an empty
#                         mount point, and writing there would put the backups on the SD card
#   ADBSYNC_BACKUP_DIR    where archives go            (default $ADBSYNC_BACKUP_MOUNT/adbsync-backups)
#   ADBSYNC_BACKUP_KEEP   how many archives to keep    (default 14)
#
# It is quiet when it works, so a cron job only mails when something is wrong.
set -euo pipefail

DATA="${ADBSYNC_DATA:-$HOME/adbsync/data}"
MOUNT="${ADBSYNC_BACKUP_MOUNT:-/mnt/usb4}"
DEST="${ADBSYNC_BACKUP_DIR:-$MOUNT/adbsync-backups}"
KEEP="${ADBSYNC_BACKUP_KEEP:-14}"

mountpoint -q "$MOUNT" || { echo "backup drive $MOUNT isn't mounted; nothing was backed up" >&2; exit 1; }
[ -f "$DATA/adbsync.db" ] || { echo "no adbsync.db in $DATA; nothing was backed up" >&2; exit 1; }

mkdir -p "$DEST"
STAMP="$(date +%Y%m%d-%H%M%S)"
WORK="$(mktemp -d "$DEST/.work-XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
mkdir "$WORK/data"

# Everything except the live databases (copied below), logs and the lock file.
(cd "$DATA" && tar --exclude='./logs' --exclude='*.db' --exclude='*.db-wal' --exclude='*.db-shm' \
    --exclude='./.instance.lock' -cf - .) | tar -xf - -C "$WORK/data"

# A plain file copy of a database in WAL mode can be torn while the server writes; SQLite's backup API can't.
python3 - "$DATA" "$WORK/data" <<'PY'
import glob, os, sqlite3, sys

source, target = sys.argv[1:3]
for path in sorted(glob.glob(os.path.join(source, "*.db"))):
    copy = os.path.join(target, os.path.basename(path))
    src = sqlite3.connect(path)
    dst = sqlite3.connect(copy)
    with dst:
        src.backup(dst)
    result = dst.execute("pragma integrity_check").fetchone()[0]
    src.close()
    dst.close()
    if result != "ok":
        sys.exit(f"integrity check failed for {path}: {result}")
PY

ARCHIVE="$DEST/adbsync-data-$STAMP.tar.gz"
tar -czf "$ARCHIVE.part" -C "$WORK" data
mv "$ARCHIVE.part" "$ARCHIVE"

# shellcheck disable=SC2012
ls -1t "$DEST"/adbsync-data-*.tar.gz | tail -n +"$((KEEP + 1))" | xargs -r rm -f --
echo "$(date -Is) $(basename "$ARCHIVE") $(du -h "$ARCHIVE" | cut -f1)" >> "$DEST/backup.log"
