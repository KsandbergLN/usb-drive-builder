using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LaptopQaUsbBuilder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        try
        {
            Localization.ValidateAll();
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            WriteCrashAndShow(ex);
            Shutdown(1);
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashAndShow(e.Exception);
        e.Handled = true;
        Current.Shutdown(1);
    }

    private static void WriteCrashAndShow(Exception exception)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaptopQAUsbBuilder", "Logs");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.WriteAllText(path, LogSanitizer.SanitizeException(exception));
        MessageBox.Show($"The application encountered an error.\n\nA crash log was saved to:\n{path}\n\n{exception.Message}",
            "USB Drive Builder", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
