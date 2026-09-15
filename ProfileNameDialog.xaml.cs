using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LaptopQaUsbBuilder;

public partial class ProfileNameDialog : Window
{
    private readonly Func<string, string?> _validateName;
    public string ProfileName { get; private set; } = "";

    private ProfileNameDialog(string title, string initialName, string theme, Func<string, string?> validateName)
    {
        _validateName = validateName;
        InitializeComponent();
        Title = title;
        DialogTitleText.Text = title;
        NameTextBox.Text = initialName;
        ThemeService.Apply(this, theme);
        Loaded += (_, _) => { NameTextBox.Focus(); NameTextBox.SelectAll(); };
    }

    public static string? Show(Window owner, string title, string initialName, string theme, Func<string, string?> validateName)
    {
        var dialog = new ProfileNameDialog(title, initialName, theme, validateName);
        if (owner.IsLoaded) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.ProfileName : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();
        var error = string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)
            ? "Enter a profile name of 1–80 characters."
            : _validateName(name);
        if (error is not null)
        {
            ValidationText.Text = error;
            ValidationText.Visibility = Visibility.Visible;
            NameTextBox.Focus();
            return;
        }
        ProfileName = name;
        DialogResult = true;
    }

    private void NameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ValidationText is not null) ValidationText.Visibility = Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
