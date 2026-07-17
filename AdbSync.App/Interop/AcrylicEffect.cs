using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AdbSync.App.Interop;

/// <summary>
/// Attached property that turns on Windows acrylic blur-behind and rounded corners for a
/// borderless (AllowsTransparency + WindowStyle=None) Window. The window is already truly
/// transparent at that point, so real per-pixel alpha blending happens regardless; this just
/// layers a gaussian blur behind it via an undocumented user32 API. Failures (older Windows
/// builds, remote sessions, etc.) are swallowed and the window still shows plain translucency.
/// </summary>
public static class AcrylicEffect
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(AcrylicEffect), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true)
            return;

        if (PresentationSource.FromVisual(window) is not null)
            Apply(window);
        else
            window.SourceInitialized += OnSourceInitialized;
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        var window = (Window)sender!;
        window.SourceInitialized -= OnSourceInitialized;
        Apply(window);
    }

    private static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            EnableAcrylicBlur(hwnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AcrylicEffect: failed to enable acrylic blur. {ex}");
        }

        try
        {
            var preference = (int)DwmWindowCornerPreference.Round;
            DwmSetWindowAttribute(hwnd, DwmWindowAttribute.WindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AcrylicEffect: failed to set window corner preference. {ex}");
        }

        try
        {
            var noBorder = DwmwaColorNone;
            DwmSetWindowAttribute(hwnd, DwmWindowAttribute.BorderColor, ref noBorder, sizeof(int));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AcrylicEffect: failed to clear window border color. {ex}");
        }
    }

    private static void EnableAcrylicBlur(IntPtr hwnd)
    {
        const byte alpha = 0x60;
        const byte r = 0x00;
        const byte g = 0x00;
        const byte b = 0x00;
        uint tint = unchecked((uint)(alpha << 24 | b << 16 | g << 8 | r));

        var accent = new AccentPolicy
        {
            AccentState = AccentState.EnableAcrylicBlurBehind,
            GradientColor = unchecked((int)tint),
        };

        var accentPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.AccentPolicy,
                Data = accentPtr,
                SizeOfData = Marshal.SizeOf<AccentPolicy>(),
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    private enum AccentState
    {
        Disabled = 0,
        EnableAcrylicBlurBehind = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    private enum WindowCompositionAttribute
    {
        AccentPolicy = 19,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private enum DwmWindowAttribute
    {
        WindowCornerPreference = 33,
        BorderColor = 34,
    }

    private enum DwmWindowCornerPreference
    {
        Round = 2,
    }

    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, DwmWindowAttribute attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}
