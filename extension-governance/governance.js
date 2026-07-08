// Marksmith Governance Monitor — content script.
//
// TRANSPARENT BY DESIGN, always-on for this extension (unlike Marksmith Connector, where
// governance is opt-in). This IS the governance product, so it:
//   1. Shows a one-time consent notice the employee must acknowledge before anything is reported.
//   2. Displays a PERSISTENT, always-visible banner while monitoring is active — it cannot be
//      hidden. Covert operation is intentionally not supported.
//   3. Reports METADATA (which AI tool, page title/topic, message size, active TIME SPENT) and
//      MASKED data-loss-prevention findings (category + a masked preview + a remediation hint) —
//      never the full matched secret, EXCEPT an AWS Access Key ID, which is an identifier (not
//      the secret half of the credential pair) and is shown in full. When something IS flagged,
//      the surrounding message is also reported with just the flagged span(s) blanked in place,
//      plus what share of the message they made up — this is what lets a security team tell "a
//      key buried in a large paste" (accidental) apart from "a key submitted alone" (deliberate)
//      without the value itself being the thing that makes that call. Clean messages (no DLP hit)
//      are NEVER reported beyond size/time — no context capture happens for the 99% of traffic
//      that doesn't trip a rule. See dlp-mask.js for the exact masking/redaction rules.

(async () => {
  const cfg = await getConfig();
  if (!cfg) return; // no policy pushed yet — extension is inert until an admin configures it

  const org = cfg.orgName || "your organization";
  showPersistentIndicator(org);
  watchComposer(cfg);
  watchTimeOnPage(cfg);
})();

async function getConfig() {
  // Managed policy (admin-pushed via Intune/GPO/managed storage) wins; Options-page config (for
  // a personal or pilot deployment) is the fallback.
  const managed = await chrome.storage.managed.get(null).catch(() => ({}));
  const sync = await chrome.storage.sync.get(null).catch(() => ({}));
  const c = { ...sync, ...managed };
  return Object.keys(c).length ? c : null;
}

function assistantName() {
  const h = location.hostname;
  if (h.includes("openai") || h.includes("chatgpt")) return "ChatGPT";
  if (h.includes("gemini")) return "Gemini";
  if (h.includes("claude")) return "Claude";
  if (h.includes("copilot")) return "Copilot";
  return h;
}

// ---- Message-send tracking: DLP scan + metadata, reported when a prompt is submitted ---------

function watchComposer(cfg) {
  let lastText = "";

  const readComposer = () => {
    const el = document.querySelector('textarea, [contenteditable="true"], div[role="textbox"]');
    if (!el) return "";
    return (el.value ?? el.innerText ?? "").trim();
  };

  const sample = () => {
    const cur = readComposer();
    if (cur) { lastText = cur; return; }
    if (lastText && lastText.length > 1) { report(cfg, lastText); lastText = ""; }
  };

  setInterval(sample, 700);
  document.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey) {
      const cur = readComposer();
      if (cur) { report(cfg, cur); lastText = ""; }
    }
  }, true);
}

function report(cfg, sentText) {
  const dlp = window.MarksmithDlp.scan(sentText);
  const words = sentText.split(/\s+/).filter(Boolean).length;

  const payload = {
    charCount: sentText.length,
    wordCount: words,
    dlpFlags: dlp.flags,
    dlpHitCount: dlp.hits,
  };

  if (dlp.hits > 0) {
    if (cfg.captureMode === "raw") {
      payload.rawMessage = sentText;
    } else {
      payload.dlpMatches = dlp.matches;
      payload.redactedContext = dlp.redactedContext;
      payload.secretDensity = dlp.secretDensity;
    }
  }

  send(cfg, payload);

  if (dlp.hits > 0) flashDlpWarning(dlp.flags);
}

// ---- Time-on-page tracking: active + visible + focused seconds, flushed periodically ----------
//
// A content script (not the MV3 service worker, which is ephemeral and unreliable for continuous
// timers) accumulates seconds while the tab is the visible, focused document, then flushes a
// heartbeat report every FLUSH_MS and again on page hide/unload so no time is lost.

const TICK_MS = 5000;
const FLUSH_MS = 60000;

function watchTimeOnPage(cfg) {
  let accumulated = 0;
  let lastTick = Date.now();

  const isActive = () => document.visibilityState === "visible" && document.hasFocus();

  setInterval(() => {
    const now = Date.now();
    if (isActive()) accumulated += (now - lastTick) / 1000;
    lastTick = now;
  }, TICK_MS);

  const flush = () => {
    const seconds = Math.round(accumulated);
    accumulated = 0;
    if (seconds > 0) send(cfg, { timeSpentSeconds: seconds });
  };

  setInterval(flush, FLUSH_MS);
  document.addEventListener("visibilitychange", () => { if (document.visibilityState === "hidden") flush(); });
  window.addEventListener("pagehide", flush);
}

