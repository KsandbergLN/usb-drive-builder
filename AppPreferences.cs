using System.Text.Json;
using System.IO;

namespace LaptopQaUsbBuilder;

public sealed class AppPreferences
{
    public string Language { get; set; } = "en-US";
    public string Theme { get; set; } = "Light";
    public string CacheRoot { get; set; } = @"C:\Cache";
    public bool ForceUnsignedDrivers { get; set; }
    public WindowsSetupConfig WindowsSetup { get; set; } = new();
}

public sealed record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

public sealed record ThemeOption(string Key, string Name)
{
    public override string ToString() => Name;
}

public static class PickerLocationStore
{
    private static readonly object Sync = new();
    private static readonly string Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaptopQAUsbBuilder", "picker-locations.json");
    private static Dictionary<string, string>? _locations;

    public static string? Get(string key)
    {
        lock (Sync)
        {
            EnsureLoaded();
            return _locations!.TryGetValue(key, out var value) && Directory.Exists(value) ? value : null;
        }
    }

    public static void Set(string key, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        lock (Sync)
        {
            EnsureLoaded(); _locations![key] = folder;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(_locations, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void EnsureLoaded()
    {
        if (_locations is not null) return;
        try { _locations = File.Exists(Path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path)) ?? [] : []; }
        catch { _locations = []; }
    }
}
