# Feature developer handoff

## Product and architecture

USB Drive Builder is a Windows-only .NET 8 WPF desktop application. It runs elevated because it enumerates disks, invokes DiskPart/PowerShell, prepares Windows media, and optionally services images with DISM. The build pipeline is: validate sources and target identity → prepare/cache Windows media → stage drivers and scripts → erase and partition the USB → copy and verify content → retain or clear cache after the queue completes.

The main window processes selected USB drives sequentially. Shared Windows-media preparation advances the queue tracker for all selected drives, while partitioning, copy, and verification remain per-drive. A single failed drive does not stop later queued drives.

## Safety invariants

- Never erase a disk without revalidating its persistent identity, USB bus type, read-only state, boot/system flags, and expected selection immediately before `clean`.
- Never treat a source folder or ISO as writable. Stage it locally and service only the staged copy.
- Keep DiskPart and DISM operations serialized where the code requires it; cancellation must discard mounted WIM state safely.
- The optional installer-USB guard applies only to generated Windows Setup and requires Disk 0, one matching 200–4000 GiB disk, and successful WMI/VBScript checks. It does not alter supplied XML.
- Do not log secrets or full user paths. Use `LogSanitizer` for exception text and diagnostics.

## Tests and verification

Run the application checks with:

```powershell
dotnet run --project tests/ConfigurationChecks/ConfigurationChecks.csproj -c Release
```

The checks cover generated XML, shell escaping, disk-guard boundaries, profile persistence and migration, cache-choice UI, WPF layout, extracted installer recognition/staging, cache invalidation, and source preservation. They do not erase physical USB media or replace validation in disposable Windows PE hardware/VM testing. Build validation should include `dotnet build LaptopQaUsbBuilder.csproj -c Release` and `cmd /c publish.cmd`.

## Release ownership

1. Update the three version fields in `LaptopQaUsbBuilder.csproj` and the technician handoff release line.
2. Run automated checks, Release build, and `publish.cmd`.
3. Inspect the generated executable in `dist/` and confirm its version and size.
4. Commit on a `codex/release-vX.Y.Z` branch, open a PR, merge to `main`, and tag the merged commit with `vX.Y.Z`.
5. Upload the self-contained `USB Drive Builder vX.Y.Z.exe` to the GitHub release and update `docs/RELEASE_NOTES.md`.

The GitHub release artifact is separate from source: rebuilding a local executable does not update GitHub until the release asset is uploaded. Preserve the release notes' statement of what was and was not tested.

## Recovery and ownership transfer

If a build fails after media preparation, retain the cache and build log for retry. If a process is interrupted, verify no WIM mount remains before deleting staging data. For handoff, provide the app version, target disk identity, source types, exact message, build log, and whether the failure happened during preparation, DiskPart, copy, or verification. Never send ISO images, answer files, or source content unless approved.
