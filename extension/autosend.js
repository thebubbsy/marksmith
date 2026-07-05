// Marksmith Connector — end-of-conversation auto-send.
//
// OPT-IN (Options page). When enabled, watches the AI chat page and, once the assistant has
// finished replying and you've stopped interacting for a few seconds, sends the whole conversation
// to the Marksmith app. Pair it with the app's "Auto-generate PDF from AI-chat ingests" toggle and
// each finished conversation becomes a polished PDF with no clicks.
//
// It never sends mid-stream: any DOM change or keystroke resets the idle timer, so a reply that is
// still streaming keeps the timer alive. A content hash prevents re-sending an unchanged conversation.

(async () => {
  const cfg = await chrome.storage.sync.get({ autoSendIdle: false, idleSeconds: 20, port: 47821, output: null });
  if (!cfg.autoSendIdle) return;

  let lastActivity = Date.now();
  let lastHash = null;
  const bump = () => (lastActivity = Date.now());

  document.addEventListener("keydown", bump, true);
  document.addEventListener("input", bump, true);
  document.addEventListener("pointerdown", bump, true);
  new MutationObserver(bump).observe(document.body, { childList: true, subtree: true, characterData: true });

  setInterval(async () => {
    if (Date.now() - lastActivity < cfg.idleSeconds * 1000) return; // still active / streaming
    const md = extractConversation();
    if (!md || md.length < 60) return;
    const h = hash(md);
    if (h === lastHash) return; // nothing new since last send
    lastHash = h;
    try {
      await fetch(`http://127.0.0.1:${cfg.port}/api/ingest`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ markdown: md, output: cfg.output || undefined }),
      });
    } catch { /* app not running — try again next idle window */ }
  }, 4000);

  function hash(s) { let h = 0; for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0; return h; }

  function extractConversation() {
    const host = location.hostname;
    let roots = [];
    if (host.includes("chatgpt.com") || host.includes("chat.openai.com"))
      roots = [...document.querySelectorAll('[data-message-author-role="assistant"] .markdown, [data-message-author-role="assistant"] .prose')];
    else if (host.includes("gemini.google.com")) {
      roots = [...document.querySelectorAll("message-content .markdown")];
      if (!roots.length) roots = [...document.querySelectorAll("message-content, model-response")];
    } else if (host.includes("claude.ai"))
      roots = [...document.querySelectorAll('[data-testid="assistant-message"], .font-claude-message')];
    if (!roots.length) return "";
    return roots.map(conv).join("\n\n---\n\n").replace(/\n{3,}/g, "\n\n").trim();
  }

  // HTML -> Markdown walker (same conversion the toolbar button uses).
  function conv(n) {
    if (n.nodeType === Node.TEXT_NODE) return n.textContent;
    if (n.nodeType !== Node.ELEMENT_NODE) return "";
    const tag = n.tagName.toLowerCase();
    const kids = () => [...n.childNodes].map(conv).join("");
    switch (tag) {
      case "h1": return `\n# ${kids().trim()}\n`;
      case "h2": return `\n## ${kids().trim()}\n`;
      case "h3": return `\n### ${kids().trim()}\n`;
      case "h4": return `\n#### ${kids().trim()}\n`;
      case "p": return `\n${kids()}\n`;
      case "br": return "\n";
      case "strong": case "b": return `**${kids()}**`;
      case "em": case "i": return `*${kids()}*`;
      case "code": return n.closest("pre") ? kids() : "`" + kids() + "`";
      case "pre": {
        const code = n.querySelector("code");
        const lang = [...(code?.classList || [])].find((c) => c.startsWith("language-"))?.slice(9) || "";
        return `\n\`\`\`${lang}\n${(code || n).textContent.replace(/\n$/, "")}\n\`\`\`\n`;
      }
      case "ul": return "\n" + [...n.children].filter((c) => c.tagName === "LI").map((li) => "- " + conv(li).trim().replace(/\n/g, "\n  ")).join("\n") + "\n";
      case "ol": return "\n" + [...n.children].filter((c) => c.tagName === "LI").map((li, i) => `${i + 1}. ` + conv(li).trim().replace(/\n/g, "\n   ")).join("\n") + "\n";
      case "li": return kids();
      case "a": return n.href && !n.href.startsWith("javascript:") ? `[${kids()}](${n.href})` : kids();
      case "blockquote": return "\n" + kids().trim().split("\n").map((l) => `> ${l}`).join("\n") + "\n";
      case "hr": return "\n---\n";
      case "table": {
        const rows = [...n.querySelectorAll("tr")].map((tr) => "| " + [...tr.children].map((td) => conv(td).trim().replace(/\|/g, "\\|").replace(/\n/g, " ")).join(" | ") + " |");
        if (rows.length > 1) rows.splice(1, 0, "|" + " --- |".repeat(rows[0].split("|").length - 2));
        return "\n" + rows.join("\n") + "\n";
      }
      case "button": case "script": case "style": case "svg": case "noscript": return "";
      default: return kids();
    }
  }
})();
