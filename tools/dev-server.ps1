# Local dev server for the web UI: scratch data dir, own port, prints a pairing code, rebuilds on change.
#
#   tools/dev-server.ps1 [-Fresh] [-Seed] [-AllowRuns] [-Port 47616] [-NoWatch]
#
# -Fresh    wipe the scratch data first (jobs, devices, pairings, certificate)
# -Seed     wipe, then fill the scratch data with a snapshot of the Pi's data folder (read-only on the Pi; the
#           paired clients, TLS certificate and adb key are left out)
# -AllowRuns  turn every Development feature flag back on (scheduler, change watches, real writes to devices). Runs
#           are on by default, but anything that would change a device is replaced by a wait. Only use this with
#           data that can't hurt anything.
# -WriteDelay seconds each simulated device write waits (default 10)
# -NoWatch  plain `dotnet run` instead of `dotnet watch`
# -NoHotReload  with watch, restart on every change instead of applying edits live
# -NoBrowser  don't open the browser; the pairing code is still printed (the /signin?code= link signs in on its own)
param(
    [switch]$Fresh,
    [switch]$Seed,
    [switch]$AllowRuns,
    [double]$WriteDelay = 10,
    [string]$PiHost = 'raspi-server',
    [int]$Port = 47616,
    [switch]$NoWatch,
    [switch]$NoHotReload,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dev = Join-Path $env:LOCALAPPDATA 'AdbSync.Dev'

if (($Fresh -or $Seed) -and (Test-Path $dev)) { Remove-Item -Recurse -Force $dev }

if ($Seed) {
    Write-Host "== snapshot $PiHost data"
    $remoteArchive = '/tmp/adbsync-dev-seed.tgz'
    $remoteScript = @'
set -euo pipefail
DATA="$HOME/adbsync/data"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir "$WORK/data"
(cd "$DATA" && tar --exclude='./logs' --exclude='*.db' --exclude='*.db-wal' --exclude='*.db-shm' --exclude='./.instance.lock'     --exclude='./adb-home' --exclude='./json-backup' --exclude='./companion-cert.pfx' -cf - .) | tar -xf - -C "$WORK/data"
python3 - "$DATA" "$WORK/data" <<'PY'
import glob, os, sqlite3, sys
source, target = sys.argv[1:3]
for path in sorted(glob.glob(os.path.join(source, "*.db"))):
    copy = os.path.join(target, os.path.basename(path))
    src = sqlite3.connect(path)
    dst = sqlite3.connect(copy)
    with dst:
        src.backup(dst)
    if os.path.basename(path) == "adbsync.db":
        with dst:
            dst.execute("delete from Clients")
            dst.execute("delete from PairingCodes")
    src.close()
    dst.close()
PY
tar -czf /tmp/adbsync-dev-seed.tgz -C "$WORK" data
'@
    $lf = $remoteScript.Replace("`r", '')
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($lf))
    ssh $PiHost "echo $encoded | base64 -d | bash"
    if ($LASTEXITCODE -ne 0) { throw "snapshot on $PiHost failed" }
    $localArchive = Join-Path ([IO.Path]::GetTempPath()) 'adbsync-dev-seed.tgz'
    scp -q "${PiHost}:$remoteArchive" $localArchive
    if ($LASTEXITCODE -ne 0) { throw "copying the snapshot from $PiHost failed" }
    ssh $PiHost "rm -f $remoteArchive"
    New-Item -ItemType Directory -Force $dev | Out-Null
    tar -xzf $localArchive -C $dev
    if ($LASTEXITCODE -ne 0) { throw "extracting the snapshot failed" }
    Remove-Item $localArchive
}

$env:ADBSYNC_DATA_DIR = Join-Path $dev 'data'
$env:ADBSYNC_PROJECTS_DIR = Join-Path $dev 'projects'
$env:ADBSYNC_PORT = $Port
$env:ADBSYNC_SERVER_NAME = 'adbsync-dev'
$env:ADBSYNC_LISTEN = '127.0.0.1'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
if ($AllowRuns) {
    foreach ($flag in 'Scheduler', 'ChangeWatch', 'CompanionBroadcast', 'RunJobs', 'DeviceWrites') { Set-Item "env:Features__$flag" 'true' }
}
$env:Features__SimulatedWriteDelaySeconds = $WriteDelay
New-Item -ItemType Directory -Force $env:ADBSYNC_DATA_DIR, $env:ADBSYNC_PROJECTS_DIR | Out-Null

$project = Join-Path $root 'AdbSync.Server\AdbSync.Server.csproj'

Write-Host "== build"
dotnet build $project -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== pairing code (single use, expires in a few minutes; run 'dotnet run --no-build --project AdbSync.Server -- pair' for another)"
$pairOutput = dotnet run --no-build --project $project -- pair
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$pairOutput | Write-Host
$code = ($pairOutput | Select-String 'Pairing code:\s*(\S+)').Matches[0].Groups[1].Value

$signIn = "https://localhost:$Port/signin?code=$code"
if (-not $NoBrowser) {
    Start-Job -ArgumentList $Port, $signIn {
        param($port, $url)
        for ($i = 0; $i -lt 120; $i++) {
            try { [Net.Sockets.TcpClient]::new('127.0.0.1', $port).Dispose(); Start-Process $url; return } catch { Start-Sleep -Milliseconds 500 }
        }
    } | Out-Null
}

Write-Host "== server on https://localhost:$Port (accept the certificate warning the first time), data in $dev"
if ($NoWatch) {
    dotnet run --no-build --project $project
} else {
    $watchArgs = @('watch', 'run', '--project', $project)
    if ($NoHotReload) { $watchArgs += '--no-hot-reload' }
    dotnet @watchArgs
}
