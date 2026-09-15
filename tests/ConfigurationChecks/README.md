# Configuration regression checks

Run on Windows with the .NET SDK and VBScript available:

```powershell
dotnet run --project tests/ConfigurationChecks/ConfigurationChecks.csproj -c Release
```

The checks exercise XML generation and shell escaping, disk-size boundaries and error handling using fake WMI data, guarded execution using stubbed disk commands, profile creation/saving/switching/removal, JSON persistence, legacy migration, locked partition styling, cache-choice labels/defaults, and WPF layout rendering. They never execute DiskPart, clear the real application cache, or modify real disks. Temporary scripts and theme previews are written to the isolated folder printed on completion. A few test-owned message dialogs close automatically.

This does not replace validating the generated answer file in a disposable Windows PE VM before installation on hardware.

Folder-media checks use synthetic installer files to verify recognition, real build validation, local staging without ISO mounting, cache invalidation, and preservation of the source folder. An optional folder path after `--` performs a read-only structure and file-count check against an actual backup; it does not run DISM or copy that backup.
