using AdbSync.Core.Services.Config;

namespace AdbSync.Core.Services.Persistence;

public static class EngineMigrations
{
    public static IReadOnlyList<EngineMigration> All { get; } =
    [
        new(1, """
            CREATE TABLE Settings (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Json TEXT NOT NULL,
                Version INTEGER NOT NULL);
            CREATE TABLE Devices (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                Position INTEGER NOT NULL,
                Json TEXT NOT NULL,
                Version INTEGER NOT NULL);
            CREATE TABLE Jobs (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                Position INTEGER NOT NULL,
                Json TEXT NOT NULL,
                Version INTEGER NOT NULL);
            CREATE TABLE DeviceRuntime (
                DeviceId TEXT PRIMARY KEY REFERENCES Devices (Id) ON DELETE CASCADE,
                CachedHostPort TEXT,
                CachedAt TEXT);
            CREATE TABLE JobRuntime (
                JobId TEXT PRIMARY KEY REFERENCES Jobs (Id) ON DELETE CASCADE,
                LastRunAt TEXT,
                LastSuccessAt TEXT);
            CREATE TABLE SchedulerState (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Paused INTEGER NOT NULL);
            """, AppConfigStore.ImportLegacyJsonAsync),
        new(2, """
            CREATE TABLE Manifests (
                JobName TEXT NOT NULL COLLATE NOCASE,
                DeviceName TEXT NOT NULL COLLATE NOCASE,
                Json TEXT NOT NULL,
                PRIMARY KEY (JobName, DeviceName));
            CREATE TABLE Checkpoints (
                JobName TEXT PRIMARY KEY COLLATE NOCASE,
                Json TEXT NOT NULL);
            CREATE TABLE PushSafety (
                JobName TEXT PRIMARY KEY COLLATE NOCASE,
                Json TEXT NOT NULL);
            CREATE TABLE PendingConflicts (
                Id TEXT PRIMARY KEY,
                JobName TEXT NOT NULL COLLATE NOCASE,
                DeviceName TEXT NOT NULL COLLATE NOCASE,
                Json TEXT NOT NULL);
            CREATE INDEX PendingConflictsByDevice ON PendingConflicts (JobName, DeviceName);
            """, LegacyStateImporter.ImportAsync),
        new(3, """
            CREATE TABLE ManifestRoots (
                JobName TEXT PRIMARY KEY COLLATE NOCASE,
                RootId TEXT NOT NULL,
                MasterPath TEXT NOT NULL);
            """),
        new(4, """
            CREATE TABLE Clients (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Kind TEXT NOT NULL,
                Version TEXT,
                TokenHash TEXT NOT NULL UNIQUE,
                CreatedAt TEXT NOT NULL,
                LastSeenAt TEXT);
            CREATE TABLE PairingCodes (
                CodeHash TEXT PRIMARY KEY,
                ExpiresAt TEXT NOT NULL);
            """),
    ];
}
