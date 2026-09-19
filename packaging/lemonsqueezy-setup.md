# Lemon Squeezy Store Setup Guide

This guide walks through configuring [Lemon Squeezy](https://www.lemonsqueezy.com) as the Merchant of Record (MoR) for selling **MarkSmith Pro** licenses.

---

## Overview

MarkSmith supports two licensing models:
1. **Offline Signed Keys (Default / Recommended)**: RSA-256 cryptographically signed tokens. No network connection or external server required for validation.
2. **Online Activation via Lemon Squeezy API (Optional)**: Direct server-side validation against Lemon Squeezy's License API (`LemonSqueezyClient.Enabled = true`). Supports machine activation tracking and remote deactivation.

---

## Step 1: Create Lemon Squeezy Account

1. Navigate to [lemonsqueezy.com](https://www.lemonsqueezy.com) and sign up for an account.
2. Complete email verification and log in to the Lemon Squeezy dashboard.

---

## Step 2: Create and Configure Your Store

1. In the dashboard, click **Create Store** (or select **Settings → Stores**).
2. Enter your store details:
   - **Store Name**: `MarkSmith` (or your company / trading name)
   - **Store URL**: `https://your-store.lemonsqueezy.com`
   - **Currency**: Select **AUD (A$)** or your preferred default billing currency.
   - **Country & Payout Details**: Complete your KYC and payout account setup (Stripe / bank transfer).

---

## Step 3: Create the 'Marksmith Pro' Product

1. In the sidebar, navigate to **Products** and click **+ New Product**.
2. Fill in the product details:
   - **Product Name**: `Marksmith Pro`
   - **Product Description**: Markdown editor and document publishing powerhouse. Perpetual license for MarkSmith Pro features including DOCX & PPTX export, batch conversion, watch folders, clipboard automation, and watermark-free exports.
   - **Product Type**: **Digital Product**
   - **Pricing Model**: **Single payment** (One-time purchase)
   - **Price**: **A$39.00** (AUD 39.00)
3. Upload product branding / banner images if desired (assets available in `packaging/store/assets/`).

---

## Step 4: Enable License Keys

1. In the product edit screen, scroll down to the **License Keys** section and toggle **Generate license keys** to **ON**.
2. Configure license key settings:
   - **Activation limit**: Set to **`3`** machines (allows activation across up to 3 personal devices).
   - **Limit license key instances per customer**: Set or leave default.
   - **Expiry**: Leave blank (perpetual license) or configure if offering timed renewals.
3. Click **Save Product** (or **Publish**).

---

## Step 5: Copy Your Buy URL

1. Go to **Products** and select **Marksmith Pro**.
2. Click **Share** (or open the **•••** menu → **Share**).
3. Under **Direct Link / Checkout Link**, copy the URL. It will look like:
   ```text
   https://YOUR-STORE.lemonsqueezy.com/buy/YOUR-PRODUCT-ID
   ```
   or with a custom subdomain:
   ```text
   https://marksmith.lemonsqueezy.com/buy/12345678-abcd-1234-abcd-1234567890ab
   ```

---

## Step 6: Update `LicenseService.cs` with the Buy URL

Open `MarkSmith.Core/Services/LicenseService.cs` and replace the placeholder `StoreUrl` constant (around line 14):

```csharp
// In MarkSmith.Core/Services/LicenseService.cs:
public const string StoreUrl = "https://YOUR-STORE.lemonsqueezy.com/buy/YOUR-PRODUCT-ID";
```

Replace with your real URL copied in Step 5:

```csharp
public const string StoreUrl = "https://your-store.lemonsqueezy.com/buy/your-real-product-id";
```

> **Note**: `LicenseService.IsStoreConfigured` checks that the string does not contain `YOUR-STORE` or `YOUR-PRODUCT-ID`. Once updated, the "Buy Pro" button in the app will launch your real checkout page.

> The constant is now `DefaultStoreUrl`, and `MARKSMITH_STORE_URL` overrides it at runtime (see Step 8). The app opens `LicenseService.CheckoutUrl(email)`, which prefills the buyer's email when it knows it — Lemon Squeezy sends the licence key to whatever address goes through checkout, so a typo there is unrecoverable without a refund.

---

## Step 7: (Optional) Set Up Webhook for Automated License Key Generation / Delivery

Lemon Squeezy automatically generates and emails license keys to customers upon purchase when **License Keys** is enabled on the product.

If you choose to issue **Offline Signed Keys** via RSA signature instead:
1. In Lemon Squeezy, go to **Settings → Webhooks**.
2. Click **+ Add Webhook**:
   - **Callback URL**: Point to your backend endpoint (e.g. AWS Lambda / Cloudflare Worker / Vercel Serverless).
   - **Events**: Check `order_created`.
   - **Signing Secret**: Generate a secure secret and save it in your webhook environment.
3. In your webhook handler:
   - Verify the `X-Signature` HMAC SHA-256 header using your webhook secret.
   - Parse `data.attributes.user_email` from the payload.
   - Execute the offline key generator (`tools/licensing/sign-license.ps1` or equivalent RSA signing logic) with the customer's email.
   - Send the generated key to the customer via transactional email (Postmark, Resend, SendGrid).

---

## Step 8: Enable Online Activation

**If Step 4 enabled license keys on the product, this step is not optional.** Lemon Squeezy issues
plain UUID keys. They carry no signature, so they can never pass the offline check in
`LicenseValidator` — with online activation off, a paying customer pastes the key from their receipt
and is told it isn't valid.

Turn it on with an environment variable, no rebuild required:

```text
MARKSMITH_LS_ACTIVATION=1
```

Or change the default in `MarkSmith.Core/Services/LemonSqueezyClient.cs` if you'd rather bake it in.

With it on:

- **Activate** — `POST /v1/licenses/activate` claims a seat and returns an instance id, which is
  stored so the app doesn't reactivate on every launch.
- **Reinstall recovery** — if activation comes back at the machine limit (a reinstall loses the
  local instance id while Lemon Squeezy still holds the seat), the app falls back to
  `POST /v1/licenses/validate`, and honours the key if it's genuinely good. Without this, the third
  reinstall locks a customer out of software they paid for.
- **Re-validation** — `POST /v1/licenses/validate` runs in the background at most every 3 days
  (`LicenseService.RevalidateEveryDays`). This is what makes a refund or chargeback actually revoke
  Pro; activation alone happened once and can never notice.
- **Offline grace** — an unreachable server is never treated as a refusal. Pro keeps working for 30
  days (`LicenseService.OfflineGraceDays`) since the last successful check, then lapses. One
  successful check restores it.
- **Deactivate** — `POST /v1/licenses/deactivate` hands the seat back so the customer can move
  machines. The Settings button calls `LicenseService.DeactivateAsync()`, which releases the seat;
  the older sync `Deactivate()` only forgets the key locally and leaks the activation.
- **Expiry and status** — `expired` and `disabled` keys stop unlocking Pro.

None of these three endpoints take an API key. That is deliberate — a desktop binary can't keep a
secret. **Never put a Lemon Squeezy API key in the app.**

### Test mode without shipping a build

Lemon Squeezy's test checkout is a different URL. Point at it with an environment variable rather
than editing `StoreUrl` and rebuilding — that round trip is how a test link ends up in a production
release:

```text
MARKSMITH_STORE_URL=https://your-store.lemonsqueezy.com/buy/test-product-id
```

---

## Testing & Verification Checklist

- [ ] **Test Mode**: Enable **Test Mode** in Lemon Squeezy dashboard (**Settings → Stores → Test Mode**).
- [ ] **Test Purchase**: Perform a test checkout using Lemon Squeezy test card numbers.
- [ ] **Receive Key**: Verify that a test license key is issued in the test order receipt and email.
- [ ] **Activate in App**: Open MarkSmith → **Settings ⚙ → License → Enter Key** → Click **Activate**.
- [ ] **Verify Pro Features**: Confirm DOCX export, PPTX export, automation tools, and footer removal are active.
- [ ] **Test Deactivation**: Test deactivating the key and verifying machine instance count decrements on Lemon Squeezy.
- [ ] **Switch to Live**: Toggle off Test Mode and verify live checkout before launch.
