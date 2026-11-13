# Running the AdbSync server in Docker

Docker is the primary deployment ([audit decision 5](server-migration-audit.md#decisions-2026-11-10)). The image and the compose file are in the repo root.

> **Status: Phase 2, in progress.** The server has HTTPS, client pairing and `/info` + `/health`, and runs the scheduler and change watchers. The jobs, devices and runs API isn't there yet, so don't point it at real jobs.

## Build

On the machine that will run it (for example a Raspberry Pi, arm64):

```sh
docker build -t adbsync-server .
```

## Run

```sh
mkdir -p data                              # create it yourself so it's owned by your user
export ADBSYNC_PROJECTS=/mnt/usb1/AdbSync  # host folder holding the project folders
export TZ=America/Chicago                  # schedules and quiet hours use this zone
docker compose up -d
docker compose ps                          # STATUS shows (healthy) once it's up
```

| Setting | Default | Purpose |
|---|---|---|
| `ADBSYNC_PROJECTS` | required | Host folder mounted at `/projects`. |
| `ADBSYNC_DATA` | `./data` | Host folder mounted at `/data`: database, logs, certificate, adb keys (`adb-home/.android`). |
| `PUID` / `PGID` | `1000` | User the server runs as, so files on the projects drive belong to that host user. |
| `TZ` | `Etc/UTC` | Time zone for daily schedules and quiet hours, unless the `timeZone` setting overrides it. |
| `ADBSYNC_SERVER_NAME` | `adbsync` | Name shown to clients. |
| `ADBSYNC_PORT` | `47615` | API port (HTTPS). |
| `ADBSYNC_LISTEN` | `0.0.0.0` | Address to listen on, for example a Tailscale address to keep it off the LAN. |
| `ADBSYNC_PUBLIC_URL` | `https://<hostname>:47615` | The address clients use; it goes into pairing links. |
| `ADBSYNC_TLS_CERT`, `ADBSYNC_TLS_KEY` | unset | PEM certificate and key to use instead of the self-signed one. |

## Pairing a client

The API is HTTPS only and every call except `/info`, `/health` and `/auth/pair` needs a client token. To pair the first client, create a one-time code on the server:

```sh
docker exec adbsync adbsync-server pair
```

It prints the code (valid for 10 minutes, single use), the certificate fingerprint and an `adbsync://pair?...` link for a QR code. The client sends the code to `POST /api/v1/auth/pair` and gets its own token back. Once a client is paired, it can create codes for further clients (`POST /api/v1/auth/pairing-codes`) and revoke any client (`DELETE /api/v1/auth/clients/{id}`).

The server is designed to be reached over Tailscale or WireGuard, or on a trusted LAN. Don't forward its port to the internet.

## Why the compose file looks the way it does

- **Host networking.** Wireless adb connects out to a random port on each phone, and mDNS discovery doesn't cross a bridge network.
- **`init: true` and `stop_grace_period: 90s`.** `docker stop` sends SIGTERM, which cancels running jobs at the next file boundary instead of killing them mid-push.
- **adb keys in `/data/adb-home/.android`.** The server sets `HOME` to `/data/adb-home` because Debian's adb ignores `ANDROID_USER_HOME`. A rebuilt container stays the same adb host, so phones don't have to authorise it again.
- **`propagation: rslave` on `/projects`.** A drive mounted after the container started becomes visible without a restart.
- **The `.adbsync-root` marker.** If the drive behind `ADBSYNC_PROJECTS` isn't mounted, the container sees an empty folder. Jobs refuse to run there ("project folder unavailable") instead of treating it as an empty master.

## Checks

```sh
curl -sk https://127.0.0.1:47615/api/v1/health   # from the host (-k: self-signed certificate)
docker exec adbsync adbsync-server healthcheck; echo $?
docker exec adbsync adb version
```

## Deploying a new build

A full `docker build` on a Raspberry Pi takes about 11 minutes, mostly compiling. `tools/deploy-pi.sh` avoids that: it publishes for `linux-arm64` on the development machine (about 10 seconds), copies the files over ssh and layers them onto a base image that already has the .NET runtime and adb.

```sh
tools/deploy-pi.sh --recreate        # build the image on the Pi, then recreate the container and wait until it is healthy
```

The Pi needs an `adbsync-server:base` image once. After a full build (`docker build -t adbsync-server:latest .`), run `docker tag adbsync-server:latest adbsync-server:base`. Repeat that only when the base changes: a new adb or .NET runtime.

Recreating the container interrupts any run in progress, so check the logs first. The adb key and everything else in `/data` survive it.

## Backing up the data folder

The project folders (the masters) are plain files and belong to whatever backs up the drive they are on. `/data` is small, 20 MB or less, and holds what is hard to rebuild: settings, the manifests that record what was last merged, run history, pending conflicts, the adb key, the TLS certificate and the paired clients. Losing the manifests is the worst case. The next run then has to treat every file as new, and the server refuses to push in that state.

`tools/pi/backup-data.sh` writes a dated `.tar.gz` of the folder and keeps the newest 14. It copies the SQLite databases with SQLite's backup API, so a snapshot taken while the server is writing is consistent, and it integrity-checks each one. It refuses to run when the backup drive isn't mounted, because an unmounted drive leaves an empty mount point and the archive would land on the SD card. It prints nothing on success, so a cron job mails only when something is wrong.

```sh
scp tools/pi/backup-data.sh raspi-server:Scripts/adbsync-data-backup.sh
ssh raspi-server chmod +x Scripts/adbsync-data-backup.sh
# crontab -e on the Pi, one line:
15 4 * * * /home/<user>/Scripts/adbsync-data-backup.sh
```

Defaults are `ADBSYNC_DATA=~/adbsync/data`, `ADBSYNC_BACKUP_MOUNT=/mnt/usb4`, `ADBSYNC_BACKUP_DIR=$ADBSYNC_BACKUP_MOUNT/adbsync-backups` and `ADBSYNC_BACKUP_KEEP=14`. Keep the backup folder outside any folder that another job mirrors with `--delete`.

The archives contain the adb private key, the certificate's private key and hashed client tokens. On an NTFS drive mounted with `fmask=111` every local user can read them, so put them on a drive only you can reach if the machine is shared.

### Restoring

```sh
cd ~/adbsync && docker compose down
mv data data.broken
mkdir data
tar -xzf /mnt/usb4/adbsync-backups/adbsync-data-<stamp>.tar.gz -C data --strip-components=1
docker compose up -d
```

The adb key comes back with it, so the phones still trust the server, and the paired web and phone clients keep their tokens. Restoring an older snapshot puts the manifests back to that moment. Files changed since then are seen as new changes on the next run, which is safe but may raise conflicts.
