# Canonical source and generated-file boundaries

This document defines which files are authoritative when changing USB Drive Builder. It is for feature developers; daily operating instructions belong in the [Quick User Guide](docs/QUICK_USER_GUIDE.md) and [Technician Handoff](docs/TECHNICIAN_HANDOFF.md).

## Source of truth

- `*.xaml` files define WPF structure, bindings, and visual states. Keep behavior in the paired `.xaml.cs` files unless a binding or template requires otherwise.
- `MainWindow.xaml.cs` owns the build queue, partition validation, content staging, generated answer-file composition, USB copy/verification flow, logging, and completion/cache choice.
- `ConfigWindow.xaml.cs` owns configuration editing, named profiles, profile persistence models, generated Windows Setup settings, and configuration validation.
- `WindowsMediaPreparer.cs` owns ISO or extracted-installer preparation, local media caching, WIM/ESD handling, driver staging/injection, DISM monitoring, and media cleanup.
- `WindowsMediaFolderSource.cs` owns recognition and deterministic enumeration of extracted Windows installer folders.
- `WindowsSetupDiskGuard.cs` owns the optional generated-installation disk guard and its VBScript/CMD lines. It must remain opt-in and must never be used to protect or erase the USB target itself.
- `AppPreferences.cs` owns JSON persistence under `%LOCALAPPDATA%\\LaptopQAUsbBuilder\\preferences.json`; do not duplicate profile serialization in UI code.
- `LaptopQaUsbBuilder.csproj` is the version source. Update `AppVersion`, `AssemblyVersion`, and `FileVersion` together for a release.

## Generated and derived files

- `Autounattend-generated.xml` is a checked-in representative output. Update it when generated XML behavior changes, but do not hand-edit it as a substitute for changing `BuildGeneratedAutounattend`.
- `dist/*.exe`, `bin/`, and `obj/` are build products. Recreate them with `publish.cmd`; do not commit them.
- `docs/images/laptop-qa-usb-drive-builder.png` is documentation artwork only and is not a UI source asset.
- Windows media cache, driver-pack cache, expanded payload cache, staging trees, logs, and preferences are runtime data under `%LOCALAPPDATA%\\LaptopQAUsbBuilder`; they are never repository fixtures.

## Answer-file boundaries

Generated XML is used only when the user requests generation or when scripts need an answer file and no source XML was supplied. A supplied `Autounattend.xml` is preserved and receives only the script-runner integration required by the build. The generated Pro path includes the standard generic installation key `VK7JG-NPHTM-C97JM-9MPGT-3V66T`; it selects the edition and does not activate Windows. Prepared media also writes `sources\\ei.cfg` from the WIM-reported edition.

## Change checklist

When a feature changes behavior, update the owning source file, representative generated output if applicable, automated checks, user/technician docs, and release notes. Keep the README concise and link to deeper documentation instead of copying implementation details into several places.
