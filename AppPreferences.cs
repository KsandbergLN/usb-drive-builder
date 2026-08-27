namespace LaptopQaUsbBuilder;

public sealed class AppPreferences
{
    public string Language { get; set; } = "en-US";
    public string Theme { get; set; } = "Light";
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
