// copybutton.test.js — the in-page buttons under each reply, run against the ChatGPT fixture.
// The content script once shipped with a syntax error (a lost `innerHTML = \``), which silently
// removed every button on every site; this runs the real file so that can't happen again.
// Run: node extension/tests/copybutton.test.js
const fs = require("fs");
const path = require("path");
const { JSDOM } = require("jsdom");

let passed = 0, failed = 0;
const t = (name, cond) => { if (cond) { passed++; console.log("  ✓", name); } else { failed++; console.error("  ✗ FAIL:", name); } };
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
    const html = fs.readFileSync(path.join(__dirname, "fixtures", "chatgpt.html"), "utf8");
    const dom = new JSDOM(html, { url: "https://chatgpt.com/c/123", runScripts: "outside-only", pretendToBeVisual: true });
    const { window } = dom;
    window.document.title = "Q3 plan - ChatGPT";
    const sent = [];
    window.chrome = {
        runtime: {
            sendMessage: (msg, cb) => {
                sent.push(msg);
                if (cb) cb(msg.type === "email-draft" ? { ok: true, opened: true } : { flashTs: 0 });
            },
            lastError: undefined,
        },
    };
    window.navigator.clipboard = { writeText: async () => {}, write: async () => {} };

    const src = fs.readFileSync(path.join(__dirname, "..", "copybutton.js"), "utf8");
    let loadError = null;
    try { window.eval(src); } catch (e) { loadError = e; }
    t("copybutton.js loads without a syntax or runtime error", loadError === null);
    await wait(500); // scan() runs on the next animation frame

    const doc = window.document;
    const buttons = [...doc.querySelectorAll(".mk-copy-md-btn")];
    t("a Copy as Markdown button is added under the reply", buttons.some((b) => b.textContent.includes("Copy as Markdown")));
    const email = buttons.find((b) => b.textContent.trim() === "Email");
    t("an Email button sits beside it", !!email);
    t("the selection bar has Send, Email, Copy and Lens", ["mk-sel-send-btn", "mk-sel-email-btn", "mk-sel-copy-btn", "mk-sel-lens-btn"].every((id) => doc.getElementById(id)));

    email.dispatchEvent(new window.MouseEvent("click", { bubbles: true }));
    await wait(100);
    const msg = sent.find((m) => m.type === "email-draft");
    t("Email sends the reply as Markdown", msg && msg.text.includes("Hello! How can I help you today?"));
    t("…with the page's source details", msg && msg.meta.source === "chatgpt" && msg.meta.title === "Q3 plan");
    t("the button confirms the draft opened", email.textContent.includes("Draft opened"));

    console.log(`\nResults: ${passed} passed, ${failed} failed`);
    process.exit(failed ? 1 : 0);
})();
