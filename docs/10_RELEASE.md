# 10 — Release / Distribution

## Distribution model (MUST)

- Distribution is **manual download from the GitHub Releases page** of the project's private GitHub repository.
- The user downloads the package, installs it, and later installs newer versions the same way.
- No Microsoft Store, no App Installer (`.appinstaller`) auto-update feed, no Winget, no self-updater that
  downloads or installs anything. A public Store release is DEFERRED until distribution to more users is needed.
- Because the repository is private, release assets are downloadable only by accounts with repository access.
  This is accepted for the current release; widening access (public repo or another host) is a Decision Gate.

## Packaging

- Primary package: **MSIX**, built as an `.msixbundle` containing x64 (required) and ARM64 (when all
  dependencies support it). If a bundle is impractical, ship per-architecture `.msix` files and record an ADR.
- Package version is `Major.Minor.Build.0` and MUST strictly increase on every release so that installing a
  newer package upgrades the existing installation in place (settings, jobs and models are preserved).
- Installing the package must work by double-clicking it (App Installer) on a default Windows 11 24H2 setup.
  Also document the PowerShell alternative (`Add-AppxPackage`).
- FFmpeg / yt-dlp / other redistributed binaries are included in the package (or downloaded explicitly by the
  user via Model/Tool Manager) with their licenses listed in Third-party notices.

## Code signing (self-signed)

- Sign with a self-signed code-signing certificate during private distribution. No paid certificate.
- Create the certificate **once** and reuse it for every release; changing the certificate (publisher) breaks
  in-place upgrade. Choose a long validity period (e.g. 5 years) and record expiry in `HANDOFF.md`.
- The `.pfx` and its password are never committed and never written to the repository, logs, or Library.
  Store the `.pfx` outside the repository (e.g. under the user profile) and supply its path/password at build
  time via environment variables or Windows Credential Manager. Tell the user where it is so they can back it up.
- Publish the matching public certificate (`.cer`) as a release asset, with instructions to import it once into
  `Local Machine > Trusted People` (admin required once) before the first install.
- The MSIX `Publisher` in the manifest must exactly match the certificate subject.

## Package data location vs. uninstall (MUST)

MSIX removes the package's private app data on uninstall, and writes by packaged apps to `%LOCALAPPDATA%` /
`%APPDATA%` may be redirected into package-private storage. Therefore:

- **Library** default location is outside package-private storage (e.g. `Documents\Kakitome\Library`) and
  MUST survive uninstall.
- **Models** and **Backups** must be stored where the Uninstall flow's "keep" choice is actually honored
  (i.e. outside package-private storage), or the Uninstall flow must state honestly that they will be removed.
- **Cache** and **AppData** (settings, SQLite, logs) may live in package-private storage.
- An acceptance test MUST verify: install → create a recording → uninstall → Library still intact →
  reinstall → Library can be reopened/re-indexed.

## Update policy

- Updates are manual: the user downloads a newer release and installs it over the existing one.
- Never silently download or install app or model updates.
- In-app update awareness: *About* shows the current version and a link to the GitHub Releases page.
- An automatic "new version available" check is **not** part of the current release: the repository is private,
  so checking it would require a stored GitHub token/account, which conflicts with the no-account policy.
  If the repository later becomes public, an optional, disableable, notification-only check against the public
  GitHub Releases API may be added (ADR required).

## Release channels and versioning

- Semantic versioning: `vX.Y.Z` (Stable), `vX.Y.Z-beta.N` (Beta). No Nightly channel.
- **Beta**: built from `dev`, tagged `vX.Y.Z-beta.N` on `dev`, published as a GitHub **pre-release**.
- **Stable**: `dev` merged into `main` (`--no-ff`), tagged `vX.Y.Z` on `main`, created as a GitHub **draft**
  release. The user reviews and publishes Stable releases; the agent does not publish Stable on its own.
- Use `gh release create` with an argument list (no shell-string interpolation of user input).

## Release checklist (agent-run)

1. Clean checkout of the release branch/tag; `dotnet build -c Release` and all tests pass.
2. Version bumped in the package manifest and app; `CHANGELOG.md` updated (Keep a Changelog style).
3. Build and sign the `.msixbundle`; verify the signature (`signtool verify /pa`).
4. Install the built package on the dev machine, launch it, and run the release smoke test; also test
   upgrade from the previous release when one exists.
5. Generate `SHA256SUMS.txt` for all assets.
6. Create the GitHub Release with assets:
   - `Kakitome_<version>_<arch|bundle>.msixbundle` (or `.msix` files),
   - `SmartRec_signing.cer`,
   - `SHA256SUMS.txt`,
   - release notes: changes, known limitations, and **installation steps in Japanese and English**
     (first-time certificate import, install, upgrade, uninstall and what happens to Library).
7. Update `HANDOFF.md` with the released version and any follow-ups.

## Third-party notices

Provide an in-app third-party notices page and include source/license information for bundled or redistributed
components (media processing, local inference, audio capture, URL import tooling). Verify redistribution terms
(e.g. FFmpeg build license variant) before bundling; record material choices in an ADR.

## Architecture/Dependency policy

Do not add a new dependency solely for convenience if an existing Windows/.NET capability is sufficient. Record
material dependency decisions in ADRs.
