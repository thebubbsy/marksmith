// Marksmith Connector — unit tests for the capture-hygiene helpers.
// Run: node extension/hygiene.test.js   (from the repo root)
const h = require('./hygiene.js');

let pass = 0, fail = 0;
const t = (name, cond) => { if (cond) { pass++; console.log("ok  -", name); } else { fail++; console.log("FAIL-", name); } };

// 1) Citation pips
const stripped = h.stripCitationPips("Line one.【7†source】\n\nLine two 【8†source】more.\n\nTrailing 【7source】.");
t("pips stripped", !/【/.test(stripped));
t("content preserved", stripped.includes("Line one.") && stripped.includes("Trailing"));

// 2) DLP scan
const dlp1 = h.scanDlp("Call 555-123-4567 or email bob@example.com. sk-abcdefghijklmnopqrstuvwxyz1234");
t("DLP finds phone/email/apikey", dlp1.includes("phone number") && dlp1.includes("email address") && dlp1.includes("API key"));
t("DLP no false positive on prose", h.scanDlp("Just a normal paragraph about the 2026 fiscal plan.").length === 0);

// 3) Smart filename (title + date)
const name = h.buildFilename({ title: 'My Chat: final?' }, 'docx');
t("filename has date", /^My Chat final \d{4}-\d{2}-\d{2}\.docx$/.test(name));
t("filename fallback", /^marksmith-export \d{4}-\d{2}-\d{2}\.pdf$/.test(h.buildFilename(null, 'pdf')));

// 4) Capture history (chrome.storage.local stub)
const store = {};
globalThis.chrome = { storage: { local: {
    get: async (k) => { const key = typeof k === "string" ? k : Object.keys(k)[0]; return { [key]: store[key] }; },
    set: async (o) => { Object.assign(store, o); },
} } };

(async () => {
    await h.pushHistory({ markdown: "hello world", meta: { title: "T" } });
    await h.pushHistory({ markdown: "second", meta: null });
    const list = await h.getHistory();
    t("history newest first", list.length === 2 && list[0].text === "second" && list[1].text === "hello world");
    for (let i = 0; i < 40; i++) await h.pushHistory({ markdown: "x" + i });
    t("history caps at 25", (await h.getHistory()).length === 25);
    console.log(`\n${pass} passed, ${fail} failed`);
    process.exit(fail ? 1 : 0);
})();
