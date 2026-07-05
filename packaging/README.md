# Marksmith packaging & distribution kit

Everything needed to publish Marksmith to the **Microsoft Store**, **winget**, and
**Chocolatey**. All three channels consume **one artifact**: a silent-capable installer,
`Marksmith-Setup-x64.exe`, built from the self-contained publish output by
[`installer/marksmith.iss`](installer/marksmith.iss).

```
packaging/
├── installer/     marksmith.iss ........... Inno Setup script → Marksmith-Setup-x64.exe
├── winget/        3 YAML manifests ........ for a PR to microsoft/winget-pkgs
├── chocolatey/    marksmith.nuspec + tools/  for `choco pack` / push
├── store/         listing.md, privacy-policy.md, assets/, screenshots/
└── msix/          optional MSIX path for the Store
```

---

## ⚠️ Read this first — the one real blocker

**winget, Chocolatey, and the Store all require a *publicly downloadable* installer.**
Your repo (`thebubbsy/marksmith`) is **private**, so its Release assets are **not** public.
Until this is resolved, the manifests here are ready but cannot be *submitted/accepted*.

Pick one:
- **Make the repo public** (simplest — Release download URLs just work), or
- **Host `Marksmith-Setup-x64.exe` on a public URL** (e.g. a public "releases" repo, or
  blob storage) and update the `InstallerUrl` in the winget + Chocolatey files.

Everything below assumes the installer is reachable at a public URL of the form
`https://github.com/thebubbsy/marksmith/releases/download/v1.0.0/Marksmith-Setup-x64.exe`.

---

## Step 0 — build the installer (automated)

The release workflow ([`.github/workflows/release.yml`](../.github/workflows/release.yml)) now
**builds the installer on every `v*` tag**, attaches `Marksmith-Setup-x64.exe` to the GitHub
Release, and **prints its SHA256** in the workflow summary and a `checksums.txt` asset.

To build locally instead:
```powershell
dotnet publish MdToPdf/MdToPdf.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:DebugType=none
iscc packaging\installer\marksmith.iss          # needs Inno Setup 6 (free): https://jrsoftware.org
Get-FileHash dist_installer\Marksmith-Setup-x64.exe -Algorithm SHA256   # → the checksum
```

Take that **SHA256** and paste it into:
- `winget/thebubbsy.Marksmith.installer.yaml` → `InstallerSha256`
- `chocolatey/tools/chocolateyinstall.ps1` → `checksum64`

(Both currently hold a `0000…` placeholder.)

---

## Channel 1 — Microsoft Store  *(you: ~30 min + account)*

1. Create a **Partner Center** developer account (one-time **$19** individual / $99 company):
   https://partner.microsoft.com/dashboard — *only you can do this (payment/identity).*
2. **Create app → reserve the name** "Marksmith".
3. Choose **EXE or MSI app** packaging (no MSIX needed — the Inno installer is enough; MSIX is
   the optional path in [`msix/`](msix/)).
4. Fill the **Store listing** from [`store/listing.md`](store/listing.md); upload the icon and
   screenshots from [`store/assets/`](store/assets) and [`store/screenshots/`](store/screenshots).
5. Under **Packages**, provide the installer URL and the silent
   install/uninstall switches + detection key — all listed at the bottom of `listing.md`.
6. Publish `privacy-policy.md` to a public URL (e.g. enable **GitHub Pages** and drop the file
   at `/privacy`) and paste that URL into the listing's Privacy policy field.
7. Submit for certification.

## Channel 2 — winget  *(you: a PR)*

1. Fill in the real `InstallerSha256` (Step 0).
2. Validate & submit. Easiest with the `wingetcreate` tool:
   ```powershell
   winget install wingetcreate
   wingetcreate submit --token <gh-token> packaging\winget\
   ```
   or copy the three files to `manifests/t/thebubbsy/Marksmith/1.0.0/` in a fork of
   [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) and open a PR.
3. Validate locally first: `winget validate --manifest packaging\winget\`

## Channel 3 — Chocolatey  *(you: pack + push)*

1. Fill in the real `checksum64` (Step 0).
2. Pack and (optionally) test, then push:
   ```powershell
   cd packaging\chocolatey
   choco pack
   choco install marksmith -s . -y     # local test in a VM/sandbox
   choco apikey --key <key> --source https://push.chocolatey.org/
   choco push marksmith.1.0.0.nupkg --source https://push.chocolatey.org/
   ```
3. The package goes through Chocolatey community moderation.

---

## Updating for a new version

1. Bump `<Version>` in `MdToPdf/MdToPdf.csproj` and the version in: `installer/marksmith.iss`,
   all three `winget/*.yaml`, and `chocolatey/marksmith.nuspec` (+ the URLs/`v1.0.0` tags).
2. Tag `vX.Y.Z` → the release workflow builds the installer and prints the new SHA256.
3. Update `InstallerSha256` / `checksum64`, then submit the winget PR and `choco push`; create
   a new Store submission.

## What only you can do (accounts / payment / identity)
- Create & pay for the **Partner Center** account; reserve the app name.
- Provide **Chocolatey** and **GitHub** push tokens.
- Decide the repo/installer **public hosting** (the blocker above).
