# MSIX packaging (optional Store path)

Marksmith can go to the Microsoft Store as a plain **EXE/MSI** app (see `../store/listing.md`)
— which is simpler and reuses the same installer as winget/Chocolatey. **You only need MSIX
if you specifically want the containerized packaging.**

If you do:

1. **Reserve the app name** in Partner Center → this gives you the **Package/Identity Name**
   and **Publisher** (`CN=…`) values.
2. Edit [`Package.appxmanifest`](Package.appxmanifest) and replace the two `REPLACE-WITH-…`
   placeholders in `<Identity>`.
3. Publish the app first, then build:
   ```powershell
   dotnet publish MdToPdf/MdToPdf.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:DebugType=none
   packaging/msix/build-msix.ps1
   ```
   This produces `dist_installer/Marksmith-1.0.0-x64.msix`.
4. Upload the **unsigned** `.msix` to Partner Center — the Store re-signs it. (To sideload for
   local testing instead, pass `-CertPfx`/`-CertPass` to sign with your own cert.)

Tile/logo assets live in [`assets/`](assets) and were generated from the 2048² source logo.
The full per-scale matrix (scale-100/125/150/200/400 and target-size icons) is included for
the two required tiles; regenerate with `packaging/../scripts` if the logo changes.
