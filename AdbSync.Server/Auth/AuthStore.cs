using AdbSync.Core.Services.Persistence;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AdbSync.Server.Auth;

public sealed record PairedClient(Guid Id, string Name, string Kind, string? Version, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt);

public sealed record NewPairingCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// Per-client bearer tokens and one-time pairing codes. Only SHA-256 hashes are stored: tokens are 32 random bytes and
/// codes expire within minutes, so a slow password hash would add nothing. Works from a second process (the
/// <c>pair</c> command) while the server runs, since SQLite coordinates the writes.
/// </summary>
public sealed class AuthStore(EngineDatabase database, TimeProvider clock)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(1);
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<NewPairingCode> CreatePairingCodeAsync(CancellationToken ct = default)
    {
        var raw = RandomNumberGenerator.GetString(CodeAlphabet, 8);
        var code = $"{raw[..4]}-{raw[4..]}";
        var expiresAt = clock.GetUtcNow() + CodeLifetime;
        await using var connection = await database.OpenAsync(ct);
        await Sql.ExecuteAsync(connection, null, "DELETE FROM PairingCodes WHERE ExpiresAt < @now;", ct, ("@now", Format(clock.GetUtcNow())));
        await Sql.ExecuteAsync(connection, null, "INSERT INTO PairingCodes (CodeHash, ExpiresAt) VALUES (@hash, @expiresAt);", ct,
            ("@hash", Hash(NormalizeCode(code))), ("@expiresAt", Format(expiresAt)));
        return new NewPairingCode(code, expiresAt);
    }

    /// <summary>Uses up <paramref name="code"/> and creates a client, or returns null when the code is unknown or expired.</summary>
    public async Task<(PairedClient Client, string Token)?> PairAsync(string code, string name, string kind, string? version, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var used = await Sql.ExecuteAsync(connection, transaction,
            "DELETE FROM PairingCodes WHERE CodeHash = @hash AND ExpiresAt >= @now;", ct,
            ("@hash", Hash(NormalizeCode(code))), ("@now", Format(clock.GetUtcNow())));
        if (used == 0)
            return null;

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var client = new PairedClient(Guid.NewGuid(), name, kind, version, clock.GetUtcNow(), null);
        await Sql.ExecuteAsync(connection, transaction, """
            INSERT INTO Clients (Id, Name, Kind, Version, TokenHash, CreatedAt) VALUES (@id, @name, @kind, @version, @hash, @createdAt);
            """, ct,
            ("@id", client.Id.ToString()), ("@name", name), ("@kind", kind), ("@version", version),
            ("@hash", Hash(token)), ("@createdAt", Format(client.CreatedAt)));
        await transaction.CommitAsync(ct);
        return (client, token);
    }

    /// <summary>The client <paramref name="token"/> belongs to, or null.</summary>
    public async Task<Guid?> AuthenticateAsync(string token, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        var rows = await Sql.QueryAsync(connection, null, "SELECT Id, LastSeenAt FROM Clients WHERE TokenHash = @hash;",
            r => (Id: Guid.Parse(r.GetString(0)), LastSeen: r.IsDBNull(1) ? (DateTimeOffset?)null : Parse(r.GetString(1))), ct,
            ("@hash", Hash(token)));
        if (rows.Count == 0)
            return null;

        var now = clock.GetUtcNow();
        if (rows[0].LastSeen is not { } seen || now - seen > LastSeenResolution)
            await Sql.ExecuteAsync(connection, null, "UPDATE Clients SET LastSeenAt = @now WHERE Id = @id;", ct,
                ("@now", Format(now)), ("@id", rows[0].Id.ToString()));
        return rows[0].Id;
    }

    public async Task<IReadOnlyList<PairedClient>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        return await Sql.QueryAsync(connection, null,
            "SELECT Id, Name, Kind, Version, CreatedAt, LastSeenAt FROM Clients ORDER BY CreatedAt;",
            r => new PairedClient(
                Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                Parse(r.GetString(4)), r.IsDBNull(5) ? null : Parse(r.GetString(5))), ct);
    }

    public async Task<bool> RevokeAsync(Guid clientId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        return await Sql.ExecuteAsync(connection, null, "DELETE FROM Clients WHERE Id = @id;", ct, ("@id", clientId.ToString())) > 0;
    }

    private static string NormalizeCode(string code) => code.Replace("-", "").Trim().ToUpperInvariant();

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
