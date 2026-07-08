// Marksmith Governance Monitor — background service worker.
// Sole job: relay governance-report messages from the content script to the configured collector.
// Keeping the collector URL/config resolution in one place means the content script never needs
// host permissions for the collector itself — this is the ONLY network call this extension makes.

const DEFAULT_COLLECTOR = "http://127.0.0.1:47821";

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (msg?.type !== "governance-report") return;
    (async () => {
        try {
            const managed = await chrome.storage.managed.get(null).catch(() => ({}));
            const sync = await chrome.storage.sync.get(null).catch(() => ({}));
            const cfg = { ...sync, ...managed };
            // collectorUrl points at wherever the org's collector lives: a shared machine running
            // Marksmith (today), or a future hosted service — the extension doesn't care which.
            const base = cfg.collectorUrl || DEFAULT_COLLECTOR;
            const resp = await fetch(base.replace(/\/$/, "") + "/api/governance/report", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(msg.payload),
            });
            sendResponse({ ok: resp.ok, status: resp.status });
        } catch (e) {
            sendResponse({ ok: false, error: e.message }); // collector unreachable — drop silently, no local queue of sensitive-adjacent metadata
        }
    })();
    return true; // async response
});