function send(cfg, extra) {
  chrome.runtime.sendMessage({
    type: "governance-report",
    payload: {
      user: cfg.userId || cfg.userEmail || "unknown",
      device: cfg.deviceId || "",
      assistant: assistantName(),
      url: location.origin + location.pathname,
      title: (document.title || "").slice(0, 120),
      timeSpentSeconds: 0,

      ...extra,
    },
  });
}

// ---- Transparent UI ----------------------------------------------------------

function showConsentNotice(org, policyUrl) {
  return new Promise((resolve) => {
    const back = el("div", {
      style: `position:fixed;inset:0;background:rgba(0,0,0,.6);z-index:2147483647;
              display:flex;align-items:center;justify-content:center;font-family:system-ui,sans-serif;`,
    });
    const box = el("div", {
      style: `background:#1c1c28;color:#e8e8f0;max-width:460px;padding:26px;border-radius:14px;
              border:1px solid #2a2a3a;box-shadow:0 20px 60px rgba(0,0,0,.5);`,
    });
    box.innerHTML = `
      <div style="font-size:17px;font-weight:700;margin-bottom:10px">AI usage on this device is monitored</div>
      <div style="font-size:14px;line-height:1.55;color:#c8c8d8">
        ${esc(org)} records <b>which AI tools you use, how long, and whether sensitive data (keys,
        credentials, personal info) appears</b> in what you send. <b>The content of your prompts and
        replies is never captured</b> for ordinary messages. <b>Only when something is flagged</b>,
        the surrounding message is also kept with the sensitive part blanked out (e.g.
        <code>password: [redacted]</code>) — so the security team can tell a key accidentally
        pasted alongside other work apart from one sent alone, without seeing most secret values in
        full. Most matched values stay masked (e.g. a credit card shows only its last 4 digits); an
        AWS key ID is shown in full because it's an identifier, not the secret itself.
        ${policyUrl ? `<br><br><a href="${esc(policyUrl)}" target="_blank" style="color:#8ab4ff">Read the full policy</a>` : ""}
      </div>
      <div style="display:flex;gap:10px;margin-top:20px;justify-content:flex-end">
        <button id="mdgov-decline" style="background:transparent;border:1px solid #3a3a4a;color:#e8e8f0;padding:9px 16px;border-radius:8px;cursor:pointer">Don't use AI here</button>
        <button id="mdgov-accept" style="background:#7c4dff;border:none;color:#fff;padding:9px 18px;border-radius:8px;cursor:pointer;font-weight:600">I understand</button>
      </div>`;
    back.appendChild(box);
    document.documentElement.appendChild(back);
    box.querySelector("#mdgov-accept").onclick = () => { back.remove(); resolve(true); };
    box.querySelector("#mdgov-decline").onclick = () => { back.remove(); resolve(false); };
  });
}

function showPersistentIndicator(org) {
  if (document.getElementById("mdgov-monitor-badge")) return;
  const bar = el("div", {
    id: "mdgov-monitor-badge",
    style: `position:fixed;bottom:14px;right:14px;z-index:2147483646;background:#1c1c28;color:#e8e8f0;
            border:1px solid #2a2a3a;border-left:4px solid #7c4dff;border-radius:10px;padding:8px 14px;
            font-family:system-ui,sans-serif;font-size:12.5px;box-shadow:0 6px 20px rgba(0,0,0,.4);
            display:flex;align-items:center;gap:8px;`,
  });
  bar.innerHTML = `<span style="width:8px;height:8px;border-radius:50%;background:#7c4dff;
      box-shadow:0 0 6px #7c4dff;display:inline-block"></span>
      AI usage monitored by ${esc(org)}`;
  document.documentElement.appendChild(bar);
}

function flashDlpWarning(flags) {
  const t = el("div", {
    style: `position:fixed;bottom:56px;right:14px;z-index:2147483646;background:#3a2020;color:#ffb4b4;
            border:1px solid #f85149;border-radius:10px;padding:10px 14px;font-family:system-ui,sans-serif;
            font-size:12.5px;max-width:280px;box-shadow:0 6px 20px rgba(0,0,0,.4);`,
  });
  t.textContent = "⚠ Sensitive data detected in your message: " + flags.join(", ") +
    ". This was flagged (masked) to your organization's policy.";
  document.documentElement.appendChild(t);
  setTimeout(() => t.remove(), 6000);
}

function el(tag, props) { const e = document.createElement(tag); Object.assign(e, props); if (props.style) e.setAttribute("style", props.style); return e; }
function esc(s) { return String(s).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c])); }
