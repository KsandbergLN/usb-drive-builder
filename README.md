![USB Drive Builder application](docs/images/laptop-qa-usb-drive-builder.png)

# USB Drive Builder

Current release: **v2.0.110**. [Download the latest Windows x64 executable](https://github.com/KsandbergLN/usb-drive-builder/releases/latest) · [Release notes](docs/RELEASE_NOTES.md) · [Quick User Guide](docs/QUICK_USER_GUIDE.md) · [Technician Handoff](docs/TECHNICIAN_HANDOFF.md).

Feature developers: see [Canonical Source](CANONICAL-SOURCE.md), [Developer Handoff](DEVELOPER-HANDOFF.md), and the [macOS workflow](macos/README.md).

The main window starts within the usable desktop area. Its complete 1360×800 logical design surface scales uniformly with the window—including cards, text, controls, spacing, partition preview, and progress indicators—so the same layout remains visible on smaller laptop displays and expands proportionally when maximized. Drag any side or corner of the borderless window to resize it; the 17:10 design ratio remains locked during dragging. Configuration uses the same behavior, preserves its 47:42 ratio, and scales its complete 940×840 design surface inside a smaller, work-area-aware starting window. Maximizing either window still fills the normal Windows work area.

**USB Drive Builder** is a Windows desktop tool for IT technicians who need to turn one or more USB drives into consistent, ready-to-use laptop support media. It replaces repetitive manual disk preparation with a guided workflow that erases approved USB disks, creates a configurable MBR partition layout, formats each volume, and copies the correct diagnostic, Windows setup, and support content to its destination.

The app is designed for repeatable bench workflows. Technicians can save a standard layout, preview how it will fit each selected drive, attach different files and folders to individual partitions, and build several USB drives in a sequential queue. Before and after every build, the app validates the target disk and resulting partition layout, while activity and file logs provide a clear record for troubleshooting.

## Highlights

- Configure 1-4 MBR partitions using FAT32, NTFS, or exFAT, including one partition that consumes all remaining space.
- Add files and folders per partition, with `Autounattend.xml` support, Windows edition selection, and optional offline driver injection into bootable 64-bit UEFI Windows media.
- Build multiple USB drives in sequence without one failed drive stopping the rest of the queue.
- Preview proportional partition layouts for every selected drive before anything is erased.
- Estimate selected content sizes, highlight partitions whose content will not fit, and block the build before erasure.
- Revalidate USB identity and reject boot, system, non-USB, changed, or unsafe source/target disks.
- Save default layouts, switch among Light, Dark, and AMOLED themes, and use the interface in 12 languages.

## Default layout

The factory defaults are:

| Partition | Size | File system |
|---|---:|---|
| `DELL DIAG` | 50 MB | FAT32 |
| `Win11 Boot` | 20 GB | NTFS |
| `IT SUPP` | `*` (all remaining space) | exFAT |

`*` may be used on exactly one partition in any position. Fixed sizes accept MB or GB values, such as `50 MB` and `20 GB`.
Size fields turn red when an entry does not contain a valid `MB`, `GB`, or `*` value.

## Extracted Windows installer folders

On a fixed-size NTFS boot partition, use **Add → Files / folders → Add Folder** to select the root of a complete extracted Windows installer. The app recognizes `setup.exe`, the BIOS/UEFI boot files, `sources/boot.wim`, and `sources/install.wim` or `sources/install.esd`, then asks which Windows edition to use. Scripts and drivers can be added just as with an ISO. A support-files folder alone is not an installer; split `install.swm` images are not supported. Only one installer source (ISO or folder) is allowed per USB.

The source folder is hashed and copied to local staging before the existing export, driver-injection, and compression workflow runs. The original folder is never serviced or edited. Only the prepared installer is copied to the USB, avoiding duplicate unserviced install images. Any root `Autounattend.xml` remains available for script integration. OneDrive-backed subfolders are included in size calculations and staging; cloud-only files may need downloading. An inaccessible file stops preparation before USB erasure. Actual links are rejected in installer folders.

## Configuration profiles

Open the three-bar menu in the upper-left corner to manage named profiles. Each profile stores 1–4 USB partitions, generated Windows Setup settings, image compression, and the unsigned-driver option. Language and theme remain app-wide. ISO, XML, driver, script, and other content paths are current-build selections and are not stored in profiles.

Select a profile from the dropdown. **Save as…** opens a popup where you name a new profile. Names must be unique, ignoring case. Canceling the popup leaves profiles unchanged. **Save profile** updates the selection and shows an inline acknowledgment; if no profile is selected, it asks for a name. **Remove** deletes the selected profile; removing the last leaves the current settings available as an unnamed configuration. Switching profiles with unsaved edits offers Save, Discard, or Cancel. The bottom **Save** commits all profile changes and the selection to `%LOCALAPPDATA%\LaptopQAUsbBuilder\preferences.json` and displays a saved confirmation; **Cancel** discards the dialog's changes. Existing settings become **My configuration** on first use.

Partition and setup editing is locked initially, with the partition list visibly greyed out. Unlock with the lock icon to restore normal brightness and editing. Saving applies the selected profile's setup options and, if no content is attached, its partition layout. If content is already selected, it is preserved: use the main-screen **Apply profile** button to load the profile's partition layout and clear the current content selections after confirmation.

Partitions can be added with the green `+`, removed with their red `-`, and reordered using the two-bar drag handles. Config removal and reordering controls are disabled while editing is locked.

### Generated Windows Setup

The **Generated Windows Setup** section is used for exported/generated `Autounattend.xml`, including when scripts are selected without supplied XML. If you provide an XML file, its existing Windows Setup settings are preserved and these generated-file fields are not used. The fields mean:

| Setting | Meaning |
|---|---|
| Target disk | Internal disk number passed to DiskPart and Windows Setup. This is normally `0`, but it must match the disk that should be erased on the computer being installed. |
| Install partition | Partition number receiving the Windows image. With the standard generated layout, partition `3` is the Windows partition. |
| EFI MB | Size of the EFI System Partition, in MB. |
| MSR MB | Size of the Microsoft Reserved partition, in MB. |
| Shrink MB | Space reserved from the Windows partition for the Recovery partition, in MB. |
| EFI / Windows / Recovery label | Volume labels assigned to those internal-disk partitions. |
| EFI / Win / Rec letters | Temporary drive letters used by Windows Setup while it creates and formats the partitions. |
| Edition | Select the Windows image name requested from the selected ISO; it must match an edition included in that ISO. |
| Prompt before erasing/installing | Shows the Windows PE **Ready to Reimage** Yes/No dialog before the internal disk is erased and Windows is installed. **No** is selected by default. |
| Protect installer USB from overwrite | One optional checkbox, off by default, enabling the target-disk checks: Disk 0 only, an inclusive size of 200–4000 GiB (binary GB), exactly one match, and stop on disk-check errors. Target disk must be 0 when enabled. Existing partitions are allowed and will be erased during installation. Unknown size information, WMI errors, or missing VBScript also block the guarded runner. On rejection the synchronous command displays the failure and stays stopped; close Setup and restart after reviewing the disk/configuration. This protects the generated installation workflow; it does not set general USB write protection or modify supplied XML. |
| OOBE language / OOBE keyboard | Choose the Windows language and keyboard from the drop-downs used after Setup. Defaults are English (United States) and US keyboard; the selected language must be included in the chosen ISO. These settings bypass the language and keyboard choices during OOBE. |

The generated answer file writes the GPT DiskPart script one line at a time before it runs it. This avoids nested shell quoting that can merge `FORMAT` and `ASSIGN` commands on some Windows PE builds. Volume labels in the generated layout may use letters, numbers, spaces, hyphens, and underscores; temporary drive letters must be one letter each. The **Allow unsigned drivers** option applies only to optional DISM driver injection and adds `/ForceUnsigned`; it does not override Windows or Secure Boot policy. The lock icon protects the profile partition rows and generated settings from accidental edits until unlocked.

The generated settings dim while locked. **Generate Autounattend.xml** stays available: choose where to save it, and it exports an answer file using the currently displayed setup settings without changing them.


## Adding content

Every partition row has a muted, theme-aware green **Add** button stacked above a muted red **Clear** button with no content heading. On FAT32 and exFAT, Add opens the Files / folders source manager directly. On NTFS, Add presents the additional XML, ISO, Drivers, and Scripts actions before opening their respective managers. Clear removes all content assigned to that partition. Green text to their right summarizes attached types—`AUXML`, `ISO`, `Windows folder`, `Folder`, `Files`, `Drivers`, and `Scripts`—without expanding the row into separate buttons. Folder contents are merged into the destination partition root, while selected files are copied directly to that root.

Every content-manager action changes from grey to green immediately after that content type has a successful selection. Cancelling a picker leaves its button unchanged.

The **Files / folders** action opens the same managed-source dialog used by Drivers and Scripts: add files or folders repeatedly from multiple locations, review the list, remove an individual selection, clear it, then close to apply it. Script entries show both their filename and their source folder, including files found by **Add Folder**.

Positive and destructive controls use the same theme-aware palette throughout the app. Light and Dark use the desaturated `#D7F3E5` green and `#D8A2A3` red; AMOLED uses higher-saturation equivalents with contrasting text.

For NTFS partitions, Add also offers **XML** and **ISO**. FAT32 and exFAT partitions accept regular file and folder content but do not offer ISO selection. XML selects an answer file that is copied to the partition root as `Autounattend.xml`. Once a Windows ISO or installer folder is selected, the Add dialog also shows **Generate Autounattend.xml**. Check it to create a new answer file from the Generated Windows Setup in Config; a supplied XML takes precedence.

ISO accepts one supported 64-bit Windows installer ISO per USB drive. After inspection, an app-themed options window lists only the editions in the image and selects Windows 11 Pro by default when available. The chosen edition is exported as the only install edition, reducing later servicing and copy work.

Prepared Windows media also receives `sources\ei.cfg`, using the exact edition ID reported by the selected WIM. Generated answer files additionally include the standard Windows 10/11 Pro generic installation key when Pro is selected, preventing a product-key page when the answer file is used with media that asks. This key only selects the edition; it does not activate Windows. A standalone generated XML used with unrelated Windows media still depends on the selected edition being present in that media.

On every NTFS partition, the Add chooser always shows **Drivers** and **Scripts** alongside XML and ISO. Drivers and Scripts may be selected before or after the ISO, so content can be configured in any order; a build is blocked with a clear validation message if those selections remain without a Windows ISO or recognized installer folder. Drivers opens a themed manager with **Add Folder** plus one **Add Driver Files** picker for individual INF packages and compressed ZIP/CAB driver packs. Multiple INF files and driver packs can be selected together. Compressed packs are safely extracted into a SHA-256-addressed cache under `%LOCALAPPDATA%\LaptopQAUsbBuilder\DriverPackCache`, then scanned and validated exactly like extracted folders. Legacy underscore-compressed payloads referenced by an INF—such as `.sy_`, `.dl_`, and `.ca_`—are expanded into `DriverPayloadCache`; the original driver source is never changed. Unsafe ZIP paths, damaged/password-protected archives, and packs containing no INF files stop before USB erasure. Entries can be removed one at a time or cleared without affecting the ISO or other content, and the Drivers button remains green while any sources are active. Before ISO hashing or servicing, the app checks the effective x64 catalog plus package-owned payloads referenced by applicable CopyFiles and service definitions. Windows inbox dependencies brought in through `Include`/`Needs`, externally mapped vendor payloads, and unused inventory entries do not create false missing-file warnings. During private driver staging, same-size candidates are SHA-256 checked; byte-identical payloads are represented by hard links while all required relative paths remain intact. Completely identical package directories have their redundant INF entry suppressed so DISM does not process the same package twice. This affects only temporary staging and never changes or reorganizes selected source folders. An incomplete package stops preflight with a themed **Incomplete driver package** warning that names the INF and missing files; every incomplete package is written to the build log. For DISM servicing failures, every rejected INF is logged individually with its reported HRESULT before the app continues or stops; fatal messages include the total failed count and direct technicians to the complete build-log list. Complete packages with DISM-invalid or incompatible data can still be skipped without a blocking prompt. The app mounts the installed image once, processes every source, and commits once; `boot.wim` is not serviced. **Allow unsigned drivers** in Config adds DISM `/ForceUnsigned`; it is off by default, and Windows or Secure Boot can still reject an unsigned driver.

Preparation happens once on fast local storage before any USB is erased. Configuration provides a saved **Windows image compression** dropdown: `FAST (Fastest, largest)` keeps a fast-compressed `install.wim`; `MAX (Slow, smaller)` performs a maximum-compression WIM export after driver servicing; `ESD (Slowest, smallest)` is the default and performs a recovery-compression export after servicing to produce `install.esd`. The app mounts a working WIM once, commits drivers once, and caches the selected final format under `%LOCALAPPDATA%\LaptopQAUsbBuilder\MediaCache`. Compressed archive extraction is cached under `DriverPackCache`, and expansion of legacy underscore-compressed payloads is cached under `DriverPayloadCache`. Before DISM begins, selected drivers are copied into the active build's private staging folder, so cache cleanup or another app instance cannot remove them while servicing is underway. Driver staging overlaps the local ISO copy, duplicate-candidate hashing uses up to four workers, package preflight uses up to six workers, and multiple ZIP/CAB packs extract two at a time. These limits improve fast-storage performance without competing with the serialized DISM mount, injection, commit, and final compression operations. During every DISM operation, the current-activity line displays elapsed time and any real percentage emitted by DISM. CPU, process I/O, watched-file timestamps, and DISM output are sampled once per second; ordinary quiet periods say DISM is still working, while ten uninterrupted minutes without any detected activity produces a non-blocking warning and continues waiting. Cache publication retries temporary access-denied locks caused by scanners or indexers; completed driver staging can continue from its unique working directory if that directory cannot be renamed after all retries. If Windows media preparation succeeds but later USB partitioning, copying, or verification fails, the prepared media and supporting driver caches survive app closure for the next retry. After the queue finishes, Keep cache preserves it across app restarts; Clear cache removes it. Keeping it makes the next matching build faster. Every USB in the queue reuses that prepared media, and a later build reuses it when the ISO, edition, complete driver-source manifest, unsigned-driver setting, and compression mode match. Use Configuration's **Clear Cache** action to reclaim space or force clean preparation; it deletes everything currently available and schedules locked remnants for removal after the app closes.

The segmented total-progress bar includes shared preparation: Windows-media validation, extraction, hashing, staging, export, driver injection, and commit advance every selected-drive segment together. Once USB writing begins, only the active drive's segment advances through partitioning, copying, and verification. The active drive card and both progress fills use uninterrupted moving diagonal barber-pole bands rather than isolated stripe blocks. The current-activity bar remains specific to the current operation and restarts for each new stage. When the queue finishes, the animation stops and the current bar becomes static dark green for success or static red for failure/cancellation.

The main window starts at a compact work-area-aware size for smaller displays and includes standard minimize, maximize/restore, and close controls. **Cancel build** is available from the start of preflight through the end of USB writing. Cancellation stops active hashing, extraction, staging, copying, PowerShell, or DISM work; a mounted Windows image is discarded and an attached ISO is dismounted before the build returns to idle.

After every queued drive finishes writing and verification, the completion dialog offers **Keep cache** (default) or **Clear cache**, when cache exists. It explains that keeping cached Windows media and drivers makes the next matching build faster, while clearing it frees disk space but requires preparation again. The choice is offered once per queue, never between partitions or drives. Keeping it cancels any pending cache-clear request and preserves cache across app restarts. Clearing runs while the build still holds its cache guard; locked remnants are scheduled for deletion after exit. USB contents, original sources, logs, and saved profiles are unaffected. If there is no cache, the normal completion acknowledgment is shown.

Configuration also shows the combined cache size and provides **Clear Cache**. On ordinary app closure only temporary staging is removed; reusable cache is deleted only when explicitly requested.

The destination must be a fixed-size NTFS partition of at least 5 GB and large enough for the prepared media. Windows boot does not use or require the FAT32 `DELL DIAG` partition; that volume remains available only for diagnostics. Bootable ISO support targets Dell-compatible removable USB flash sticks with native NTFS UEFI support, not fixed-media external hard disks such as WD My Passport. Select the USB's UEFI entry on the Dell boot menu so Windows Setup installs to a GPT system disk. An explicitly selected `Autounattend.xml` is copied afterward.

The always-visible **Scripts** action opens a themed source manager that retains the current list while **Add Files** is used repeatedly, allowing scripts and supporting resources to be collected from multiple folders or drives before choosing **Close**. It accepts every file type; entries can be removed individually or cleared together. Everything is copied after Windows media preparation into `sources\$OEM$\$$\Setup\Scripts` on the finished USB. At installation time the complete selection is copied again to `%ProgramData%\USBDriveBuilder\ScriptRun` and executed there, preventing a batch file that deletes its original Setup copy from deleting the file CMD is currently reading. Only CMD, BAT, PowerShell, VBS, JS, and WSF files are automatically executed; other files remain available to those scripts through `%~dp0`. The app removes both script locations afterward. Duplicate filenames from different locations and app-reserved helper names are rejected, and all selected files participate in existence, target-disk, and partition-capacity safety checks.

The app automatically adds a synchronous `specialize` command to the copy of `Autounattend.xml` written to the USB; the technician's source XML is never modified. If no XML was selected, the app generates a minimal answer file containing the command. During Windows Setup, the command runs recognized scripts sequentially as `SYSTEM` before OOBE. After the last script exits, a generated cleanup helper removes every selected script or support file and both generated helper files. Existing unattended settings and existing specialize commands are preserved, and the new command is placed after them.

Content selections stay with their partition when the partition is reordered. Hover over the content controls to review the selected paths, or use **Clear** to remove all content selections from that partition.

## Progress and activity

The Activity card shows a determinate current-activity progress bar plus a per-drive queue tracker. The upper bar is only for the process currently running. The lower tracker begins empty, divides evenly into one segment per selected USB, and fills each segment progressively as that drive is partitioned, copied, and verified. The selected drive card is pale green; while writing it changes to dark green with diagonal green stripes, succeeds as solid dark green, and fails red. Time estimates are intentionally omitted because formatting, ISO mounting, antivirus activity, small-file overhead, and changing USB speeds made them unreliable.

## Selecting and building USB drives

The drive picker shows disks that Windows reports with a USB bus type, with any assigned drive letters beside the disk number. Select one or more drive cards to create a sequential build queue. Each selected drive is revalidated immediately before it is erased, partitioned, populated, and verified. A failure on one drive is logged without preventing later queued drives from running.

After **Build USB Queue** is selected, the app immediately enters a visible **Preparing build** state while it checks targets, source paths, capacity, and prepares or retrieves cached Windows media. Initial DISM preparation can take several minutes. Once `ERASE` has enabled the Build button, a valid preflight flows directly into the USB queue without another confirmation or skipped-driver prompt. A safety or preparation failure still stops before erasure and explains what must be corrected.

Before creating the requested MBR layout, the app revalidates the selected USB identity and safety flags, then runs DiskPart `clean`. This removes all partition and volume metadata—including ordinary partition tables and offset-zero “superfloppy” formatting—without deleting partitions one at a time or zero-filling the USB stick. Afterward, the app rediscovers the device by its persistent hardware ID in case Windows changes its disk number, verifies that no partition remains, changes the empty disk to MBR only when necessary, and creates the requested layout. If Windows reports error 433, the app explains that the USB disconnected while its partition table was being written.

Before building, enter `ERASE` in the confirmation field. Every partition and file on each selected target is permanently removed.

The Partition Layout card remains blank until a drive is selected. It then displays proportional, color-coded partition segments using each drive's calculated capacity. Multiple selected drives share the available height dynamically. Hover over a segment to see its drive number, label, calculated size, and file system.

When files or folders are selected, the app scans their logical sizes and allows additional filesystem working space. A partition that is too small is shown with warning colors in the layout, and its hover bubble shows the estimated required space. The app checks again during preflight—including extracted ISO contents and other content assigned to the same partition—and will not erase a drive while any selected content is estimated not to fit.

## Appearance and language

The configuration menu includes Light, Dark, and AMOLED themes and the same 12-language set used by Laptop QA V2. Theme changes preview live, and saved theme and language preferences persist between launches.

Application confirmations, warnings, errors, completion messages, and tooltips use the active app theme instead of the standard Windows message-box appearance. Windows file and folder selection dialogs remain native so they retain normal Explorer navigation and shell integration.

## Run

Double-click the newest **USB Drive Builder vX.Y.Z.exe** in the `dist` folder and accept the administrator prompt. Disk partitioning requires elevation. The WPF application performs storage operations without displaying a PowerShell window.

The version appears in the app footer and executable metadata. Historical versioned executables can coexist in the shared `dist` folder.

For operating instructions, see the [Quick User Guide](docs/QUICK_USER_GUIDE.md). For support ownership, troubleshooting, and escalation details, see the [Technician Handoff](docs/TECHNICIAN_HANDOFF.md).

## Build and publish

The project targets .NET 8 for Windows:

```powershell
dotnet build .\LaptopQaUsbBuilder.csproj -c Release
```

For a versioned release, update `AppVersion`, `AssemblyVersion`, and `FileVersion` in `LaptopQaUsbBuilder.csproj`, then run:

```powershell
.\publish.cmd
```

The publish script uses a staging directory and places the versioned executable in `dist` without deleting historical builds.

## Safety and logs

- The app initializes every selected USB target as MBR. Windows installation media is UEFI-only so the laptop's internal Windows system disk is GPT.
- The selected USB disks are completely erased; this cannot be undone.
- Targets are checked again before erasure and rejected if Windows reports them as boot, system, non-USB, or changed since selection.
- After erasure, the app refreshes Windows' storage state and initializes the disk only when it is actually RAW, avoiding redundant initialization failures on USB sticks that remain MBR.
- Sources stored on a queued target disk are rejected before building.
- Protected metadata such as `System Volume Information` and `$RECYCLE.BIN` is skipped when a drive root is used as a source.
- FAT32 sizes and volume-label lengths are validated against Windows limits.
- Copy, build, and crash logs are saved under `%LOCALAPPDATA%\LaptopQAUsbBuilder\Logs`.
- PowerShell CLIXML errors are decoded before logging so Windows storage failures retain their useful error message. Exception stack traces retain source filenames and line numbers while removing the developer's local build path.
- Bootable ISO preparation targets supported removable USB flash sticks with native NTFS UEFI support. It does not create FAT32 or legacy-BIOS boot media, and fixed-media external hard disks are not supported as boot targets. Secure Boot acceptance still depends on the ISO and injected-driver signatures and target firmware policy.

undefined
