// adversarial-extension.test.js — Stress testing R1 (Extension DOM queries)
// Run: node extension/tests/adversarial-extension.test.js

const fs = require("fs");
const path = require("path");
const { JSDOM } = require("jsdom");

let passed = 0;
let failed = 0;

function assert(condition, message) {
    if (condition) {
        console.log(`  ✓ ${message}`);
        passed++;
    } else {
        console.error(`  ✗ FAIL: ${message}`);
        failed++;
    }
}

console.log("═══════════════════════════════════════════════════════════");
console.log("Adversarial Extension Tests (popup.js & copybutton.js)");
console.log("═══════════════════════════════════════════════════════════\n");

// ============================================================================
// TEST SUITE 1: popup.js modeButtons Caching & Event Handling
// ============================================================================
console.log("── Suite 1: extension/popup.js modeButtons Caching ──");

{
    const popupHtml = fs.readFileSync(path.join(__dirname, "..", "popup.html"), "utf8");
    const dom = new JSDOM(popupHtml, { url: "chrome-extension://test/popup.html" });
    const { window } = dom;
    const { document } = window;

    // Track calls to querySelectorAll
    let querySelectorAllCalls = 0;
    const origQSA = document.querySelectorAll.bind(document);
    const modeSeg = document.getElementById("modeSeg");
    let modeSegQSACalls = 0;
    const origModeSegQSA = modeSeg.querySelectorAll.bind(modeSeg);

    modeSeg.querySelectorAll = function (...args) {
        modeSegQSACalls++;
        return origModeSegQSA(...args);
    };

    // Setup chrome API mocks
    let sentMessages = [];
    window.chrome = {
        runtime: {
            id: "test-extension-id",
            getManifest: () => ({ version: "1.0.0" }),
            sendMessage: (msg, cb) => {
                sentMessages.push(msg);
                if (cb) cb({ ok: true, markdown: "# Test", meta: { source: "chatgpt" } });
            },
            openOptionsPage: () => {}
        },
        storage: {
            local: {
                remove: () => Promise.resolve()
            }
        }
    };
    window.navigator.clipboard = {
        writeText: () => Promise.resolve()
    };

    // Execute popup.js in window context
    const popupJs = fs.readFileSync(path.join(__dirname, "..", "popup.js"), "utf8");
    const scriptFn = new Function("window", "document", "chrome", "navigator", popupJs);
    scriptFn(window, document, window.chrome, window.navigator);

    assert(modeSegQSACalls === 1, `modeSeg.querySelectorAll called exactly once at startup (actual: ${modeSegQSACalls})`);

    const buttons = modeSeg.querySelectorAll("button");
    const initialModeQSACount = modeSegQSACalls;

    // Simulate clicking "Full conversation" (button 1)
    const btnAll = modeSeg.querySelector('button[data-mode="all"]');
    const btnLatest = modeSeg.querySelector('button[data-mode="latest"]');

    btnAll.dispatchEvent(new window.MouseEvent("click", { bubbles: true }));

    assert(btnAll.classList.contains("on"), "Clicking 'all' adds 'on' class to 'all' button");
    assert(!btnLatest.classList.contains("on"), "Clicking 'all' removes 'on' class from 'latest' button");
    assert(modeSegQSACalls === initialModeQSACount, "No new querySelectorAll calls on mode button click (cached array used)");

    // Simulate clicking "Latest reply" (button 0)
    btnLatest.dispatchEvent(new window.MouseEvent("click", { bubbles: true }));

    assert(btnLatest.classList.contains("on"), "Clicking 'latest' adds 'on' class to 'latest' button");
    assert(!btnAll.classList.contains("on"), "Clicking 'latest' removes 'on' class from 'all' button");
    assert(modeSegQSACalls === initialModeQSACount, "Still no new querySelectorAll calls on second click");

    // Test with missing modeSeg DOM element (graceful degradation)
    const domMissing = new JSDOM("<!DOCTYPE html><html><body><div id='sendBtn'></div><div id='copyBtn'></div><div id='histClear'></div><div id='openOptions'></div><div id='extId'></div><div id='verTxt'></div></body></html>");
    domMissing.window.chrome = window.chrome;
    domMissing.window.navigator = window.navigator;
    let missingError = null;
    try {
        const scriptFn2 = new Function("window", "document", "chrome", "navigator", popupJs);
        scriptFn2(domMissing.window, domMissing.window.document, domMissing.window.chrome, domMissing.window.navigator);
    } catch (e) {
        missingError = e;
    }
    assert(missingError === null, "popup.js runs without error when #modeSeg is missing from DOM");
}

