using System.Windows;

namespace AdbSync.App.Interop;

/// <summary>
/// Attached property that caps a Window's MaxHeight at a fraction of the current screen's work
/// area, so windows with a fixed or content-driven Height don't get clipped off the bottom on
/// lower-resolution displays. Set explicitly per-window in XAML (see AcrylicEffect.Enabled usage).
/// </summary>
public static class WindowHeightLimit
{
    private const double MaxHeightFraction = 0.9;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WindowHeightLimit), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true)
            return;

        window.MaxHeight = SystemParameters.WorkArea.Height * MaxHeightFraction;
    }
}
