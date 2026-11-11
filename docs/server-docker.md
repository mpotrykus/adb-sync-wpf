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
| `ADBSYNC_DATA` | `./data` | Host folder mounted at `/data`: database, logs, certificate, adb keys. |
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
- **adb keys in `/data/android`.** A rebuilt container stays the same adb host, so phones don't have to authorise it again.
- **`propagation: rslave` on `/projects`.** A drive mounted after the container started becomes visible without a restart.
- **The `.adbsync-root` marker.** If the drive behind `ADBSYNC_PROJECTS` isn't mounted, the container sees an empty folder. Jobs refuse to run there ("project folder unavailable") instead of treating it as an empty master.

## Checks

```sh
curl -sk https://127.0.0.1:47615/api/v1/health   # from the host (-k: self-signed certificate)
docker exec adbsync adbsync-server healthcheck; echo $?
docker exec adbsync adb version
```
