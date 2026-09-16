# Configuration and profiles

## Default layout

| Partition | Size | File system |
|---|---:|---|
| `DELL DIAG` | 50 MB | FAT32 |
| `Win11 Boot` | 20 GB | NTFS |
| `IT SUPP` | `*` (remaining space) | exFAT |

Use `*` on exactly one partition. Fixed sizes accept MB or GB values. The layout preview estimates content size and flags partitions that cannot fit before erasure.

## Profiles

Open the three-bar menu to manage named profiles. A profile stores the 1–4 partition layout, generated Windows Setup settings, image compression, and unsigned-driver option. Content paths are current-build selections and are not stored in profiles; language and theme are app-wide.

Save as creates a uniquely named profile. Save updates the selected profile, Remove deletes it, and switching with unsaved edits offers Save, Discard, or Cancel. Existing settings migrate to **My configuration**. Profile and preference data are stored at `%LOCALAPPDATA%\\LaptopQAUsbBuilder\\preferences.json`.

Partition and setup editing starts locked. Unlock with the lock icon before changing rows or generated settings. **Apply profile** can replace the current layout and clear attached content after confirmation.

## Generated Windows Setup

These fields create `Autounattend.xml` when generation is requested or when scripts need an answer file and no XML was supplied:

| Setting | Purpose |
|---|---|
| Target disk / install partition | Internal disk and destination partition used by Windows Setup; defaults are `0` and `3`. |
| EFI / MSR / Shrink MB | EFI System, Microsoft Reserved, and Recovery sizing. |
| Volume labels and temporary letters | Names and Setup-time letters for EFI, Windows, and Recovery. |
| Edition | Windows image name; it must exist in the selected ISO or installer folder. |
| Prompt before erasing/installing | Adds the Ready to Reimage Yes/No prompt, with No selected by default. |
| Protect installer USB from overwrite | Optional guard requiring exactly one matching Disk 0 between 200–4000 GiB; it does not modify supplied XML. |
| OOBE language / keyboard | Locale and input profile selected after Setup; the language must exist in the media. |

Generated DiskPart commands are written one per line to avoid Windows PE shell-quoting issues. The generated Pro path includes the standard generic installation key `VK7JG-NPHTM-C97JM-9MPGT-3V66T`; it selects the edition but does not activate Windows. Prepared media also receives `sources\\ei.cfg` using the WIM-reported edition. A supplied XML is preserved except for required script-runner integration.

## Appearance

Light, Dark, and AMOLED themes and the 12-language set persist between launches. The borderless main and configuration windows scale their complete logical surfaces and remain work-area aware when resized or maximized.
