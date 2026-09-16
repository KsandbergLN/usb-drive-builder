# macOS developer workflow

USB Drive Builder is a Windows-only WPF application. macOS is a source-development and documentation environment, not a supported runtime or packaging target.

## What works on macOS

- Edit C# and XAML, review documentation, and prepare pull requests.
- Run repository-only text checks and inspect generated XML as plain files.
- Use a Windows 10/11 VM or a Windows CI runner for the .NET WPF build, WPF regression checks, DISM, DiskPart, ISO mounting, and self-contained executable publishing.

## What does not work natively

The app cannot be launched, elevated, or packaged as a macOS app. DiskPart, Windows PowerShell storage cmdlets, WMI/VBScript disk guards, DISM, Windows PE, and native WPF layout checks require Windows. Apple Silicon packaging is not supported; do not add a `.app`, Homebrew dependency, or cross-platform USB writer as an implicit feature.

## Shared data behavior

Repository source, docs, tests, and checked-in representative XML are portable text assets. Runtime preferences, logs, Windows media cache, driver caches, and staging data are Windows-local under `%LOCALAPPDATA%\\LaptopQAUsbBuilder`; they are not shared through the repository or OneDrive. Never commit runtime caches or copy a user's answer file, ISO, driver package, or log into a shared folder without approval.

When using a shared checkout, keep generated `bin/`, `obj/`, and `dist/` outputs out of commits. A macOS edit is complete only after a Windows build and the configuration checks pass on the same commit.
