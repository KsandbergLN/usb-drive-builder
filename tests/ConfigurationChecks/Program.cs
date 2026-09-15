using LaptopQaUsbBuilder;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class Program
{
    private static int _checks;
    private static readonly string Work = Path.Combine(Path.GetTempPath(), "LaptopQA-config-checks-" + Guid.NewGuid().ToString("N"));
    private static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(Work);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        CheckGeneratedXml();
        CheckProfiles();
        CheckCacheChoiceUi();
        CheckFolderMedia();
        if (args.Length > 0) CheckBackupFolder(args[0]);
        Console.WriteLine($"PASS: {_checks} checks. Test artifacts: {Work}");
        app.Shutdown();
    }

    private static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException(description);
        _checks++;
    }

    private static readonly Type FolderSourceType = typeof(MainWindow).Assembly.GetType("LaptopQaUsbBuilder.WindowsMediaFolderSource")!;
    private static bool IsInstaller(string path) => (bool)FolderSourceType.GetMethod("IsInstaller", Hidden)!.Invoke(null, [path])!;
    private static string[] MediaFiles(string path) => ((IEnumerable<string>)FolderSourceType.GetMethod("EnumerateFiles", Hidden)!.Invoke(null, [path, CancellationToken.None])!).ToArray();

    private static void CheckFolderMedia()
    {
        var root = Path.Combine(Work, "installer-source");
        var required = (string[])FolderSourceType.GetField("RequiredBootFiles", Hidden)!.GetValue(null)!;
        Directory.CreateDirectory(root);
        Check(!IsInstaller(root), "An ordinary support folder must not qualify as Windows media.");
        foreach (var path in required.Append("sources/install.esd").Append("Autounattend.xml"))
        {
            var file = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, path == "Autounattend.xml" ? "<unattend xmlns=\"urn:schemas-microsoft-com:unattend\"/>" : "fixture data");
        }
        Check(IsInstaller(root), "Complete extracted ESD media must be recognized.");
        var moved = Path.Combine(root, "sources", "temporarily-missing.esd");
        File.Move(Path.Combine(root, "sources", "install.esd"), moved);
        Check(!IsInstaller(root), "Boot files alone must not qualify without an installation image.");
        File.Move(moved, Path.Combine(root, "sources", "install.wim"));
        Check(IsInstaller(root), "Extracted WIM media must also be recognized.");
        var partition = new PartitionConfig { Number = 1, Name = "BOOT", SizeText = "20 GB", FileSystem = "NTFS", WindowsMediaFolder = root, IsoEditionIndex = 1 };
        partition.SourceFolders.Add(root); partition.SourceFolders.Add(Work);
        partition.ScriptFiles.Add("fixture.ps1"); partition.DriverFiles.Add("fixture.inf");
        Check(partition.HasWindowsMedia && !partition.HasIso && partition.HasDrivers && partition.HasScripts, "Installer folder must support scripts/drivers without an ISO.");
        Check(partition.AdditionalSourceFolders.SequenceEqual(new[] { Work }), "Raw installer must be excluded from additional copies, avoiding duplicate/unserviced install images.");
        Check(partition.Clone().WindowsMediaFolder == root && partition.Clone().HasScripts, "Cloning must retain the installer selection and scripts.");
        Check(partition.FolderXmlSource == Path.Combine(root, "Autounattend.xml"), "Folder's supplied answer file must remain available to the script workflow.");
        // Exercise the validation method without initializing a live main window or reading user preferences.
        var main = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        var partitions = new List<PartitionConfig> { partition, new() { Number = 2, Name = "DATA", SizeText = "*", FileSystem = "exFAT" } };
        typeof(MainWindow).GetField("_partitions", Hidden)!.SetValue(main, partitions);
        bool ValidLayout() => (bool)typeof(MainWindow).GetMethod("ValidatePartitionLayout", Hidden)!.Invoke(main, new object?[] { null })!;
        Check(ValidLayout(), "Real build validation must accept scripts and drivers with installer-folder media.");
        partition.WindowsMediaFolder = null;
        Check(!ValidLayout(), "Scripts/drivers with an ordinary folder must still be rejected.");
        partition.WindowsMediaFolder = root;
        partition.IsoSource = "another.iso";
        Check(!ValidLayout(), "Build validation must reject mixed ISO and folder sources.");
        partition.IsoSource = null;
        partitions[1].WindowsMediaFolder = root;
        Check(!ValidLayout(), "Two installer partitions must be rejected.");
        partitions[1].WindowsMediaFolder = null;
        var content = new PartitionContentDialog(partition, "Light");
        Check(((CheckBox)content.FindName("GenerateAutounattendCheckBox")).Visibility == Visibility.Visible, "Folder media must expose answer-file generation.");
        Check(!((CheckBox)content.FindName("GenerateAutounattendCheckBox")).IsEnabled, "Supplied XML must retain precedence.");
        content.Close();
        var mountCalled = false;
        var preparer = new WindowsMediaPreparer(_ => { }, (_, _) => { }, _ => { }, _ => { },
            _ => { mountCalled = true; throw new InvalidOperationException("Folder must not be mounted as an ISO."); },
            _ => throw new InvalidOperationException("Folder must not be dismounted."), CancellationToken.None);
        var hashTask = (Task<string>)Call(preparer, "HashMediaFolderAsync", root)!;
        var firstHash = hashTask.GetAwaiter().GetResult();
        var staged = Path.Combine(Work, "staged-installer");
        ((Task)Call(preparer, "CopyIsoToStagingAsync", root, staged)!).GetAwaiter().GetResult();
        Check(!mountCalled && IsInstaller(staged), "Folder staging must copy boot files and image without an ISO mount.");
        Check(MediaFiles(root).All(f => File.ReadAllBytes(f).SequenceEqual(File.ReadAllBytes(Path.Combine(staged, Path.GetRelativePath(root, f))))), "Staged installer must match every source file.");
        File.WriteAllText(Path.Combine(staged, "sources", "install.wim"), "serviced copy");
        Check(firstHash == ((Task<string>)Call(preparer, "HashMediaFolderAsync", root)!).GetAwaiter().GetResult(), "Changes to staging must not affect original installer.");
        var sourceImage = Path.Combine(root, "sources", "install.wim");
        var stamp = File.GetLastWriteTimeUtc(sourceImage);
        File.WriteAllText(sourceImage, "changed data"); File.SetLastWriteTimeUtc(sourceImage, stamp);
        Check(firstHash != ((Task<string>)Call(preparer, "HashMediaFolderAsync", root)!).GetAwaiter().GetResult(), "Cache hash must detect same-size source content changes, even with identical timestamps.");
        partition.ClearIsoSelection();
        Check(!partition.HasWindowsMedia && partition.WindowsMediaFolder is null, "Clearing media must clear the folder selection too.");
    }

    private static void CheckBackupFolder(string root)
    {
        Check(IsInstaller(root), "Supplied backup folder must contain all supported installer files.");
        var files = MediaFiles(root);
        Check(files.Any(f => Path.GetRelativePath(root, f).Equals("sources\\install.esd", StringComparison.OrdinalIgnoreCase)), "OneDrive traversal must include the actual installation image.");
        Console.WriteLine($"Read-only backup check: {files.Length} files, {files.Sum(f => new FileInfo(f).Length):N0} bytes. No source files changed.");
    }

    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, Hidden)!.Invoke(target, args);

    private static string Generate(WindowsSetupConfig setup) => (string)typeof(MainWindow)
        .GetMethod("BuildGeneratedAutounattend", Hidden)!.Invoke(null, [setup])!;

    private static void CheckGeneratedXml()
    {
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        var plain = Generate(new WindowsSetupConfig());
        Check(!plain.Contains("check-target-disk"), "Guard must be opt-in.");
        Check((int)XDocument.Parse(Generate(new WindowsSetupConfig { TargetDisk = 2 })).Descendants(ns + "DiskID").Single() == 2, "Nonzero targets remain available when the guard is off.");
        try
        {
            Generate(new WindowsSetupConfig { RequireEmptyDisk200Gb = true, TargetDisk = 2 });
            throw new InvalidOperationException("Guard must reject a target other than Disk 0.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        {
            Check(true, "Nonzero guarded target rejected.");
        }
        foreach (var prompt in new[] { false, true })
        {
            var document = XDocument.Parse(Generate(new WindowsSetupConfig { RequireEmptyDisk200Gb = true, TargetDisk = 0, PromptBeforeInstall = prompt }));
            var commands = document.Descendants(ns + "RunSynchronousCommand").ToArray();
            Check(commands.Select(c => (int)c.Element(ns + "Order")!).SequenceEqual(Enumerable.Range(1, commands.Length)), "Command orders must be contiguous.");
            Check((int)document.Descendants(ns + "DiskID").Single() == 0, "Guarded ImageInstall must use Disk 0.");
            Check(commands.Last().Element(ns + "Path")!.Value == "cmd.exe /c X:\\partition-checked.cmd", "Guarded runner must be the only final disk execution command.");
            Check(!commands.Any(c => c.Element(ns + "Path")!.Value.StartsWith("cmd.exe /c diskpart.exe")), "No unguarded DiskPart path when enabled.");
            Check(document.ToString().Contains("Index = 0"), "Guard must use Disk 0.");
            Check(document.ToString().Contains("SELECT DISK=0"), "DiskPart must use Disk 0.");
            // Execute only ECHO commands in a private temp directory. Never run the disk commands.
            foreach (var command in commands.Select(c => c.Element(ns + "Path")!.Value)
                .Where(c => c.StartsWith("cmd.exe /c echo ")))
            {
                var lastPath = command.LastIndexOf("X:\\", StringComparison.Ordinal);
                var redirected = command[..lastPath] + "\"" + Path.Combine(Work, command[(lastPath + 3)..]) + "\"";
                Check(Run("cmd.exe", "/d /c " + redirected[11..]).Code == 0, "Script echo failed.");
            }
            var guardType = typeof(MainWindow).Assembly.GetType("LaptopQaUsbBuilder.WindowsSetupDiskGuard")!;
            var expected = (string[])guardType.GetMethod("ScriptLines", Hidden)!.Invoke(null, [])!;
            Check(File.ReadAllLines(Path.Combine(Work, "check-target-disk.vbs")).Select(s => s.TrimEnd()).SequenceEqual(expected), "CMD/XML escaping must preserve VBScript exactly.");
            var runner = (string[])guardType.GetProperty("PartitionRunnerLines", Hidden)!.GetValue(null)!;
            Check(File.ReadAllLines(Path.Combine(Work, "partition-checked.cmd")).Select(s => s.TrimEnd()).SequenceEqual(runner), "CMD/XML escaping must preserve guarded runner exactly.");
        }
        var script = File.ReadAllText(Path.Combine(Work, "check-target-disk.vbs"));
        Check(!script.Contains("drive.Partitions"), "The target disk may already have partitions; partition count must not be checked.");
        var wmiLine = script.Split('\n').Single(s => s.Contains("Set wmi = GetObject")).TrimEnd('\r');
        var scenarios = new[]
        {
            (199.999, "0", 1, 1), (200.0, "0", 1, 0), (4000.0, "0", 1, 0), (4000.001, "0", 1, 1),
            (256.0, "1", 1, 0), (256.0, "Null", 1, 0), (256.0, "0", 0, 1), (256.0, "0", 2, 1)
        };
        foreach (var (size, partitions, count, code) in scenarios)
        {
            var bytes = (size * 1024 * 1024 * 1024).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            var mock = $"\r\nClass FakeDisk\r\nPublic Size, Partitions\r\nEnd Class\r\nClass FakeWmi\r\nPublic Function ExecQuery(query)\r\nDim result, disk, i\r\nSet result = CreateObject(\"Scripting.Dictionary\")\r\nFor i = 1 To {count}\r\nSet disk = New FakeDisk\r\ndisk.Size = \"{bytes}\"\r\ndisk.Partitions = {partitions}\r\nresult.Add i, disk\r\nNext\r\nExecQuery = result.Items\r\nEnd Function\r\nEnd Class\r\n";
            var path = Path.Combine(Work, "mock-check.vbs");
            File.WriteAllText(path, script.Replace(wmiLine, "  Set wmi = New FakeWmi").Replace("Set drives = wmi.ExecQuery", "drives = wmi.ExecQuery") + mock, Encoding.ASCII);
            var result = Run("cscript.exe", $"//nologo //E:vbscript \"{path}\"");
            Check(result.Code == code, $"Guard scenario {size}/{partitions}/{count}: {result.Output}");
        }
        var unavailable = Path.Combine(Work, "unavailable-wmi.vbs");
        File.WriteAllText(unavailable, script.Replace(wmiLine, "  Err.Raise 5"), Encoding.ASCII);
        Check(Run("cscript.exe", $"//nologo //E:vbscript \"{unavailable}\"").Code == 1, "WMI failure must reject.");
        foreach (var exitCode in new[] { 0, 1, 9009, -1 })
        {
            // Stub all external disk/script operations; exit the blocking loop for the test.
            var marker = Path.Combine(Work, $"diskpart-stub-{exitCode}.txt");
            var runner = File.ReadAllText(Path.Combine(Work, "partition-checked.cmd"));
            var lines = runner.Split('\n').Select(l => l.TrimEnd());
            var stub = lines.Select(l => l.StartsWith("cscript.exe") ? $"cmd.exe /c exit {exitCode}" :
                l.StartsWith("diskpart.exe") ? $"echo reached > \"{marker}\"" :
                l.StartsWith("type X:") ? "echo mock rejection" : l == "pause" ? "exit /b 77" : l);
            var path = Path.Combine(Work, "stub-runner.cmd");
            File.WriteAllLines(path, stub, Encoding.ASCII);
            var result = Run("cmd.exe", $"/d /c \"{path}\"");
            Check(File.Exists(marker) == (exitCode == 0), "Partition runner must never reach DiskPart after rejection/missing host.");
            Check(exitCode == 0 || result.Code == 77, "Failure must enter blocking branch.");
        }
    }

    private static (int Code, string Output) Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000)) { process.Kill(true); throw new TimeoutException(file); }
        return (process.ExitCode, output);
    }

    private static void AnswerNextDialog(MessageBoxResult result)
    {
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<ThemedMessageDialog>().Single();
            Call(dialog, "CloseWithResult", result);
        }));
    }

    private static void AnswerNameDialog(string? name, bool expectInvalid = false)
    {
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<ProfileNameDialog>().Single();
            if (name is null)
            {
                Call(dialog, "Cancel_Click", null, new RoutedEventArgs());
                return;
            }
            ((TextBox)dialog.FindName("NameTextBox")).Text = name;
            // Capture the actual naming popup for visual review.
            var surface = (FrameworkElement)dialog.Content;
            surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(Work, "profile-name-popup.png"))) encoder.Save(output);
            Call(dialog, "Save_Click", null, new RoutedEventArgs());
            if (expectInvalid)
            {
                Check(dialog.IsVisible && ((TextBlock)dialog.FindName("ValidationText")).Visibility == Visibility.Visible, "Invalid names must keep the popup open and show an error.");
                Call(dialog, "Cancel_Click", null, new RoutedEventArgs());
            }
        }));
    }

    private static void CheckProfiles()
    {
        var original = new ConfigurationProfile { Name = "Standard", Partitions = PartitionConfig.CreateDefaults(), WindowsSetup = new() { TargetDisk = 2 } };
        var input = new[] { original };
        var window = new ConfigWindow(original.Partitions, "en-US", "Light", false, "ESD", original.WindowsSetup, input, original.Id);
        T Control<T>(string name) => (T)window.FindName(name);
        Check(window.FindName("ProfileNameTextBox") is null, "Profile name must not be a static config field.");
        var guard = Control<CheckBox>("RequireEmptyDiskCheckBox");
        var target = Control<TextBox>("TargetDiskTextBox");
        var picker = Control<ComboBox>("ProfilePicker");
        guard.IsChecked = true; target.Text = "0";
        AnswerNameDialog("Blank laptops");
        Check((bool)Call(window, "SaveProfile", true)!, "Save as new failed.");
        Check(window.Profiles.Count == 2 && window.SelectedProfileId != original.Id, "Save as new must create a distinct profile.");
        Check(Control<TextBlock>("ProfileSaveStatusText").Visibility == Visibility.Visible && Control<TextBlock>("ProfileSaveStatusText").Text.Contains("Blank laptops"), "Save must visibly acknowledge the named profile.");
        AnswerNameDialog(null);
        Check(!(bool)Call(window, "SaveProfile", true)! && window.Profiles.Count == 2, "Cancelling Save as must not create a profile.");
        Check(!original.WindowsSetup.RequireEmptyDisk200Gb && original.Name == "Standard", "Dialog must not mutate original preferences before Save.");
        picker.SelectedIndex = 0;
        Check(guard.IsChecked == false && target.Text == "2", "Profile selection must restore setup settings.");
        picker.SelectedIndex = 1;
        Check(guard.IsChecked == true && target.Text == "0", "Profile settings must remain independent, including edits back to Disk 0.");
        target.Text = "0";
        Check((bool)Call(window, "SaveProfile", false)!, "Profile update failed.");
        Check(window.Profiles[1].WindowsSetup.TargetDisk == 0 && window.Profiles[1].Name == "Blank laptops", "Save must preserve the profile name and updated target.");
        Check(window.FindName("RenameProfileButton") is null, "Rename must be removed from Config.");
        Check(Control<Grid>("DefaultPartitionArea").Opacity < 1 && Control<DataGrid>("PartitionGrid").IsReadOnly, "Locked profile partitions must be visibly dimmed and read-only.");
        Call(window, "DefaultsLock_Click", null, new RoutedEventArgs());
        Check(Control<Grid>("DefaultPartitionArea").Opacity == 1 && !Control<DataGrid>("PartitionGrid").IsReadOnly, "Unlock must restore opacity and editing.");
        Call(window, "DefaultsLock_Click", null, new RoutedEventArgs());
        AnswerNameDialog(" standard ", true);
        Check(!(bool)Call(window, "SaveProfile", true)!, "Duplicate names must be rejected case-insensitively.");
        AnswerNameDialog("", true);
        Check(!(bool)Call(window, "SaveProfile", true)!, "Blank names must be rejected.");
        Check((bool)Call(window, "SaveProfile", false)!, "Saving an existing profile must not ask for its name.");
        guard.IsChecked = false; AnswerNextDialog(MessageBoxResult.Cancel); picker.SelectedIndex = 0;
        Check(picker.SelectedIndex == 1 && guard.IsChecked == false, "Cancelled switch must preserve edits.");
        AnswerNextDialog(MessageBoxResult.No); picker.SelectedIndex = 0;
        picker.SelectedIndex = 1;
        Check(guard.IsChecked == true, "Discarded edits must not change saved profile.");
        guard.IsChecked = false; AnswerNextDialog(MessageBoxResult.Yes); picker.SelectedIndex = 0;
        Check(window.Profiles[1].WindowsSetup.RequireEmptyDisk200Gb == false, "Save-on-switch failed.");
        var roundTrip = JsonSerializer.Deserialize<AppPreferences>(JsonSerializer.Serialize(new AppPreferences { Profiles = window.Profiles, SelectedProfileId = window.SelectedProfileId }))!;
        Check(roundTrip.Profiles!.Count == 2 && roundTrip.SelectedProfileId == original.Id, "Profiles/selection must round trip.");
        var clone = window.Profiles[0]; clone.Partitions[0].Name = "Changed";
        Check(window.Profiles[0].Partitions[0].Name != "Changed", "Profile clones must isolate partition edits.");
        Render(window, "Light"); Render(window, "Dark"); Render(window, "AMOLED");
        AnswerNextDialog(MessageBoxResult.Yes); Call(window, "RemoveProfile_Click", null, new RoutedEventArgs());
        Check(window.Profiles.Count == 1, "Remove selected profile failed.");
        AnswerNextDialog(MessageBoxResult.Yes); Call(window, "RemoveProfile_Click", null, new RoutedEventArgs());
        Check(window.Profiles.Count == 0 && window.SelectedProfileId is null, "Removing last profile must leave an unnamed configuration.");
        Check(JsonSerializer.Deserialize<AppPreferences>(JsonSerializer.Serialize(new AppPreferences { Profiles = [] }))!.Profiles!.Count == 0, "Empty list must survive restart without legacy migration.");
        Check(JsonSerializer.Deserialize<AppPreferences>("{}")!.Profiles is null, "Legacy preferences must be distinguishable for migration.");
        var legacyLayout = PartitionConfig.CreateDefaults(); legacyLayout[0].Name = "CUSTOM";
        var legacy = new AppPreferences { ImageCompression = "FAST", ForceUnsignedDrivers = true, WindowsSetup = new() { TargetDisk = 4 } };
        legacy.MigrateLegacyProfile(legacyLayout);
        Check(legacy.Profiles!.Single().Name == "My configuration" && legacy.SelectedProfileId == legacy.Profiles[0].Id, "Migration must create and select the named profile.");
        Check(legacy.Profiles[0].Partitions[0].Name == "CUSTOM" && legacy.Profiles[0].ImageCompression == "FAST" && legacy.Profiles[0].ForceUnsignedDrivers && legacy.Profiles[0].WindowsSetup.TargetDisk == 4, "Migration must preserve all existing config settings.");
        legacyLayout[0].Name = "ALTERED"; legacy.WindowsSetup.TargetDisk = 9;
        Check(legacy.Profiles[0].Partitions[0].Name == "CUSTOM" && legacy.Profiles[0].WindowsSetup.TargetDisk == 4, "Migrated settings must be independent copies.");
        var empty = new AppPreferences { Profiles = [] }; empty.MigrateLegacyProfile(legacyLayout);
        Check(empty.Profiles!.Count == 0, "Removing every profile must not trigger remigration.");
        AnswerNameDialog("Replacement");
        Check((bool)Call(window, "SaveProfile", false)!, "Creating a profile after deleting all failed.");
        window.Close();
    }

    private static void CheckCacheChoiceUi()
    {
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<ThemedMessageDialog>().Single();
            var buttons = ((StackPanel)dialog.FindName("ButtonPanel")).Children.OfType<Button>().ToArray();
            Check(buttons.Single(b => Equals(b.Tag, MessageBoxResult.No)).Content.Equals("Keep cache"), "Completion must name the Keep cache choice.");
            Check(buttons.Single(b => Equals(b.Tag, MessageBoxResult.Yes)).Content.Equals("Clear cache"), "Completion must name the Clear cache choice.");
            Check(buttons.Single(b => Equals(b.Tag, MessageBoxResult.No)).IsDefault, "Keeping the cache must be the default.");
            var surface = (FrameworkElement)dialog.Content;
            surface.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(Path.Combine(Work, "cache-choice.png"))) encoder.Save(output);
            Call(dialog, "Close_Click", null, new RoutedEventArgs());
        }));
        var result = ThemedMessageDialog.Show(null, "Queue finished.\n\nKeeping the cache makes the next matching build faster. Clearing it frees disk space but requires preparing Windows media and drivers again.",
            "USB queue complete", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No,
            yesButtonText: "Clear cache", noButtonText: "Keep cache");
        Check(result == MessageBoxResult.No, "Closing the completion dialog must keep the cache.");
    }

    private static void Render(ConfigWindow window, string theme)
    {
        ThemeService.Apply(window, theme);
        var surface = (FrameworkElement)window.Content;
        surface.Measure(new Size(940, 840)); surface.Arrange(new Rect(0, 0, 940, 840)); surface.UpdateLayout();
        Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        surface.UpdateLayout();
        var grid = (DataGrid)window.FindName("PartitionGrid");
        Check(grid.Columns.All(c => c.ActualWidth >= 38), "Partition columns must remain readable.");
        var image = new RenderTargetBitmap(940, 840, 96, 96, PixelFormats.Pbgra32);
        image.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(Work, $"config-{theme}.png")); encoder.Save(output);
        foreach (var controlName in new[] { "ProfilePicker", "RemoveProfileButton", "RequireEmptyDiskCheckBox", "SaveButton" })
        {
            var control = (FrameworkElement)window.FindName(controlName);
            var bounds = control.TransformToAncestor(surface).TransformBounds(new Rect(control.RenderSize));
            Check(bounds.Width > 0 && bounds.Height > 0 && bounds.Top >= 0 && bounds.Bottom <= 840 && bounds.Right <= 940, $"{controlName} must fit the config layout.");
        }
    }
}
