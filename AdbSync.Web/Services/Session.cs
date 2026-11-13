using Microsoft.JSInterop;

namespace AdbSync.Web.Services;

/// <summary>The browser's client token, kept in localStorage so a reload stays signed in.</summary>
public sealed class Session(IJSRuntime js)
{
    private const string TokenKey = "adbsync.token";

    public string? Token { get; private set; }
    public bool Loaded { get; private set; }
    public bool SignedIn => Token is not null;

    public event Action? Changed;

    public async Task LoadAsync()
    {
        if (Loaded)
            return;
        try
        {
            Token = await js.InvokeAsync<string?>("localStorage.getItem", TokenKey);
        }
        catch (JSException)
        {
            Token = null;
        }
        Loaded = true;
    }

    public async Task SignInAsync(string token)
    {
        Token = token;
        await TryStorageAsync("localStorage.setItem", TokenKey, token);
        Changed?.Invoke();
    }

    public async Task SignOutAsync()
    {
        if (Token is null)
            return;
        Token = null;
        await TryStorageAsync("localStorage.removeItem", TokenKey);
        Changed?.Invoke();
    }

    private async Task TryStorageAsync(string fn, params object[] args)
    {
        try
        {
            await js.InvokeVoidAsync(fn, args);
        }
        catch (JSException)
        {
        }
    }
}
