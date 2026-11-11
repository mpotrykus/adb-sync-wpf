# Running the AdbSync server in Docker

Docker is the primary deployment ([audit decision 5](server-migration-audit.md#decisions-2026-11-10)). The image and the compose file are in the repo root.

> **Status: Phase 2 skeleton.** The server answers `/api/v1/info` and `/api/v1/health` and runs the scheduler and change watchers, but it only listens on `127.0.0.1` until client authentication exists. Don't point it at real jobs yet.

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
| `ADBSYNC_DATA` | `./data` | Host folder mounted at `/data`: database, logs, certificate, adb keys. |
| `PUID` / `PGID` | `1000` | User the server runs as, so files on the projects drive belong to that host user. |
| `TZ` | `Etc/UTC` | Time zone for daily schedules and quiet hours, unless the `timeZone` setting overrides it. |
| `ADBSYNC_SERVER_NAME` | `adbsync` | Name shown to clients. |
| `ADBSYNC_PORT` | `47615` | API port. |

## Why the compose file looks the way it does

- **Host networking.** Wireless adb connects out to a random port on each phone, and mDNS discovery doesn't cross a bridge network.
- **`init: true` and `stop_grace_period: 90s`.** `docker stop` sends SIGTERM, which cancels running jobs at the next file boundary instead of killing them mid-push.
- **adb keys in `/data/android`.** A rebuilt container stays the same adb host, so phones don't have to authorise it again.
- **`propagation: rslave` on `/projects`.** A drive mounted after the container started becomes visible without a restart.
- **The `.adbsync-root` marker.** If the drive behind `ADBSYNC_PROJECTS` isn't mounted, the container sees an empty folder. Jobs refuse to run there ("project folder unavailable") instead of treating it as an empty master.

## Checks

```sh
curl -s 127.0.0.1:47615/api/v1/health     # from the host
docker exec adbsync adbsync-server healthcheck; echo $?
docker exec adbsync adb version
```
