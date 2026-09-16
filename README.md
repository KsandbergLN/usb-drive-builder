![USB Drive Builder application](docs/images/laptop-qa-usb-drive-builder.png)

# USB Drive Builder

Current release: **v2.0.110**. [Download the latest Windows x64 executable](https://github.com/KsandbergLN/usb-drive-builder/releases/latest) · [Release notes](docs/RELEASE_NOTES.md).

USB Drive Builder is a Windows desktop tool for IT technicians who create repeatable laptop support USB media. It validates approved USB targets, creates configurable MBR partitions, prepares Windows installer media, copies support content, and verifies each drive in a sequential queue.

## Start here

- [Quick User Guide](docs/QUICK_USER_GUIDE.md) — daily operating instructions.
- [Technician Handoff](docs/TECHNICIAN_HANDOFF.md) — requirements, troubleshooting, logs, and escalation.
- [Configuration and profiles](docs/CONFIGURATION.md) — layouts, generated Windows Setup, profiles, themes, and language.
- [Content and Windows media](docs/CONTENT_AND_MEDIA.md) — files, folders, ISO/folder installers, drivers, scripts, and answer files.
- [Build workflow and safety](docs/BUILD_WORKFLOW.md) — queue behavior, progress, cache, destructive operations, and recovery.

## Key capabilities

- Configure 1–4 MBR partitions using FAT32, NTFS, or exFAT, including one remaining-space partition.
- Prepare supported 64-bit UEFI Windows media from an ISO or complete extracted installer folder.
- Add drivers, scripts, XML, files, and folders with preflight validation and offline driver injection.
- Queue multiple USB drives while retaining per-drive verification and logs.
- Save named configurations and use Light, Dark, or AMOLED themes with 12 languages.

## Feature developers

See [Canonical Source](CANONICAL-SOURCE.md), [Developer Handoff](DEVELOPER-HANDOFF.md), and the [macOS workflow](macos/README.md). The project targets .NET 8/WPF on Windows; generated files and runtime caches have explicit ownership boundaries in the developer docs.

## Build locally

```powershell
dotnet build .\LaptopQaUsbBuilder.csproj -c Release
dotnet run --project tests/ConfigurationChecks/ConfigurationChecks.csproj -c Release
cmd /c publish.cmd
```

The self-contained executable is written to `dist/`. USB erasure, DiskPart, DISM, Windows PE, and WPF runtime checks require Windows and must be validated only on disposable test hardware or a VM.
