using System.Text.Json;
using System.IO;
using System.Diagnostics.CodeAnalysis;

namespace LaptopQaUsbBuilder;

public sealed class AppPreferences
{
    public string Language { get; set; } = "en-US";
    public string Theme { get; set; } = "Light";
    public bool ForceUnsignedDrivers { get; set; }
    public string ImageCompression { get; set; } = WindowsImageCompression.Esd;
    public WindowsSetupConfig WindowsSetup { get; set; } = new();
    // Null distinguishes legacy preferences from an intentionally empty profile list.
    public List<ConfigurationProfile>? Profiles { get; set; }
    public string? SelectedProfileId { get; set; }

    [MemberNotNull(nameof(Profiles))]
    public void MigrateLegacyProfile(IEnumerable<PartitionConfig> partitions)
    {
        if (Profiles is not null) return;
        var migrated = new ConfigurationProfile
        {
            Name = "My configuration", Partitions = partitions.ToList(),
            ForceUnsignedDrivers = ForceUnsignedDrivers, ImageCompression = ImageCompression,
            WindowsSetup = WindowsSetup
        }.Clone();
        Profiles = [migrated];
        SelectedProfileId = migrated.Id;
    }
}

public sealed class ConfigurationProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My configuration";
    public List<PartitionConfig> Partitions { get; set; } = [];
    public bool ForceUnsignedDrivers { get; set; }
    public string ImageCompression { get; set; } = WindowsImageCompression.Esd;
    public WindowsSetupConfig WindowsSetup { get; set; } = new();

    public ConfigurationProfile Clone() => new()
    {
        Id = Id, Name = Name,
        Partitions = Partitions.Select(p => new PartitionConfig
        {
            Number = p.Number, Name = p.Name, SizeText = p.SizeText, FileSystem = p.FileSystem
        }).ToList(),
        ForceUnsignedDrivers = ForceUnsignedDrivers, ImageCompression = ImageCompression,
        WindowsSetup = WindowsSetup.Clone()
    };

    public override string ToString() => Name;
}

public static class WindowsImageCompression
{
    public const string Fast = "FAST";
    public const string Max = "MAX";
    public const string Esd = "ESD";
    public static readonly string[] Values = [Fast, Max, Esd];

    public static string Normalize(string? value) =>
        Values.FirstOrDefault(item => item.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? Esd;
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
