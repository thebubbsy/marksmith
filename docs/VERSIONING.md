# Marksmith Versioning & Release Workflow

How versions are stamped, compared, and shipped — and what a dev must do before cutting a
stable release.

## The two channels

| Channel | Stamp | Example | Where it comes from |
|---|---|---|---|
| **Dev** (any local `dotnet build` / `dotnet run`) | SemVer **prerelease** `<next>-dev.<utc>` | `2.18.0-dev.8161030` | `MarkSmith.Desktop.csproj` — `<Version>$(MarksmithBaseVersion)-dev.$(BuildRevision)</Version>`, revision = UTC timestamp `MddHHmm` (unique per minute, monotonic within a year) |
| **Stable** (GitHub release) | clean SemVer `<tag>` | `2.17.0` | `release.yml` overrides everything with `-p:Version=<tag> -p:AssemblyVersion=<tag>.0 -p:FileVersion=<tag>.0`, wiping the `-dev` suffix |

`MarksmithBaseVersion` lives **once** in `marksmith-v2/Directory.Build.props` — the single
version source of truth. It always holds the **next planned release**.

### Why a prerelease suffix?

1. **Ordering.** `2.18.0-dev.x` sorts *below* the future stable `2.18.0` but *above* every
   lower stable tag (`2.17.0`, …). A locally-built copy therefore never reads as "outdated"
   against the last published release.
2. **Detection.** `UpdateService.IsDevelopmentBuild` tests the entry assembly's
   informational version for a `-` (SemVer prerelease marker; `+build` metadata doesn't
   count). `CheckAsync()` short-circuits for dev builds and never calls the GitHub releases
   feed — dev versions are ahead of (or between) stable tags by design, so the feed can only
   produce false "update available" noise. Settings shows:
   *"Development build (2.18.0-dev.8161030) — update checks are disabled for non-release builds."*

Repo convention reminder: tags are plain SemVer (`v2.17.0`) and sorted with
`git tag -l --sort=v:refname` — never rely on lexicographic order (`v2.9.0` > `v2.10.0`
lexicographically).

## How the auto-updater decides "update available"

1. `UpdateService.CheckAsync()` → `GET api.github.com/repos/thebubbsy/marksmith/releases/latest`
   (dev builds never reach this step).
2. `EvaluateReleaseJson` extracts `tag_name` + the arch-matching `Marksmith-Setup-*.exe` asset.
3. `Compare(latestTag, currentVersion)` — dotted-numeric compare (longs, so the 12-digit UTC
   revision fits) with SemVer prerelease ranking: a prerelease sorts below its own numeric
   core. Result `> 0` → banner in the main window + Settings' "Check for updates" pane.
4. Install paths: **delta** (`DeltaUpdateService` — file-manifest diff from the `release-dist`
   branch / GitHub Pages feed, staged under `%LOCALAPPDATA%\MarkSmith\update-staging`, applied
   on next launch) with fallback to the **full installer** spooled to
   `%TEMP%\MarksmithUpdates\Marksmith-Setup-Latest.exe` and run `/VERYSILENT`.

## Release checklist (what a dev does before cutting a stable release)

1. **Bump the version base** — in `marksmith-v2/Directory.Build.props`, set
   `MarksmithBaseVersion` to the version you are about to ship (it should already be there,
   since dev builds work toward it; verify!).
2. **Sync store manifests** (winget + Chocolatey pin the *previous* release by design — they
   point at shipped installer URLs + SHA256):
   - `packaging/winget/*.yaml` → `PackageVersion`, `InstallerUrl`, `InstallerSha256`
   - `packaging/chocolatey/marksmith.nuspec` → `<version>`, releaseNotes URL
   - `packaging/chocolatey/tools/chocolateyinstall.ps1` → `url64bit`, `checksum64`
   (The release workflow prints the SHA256s into the release notes + `checksums.txt` asset;
   `packaging/installer/marksmith.iss` needs **no** version edit — it derives `AppVersion`
   from the published exe.)
3. **Cut the release**: `git tag v2.18.0 && git push origin v2.18.0` — `release.yml` builds
   x64 + arm64 (ARM64 is CI-only, never built locally), stamps clean SemVer via `-p:Version`,
   uploads zips + installers + `checksums.txt`, and publishes the delta feed to the
   `release-dist` branch under `update/<ver>/<arch>/`.
4. **Immediately bump `MarksmithBaseVersion` to the NEXT version** (e.g. `2.19.0`). Without
   this, post-release dev builds stamp `2.18.0-dev.x`, which SemVer ranks *below* the freshly
   published stable `2.18.0` — the exact "dev looks outdated" trap this scheme exists to kill.
   (Dev builds stay quiet either way — the check short-circuits — but the ordering must stay
   truthful for the About screen and any future telemetry.)
5. Submit the updated winget manifest (`wingetcreate submit` or PR to `microsoft/winget-pkgs`)
   and push the Chocolatey package (`choco pack` + `choco push`).

## What NOT to do

- Don't edit versions in `MarkSmith.Desktop.csproj` directly — the base version is inherited
  from `Directory.Build.props`.
- Don't build/publish ARM64 or packaging artifacts locally — those are CI-only; local
  verification of secondary arches goes to a temp dir outside the repo, cleaned up afterwards.
- Don't hand-edit the `.iss` `AppVersion` (override via `iscc /DAppVersion=…` if ever needed).
