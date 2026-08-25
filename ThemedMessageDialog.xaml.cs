using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace LaptopQaUsbBuilder;

public partial class ThemedMessageDialog : Window
{
    private readonly MessageBoxResult _defaultResult;
    private MessageBoxResult _result;

    private ThemedMessageDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult defaultResult, string theme)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        _defaultResult = NormalizeDefault(buttons, defaultResult);
        _result = _defaultResult;
        ConfigureIcon(image);
        ConfigureButtons(buttons);
        ThemeService.Apply(this, theme);
        Loaded += (_, _) =>
        {
            ThemeService.Apply(this, theme);
            FindButton(_defaultResult)?.Focus();
        };
    }

    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                  ?? Application.Current?.MainWindow;
        var dialog = new ThemedMessageDialog(message, title, buttons, image, defaultResult, ResolveTheme(owner));
        if (owner is not null && owner.IsLoaded)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._result;
    }

    private void ConfigureIcon(MessageBoxImage image)
    {
        if (image is MessageBoxImage.Warning or MessageBoxImage.Error)
        {
            IconBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "WarningBackground");
            IconBadge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "WarningBorder");
            IconText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarningText");
            IconText.Text = image == MessageBoxImage.Error ? "×" : "!";
        }
        else if (image == MessageBoxImage.Question)
        {
            IconBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "ThemeSelection");
            IconBadge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "ReadyBorder");
            IconText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeText");
            IconText.Text = "?";
        }
        else IconText.Text = "i";
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("OK", MessageBoxResult.OK, "#16844F");
                break;
            case MessageBoxButton.OKCancel:
                AddButton("Cancel", MessageBoxResult.Cancel, "#687F87");
                AddButton("OK", MessageBoxResult.OK, "#16844F");
                break;
            case MessageBoxButton.YesNo:
                AddButton("No", MessageBoxResult.No, "#687F87");
                AddButton("Yes", MessageBoxResult.Yes, "#16844F");
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("Cancel", MessageBoxResult.Cancel, "#687F87");
                AddButton("No", MessageBoxResult.No, "#687F87");
                AddButton("Yes", MessageBoxResult.Yes, "#16844F");
                break;
        }
    }

    private void AddButton(string text, MessageBoxResult result, string background)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = text,
            Tag = result,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(background)),
            Margin = new Thickness(ButtonPanel.Children.Count == 0 ? 0 : 8, 0, 0, 0),
            IsDefault = result == _defaultResult
        };
        button.Click += (_, _) => CloseWithResult(result);
        ButtonPanel.Children.Add(button);
    }

    private System.Windows.Controls.Button? FindButton(MessageBoxResult result) =>
        ButtonPanel.Children.OfType<System.Windows.Controls.Button>().FirstOrDefault(button => Equals(button.Tag, result));

    private void CloseWithResult(MessageBoxResult result)
    {
        _result = result;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseWithResult(_defaultResult);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseWithResult(_defaultResult);
        e.Handled = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private static MessageBoxResult NormalizeDefault(MessageBoxButton buttons, MessageBoxResult requested) => buttons switch
    {
        MessageBoxButton.OK => MessageBoxResult.OK,
        MessageBoxButton.OKCancel when requested is MessageBoxResult.OK or MessageBoxResult.Cancel => requested,
        MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
        MessageBoxButton.YesNo when requested is MessageBoxResult.Yes or MessageBoxResult.No => requested,
        MessageBoxButton.YesNo => MessageBoxResult.No,
        MessageBoxButton.YesNoCancel when requested is MessageBoxResult.Yes or MessageBoxResult.No or MessageBoxResult.Cancel => requested,
        MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
        _ => MessageBoxResult.OK
    };

    private static string ResolveTheme(Window? owner)
    {
        if (owner is MainWindow main) return main.CurrentTheme;
        if (owner is ConfigWindow config) return config.SelectedTheme;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LaptopQAUsbBuilder", "preferences.json");
            if (File.Exists(path))
                return ThemeService.Normalize(JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path))?.Theme);
        }
        catch { }
        return "Light";
    }
}

public static class MessageBox
{
    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        Show(messageBoxText, caption, button, icon, MessageBoxResult.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button,
        MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        try
        {
            return ThemedMessageDialog.Show(null, messageBoxText, caption, button, icon, defaultResult);
        }
        catch
        {
            return System.Windows.MessageBox.Show(messageBoxText, caption, button, icon, defaultResult);
        }
    }
}
