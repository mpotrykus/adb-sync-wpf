using Microsoft.JSInterop;

namespace AdbSync.Web.Services;

public sealed class ThemeState(IJSRuntime js)
{
    private const string ThemeKey = "adbsync.theme";

    public bool Dark { get; private set; } = true;

    public event Action? Changed;

    public async Task LoadAsync()
    {
        try
        {
            Dark = await js.InvokeAsync<string?>("localStorage.getItem", ThemeKey) != "light";
        }
        catch (JSException)
        {
        }
        await ApplyGlassAsync();
        Changed?.Invoke();
    }

    public async Task SetDarkAsync(bool dark)
    {
        Dark = dark;
        await ApplyGlassAsync();
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", ThemeKey, dark ? "dark" : "light");
        }
        catch (JSException)
        {
        }
        Changed?.Invoke();
    }

    private async Task ApplyGlassAsync()
    {
        try
        {
            await js.InvokeVoidAsync("document.body.classList.toggle", "glass", Dark);
        }
        catch (JSException)
        {
        }
    }
}
