# Marksmith — five-year plan (2026–2031)

The strategy in one line: **own the "AI output → real document" wedge, then widen it — desktop →
web → teams — while the proprietary MD-to-Word engine stays the moat.**

Companion docs: [LAUNCH-CHECKLIST.md](LAUNCH-CHECKLIST.md) (the next 90 days),
[ROADMAP.md](ROADMAP.md) (feature-level Now/Next/Later). This file is the altitude above both.

> Honesty clause: years 2–5 are *contingent* plans. Each year has a **gate** — if the gate isn't
> met, the plan says what to do instead. A five-year plan that only describes success is fiction.

---

## Year 1 (2026–27) — Prove it. Ship it. First A$1,000.

**Theme: validation and a real launch.**

- Ship v1.x on Windows: landing page live on a domain, Lemon Squeezy checkout, GitHub releases
  public (installer hosted publicly), winget + Chocolatey submitted, Microsoft Store listing.
- Market the wedge relentlessly: "Copy as Markdown" browser button as the free hook; the
  **proprietary MD-to-Word engine** (editable equations, ShapeForge™ native-shape diagrams,
  branding kit) as the paid differentiator.
- Content flywheel started: the two SEO articles, demo videos, PH/Reddit/HN launches.
- Targets: **500+ installs · 25+ Pro sales (~A$1,000) · one repeatable acquisition channel.**

**⛩ Gate:** ≥ 25 paying customers by mid-2027, or clear qualitative pull (users asking for more).
**If missed:** stop building, run 10 customer interviews, either re-position to ONE niche
(consultants? students? researchers?) and retry for one quarter — or shelve gracefully and keep it
as a portfolio asset. Do not proceed to Year 2 spending on a product nobody pulled.

## Year 2 (2027–28) — Escape the desktop ceiling. First A$1,000/month.

**Theme: reach.** Windows-only caps the market more than any feature gap.

- **Extension-does-the-whole-job**: convert + download PDF/DOCX directly from the browser button
  (the desktop app becomes the power tier, not the entry ticket).
- **marksmith.app web converter**: paste → polished document in the browser. Free tier watermarked,
  Pro unlock shared with desktop licenses. The MD-to-Word engine already runs on .NET — a thin
  server or WASM port keeps the moat intact.
- Pro (annual) subscription variant becomes the default web offer; perpetual stays on desktop.
- Targets: **10k web conversions/month · A$1,000 MRR-equivalent · macOS demand measured** (waitlist
  counter, not a build).

**⛩ Gate:** web funnel converts ≥ 1% visitor→signup and pays for its hosting.
**If missed:** stay desktop-first, price up (A$59), and treat Marksmith as a healthy micro-business
instead of a growth product.

## Year 3 (2028–29) — Sell to teams. First A$5,000/month.

**Theme: the money is in organizations, not individuals.**

- **Teams tier** (per-seat, ~A$8/user/mo): central output profiles (fonts/branding enforced
  org-wide), license pooling, priority support.
- **Governance add-on** matures: the consent-based AI-usage dashboard + DLP flags — compliance
  teams pay for visibility, employees get the best converter as the carrot. Transparent-by-design
  stays non-negotiable (it's both the ethics and the sales pitch).
- Delivery connectors (SharePoint/Drive/Slack) — the "it lands where work lives" feature.
- Targets: **5–10 paying orgs · A$5k/month blended · churn < 3%/mo.**

**⛩ Gate:** 3 orgs renew.
**If missed:** teams tier becomes "multi-seat discount" only; refocus on prosumer volume.

## Year 4 (2029–30) — Become the pipe, not just the app.

**Theme: platform.**

- **Public API / CLI** (hosted): `POST markdown → DOCX/PDF/PPTX` with the full engine — priced
  per-conversion for developers and no-code tools (Zapier/Make/Power Automate connectors).
- Template marketplace: branding kits, house styles, export presets — shareable, eventually sellable.
- The engine keeps compounding: real tracked-changes diffing, PPTX themes, EPUB polish.
- Targets: **API revenue ≥ 25% of total · 2+ integration partners shipping.**

**⛩ Gate:** developers integrate without hand-holding.
**If missed:** API stays an internal capability powering the web/teams tiers; skip marketplace.

## Year 5 (2030–31) — Own the category.

**Theme: "the document layer for AI work."**

- Whatever assistants exist by 2030, their output still has to land somewhere professional.
  Marksmith is the neutral, local-first, brand-enforcing document layer across them.
- Options unlocked by then (choose from strength, not desperation): macOS/mobile companions,
  enterprise self-hosted governance, or an acquisition conversation with a document/productivity
  player — the proprietary engine + the governance install base are the assets a buyer values.
- Target: **A$20k+/month blended, or a deliberate decision to run it calm and profitable at
  whatever level Year 4 proved.**

---

## Constants across all five years

1. **The moat is the engine.** Every year deepens the proprietary MD-to-Word conversion
   (equations, ShapeForge, branding, tracked changes) — it's the thing competitors and the
   platforms themselves don't bother to build properly.
2. **Local-first + transparent** stays the brand: no content harvesting, governance only with
   consent, cleanup always disclosed. In an era of AI distrust, that's a moat too.
3. **Costs stay near zero** until revenue justifies them (static site, MoR checkout, no servers
   before Year 2's web tier).
4. **Every year has a gate.** Passing gates funds the next year; missing them triggers the
   written fallback, not denial.

## Revenue picture (honest ranges, AUD)

| Year | Pessimistic | Base | Optimistic |
| --- | --- | --- | --- |
| 1 | ~$0 | $1–3k total | $10k total |
| 2 | $50/mo | $1k/mo | $4k/mo |
| 3 | $300/mo | $5k/mo | $15k/mo |
| 4 | $500/mo | $10k/mo | $40k/mo |
| 5 | run-calm | $20k/mo | acquisition |

The pessimistic column is real and survivable — costs are near zero, so even it doesn't kill the
project; it just means gates fire and the plan narrows.
