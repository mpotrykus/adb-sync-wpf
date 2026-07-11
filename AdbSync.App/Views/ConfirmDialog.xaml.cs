using System.Windows;

namespace AdbSync.App.Views;

public enum ConfirmDialogResult { Cancel, Confirm, Secondary }

public partial class ConfirmDialog : Window
{
    public ConfirmDialogResult Result { get; private set; } = ConfirmDialogResult.Cancel;

    public ConfirmDialog(string title, string message, string confirmText = "Confirm", string? secondaryText = null)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        if (secondaryText is not null)
        {
            SecondaryButton.Content = secondaryText;
            SecondaryButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Shows a themed confirmation dialog and returns true only if the user clicked the confirm button.</summary>
    public static bool Show(Window owner, string title, string message, string confirmText = "Confirm") =>
        ShowWithResult(owner, title, message, confirmText) == ConfirmDialogResult.Confirm;

    /// <summary>Shows a themed confirmation dialog with an optional third ("secondary") action button, e.g. a
    /// non-destructive way to branch into a different flow instead of just confirming or cancelling.</summary>
    public static ConfirmDialogResult ShowWithResult(
        Window owner, string title, string message, string confirmText = "Confirm", string? secondaryText = null)
    {
        var dialog = new ConfirmDialog(title, message, confirmText, secondaryText) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Result;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Result = ConfirmDialogResult.Confirm;
        DialogResult = true;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        Result = ConfirmDialogResult.Secondary;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = ConfirmDialogResult.Cancel;
        DialogResult = false;
    }
}
