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

## Step 8: Enable Online Activation in `LemonSqueezyClient.cs`

If using Lemon Squeezy's License API for online activation and machine deactivation:

1. Open `MarkSmith.Core/Services/LemonSqueezyClient.cs`.
2. Change `Enabled` to `true`:
   ```csharp
   public static bool Enabled { get; set; } = true;
   ```
3. When `LemonSqueezyClient.Enabled` is `true`:
   - `LemonSqueezyClient.ActivateAsync(key)` validates against `https://api.lemonsqueezy.com/v1/licenses/activate`.
   - `LemonSqueezyClient.DeactivateAsync(key, instanceId)` validates against `https://api.lemonsqueezy.com/v1/licenses/deactivate`.
   - The app persists the machine `InstanceId` and allows deactivation / migration to new machines within the 3-machine limit.

---

## Testing & Verification Checklist

- [ ] **Test Mode**: Enable **Test Mode** in Lemon Squeezy dashboard (**Settings → Stores → Test Mode**).
- [ ] **Test Purchase**: Perform a test checkout using Lemon Squeezy test card numbers.
- [ ] **Receive Key**: Verify that a test license key is issued in the test order receipt and email.
- [ ] **Activate in App**: Open MarkSmith → **Settings ⚙ → License → Enter Key** → Click **Activate**.
- [ ] **Verify Pro Features**: Confirm DOCX export, PPTX export, automation tools, and footer removal are active.
- [ ] **Test Deactivation**: Test deactivating the key and verifying machine instance count decrements on Lemon Squeezy.
- [ ] **Switch to Live**: Toggle off Test Mode and verify live checkout before launch.
