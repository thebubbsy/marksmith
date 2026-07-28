// Marksmith Connector — Single Source of Truth for all per-site DOM selectors.
// Every content script (copybutton.js, autosend.js) and the service worker (background.js)
// import from here so a DOM drift fix only ever happens in ONE place.
//
// Structure: each site entry carries the selectors needed to:
//   messages  — locate assistant reply containers
//   content   — (optional) narrow to the markdown/prose child within a message
//   composer  — the chat input element (for auto-inject / prompt paste)
//   sendBtn   — the submit button next to the composer
//   model     — (optional) the current model label element
//   radius    — CSS border-radius that feels native on the site
//   id        — canonical source identifier
//   accent    — brand accent color

const MARKSMITH_SITES = [
    {
        id: "chatgpt",
        host: /(^|\.)chatgpt\.com$|(^|\.)chat\.openai\.com$/,
        urls: ["https://chatgpt.com/*", "https://chat.openai.com/*"],
        messages: '[data-message-author-role="assistant"]',
        content: ".markdown, .prose",
        composer: "#prompt-textarea, textarea[data-id='root'], div[contenteditable='true'][id='prompt-textarea']",
        sendBtn: "[data-testid='send-button'], button[aria-label='Send prompt']",
        model: '[data-testid="model-switcher-dropdown-button"], [data-testid^="model-switcher"]',
        radius: "8px",
        accent: "#10a37f",
    },
    {
        id: "gemini",
        host: /(^|\.)gemini\.google\.com$/,
        urls: ["https://gemini.google.com/*"],
        messages: "model-response, message-content",
        content: ".markdown",
        composer: "rich-textarea div[contenteditable='true'], textarea[aria-label='Prompt']",
        sendBtn: "button[aria-label='Send message'], button.send-button",
        model: '[data-test-id="bard-mode-menu-button"], .logo-pill-label-container, .current-mode-title',
        radius: "16px",
        accent: "#1a73e8",
    },
    {
        id: "claude",
        host: /(^|\.)claude\.ai$/,
        urls: ["https://claude.ai/*"],
        messages: '[data-testid="assistant-message"], .font-claude-message',
        content: null,
        composer: "div[contenteditable='true'][data-placeholder], textarea[placeholder*='Reply']",
        sendBtn: "button[aria-label='Send Message'], button[data-testid='send-button']",
        model: '[data-testid="model-selector-dropdown"], button[aria-haspopup="menu"] [data-testid="model-name"]',
        radius: "8px",
        accent: "#d97757",
    },
    {
        id: "copilot",
        host: /(^|\.)copilot\.microsoft\.com$/,
        urls: ["https://copilot.microsoft.com/*"],
        messages: '[data-content="ai-message"], [data-testid="ai-message"], [class*="ai-message"]',
        content: null,
        composer: "textarea[placeholder*='Ask'], div[contenteditable='true'][role='textbox']",
        sendBtn: "button[aria-label='Submit'], button[data-testid='send-button']",
        model: null,
        radius: "4px",
        accent: "#0f6cbd",
    },
];

// Helper: find the site config for the current hostname.
function marksmithSiteForHost(hostname) {
    return MARKSMITH_SITES.find((s) => s.host.test(hostname)) || null;
}

// Export for MV3 module scripts (background.js uses importScripts or static import).
// Content scripts that are IIFEs can access via globalThis if loaded first.
if (typeof globalThis !== "undefined") {
    globalThis.MARKSMITH_SITES = MARKSMITH_SITES;
    globalThis.marksmithSiteForHost = marksmithSiteForHost;
}
