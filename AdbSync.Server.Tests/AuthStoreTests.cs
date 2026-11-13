using AdbSync.Core.Services.Config;
using AdbSync.Core.Services.Persistence;
using AdbSync.Server.Auth;
using Microsoft.Extensions.Time.Testing;

namespace AdbSync.Server.Tests;

public class AuthStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdbSync.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly AuthStore _store;

    public AuthStoreTests()
    {
        _store = new AuthStore(new EngineDatabase(new AppPaths(_root)), _clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Pair_ValidCode_ReturnsATokenThatAuthenticatesAsThatClient()
    {
        var code = await _store.CreatePairingCodeAsync();

        var paired = await _store.PairAsync(code.Code, "Pixel", "android", "1.6.0");

        Assert.NotNull(paired);
        Assert.Equal(paired.Value.Client.Id, await _store.AuthenticateAsync(paired.Value.Token));
    }

    [Fact]
    public async Task Pair_CodeIsSingleUse()
    {
        var code = await _store.CreatePairingCodeAsync();
        Assert.NotNull(await _store.PairAsync(code.Code, "Pixel", "android", null));

        Assert.Null(await _store.PairAsync(code.Code, "Other", "web", null));
    }

    [Fact]
    public async Task Pair_ExpiredCode_IsRejected()
    {
        var code = await _store.CreatePairingCodeAsync();
        _clock.Advance(AuthStore.CodeLifetime + TimeSpan.FromSeconds(1));

        Assert.Null(await _store.PairAsync(code.Code, "Pixel", "android", null));
    }

    [Fact]
    public async Task Pair_CodeIgnoresCaseAndTheDash()
    {
        var code = await _store.CreatePairingCodeAsync();

        Assert.NotNull(await _store.PairAsync(code.Code.Replace("-", "").ToLowerInvariant(), "Pixel", "android", null));
    }

    [Fact]
    public async Task Authenticate_UnknownToken_ReturnsNull() =>
        Assert.Null(await _store.AuthenticateAsync("not-a-token"));

    [Fact]
    public async Task Revoke_TokenStopsWorkingAndTheClientIsNoLongerListed()
    {
        var code = await _store.CreatePairingCodeAsync();
        var paired = (await _store.PairAsync(code.Code, "Pixel", "android", null))!.Value;

        Assert.True(await _store.RevokeAsync(paired.Client.Id));

        Assert.Null(await _store.AuthenticateAsync(paired.Token));
        Assert.Empty(await _store.ListAsync());
        Assert.False(await _store.RevokeAsync(paired.Client.Id));
    }

    [Fact]
    public async Task Tokens_AreNotStoredInPlainText()
    {
        var code = await _store.CreatePairingCodeAsync();
        var paired = (await _store.PairAsync(code.Code, "Pixel", "android", null))!.Value;

        var bytes = await File.ReadAllBytesAsync(new AppPaths(_root).EngineDbFile);
        var wal = new AppPaths(_root).EngineDbFile + "-wal";
        var all = File.Exists(wal) ? bytes.Concat(await File.ReadAllBytesAsync(wal)).ToArray() : bytes;
        Assert.DoesNotContain(paired.Token, System.Text.Encoding.UTF8.GetString(all));
    }
}
