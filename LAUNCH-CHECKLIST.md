# Marksmith — launch & money checklist

The strategy in one line: **prove someone wants it before building more, then treat distribution as the
real job.** Work top to bottom; don't skip a gate. Tick boxes as you go.

---

## Phase 0 — Already done ✅ (momentum check)

- [x] Product built — cleanup engine, PDF + DOCX, **editable equations**, themes, live editor, browser
      extension, automation
- [x] Proprietary EULA + third-party notices
- [x] Licensing/entitlement layer — Free / 14-day Trial / Pro, signed keys
- [x] Lemon Squeezy integration prepped (`packaging/lemonsqueezy-setup.md`)
- [x] Packaging kit — Microsoft Store / winget / Chocolatey + installer + CI/release automation
- [x] Landing page (`site/`) + shareable Artifact

You've done the expensive part. Everything below is time, not code.

---

## Phase 1 — Prove someone wants it 🎯 (this week, ~$0)

Goal: a **real demand signal** before spending another day building.

- [ ] Put the landing page on a real address — buy **marksmith.app** (or a free Netlify / Cloudflare
      Pages subdomain) and deploy `site/`.
- [ ] Swap the `mailto:` buttons for a real **waitlist** (free Tally or Formspree form).
- [ ] Cut a **30–60s demo video** (you already have the recording) for social.
- [ ] Post it where the niche actually is: r/ChatGPT, r/students, r/consulting, X, a relevant Discord.
      Ask for blunt reactions, not praise.
- [ ] (Optional) Product Hunt "Coming soon" page.

**✅ Gate — move on only when:** ~**25–50 waitlist emails**, or **≥10 people say "I'd pay for this"**,
from genuine posts. If two good posts get crickets → read *"If validation fails"* at the bottom.

---

## Phase 2 — Turn on payments 💳 (a weekend)

Goal: a stranger can buy and get a working key. *(Account creation is yours — I can't do it.)*

- [ ] Create a **Lemon Squeezy** account in **Test mode**.
- [ ] Create product **Marksmith Pro**, **A$39**, enable **license keys** (per `lemonsqueezy-setup.md`).
- [ ] Generate **your own** signing keypair (`tools/licensing/generate-keys.ps1`) and paste the public
      key into `LicenseValidator.PublicKeyPem`.
- [ ] Paste the LS **Buy link** into `LicenseService.StoreUrl`; set `LemonSqueezyClient.Enabled = true`;
      rebuild.
- [ ] **Test purchase end-to-end:** buy → key emailed → Activate in app → Pro unlocks → survives restart.
- [ ] Switch LS to **Live**; add payout + tax details.

**✅ Gate:** a test purchase unlocks Pro cleanly.

---

## Phase 3 — Make it publicly installable 📦 (a day)

Goal: you can send anyone a link and they can install and run it.

- [ ] Decide hosting: **keep the source private, host only the installer publicly** (recommended for a
      proprietary product) — or make the repo public.
- [ ] Cut the **v1.0 release** (the release workflow builds the installer) at a public download URL.
- [ ] Put the real **SHA256** into the winget + Chocolatey manifests → submit the winget PR, `choco push`.
- [ ] (Optional) **Microsoft Store** submission (Partner Center, one-time $19).

**✅ Gate:** send the link to a friend → they install and run it with no help.

---

## Phase 4 — Get users 🚀 (ongoing — this is the actual work)

Goal: first sales, then *repeatable* traffic. This is where money is made or lost.

- [ ] Launch to the **waitlist** — download link + founder's price.
- [ ] Real launch: **Product Hunt / Reddit / X** with the demo video.
- [ ] Write 2–3 posts targeting the exact search ("turn ChatGPT into a Word doc", "remove AI formatting")
      — that's SEO that compounds.
- [ ] Ask every user for **one line of feedback**; fix the #1 complaint fast.

**Milestones:** [ ] first sale · [ ] first **A$100** · [ ] first **A$1,000**

---

## Phase 5 — Grow or pivot 📈 (after you have data)

Goal: raise the ceiling.

- [ ] Look at what converts and what people keep asking for.
- [ ] Decide the **reach lever** — a **web version** or the **browser extension doing the whole job**.
      Windows-desktop-only is the #1 thing capping this; the ChatGPT crowd lives on web/mobile.
- [ ] If any organisation bites, stand up the **Teams / governance tier** (per-seat = real recurring
      revenue).

---

## Reality check (keep it honest)

- **Costs are near-zero** (local app), so even a trickle is almost pure margin.
- **Realistic outcomes:** $0–a few hundred AUD/month solo without strong marketing; **$1–5k/month** with
  a sharp niche + real distribution; a full income only if you crack **reach** (web/mobile).
- The single biggest lever is **not another feature** — it's getting in front of more of the right people.

## If validation fails (Phase 1 crickets after real effort)

Don't pour months in. Either **(a)** sharpen the pitch to **one** specific audience and retry, or
**(b)** shelve it as a strong portfolio piece and reuse the parts (the LaTeX→OMML converter, the
licensing layer, the DOCX engine) elsewhere. Learning that cheaply *is* a win.
