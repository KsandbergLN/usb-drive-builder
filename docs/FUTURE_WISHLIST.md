# Future wishlist

This list records ideas, not commitments. A wishlist item must not change current safety behavior until it has an owner, design, tests, and an explicit release plan.

## Automation

- Add a scripted, reviewable release workflow that runs configuration checks, builds the self-contained executable, creates the tag, uploads the asset, and publishes release notes only from a merged commit.
- Add a dry-run/preflight report that exports target-disk checks, media identity, driver/package results, estimated capacity, and cache decisions without touching a USB disk.
- Add machine-readable build and cache summaries for fleet orchestration while continuing to sanitize paths and secrets.
- Add optional signed update discovery with administrator-controlled approval; never auto-update during a destructive USB build.

## Windows launch and servicing

- Offer a documented Windows launcher/shortcut that requests elevation and points technicians to the latest local executable without silently replacing it.
- Add a first-class disposable VM/Windows PE validation harness for generated answer files, generic product-key behavior, guarded installs, and offline driver injection.
- Improve recovery diagnostics for stale WIM mounts, disconnected USB devices, and locked cache remnants.
- Consider additional Windows editions' generic setup-key mappings only with edition-specific validation; the current generic key is intentionally limited to Pro.

## Cross-platform developer experience

- Add CI checks that validate docs links, representative XML, version consistency, and forbidden runtime artifacts on every pull request.
- Provide a small read-only inspector for macOS/Linux that can summarize installer folders and answer files without attempting Windows media preparation.
