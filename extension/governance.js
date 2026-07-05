// Marksmith — AI Usage Governance content script.
//
// TRANSPARENT BY DESIGN. This script does nothing unless an administrator has explicitly enabled
// org mode (via managed policy or the Options page). When enabled it:
//   1. Shows a one-time consent notice the employee must acknowledge.
//   2. Displays a PERSISTENT, always-visible banner that monitoring is active — it cannot be
//      hidden while org mode is on. Covert operation is intentionally not supported.
//   3. Reports METADATA ONLY (which AI tool, page title/topic, message size, and DLP category
//      flags). It never captures or transmits the text of prompts or replies.
//
// The matched sensitive values are never sent — only the category label (e.g. "AWS key") and a
// count — so the leak-risk signal reaches the admin without the secret itself doing so.

(async () => {
  const cfg = await getConfig();
  if (!cfg || !cfg.orgMode) return; // personal users are entirely unaffected

  const org = cfg.orgName || "your organization";
  const consentKey = "mdpdfm_consent_" + (cfg.orgId || "default");

  const consented = (await chrome.storage.local.get(consentKey))[consentKey];
  if (!consented) {
    const ok = await showConsentNotice(org, cfg.policyUrl);
    if (!ok) return; // employee declined — nothing is reported
    await chrome.storage.local.set({ [consentKey]: { at: Date.now() } });
  }

  showPersistentIndicator(org);
  watchComposer(cfg);
})();

async function getConfig() {
  // Managed policy (admin-pushed via Intune/GPO) wins; Options-page config is the fallback.
  const managed = await chrome.storage.managed.get(null).catch(() => ({}));
  const sync = await chrome.storage.sync.get(null).catch(() => ({}));
  const c = { ...sync, ...managed };
  return c.orgMode ? c : null;
}

// ---- Client-side DLP: mirrors DlpScanService.cs. Returns category labels + count, never values.
const DLP_RULES = [
  ["AWS access key", /\b(AKIA|ASIA)[0-9A-Z]{16}\b/g],
  ["API key", /\b(sk|pk)-[A-Za-z0-9]{20,}\b/g],
  ["GitHub token", /\bgh[pousr]_[A-Za-z0-9]{36,}\b/g],
  ["Private key", /-----BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----/g],
  ["Credential", /\b(password|passwd|secret|api[_ ]?key|bearer|authorization:)\b\s*[:=]?\s*\S+/gi],
  ["Credit-card-like number", /\b(?:\d[ \-]?){13,16}\b/g],
  ["SSN-like number", /\b\d{3}[ \-]?\d{2}[ \-]?\d{4}\b/g],
  ["Email address", /\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b/g],
];

function scanDlp(text) {
  const flags = [];
  let hits = 0;
  for (const [label, rx] of DLP_RULES) {
    const m = text.match(rx);
    if (m) { flags.push(label); hits += m.length; }
  }
  return { flags, hits };
}

function assistantName() {
  const h = location.hostname;
  if (h.includes("openai") || h.includes("chatgpt")) return "ChatGPT";
  if (h.includes("gemini")) return "Gemini";
  if (h.includes("claude")) return "Claude";
  return h;
}

// Reports when the user SENDS a prompt. We detect a send by watching the composer empty out after
// holding text (Enter or send-button submit), then report the metadata of what was just sent.
function watchComposer(cfg) {
  let lastText = "";

  const readComposer = () => {
    const el = document.querySelector(
      'textarea, [contenteditable="true"], div[role="textbox"]');
    if (!el) return "";
    return (el.value ?? el.innerText ?? "").trim();
  };

  const sample = () => {
    const cur = readComposer();
    if (cur) { lastText = cur; return; }
    // composer just went from non-empty to empty → a submit happened
    if (lastText && lastText.length > 1) {
      report(cfg, lastText);
      lastText = "";
    }
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
  const dlp = scanDlp(sentText);
  const words = sentText.split(/\s+/).filter(Boolean).length;

  // Metadata + flags ONLY. sentText is used locally for counting/scanning and never leaves here.
  chrome.runtime.sendMessage({
    type: "governance-report",
    payload: {
      user: cfg.userId || cfg.userEmail || "unknown",
      device: cfg.deviceId || "",
      assistant: assistantName(),
      url: location.origin + location.pathname,
      title: (document.title || "").slice(0, 120),
      charCount: sentText.length,
      wordCount: words,
      dlpFlags: dlp.flags,
      dlpHitCount: dlp.hits,
      consentAcknowledged: true,
    },
  });

  if (dlp.hits > 0) flashDlpWarning(dlp.flags);
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
        ${esc(org)} records <b>which AI tools you use, when, and whether sensitive data (keys,
        credentials, personal info) appears</b> in what you send. <b>The content of your prompts and
        replies is never captured</b> — only metadata and data-loss flags.
        ${policyUrl ? `<br><br><a href="${esc(policyUrl)}" target="_blank" style="color:#8ab4ff">Read the full policy</a>` : ""}
      </div>
      <div style="display:flex;gap:10px;margin-top:20px;justify-content:flex-end">
        <button id="mdpdfm-decline" style="background:transparent;border:1px solid #3a3a4a;color:#e8e8f0;padding:9px 16px;border-radius:8px;cursor:pointer">Don't use AI here</button>
        <button id="mdpdfm-accept" style="background:#7c4dff;border:none;color:#fff;padding:9px 18px;border-radius:8px;cursor:pointer;font-weight:600">I understand</button>
      </div>`;
    back.appendChild(box);
    document.documentElement.appendChild(back);
    box.querySelector("#mdpdfm-accept").onclick = () => { back.remove(); resolve(true); };
    box.querySelector("#mdpdfm-decline").onclick = () => { back.remove(); resolve(false); };
  });
}

function showPersistentIndicator(org) {
  if (document.getElementById("mdpdfm-monitor-badge")) return;
  const bar = el("div", {
    id: "mdpdfm-monitor-badge",
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
    ". This was flagged to your organization's policy.";
  document.documentElement.appendChild(t);
  setTimeout(() => t.remove(), 6000);
}

function el(tag, props) { const e = document.createElement(tag); Object.assign(e, props); if (props.style) e.setAttribute("style", props.style); return e; }
function esc(s) { return String(s).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c])); }
