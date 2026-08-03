// Client-side DLP scan + masking — mirrors MarkSmith.Core/Services/DlpScanService.cs EXACTLY (rules,
// mask styles, remediation text, redacted-context building, and secret-density scoring).
//
// Returns category labels, a MASKED preview of each match (never the raw value, except AWS Access
// Key IDs — see MASK.REVEAL below), a redacted-context string (the surrounding message with only
// matched spans blanked), and a density score: what SHARE of the message the matches made up.
// Density is what separates "a key buried in 2,000 characters of legitimate text" (low density —
// accidental) from "a key submitted alone as the entire message" (density near 1.0 — looks
// deliberate) — without ever needing the raw secret value to tell the two apart.
//
// Loaded before governance.js (see manifest.json content_scripts order) and exposes
// window.MarksmithDlp = { scan(text) }.

(function () {
  const MASK = { FULL: "full", EDGES: "edges", LAST4: "last4", LOCAL: "local", REVEAL: "reveal" };
  const MAX_CONTEXT_CHARS = 500; // data minimization cap, matches the server

  const RULES = [
    // AWS Access Key ID (AKIA/ASIA-prefixed) is an IDENTIFIER, not the secret half of the
    // credential pair — it's already recorded in every CloudTrail event and visible in IAM. The
    // actual secret (the paired Secret Access Key) has no fixed prefix and isn't reliably
    // regex-matchable; if labelled, it falls under Credential/API key below and stays masked
    // there. Revealing the Access Key ID costs nothing and lets an investigator search CloudTrail
    // immediately, so it's shown in full — in both the masked-match table and the context.
    ["AWS access key", /\b(AKIA|ASIA)[0-9A-Z]{16}\b/g, MASK.REVEAL,
      "Rotate this key in IAM immediately and check CloudTrail for unauthorized use."],
    ["API key", /\b(sk|pk)-[A-Za-z0-9]{20,}\b/g, MASK.EDGES,
      "Revoke and reissue this API key from the issuing service's dashboard."],
    ["GitHub token", /\bgh[pousr]_[A-Za-z0-9]{36,}\b/g, MASK.EDGES,
      "Revoke this token in GitHub Settings -> Developer settings, then reissue."],
    ["Private key", /-----BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----/g, MASK.FULL,
      "Treat the corresponding key pair as fully compromised; regenerate and redeploy."],
    ["Credential", /\b(password|passwd|secret|api[_ ]?key|bearer|authorization:)\b\s*[:=]?\s*\S+/gi, MASK.FULL,
      "Rotate the affected password/credential; check for reuse elsewhere."],
    ["Credit-card-like number", /\b(?:\d[ \-]?){13,16}\b/g, MASK.LAST4,
      "Verify with the cardholder; contact the card issuer if this was a real number."],
    ["SSN-like number", /\b\d{3}[ \-]?\d{2}[ \-]?\d{4}\b/g, MASK.LAST4,
      "Confirm with the individual; consider identity-theft monitoring if genuine."],
    ["Email address", /\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b/g, MASK.LOCAL,
      "Confirm this recipient/identity was intended to be shared."],
  ];

  function maskEdges(v) {
    if (v.length <= 8) return "*".repeat(v.length);
    return v.slice(0, 4) + "*".repeat(Math.max(4, v.length - 8)) + v.slice(-4);
  }

  function maskLast4(v) {
    const digits = [...v].filter((c) => /\d/.test(c));
    const keepFrom = Math.max(0, digits.length - 4);
    let seen = 0, out = "";
    for (const c of v) {
      if (/\d/.test(c)) { out += seen >= keepFrom ? c : "•"; seen++; }
      else out += c;
    }
    return out;
  }

  function maskLocal(email) {
    const at = email.indexOf("@");
    if (at <= 0) return "[redacted]";
    const local = email.slice(0, at), domain = email.slice(at);
    const visible = local.length <= 2 ? local.slice(0, 1) : local.slice(0, 2);
    return visible + "*".repeat(Math.max(1, local.length - visible.length)) + domain;
  }

  function mask(value, style) {
    switch (style) {
      case MASK.FULL: return "[redacted — value not stored]";
      case MASK.EDGES: return maskEdges(value);
      case MASK.LAST4: return maskLast4(value);
      case MASK.LOCAL: return maskLocal(value);
      case MASK.REVEAL: return value; // identifier, not a secret
      default: return "[redacted]";
    }
  }

  // Rebuilds `text` with every matched span replaced by a category marker — except REVEAL-style
  // spans, which keep their real text (matching the masked-match table). Everything else (the
  // actual surrounding words) is preserved verbatim.
  function buildRedactedContext(text, spans) {
    if (spans.length === 0) return "";
    const sorted = [...spans].sort((a, b) => a.start - b.start);
    let out = "", pos = 0, lastEnd = -1;
    for (const s of sorted) {
      if (s.start < lastEnd) continue; // overlapping match already covered
      if (s.start > pos) out += text.slice(pos, s.start);
      out += s.placeholder;
      pos = s.start + s.length;
      lastEnd = pos;
    }
    if (pos < text.length) out += text.slice(pos);
    return out.length > MAX_CONTEXT_CHARS ? out.slice(0, MAX_CONTEXT_CHARS) + "…" : out;
  }

  // Defense-in-depth: re-run every non-REVEAL rule against the built context and blank any
  // residual match, guarding against a bug in span math ever leaving a raw secret in context.
  function redactResidual(context) {
    if (!context) return context;
    let result = context;
    for (const [label, rx, style] of RULES) {
      if (style === MASK.REVEAL) continue;
      result = result.replace(new RegExp(rx.source, rx.flags), `[${label}]`);
    }
    return result;
  }

  function scan(text) {
    const flags = [];
    const matches = [];
    const spans = [];
    const perCategoryCount = {};
    let hits = 0;
    let secretChars = 0;

    for (const [label, rx, style, remediation] of RULES) {
      const found = [...text.matchAll(rx)];
      if (found.length === 0) continue;
      flags.push(label);
      hits += found.length;
      for (const m of found) {
        secretChars += m[0].length;
        spans.push({ start: m.index, length: m[0].length, placeholder: style === MASK.REVEAL ? m[0] : `[${label}]` });
        perCategoryCount[label] = perCategoryCount[label] || 0;
        if (perCategoryCount[label] < 5) { // cap stored MASKED previews per category
          matches.push({ category: label, masked: mask(m[0], style), remediation });
          perCategoryCount[label]++;
        }
      }
    }

    if (hits === 0) return { flags, hits: 0, matches, redactedContext: "", secretDensity: 0 };

    const redactedContext = redactResidual(buildRedactedContext(text, spans));
    const secretDensity = text.length > 0 ? Math.min(1, secretChars / text.length) : 0;
    return { flags, hits, matches, redactedContext, secretDensity };
  }

  window.MarksmithDlp = { scan };
})();
