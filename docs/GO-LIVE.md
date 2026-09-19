# Marksmith — go-live checklist

What still has to happen before Marksmith can be sold, in the order it should happen. Each item
says who can do it: **you** (needs your identity, money or signature) or **code** (already done, or
doable in the repo).

Status as of this document: the licensing *mechanism* works and is now genuinely tested. The
*commercial plumbing around it* is not connected.

---

## 🔴 Blockers — you cannot take money until these are done

### 1. Confirm you still have the licence signing private key — **do this first**

`LicenseValidator.PublicKeyPem` embeds a public key. The matching private key is meant to be at
`%USERPROFILE%\.marksmith-keys\private-key.pem` (or `tools/licensing/private-key.pem`).

**If that private key is lost, you cannot issue a single working licence key**, and every key you'd
generate from a new keypair would be rejected by every build already shipped. Check now, before
anything else:

```powershell
pwsh tools/licensing/sign-license.ps1 -Email "you@example.com"
```

If it signs, paste the key into the app and confirm it activates. If the file is gone, generate a
new pair (`generate-keys.ps1`), paste the new public key into `LicenseValidator.PublicKeyPem`, and
ship a release **before** selling anything.

Then: **back the private key up somewhere you will still have in five years.** Losing it later means
never being able to serve an existing customer again.

- [ ] Signing works end to end
- [ ] Private key backed up (offline, and not in this repo)

### 2. Set up a payment provider and wire the checkout link — **you**

`LicenseService.StoreUrl` is still the placeholder
`https://YOUR-STORE.lemonsqueezy.com/buy/YOUR-PRODUCT-ID`. There is a guard (`IsStoreConfigured`)
so the Buy buttons say "store not configured" rather than opening a dead link — but **nobody can
give you money.**

Strongly consider a **merchant of record** (Lemon Squeezy, Paddle) rather than raw Stripe. They
handle GST/VAT registration and remittance worldwide, which is otherwise a genuine compliance
burden for a solo seller shipping to the EU/UK.

- [ ] Merchant account created and verified (bank + tax details — only you can do this)
- [ ] Product created, price set (README currently advertises **A$39 one-time**)
- [ ] License keys enabled on the product, activation limit set (`packaging/lemonsqueezy-setup.md` step 4)
- [ ] `LicenseService.DefaultStoreUrl` updated to the real buy URL — **this is the last thing
      standing between the app and a working Buy button**
- [ ] `MARKSMITH_LS_ACTIVATION=1` so Lemon Squeezy keys actually activate (see below)

Post-purchase automation is no longer a prerequisite: with license keys enabled on the product,
Lemon Squeezy generates the key and emails it to the buyer itself, and the app now activates that
key against their API. The webhook → `sign-license.ps1` → email route remains available if you'd
rather issue your own signed keys, but you no longer need it to make the first sale.

### 3. Buy a code-signing certificate — **you**

Release builds are unsigned. Every download shows Windows SmartScreen **"Unknown publisher"**, on
software people have just paid for. This costs more conversions than any missing feature.

The pipeline is ready: `.github/workflows/release.yml` signs binaries and the installer, with proper
RFC-3161 timestamping, as soon as two repository secrets exist. Until then it skips signing and the
release still builds.

- [ ] Certificate obtained — an OV cert (~US$200–400/yr) or **Azure Trusted Signing** (~US$10/mo,
      usually the cheaper and less painful route for an individual)
- [ ] Repository secret `WINDOWS_CERT_PFX_BASE64` (base64 of the .pfx)
- [ ] Repository secret `WINDOWS_CERT_PASSWORD`
- [ ] Cut a test release and confirm the installer shows your publisher name

> Note: an OV certificate still accrues SmartScreen reputation from zero, so early downloads may
> warn anyway. EV certificates get reputation immediately but cost more and need a hardware token.

### 4. Publish legal documents — **you (with a lawyer)**

Drafts are in `legal/`: `EULA.md`, `PRIVACY.md`, `REFUNDS.md`. **They were drafted by an AI
assistant and are explicitly not legal advice.** Every one has `[SQUARE BRACKET]` placeholders that
must be filled in, and claims that must be verified against what the app actually does.

You are selling to consumers. In Australia the Australian Consumer Law provides guarantees you
cannot exclude, and a refund policy that appears to exclude them can itself be a breach.

- [ ] Reviewed by a solicitor in your jurisdiction
- [ ] Placeholders filled (legal entity, ABN, address, support email, governing law, retention)
- [ ] Privacy claims verified against actual app behaviour (check for any analytics/telemetry)
- [ ] Published at a stable URL and linked from the app and the checkout

