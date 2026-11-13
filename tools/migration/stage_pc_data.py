"""Copies a Windows AdbSync data folder into a form the Linux server imports on first start. Read-only on the source."""
import json, os, shutil, sqlite3, sys
from pathlib import Path

if len(sys.argv) != 6:
    sys.exit("usage: stage_pc_data.py <pc-data-dir> <out-dir> <old-projects-dir> <new-projects-dir> <iana-time-zone>")
src = Path(sys.argv[1])
out = Path(sys.argv[2])
old_root = sys.argv[3]
new_root = sys.argv[4]
tz = sys.argv[5]

COPY_DIRS = ["manifests", "checkpoints", "push-safety", "pending-conflicts"]
DROP = {"companion-cert.pfx": "server makes its own certificate; v1 clients pair fresh",
        "local-state.json": "WPF window sort state; dashboard sort is a server preference now",
        ".sync_checkpoint.json": "legacy single-file checkpoint from 2026-07-15, not read by the new runner",
        "logs": "WPF transcript logs; run logs live in run-history.db",
        "config/settings.json.tmp-*": "leftover temp file from an interrupted save"}

def remap(p):
    if p is None: return None, None
    norm = p.replace("/", "\\").rstrip("\\")
    if norm.lower() == old_root.lower(): return new_root, "root"
    if norm.lower().startswith(old_root.lower() + "\\"):
        return new_root + "/" + norm[len(old_root)+1:].replace("\\", "/"), "under root"
    return None, "OUTSIDE ROOT - needs a decision"

if out.exists(): shutil.rmtree(out)
(out / "config").mkdir(parents=True)
report = []
w = report.append

s = json.loads((src/"config/settings.json").read_text(encoding="utf-8"))
w("## settings.json")
nr, how = remap(s["projectsDirectory"])
w(f"  projectsDirectory: {s['projectsDirectory']!r} -> {nr!r} ({how})")
s["projectsDirectory"] = nr
for k in ("companionServerToken", "companionServerPort", "companionServerEnabled", "startAtLogin"):
    if k in s: w(f"  drop {k} (WPF / legacy companion server only)"); s.pop(k)
if s.get("companionBroadcastsEnabled", True):
    w("  companionBroadcastsEnabled: true -> false (until the companion app speaks v1; it would repoint the app away from the PC)")
s["companionBroadcastsEnabled"] = False
w(f"  timeZone: {s.get('timeZone')!r} -> {tz!r} (PC schedules ran in its local zone)")
s["timeZone"] = tz
(out/"config/settings.json").write_text(json.dumps(s, indent=2), encoding="utf-8")

d = json.loads((src/"config/devices.json").read_text(encoding="utf-8"))
w("## devices.json")
for dev in d["devices"]:
    w(f"  {dev['name']}: ip {dev['ip']}, cached port {dev.get('cachedHostPort')} -> cleared (wireless-debugging ports change; server re-resolves via mDNS)")
    dev["cachedHostPort"] = None; dev["cachedAt"] = None
(out/"config/devices.json").write_text(json.dumps(d, indent=2), encoding="utf-8")

p = json.loads((src/"config/projects.json").read_text(encoding="utf-8"))
w("## projects.json")
for job in p["projects"]:
    pd = job.get("projectDirectory")
    if pd:
        nr, how = remap(pd); w(f"  {job['name']}: projectDirectory {pd!r} -> {nr!r} ({how})"); job["projectDirectory"] = nr
    else:
        w(f"  {job['name']}: no folder override -> {new_root}/{job['name']} (enabled={job['enabled']}, schedule kind={job['schedule']['kind']}, devices={[x['deviceName'] for x in job['devices']]})")
(out/"config/projects.json").write_text(json.dumps(p, indent=2), encoding="utf-8")

w("## state copied as-is (no paths inside; imported into adbsync.db on first start)")
for dname in COPY_DIRS:
    sd = src/dname
    if not sd.exists(): w(f"  {dname}/: absent"); continue
    files = [f for f in sd.rglob("*") if f.is_file()]
    shutil.copytree(sd, out/dname)
    w(f"  {dname}/: {len(files)} files, {sum(f.stat().st_size for f in files):,} bytes")
    for f in files: w(f"      {f.relative_to(src).as_posix()}")
c = sqlite3.connect(f"file:{src/'run-history.db'}?mode=ro", uri=True)
c.execute(f"VACUUM INTO '{(out/'run-history.db').as_posix()}'")
n = c.execute("select count(*) from Runs").fetchone()[0]
w(f"  run-history.db: {n} runs (consistent snapshot via VACUUM INTO); old log text keeps its Windows paths as history")

w("## not migrated")
for k, why in DROP.items(): w(f"  {k}: {why}")
print("\n".join(report))
