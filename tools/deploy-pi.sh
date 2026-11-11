#!/usr/bin/env bash
# Fast deploy to the Raspberry Pi: cross-publish for linux-arm64 here, ship the files over ssh and layer them onto a
# base image that already has the .NET runtime and adb. A full on-Pi `docker build` (about 11 minutes) is only needed
# when the base image changes; see docs/server-docker.md.
#
#   tools/deploy-pi.sh [--recreate] [ssh-host]
#
# --recreate  also recreates the container from the new image (otherwise only the image is built).
set -euo pipefail

RECREATE=0
HOST=raspi-server
for arg in "$@"; do
  case "$arg" in
    --recreate) RECREATE=1 ;;
    *) HOST="$arg" ;;
  esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/obj/arm-out"
TAG="$(git -C "$ROOT" rev-parse --short HEAD)"
[ -z "$(git -C "$ROOT" status --porcelain -- AdbSync.Core AdbSync.Engine AdbSync.Contracts AdbSync.Web AdbSync.Server)" ] || TAG="$TAG-dirty"

if ! ssh "$HOST" 'docker image inspect adbsync-server:base >/dev/null 2>&1'; then
  echo "The Pi has no adbsync-server:base image. After a full build, run on the Pi:" >&2
  echo "  docker tag adbsync-server:latest adbsync-server:base" >&2
  exit 1
fi

echo "== publish linux-arm64 ($TAG)"
rm -rf "$OUT"
dotnet publish "$ROOT/AdbSync.Server/AdbSync.Server.csproj" -c Release -r linux-arm64 --self-contained false -o "$OUT" -nologo -v q

echo "== ship to $HOST"
tar -C "$OUT" -czf - . | ssh "$HOST" 'rm -rf ~/adbsync-fast && mkdir -p ~/adbsync-fast/app && tar -xz -C ~/adbsync-fast/app'
scp -q "$ROOT/tools/pi/Dockerfile.deploy" "$HOST:adbsync-fast/Dockerfile"

echo "== build image on $HOST"
ssh "$HOST" "cd ~/adbsync-fast && docker build -q -t adbsync-server:latest -t adbsync-server:$TAG ."

if [ "$RECREATE" = 1 ]; then
  echo "== recreate container"
  ssh "$HOST" 'cd ~/adbsync && docker compose up -d --no-build --force-recreate'
  for _ in $(seq 1 30); do
    state="$(ssh "$HOST" "docker inspect -f '{{.State.Health.Status}}' adbsync 2>/dev/null" || true)"
    [ "$state" = healthy ] && break
    sleep 3
  done
  echo "container: ${state:-unknown}"
  [ "$state" = healthy ]
fi
echo "done: adbsync-server:$TAG"
