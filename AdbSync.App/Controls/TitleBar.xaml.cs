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

    public static readonly DependencyProperty ShowCloseProperty = DependencyProperty.Register(
        nameof(ShowClose), typeof(bool), typeof(TitleBar), new PropertyMetadata(true, OnShowCloseChanged));

    public bool ShowClose
    {
        get => (bool)GetValue(ShowCloseProperty);
        set => SetValue(ShowCloseProperty, value);
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

    private static void OnShowCloseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (TitleBar)d;
        bar.CloseButton.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)!.WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this)!;
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)!.Close();
}
