# Marksmith licensing

Marksmith Pro is unlocked by a **license key**. Two mechanisms are supported:

1. **Offline signed keys (default, recommended).** You sign each key with a private RSA key; the app
   verifies it offline with the embedded public key. No server, no network, unforgeable.
2. **Online activation via Lemon Squeezy** (optional). Flip `LemonSqueezyClient.Enabled` to `true`
   once your store issues license keys and they'll be validated against Lemon Squeezy's API.

## One-time setup (offline keys)

```powershell
pwsh tools/licensing/generate-keys.ps1
```
This writes `private-key.pem` (**secret — gitignored, never commit**) and `public-key.pem`. Paste the
public key into `MarkSmith.Core/Services/LicenseValidator.cs` → `PublicKeyPem`, then rebuild.

## Issuing a key per sale

```powershell
# perpetual Pro license
pwsh tools/licensing/sign-license.ps1 -Email "buyer@example.com"

# time-limited (e.g. an annual subscription)
pwsh tools/licensing/sign-license.ps1 -Email "buyer@example.com" -ExpiresUtc "2027-01-01"
```
Send the printed key to the customer; they paste it into **Settings ⚙ → License → Activate**.

To automate this, run `sign-license.ps1` from your store's post-purchase webhook (Lemon Squeezy,
Paddle, Gumroad, Stripe) and email the key to the buyer.

## The paywall

Defined in one place — `MarkSmith.Core/Models/LicenseModels.cs` (`LicenseState`):

| Entitlement | Free | Trial (3 DOCX exports) | Pro |
| --- | --- | --- | --- |
| PDF export, all themes, AI cleanup | ✅ | ✅ | ✅ |
| DOCX export + editable equations | — | ✅ | ✅ |
| Hands-free automation (clipboard / watch folder / extension) | — | ✅ | ✅ |
| "Made with Marksmith" footer removed | — | ✅ | ✅ |

New installs get a 14-day Pro trial automatically. Adjust `LicenseService.TrialDays` and the
`StoreUrl` (the "Buy" button target) to taste.

## Security notes
- The private key never ships and never enters git. If it leaks, rotate it (new keypair → new
  embedded public key → all old keys stop working).
- Offline keys can't be revoked individually without an online check; for revocation, use the Lemon
  Squeezy online path or add a periodic online re-validation.
- .NET assemblies are decompilable — for real-world hardening, run an obfuscator over the release
  build so the gating logic isn't trivially patched out.
