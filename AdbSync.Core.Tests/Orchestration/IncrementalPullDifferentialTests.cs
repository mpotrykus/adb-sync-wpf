using System.Security.Cryptography;
using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Merge;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Logging;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Services.Orchestration;
using AdbSync.Core.Services.Orchestration.RunHistory;
using AdbSync.Core.Tests.Orchestration.Fakes;
using Xunit.Abstractions;

namespace AdbSync.Core.Tests.Orchestration;

/// <summary>
/// Runs the same random edit history through two isolated local worlds - one pulling everything, one pulling
/// incrementally - and requires identical merge results after every run. Devices are plain local folders, so nothing
/// ever reaches a phone or the server.
/// </summary>
public class IncrementalPullDifferentialTests(ITestOutputHelper output)
{
    private static readonly DateTime Epoch = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class World : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
        private readonly AppPaths _appPaths;
        private readonly GlobalSettings _settings;
        private readonly string[] _devices;
        private readonly SyncJobConfig _job;

        public FakeAdbTransferEngine Engine { get; }
        public ManifestStore Manifests { get; }
        public string Master { get; }

        public World(bool incremental, string[] devices, ConflictPolicy policy)
        {
            _devices = devices;
            var projects = Path.Combine(_root, "projects");
            _appPaths = new AppPaths(Path.Combine(_root, "appdata"));
            _settings = new GlobalSettings
            {
                ProjectsDirectory = projects, StaleLockHours = 4, IncrementalPull = incremental, FullPullIntervalDays = 7, ConflictPolicy = policy,
            };
            ProjectFolders.AdoptRoot(projects);
            Master = Path.Combine(projects, "Diff", "master");
            Engine = new FakeAdbTransferEngine(devices.ToDictionary(d => d, DeviceFolder));
            Manifests = new ManifestStore(_appPaths);
            _job = new SyncJobConfig
            {
                Name = "Diff",
                Devices = devices.Select(d => new JobDeviceBinding { DeviceName = d, RemotePath = "/sdcard/app" }).ToList(),
            };
        }

        public string DeviceFolder(string name) => Path.Combine(_root, "devices", name);

        public string Root(string target) => target == "master" ? Master : DeviceFolder(target);

        public async Task<JobRunResult> RunAsync()
        {
            var runner = new SyncJobRunner(
                new FakeDeviceResolver(), new FakeAppRunningGuard(false), new SyncLockManager(), Engine, new TwoWayMergeEngine(),
                Manifests, new PushSafetyGuard(_appPaths), new CheckpointManager(_appPaths), NullSyncEventSink.Instance,
                new RunHistoryStore(_appPaths));
            return await runner.RunAsync(_job, 0, _devices.Select(d => new DeviceConfig { Name = d, Serial = d }).ToList(), _settings, resumeFrom: null);
        }

        public async Task AgeFullPullStampAsync()
        {
            foreach (var device in _devices)
            {
                if (await Manifests.TryGetAsync("Diff", device) is { } manifest)
                {
                    manifest.LastFullPullAtUtc = DateTimeOffset.UtcNow.AddDays(-30);
                    await Manifests.SaveAsync("Diff", device, manifest);
                }
            }
        }