---

## 🟡 Strongly recommended before launch

### 5. Run the test suite in CI — **code**

CI builds the desktop app but **never runs the ~2,660 tests**. A regression can merge green. There
is an open task card for this. Note the suite has pre-existing Linux-only failures (SkiaSharp native
assets, Windows path assumptions, governance doc paths), so a naive `dotnet test` job is red on day
one — pick the runner deliberately.

### 6. Decide your support channel — **you**

Every legal document above says `[SUPPORT EMAIL]`. You need one that you will actually read, and
somewhere for customers to reach you when a key doesn't activate. This is the difference between a
refund and a fix.

### 7. Write the "I got a new laptop" answer — **you/code**

A perpetual key works on any machine the buyer uses, but nothing in the app tells them that, and
nothing tells them how to recover a lost key. Decide the policy, then put it on the website and in
the purchase email.

### 8. Do a real end-to-end purchase rehearsal — **you**

Buy your own product with a real card, on a clean Windows VM:

- [ ] Checkout completes and the receipt arrives
- [ ] A licence key arrives, by whatever mechanism you chose
- [ ] The key activates in a **Release** build (not Debug — the dev backdoor key is compiled out of
      Release, so activation must work without it)
- [ ] Pro features unlock and the "made with Marksmith" footer disappears
- [ ] Refund yourself, add the serial to `LicenseValidator.RevokedKeyIds`, ship, and confirm the
      key stops working

---

## 🟢 Already done in code

- **Offline RSA-signed licence keys** — unforgeable, no server, verified with the embedded public key.
- **Issuer validation** — a key minted for a different product no longer unlocks this one.
- **Per-key serial numbers (`jti`)** — every key `sign-license.ps1` issues carries a unique serial.
  This is what makes revocation possible, and it **cannot be retrofitted onto keys already sold** —
  which is why it went in before the first sale rather than after the first chargeback.
- **Key revocation** — add a serial to `LicenseValidator.RevokedKeyIds` and ship; the key dies on
  the customer's next update.
- **An issued-key ledger** — `sign-license.ps1` appends every key to `issued-keys.csv` (gitignored,
  contains customer emails) so you can answer "did they buy?" and "which serial do I revoke?".
- **Self-verification at signing time** — the script verifies each key against its own public key
  before printing it, so a broken key is never sent to a paying customer.
- **Expiry** — time-limited keys stop validating after their date, covering a subscription model.
- **Trial that can't be farmed** — full Pro capped at 3 DOCX exports, shadow-recorded so deleting
  `license.json` doesn't mint more.
- **No Pro backdoor in shipped builds** — the developer key is `#if DEBUG` only.
- **Licensing is actually tested** — four licensing tests were permanently failing (they signed with
  a throwaway keypair and verified against the production public key, which can never succeed), so
  the paywall was effectively untested. Fixed, plus coverage for revocation, foreign issuers,
  expiry, and the integrity of the embedded key itself.

---

## Known gaps you are accepting

Worth deciding consciously rather than discovering later:

These apply to the **offline signed-key** model. The Lemon Squeezy online path (below) closes all
three, at the cost of needing the network.

- **Revocation needs an app update to take effect.** A refunded customer keeps Pro until they
  update. That is inherent to offline keys; the alternative is an activation server you don't have.
- **No device or seat limit.** One key works on unlimited machines. Fine for a one-person licence
  sold on trust.
- **No automatic key re-delivery.** If a customer loses their key, you look it up in the ledger and
  resend by hand.

## The online path (Lemon Squeezy keys)

Switched on with `MARKSMITH_LS_ACTIVATION=1`. Lemon Squeezy generates and emails the key on
purchase, so there is no backend and nothing to run per sale. In exchange:

- Refunds and chargebacks **do** revoke Pro, via a background re-check every 3 days — no app update
  needed.
- Seat limits work, and deactivating in Settings hands the seat back.
- A reinstall that hits the activation limit recovers on its own rather than needing a support email.
- Activation and re-validation need the network. An unreachable server never revokes anything: Pro
  keeps working for 30 days since the last successful check.

What you are accepting instead: a customer who is permanently offline loses Pro after 30 days, and
the app depends on Lemon Squeezy's API being up at activation time. Note also that Lemon Squeezy is
mid-migration into Stripe Managed Payments — no sunset date announced, but worth watching, and a
reason to keep the offline signed-key path working rather than deleting it.
