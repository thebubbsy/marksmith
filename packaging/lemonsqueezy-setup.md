# Selling Marksmith Pro with Lemon Squeezy

Lemon Squeezy (LS) is a **Merchant of Record** — it becomes the seller, so it collects and remits
**GST/VAT/sales tax worldwide for you**, handles checkout, generates + emails license keys, and pays
you out. For an Australian selling globally that removes the tax headache (and AU GST at the $75k
threshold). LS fee is ~**5% + 50¢** per sale.

> Claude can't create your account or enter your details — that part is yours. Everything below is
> prepared so signup + config takes ~15 minutes. Do it all in **Test mode** first.

## Recommended path: LS-native license keys (no server)

The app already speaks LS's license API (`LemonSqueezyClient`). You don't need to host anything — LS
generates and emails keys, and the app validates them.

### 1. Account + store
1. Sign up at **lemonsqueezy.com** (start in **Test mode** — toggle top-right).
2. **Settings → Stores →** create a store: name `Marksmith`, upload the logo
   (`packaging/store/assets/Store-Logo-512x512.png`).
3. Add a payout method (bank/PayPal) — needed before going live, not for testing.

### 2. Product
1. **Products → New Product.**
2. Name: **Marksmith Pro**. Description: pull from `packaging/store/listing.md`.
3. Pricing — **one-time**, **AUD $39** (set "Founder's price" $29 if you want a launch discount).
   - *(Optional annual subscription)*: add a second variant, **AUD $34.99 / year**.
4. Media: upload the logo + a screenshot from `packaging/store/screenshots/`.

### 3. Turn on license keys
On the product/variant → **License keys**:
- **Enable license keys.**
- **Activation limit:** e.g. **3** (devices per license). The app sends `instance_name = machine name`.
- **Length/expiry:** no expiry for the perpetual $39 license. For the annual variant, set the key to
  expire in 1 year (subscription renewals reissue).
- LS auto-generates a key per purchase and **emails it to the buyer** on the receipt.

### 4. Wire the app
1. **Product → Share → copy the Buy link** (looks like `https://<store>.lemonsqueezy.com/buy/<uuid>`).
2. Paste it into `MdToPdf/Services/LicenseService.cs` → `StoreUrl` (the in-app **Buy Marksmith Pro**
   button opens it).
3. Set `MdToPdf/Services/LemonSqueezyClient.cs` → `Enabled => true`.
4. Rebuild.

### 5. Test it (Test mode)
1. Make a **test purchase** (LS provides test card `4242 4242 4242 4242`).
2. Copy the license key from the confirmation email/receipt.
3. In Marksmith: **Settings ⚙ → License → paste key → Activate.** The app calls LS `/activate`; on
   success the **PRO** badge disappears, DOCX + automation unlock, and the state persists across
   restarts (via the stored activation instance).
4. Test **deactivate** removes it.

### 6. Go live
1. Switch LS from **Test → Live**, confirm the live Buy link (update `StoreUrl` if the id differs),
   finish payout/tax details in LS.
2. Ship the build (via your GitHub release / installer). Done.

## Advanced path: your own signed keys via webhook (optional)

If you'd rather issue the **offline RSA-signed keys** (perpetual, verified with no network — see
`tools/licensing/`), keep `LemonSqueezyClient.Enabled = false` and instead:
1. LS → **Settings → Webhooks →** add your endpoint, subscribe to `order_created`.
2. Your endpoint runs `tools/licensing/sign-license.ps1 -Email <buyer>` and emails the key.
3. Host the endpoint anywhere (a Cloudflare Worker / small function). This needs a server; the
   LS-native path above does not — prefer native keys unless you specifically want offline perpetual
   keys or per-key revocation control.

## Pricing (AUD) — as configured above

| Tier | Price | Notes |
| --- | --- | --- |
| Free | $0 | PDF, all themes, AI cleanup, "Made with Marksmith" footer |
| Pro (one-time) | **$39** (launch $29) | DOCX + editable math, automation, no footer — perpetual |
| Pro (annual, optional) | $34.99/yr | Same, always-latest |
| Teams / Business (later) | ~$8/user/mo | Governance dashboard + central config |

## Notes
- Prices in LS are set once; LS shows local currency + adds tax at checkout automatically.
- Refunds: honour a 14–30 day window (also good for Australian Consumer Law) — refund from the LS
  dashboard; the key stays "active" unless you also revoke it (native keys) or let a subscription
  lapse.
- Keep **Test mode** purchases out of your live numbers — they're separate.
