// selector-drift.test.js — validates extension/selectors.js against captured DOM fixtures.
// Run: node extension/tests/selector-drift.test.js
// Exit code 0 = all selectors match; 1 = drift detected.

const fs = require("fs");
const path = require("path");
const { JSDOM } = require("jsdom");

// Load selectors.js in a sandboxed context to get MARKSMITH_SITES.
const selectorsSrc = fs.readFileSync(path.join(__dirname, "..", "selectors.js"), "utf8");
const sandbox = { globalThis: {} };
new Function("globalThis", selectorsSrc)(sandbox.globalThis);
const SITES = sandbox.globalThis.MARKSMITH_SITES;

if (!SITES || !SITES.length) {
    console.error("FATAL: could not load MARKSMITH_SITES from selectors.js");
    process.exit(1);
}

const FIXTURES_DIR = path.join(__dirname, "fixtures");
let failures = 0;

for (const site of SITES) {
    const fixturePath = path.join(FIXTURES_DIR, `${site.id}.html`);
    if (!fs.existsSync(fixturePath)) {
        console.warn(`SKIP ${site.id}: no fixture file at ${fixturePath}`);
        continue;
    }

    const html = fs.readFileSync(fixturePath, "utf8");
    const dom = new JSDOM(html);
    const doc = dom.window.document;

    console.log(`\n── ${site.id} ──`);

    // Test each selector category.
    const checks = [
        ["messages", site.messages],
        ["content", site.content],
        ["composer", site.composer],
        ["sendBtn", site.sendBtn],
        ["model", site.model],
    ];

    for (const [label, selector] of checks) {
        if (!selector) {
            console.log(`  ✓ ${label}: (not defined — skipped)`);
            continue;
        }
        // Selectors may be comma-separated alternatives; at least one must match.
        const alternatives = selector.split(",").map((s) => s.trim());
        const matched = alternatives.some((sel) => {
            try {
                return doc.querySelector(sel) !== null;
            } catch {
                return false;
            }
        });
        if (matched) {
            console.log(`  ✓ ${label}: matched`);
        } else {
            console.error(`  ✗ ${label}: NO MATCH — selector: "${selector}"`);
            failures++;
        }
    }
}

console.log(`\n${"═".repeat(50)}`);
if (failures > 0) {
    console.error(`DRIFT DETECTED: ${failures} selector(s) failed to match fixtures.`);
    process.exit(1);
} else {
    console.log("All selectors match fixtures. No drift detected.");
    process.exit(0);
}
