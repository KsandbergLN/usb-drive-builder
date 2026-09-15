# USB Drive Builder v2.0.110

This release includes the changes developed in v2.0.107–v2.0.110 since the previous GitHub release, v2.0.106.

## Changes

- Named configuration profiles with dropdown selection, a naming popup for Save as, save acknowledgments, and removal. Existing settings migrate to My configuration. Profile partition rows are greyed out while locked. The final interface has no Rename action.
- One optional Protect installer USB from overwrite checkbox in generated Windows Setup configuration. It requires Disk 0, 200–4000 GiB inclusive, exactly one match, and successful disk validation before DiskPart. Existing partitions are allowed and will be erased during installation. Supplied answer files are not changed by this option.
- Full extracted Windows installer folders can be selected instead of an ISO, with edition selection, scripts, driver injection, local staging, and cache reuse. WIM and ESD installation images are supported; ordinary support folders and split SWM images are not. Source backups stay unchanged.
- OneDrive-backed installer subfolders are included in content measurement and staging. Folder cache identity includes file contents.
- A completion choice after the entire USB queue finishes: Keep cache for faster matching builds or Clear cache to reclaim local space. Keep is the default and survives app restarts. Logs, profiles, sources, and written USB contents are retained.
- Target disk can be changed back to 0 correctly; generated-layout numeric settings are validated before saving or exporting.

## Download and use

Download the self-contained Windows x64 executable from the GitHub release and run it as administrator. A separate .NET installation is not required. For installer-folder workflows, reselect the complete installer root on the fixed-size NTFS boot partition and choose an edition, then add scripts and drivers.

See [Quick User Guide](https://github.com/KsandbergLN/usb-drive-builder/blob/v2.0.110/docs/QUICK_USER_GUIDE.md) for daily operation and [Technician Handoff](https://github.com/KsandbergLN/usb-drive-builder/blob/v2.0.110/docs/TECHNICIAN_HANDOFF.md) for requirements and troubleshooting.

## Validation

- Release build and self-contained publish succeeded without compiler warnings or errors.
- 216 automated checks passed for XML generation, disk-guard logic with simulated disks, script escaping, profile behavior, installer-folder staging/cache behavior, and UI choices/layout.
- No physical USB erasure or Windows PE installation was performed during these checks. Actual DISM driver servicing could not be exercised in the non-administrator test session. Validate installation on disposable test hardware or a VM before deployment.
