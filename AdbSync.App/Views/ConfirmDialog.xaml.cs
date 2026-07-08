using System.Windows;

namespace AdbSync.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText = "Confirm")
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    /// <summary>Shows a themed confirmation dialog and returns true only if the user clicked the confirm button.</summary>
    public static bool Show(Window owner, string title, string message, string confirmText = "Confirm") =>
        new ConfirmDialog(title, message, confirmText) { Owner = owner }.ShowDialog() == true;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