        public void Apply(Edit edit)
        {
            var path = Path.Combine(Root(edit.Target), edit.Path);
            if (edit.Content is null)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, edit.Content);
            File.SetLastWriteTimeUtc(path, edit.Mtime);
        }

        public bool Exists(Edit edit) => File.Exists(Path.Combine(Root(edit.Target), edit.Path));

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record Edit(string Target, string Path, string? Content, DateTime Mtime)
    {
        public override string ToString() => $"{(Content is null ? "delete" : "write")} {Target}:{Path}";
    }

    private static Dictionary<string, (string Hash, DateTime Mtime)> Tree(string root)
    {
        var tree = new Dictionary<string, (string, DateTime)>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
            return tree;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            tree[rel] = (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), File.GetLastWriteTimeUtc(file));
        }
        return tree;
    }

    private static string Diff(string label, Dictionary<string, (string Hash, DateTime Mtime)> expected, Dictionary<string, (string Hash, DateTime Mtime)> actual)
    {
        var problems = new List<string>();
        foreach (var path in expected.Keys.Union(actual.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            var inExpected = expected.TryGetValue(path, out var e);
            var inActual = actual.TryGetValue(path, out var a);
            if (!inExpected) problems.Add($"{label}: extra '{path}' in incremental world");
            else if (!inActual) problems.Add($"{label}: '{path}' missing in incremental world");
            else if (e.Hash != a.Hash) problems.Add($"{label}: content differs for '{path}'");
            else if ((e.Mtime - a.Mtime).Duration() > TimeSpan.FromSeconds(2)) problems.Add($"{label}: mtime differs for '{path}' ({e.Mtime:O} vs {a.Mtime:O})");
        }
        return string.Join("; ", problems);
    }

    private static string DiffManifests(SyncManifest? expected, SyncManifest? actual)
    {
        if (expected is null || actual is null)
            return expected is null && actual is null ? "" : "manifest presence differs";
        var problems = new List<string>();
        foreach (var path in expected.Entries.Keys.Union(actual.Entries.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            var inE = expected.Entries.TryGetValue(path, out var e);
            var inA = actual.Entries.TryGetValue(path, out var a);
            if (inE != inA) problems.Add($"manifest entry '{path}' present only in {(inE ? "full" : "incremental")} world");
            else if (e!.Size != a!.Size || (e.ModifiedUtc - a.ModifiedUtc).Duration() > TimeSpan.FromSeconds(2))
                problems.Add($"manifest entry '{path}' differs ({e.Size}/{e.ModifiedUtc:O} vs {a.Size}/{a.ModifiedUtc:O})");
        }
        return string.Join("; ", problems);
    }

    private static readonly string[] Dirs = ["", "", "sub/", "sub/deep/", "other/"];

    private static List<Edit> GenerateStep(Random rng, int step, string[] devices, World reference, bool allowSharedPaths)
    {
        var edits = new List<Edit>();
        var count = rng.Next(0, 4);
        for (var i = 0; i < count; i++)
        {
            var targets = devices.Append("master").ToArray();
            var target = targets[rng.Next(targets.Length)];
            var owner = target == "master" ? devices[rng.Next(devices.Length)] : target;
            var prefix = allowSharedPaths ? "f" : $"{owner.ToLowerInvariant()}_f";
            var path = $"{Dirs[rng.Next(Dirs.Length)]}{prefix}{rng.Next(0, 6)}.txt";
            var mtime = Epoch.AddMinutes(step * 10 + i);
            var edit = rng.Next(5) == 0
                ? new Edit(target, path, null, mtime)
                : new Edit(target, path, $"v{step}-{i}-{new string('x', rng.Next(1, 40))}", mtime);
            if (edit.Content is null && !reference.Exists(edit))
                continue;
            if (edits.Any(e => e.Path == edit.Path))
                continue;
            edits.Add(edit);
        }
        return edits;
    }

    private async Task RunScenarioAsync(int seed, string[] devices, ConflictPolicy policy, bool allowSharedPaths, int steps)
    {
        using var full = new World(incremental: false, devices, policy);
        using var incremental = new World(incremental: true, devices, policy);
        var rng = new Random(seed);
        var log = new List<string>();

        foreach (var device in devices)
        {
            for (var i = 0; i < 4; i++)
            {
                var seedEdit = new Edit(device, $"{device.ToLowerInvariant()}_f{i}.txt", $"seed-{device}-{i}", Epoch.AddMinutes(i));
                full.Apply(seedEdit);
                incremental.Apply(seedEdit);
            }
        }

        for (var step = 1; step <= steps; step++)
        {
            var edits = GenerateStep(rng, step, devices, full, allowSharedPaths);
            foreach (var edit in edits)
            {
                full.Apply(edit);
                incremental.Apply(edit);
            }
            log.Add($"step {step}: {(edits.Count == 0 ? "no edits" : string.Join(", ", edits))}");

            if (step % 6 == 0)
            {
                await incremental.AgeFullPullStampAsync();
                log.Add($"step {step}: aged the full-pull stamp");
            }

            var fullResult = await full.RunAsync();
            var incResult = await incremental.RunAsync();
            var context = $"seed {seed}, step {step}\n  " + string.Join("\n  ", log.TakeLast(4));

            Assert.True(fullResult.Outcome is JobRunOutcome.Completed or JobRunOutcome.CompletedNoChanges, $"full world {fullResult.Outcome} ({fullResult.ErrorMessage})\n{context}");
            Assert.True(incResult.Outcome is JobRunOutcome.Completed or JobRunOutcome.CompletedNoChanges, $"incremental world {incResult.Outcome} ({incResult.ErrorMessage})\n{context}");

            var problems = new List<string> { Diff("master", Tree(full.Master), Tree(incremental.Master)) };
            foreach (var device in devices)
                problems.Add(Diff($"device {device}", Tree(full.DeviceFolder(device)), Tree(incremental.DeviceFolder(device))));
            if (devices.Length == 1)
                problems.Add(DiffManifests(await full.Manifests.TryGetAsync("Diff", devices[0]), await incremental.Manifests.TryGetAsync("Diff", devices[0])));

            var message = string.Join(" | ", problems.Where(p => p.Length > 0));
            Assert.True(message.Length == 0, $"{message}\n{context}");
        }

        output.WriteLine($"seed {seed} ({devices.Length} device(s), {policy}): {steps} steps identical; files pulled full={full.Engine.FilesPulledTotal} incremental={incremental.Engine.FilesPulledTotal}");
        Assert.True(incremental.Engine.FilesPulledTotal <= full.Engine.FilesPulledTotal, $"seed {seed}: incremental pulled more than full");
    }

    /// <summary>Scenarios per test. CI runs a few; set ADBSYNC_DIFF_SEEDS=25 (or more) locally for the full sweep.</summary>
    private static int SeedCount(int full) =>
        int.TryParse(Environment.GetEnvironmentVariable("ADBSYNC_DIFF_SEEDS"), out var n) && n > 0 ? n : Math.Min(3, full);

    [Fact]
    public async Task SingleDevice_NewestWins_RandomHistoriesMergeIdentically()
    {
        for (var seed = 1; seed < 1 + SeedCount(25); seed++)
            await RunScenarioAsync(seed, ["DeviceA"], ConflictPolicy.NewestWins, allowSharedPaths: true, steps: 14);
    }

    [Fact]
    public async Task SingleDevice_AskPolicy_RandomHistoriesMergeIdentically()
    {
        for (var seed = 101; seed < 101 + SeedCount(15); seed++)
            await RunScenarioAsync(seed, ["DeviceA"], ConflictPolicy.Ask, allowSharedPaths: true, steps: 14);
    }

    [Fact]
    public async Task TwoDevices_DisjointDeviceFiles_RandomHistoriesMergeIdentically()
    {
        for (var seed = 201; seed < 201 + SeedCount(15); seed++)
            await RunScenarioAsync(seed, ["DeviceA", "DeviceB"], ConflictPolicy.NewestWins, allowSharedPaths: false, steps: 12);
    }
}
