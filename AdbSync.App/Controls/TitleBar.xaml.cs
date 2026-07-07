using System.Windows;
using System.Windows.Controls;

namespace AdbSync.App.Controls;

public partial class TitleBar : UserControl
{
    public static readonly DependencyProperty ShowMinMaxProperty = DependencyProperty.Register(
        nameof(ShowMinMax), typeof(bool), typeof(TitleBar), new PropertyMetadata(true, OnShowMinMaxChanged));

    public bool ShowMinMax
    {
        get => (bool)GetValue(ShowMinMaxProperty);
        set => SetValue(ShowMinMaxProperty, value);
    }

    public TitleBar()
    {
        InitializeComponent();
    }

    private static void OnShowMinMaxChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (TitleBar)d;
        var visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        bar.MinimizeButton.Visibility = visibility;
        bar.MaximizeButton.Visibility = visibility;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)!.WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this)!;
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)!.Close();
}