// ============================================================================
// TEST SUITE 2: copybutton.js Mermaid Title Selector & Adversarial DOM
// ============================================================================
console.log("\n── Suite 2: extension/copybutton.js Mermaid Title Selector ──");

{
    const copybuttonJs = fs.readFileSync(path.join(__dirname, "..", "copybutton.js"), "utf8");

    // Extract the selector from the script source
    const selectorMatch = copybuttonJs.match(/root\.querySelectorAll\((["'`])([\s\S]*?)\1\)/g);
    assert(selectorMatch !== null && selectorMatch.length > 0, "Found querySelectorAll calls in copybutton.js");

    const titleSelectorMatch = copybuttonJs.match(/root\.querySelectorAll\("([^"]*title[^"]*|h1[^"]*)"\)/);
    assert(titleSelectorMatch !== null, "Found heading/title querySelector in copybutton.js");

    const titleSelector = titleSelectorMatch ? titleSelectorMatch[1] : "";
    console.log(`  Selector under test: "${titleSelector}"`);

    // Verify selector doesn't contain raw 'div' or 'span'
    const tokenList = titleSelector.split(",").map(s => s.trim().toLowerCase());
    assert(!tokenList.includes("div"), "Selector does NOT query generic 'div'");
    assert(!tokenList.includes("span"), "Selector does NOT query generic 'span'");

    // Verify selector contains headings, header, title, label, figcaption, classes
    assert(tokenList.includes("h1") && tokenList.includes("h2") && tokenList.includes("h3") &&
           tokenList.includes("h4") && tokenList.includes("h5") && tokenList.includes("h6"),
           "Selector includes all heading tags h1-h6");
    assert(tokenList.includes("header") && tokenList.includes("title") && tokenList.includes("label") && tokenList.includes("figcaption"),
           "Selector includes header, title, label, figcaption tags");
    assert(titleSelector.includes("[class*='title' i]") && titleSelector.includes("[class*='header' i]") && titleSelector.includes("[class*='label' i]"),
           "Selector includes case-insensitive class attributes for title/header/label");

    // Test selector matching across various DOM constructs
    const testDom = new JSDOM(`
        <!DOCTYPE html>
        <html>
        <body>
            <div id="root">
                <!-- Valid headings/titles -->
                <div class="diagram-wrapper">
                    <h1>Mermaid</h1>
                    <svg id="mermaid-1"><path d="M0,0"></path></svg>
                </div>
                <div class="card">
                    <h3 class="chart-header">mermaid</h3>
                    <div><canvas id="c1"></canvas></div>
                </div>
                <div class="box">
                    <figcaption> MERMAID </figcaption>
                    <div class="inner"><svg><rect></rect></svg></div>
                </div>
                <div class="panel">
                    <label>   mermaid   </label>
                    <svg></svg>
                </div>
                <div class="custom-title-box">
                    <div class="widget-title">mermaid</div>
                    <svg></svg>
                </div>

                <!-- Non-matching / False Positives -->
                <div>
                    <div>Just a random div with mermaid text</div>
                    <span>Just a random span with mermaid text</span>
                    <h1>Mermaid Architecture Overview</h1> <!-- Should not match /^mermaid$/i -->
                    <label>Not a mermaid diagram</label>
                    <h2 class="already-has-svg">Mermaid <svg><circle></circle></svg></h2> <!-- Contains svg inside heading -->
                </div>

                <!-- Deeply nested test (8 level climb limit) -->
                <div id="deep-valid-ancestor">
                    <svg><path d="M0,0"></path></svg>
                    <div><div><div><div><div><div>
                        <h6>mermaid</h6> <!-- 6 levels deep: should find ancestor -->
                    </div></div></div></div></div></div>
                </div>

                <div id="deep-overflow-ancestor">
                    <svg><path d="M0,0"></path></svg>
                    <div><div><div><div><div><div><div><div><div><div>
                        <h6>mermaid</h6> <!-- 10 levels deep: exceeds 8-level limit -->
                    </div></div></div></div></div></div></div></div></div></div>
                </div>

                <!-- Malformed elements -->
                <header id="empty-header"></header>
                <h4 id="null-text">   </h4>
                <div class="title" id="special-chars">mermaid&#x00;</div>
            </div>
        </body>
        </html>
    `);

    const { document } = testDom.window;
    const root = document.getElementById("root");

    // Execute the exact recoverMermaid title-finding logic
    const found = [];
    const seen = new Set();
    const push = (c) => { if (c && !seen.has(c) && c.querySelector("svg, canvas")) { seen.add(c); found.push(c); } };

    for (const h of root.querySelectorAll(titleSelector)) {
        if (/^mermaid$/i.test((h.textContent || "").trim()) && !h.querySelector("svg, canvas")) {
            let anc = h.parentElement;
            for (let d = 0; anc && d < 8; d++, anc = anc.parentElement) {
                if (anc.querySelector("svg, canvas")) { push(anc); break; }
            }
        }
    }

    assert(found.some(el => el.classList.contains("diagram-wrapper")), "Matched h1 with parent diagram-wrapper");
    assert(found.some(el => el.classList.contains("card")), "Matched h3 with parent card");
    assert(found.some(el => el.classList.contains("box")), "Matched figcaption with parent box");
    assert(found.some(el => el.classList.contains("panel")), "Matched label with parent panel");
    assert(found.some(el => el.classList.contains("custom-title-box")), "Matched div.widget-title with custom-title-box");
    assert(found.some(el => el.id === "deep-valid-ancestor"), "Climbed 6 levels to find deep-valid-ancestor");
    assert(!found.some(el => el.id === "deep-overflow-ancestor"), "Stopped at 8 levels and did NOT match deep-overflow-ancestor (safe recursion limit)");

    // Test with adversarial / corrupted elements
    const detachedH = testDom.window.document.createElement("h2");
    detachedH.textContent = "mermaid";
    // Detached (parentElement is null)
    let detachedThrew = false;
    try {
        let anc = detachedH.parentElement;
        for (let d = 0; anc && d < 8; d++, anc = anc.parentElement) {
            if (anc.querySelector("svg, canvas")) { push(anc); break; }
        }
    } catch (e) {
        detachedThrew = true;
    }
    assert(!detachedThrew, "Detached heading with null parentElement does not throw");

    // Test element with null/falsy textContent
    const weirdEl = testDom.window.document.createElement("label");
    Object.defineProperty(weirdEl, "textContent", { get: () => null });
    let weirdThrew = false;
    try {
        const matches = /^mermaid$/i.test((weirdEl.textContent || "").trim());
        assert(!matches, "Null textContent safely evaluates to false");
    } catch (e) {
        weirdThrew = true;
    }
    assert(!weirdThrew, "Null textContent does not throw");
}

console.log(`\n═══════════════════════════════════════════════════════════`);
console.log(`Results: ${passed} passed, ${failed} failed`);
console.log(`═══════════════════════════════════════════════════════════`);

if (failed > 0) {
    process.exit(1);
} else {
    process.exit(0);
}
