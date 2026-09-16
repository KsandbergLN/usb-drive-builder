# Content and Windows media

## Partition content

FAT32 and exFAT partitions accept files and folders. NTFS also offers XML, ISO, Drivers, and Scripts. Folder contents merge into the partition root; selected files copy directly there. Managed source dialogs support repeated additions, individual removal, Clear, and Close. Content remains attached when partitions are reordered.

## ISO and extracted installer folders

An ISO or a complete extracted installer folder may be selected, but only one Windows installer source is allowed per USB. A recognized folder contains `setup.exe`, BIOS/UEFI boot files, `sources/boot.wim`, and `sources/install.wim` or `sources/install.esd`; ordinary support folders and split `install.swm` media are rejected. Select the edition reported by the image; Windows 11 Pro is selected by default when available.

Sources are hashed and copied to local staging before export, servicing, and compression. The original source is never edited. OneDrive-backed files participate in measurements and staging; cloud-only or inaccessible files stop preparation before erasure. Installer-folder sources are not copied a second time as ordinary support content.

Prepared media writes `sources\\ei.cfg` for the selected WIM edition. Generated Pro answer files contain the generic installation key documented in [Configuration](CONFIGURATION.md). The key is not an activation license.

## Drivers

Drivers can be selected before or after media selection. Add Folder recursively scans directories; Add Driver Files accepts INF, ZIP, and CAB. Archives are extracted into a SHA-256 cache, unsafe paths and damaged/password-protected packs stop before erasure, and legacy underscore-compressed payloads are expanded into a separate cache without changing the source.

Preflight checks the effective x64 catalog and payloads referenced by applicable CopyFiles and service definitions. Private staging uses hashes and hard links for byte-identical payloads while preserving paths; duplicate complete packages are suppressed. DISM services the installed image, not `boot.wim`, and every rejected INF is recorded in the build log. **Allow unsigned drivers** only adds DISM `/ForceUnsigned`; Windows and Secure Boot can still reject them.

## Scripts and XML

Scripts accepts every file type and retains selections across repeated additions. CMD, BAT, PowerShell, VBS, JS, and WSF files execute sequentially as `SYSTEM` before OOBE; other files remain supporting resources. The app stages them under `sources\\$OEM$\\$$\\Setup\\Scripts`, copies them to `%ProgramData%\\USBDriveBuilder\\ScriptRun`, runs from that isolated directory, and removes both locations afterward. Duplicate filenames and reserved helper names are rejected.

The USB copy of a selected `Autounattend.xml` receives a synchronous `specialize` runner command; the source XML is never changed. If no XML exists, a minimal generated file is created. Existing unattended settings and specialize commands are preserved.
