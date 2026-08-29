// Dependency-free Chrome DevTools Protocol capture harness.
//
// Drives headless Chrome over CDP (Node's built-in WebSocket) to produce
// high-DPI PNG screenshots and frame sequences of Marksmith Express, so the
// README media can be regenerated from a real running server.
//
//   node tools/capture/cdp-capture.mjs <url> <outdir>

import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, existsSync } from 'node:fs';
import { setTimeout as sleep } from 'node:timers/promises';

const CHROME = [
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
].find(existsSync);

const url = process.argv[2] ?? 'http://127.0.0.1:5000/';
const outDir = process.argv[3] ?? 'docs/media';
const PORT = 9333;
const WIDTH = 1600;
const HEIGHT = 1000;
const SCALE = 2;

mkdirSync(outDir, { recursive: true });

const chrome = spawn(CHROME, [
  '--headless=new',
  `--remote-debugging-port=${PORT}`,
  '--remote-allow-origins=*',
  '--hide-scrollbars',
  '--disable-gpu',
  '--no-first-run',
  '--user-data-dir=' + process.env.TEMP + '/marksmith-cdp-profile',
  `--window-size=${WIDTH},${HEIGHT}`,
  'about:blank',
], { stdio: 'ignore' });

let msgId = 0;
const pending = new Map();

async function findTarget() {
  for (let i = 0; i < 60; i++) {
    try {
      const res = await fetch(`http://127.0.0.1:${PORT}/json/list`);
      const targets = await res.json();
      const page = targets.find(t => t.type === 'page');
      if (page?.webSocketDebuggerUrl) return page.webSocketDebuggerUrl;
    } catch { /* chrome not up yet */ }
    await sleep(250);
  }
  throw new Error('Chrome DevTools endpoint never came up');
}

const ws = new WebSocket(await findTarget());
await new Promise(r => ws.addEventListener('open', r, { once: true }));
ws.addEventListener('message', ev => {
  const msg = JSON.parse(ev.data);
  const resolve = pending.get(msg.id);
  if (resolve) { pending.delete(msg.id); resolve(msg.result ?? {}); }
});

const send = (method, params = {}) => new Promise(resolve => {
  const id = ++msgId;
  pending.set(id, resolve);
  ws.send(JSON.stringify({ id, method, params }));
});

const evaluate = expr => send('Runtime.evaluate', { expression: expr, awaitPromise: true });

async function shoot(name) {
  const { data } = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  const path = `${outDir}/${name}.png`;
  writeFileSync(path, Buffer.from(data, 'base64'));
  console.log(`[shot] ${path}`);
}

await send('Page.enable');
await send('Runtime.enable');
await send('Emulation.setDeviceMetricsOverride', {
  width: WIDTH, height: HEIGHT, deviceScaleFactor: SCALE, mobile: false,
});

await send('Page.navigate', { url });
await sleep(2500);

// 1. Empty state — the landing view.
await shoot('express-landing');

// 2. Sample document loaded, ready to convert.
await evaluate('loadSample()');
await sleep(1200);
await shoot('express-loaded');

// 3. Frame sequence for the animated demo: type the doc, pick a format, convert.
const frames = `${outDir}/frames`;
mkdirSync(frames, { recursive: true });

await evaluate('clearEditor()');
await sleep(400);

const doc = `# Q4 Platform Review

> [!IMPORTANT]
> Rendered natively — no Word round-trip.

## Availability

| Region | Uptime | Trend |
| :--- | ---: | :--- |
| ap-southeast-2 | 99.98% | [sparkline: 12, 20, 18, 31, 44] |
| us-east-1 | 99.95% | [sparkline: 30, 24, 28, 22, 19] |

The error budget follows $E = 1 - \\frac{S_{obs}}{S_{target}}$ across the window.

\`\`\`mermaid
flowchart LR
  Ingest --> Normalize --> OpenXML[Native OOXML] --> Word
\`\`\`
`;

let frame = 0;
const capture = async () => {
  const { data } = await send('Page.captureScreenshot', { format: 'png' });
  writeFileSync(`${frames}/f${String(frame++).padStart(4, '0')}.png`, Buffer.from(data, 'base64'));
};

// Type the document in chunks so the frame sequence reads as live authoring.
const lines = doc.split('\n');
let typed = '';
for (const line of lines) {
  typed += line + '\n';
  await evaluate(`(() => {
    const ta = document.getElementById('editor') || document.querySelector('textarea');
    ta.value = ${JSON.stringify(typed)};
    ta.dispatchEvent(new Event('input', { bubbles: true }));
    ta.scrollTop = ta.scrollHeight;
  })()`);
  await capture();
}

// Hold on the finished document, then walk the format tiles.
for (let i = 0; i < 6; i++) await capture();

for (const fmt of ['html', 'pptx', 'epub', 'docx']) {
  await evaluate(`(() => {
    const el = document.querySelector('[data-format="${fmt}"]');
    if (el) selectFormat(el);
  })()`);
  await sleep(150);
  for (let i = 0; i < 4; i++) await capture();
}

for (let i = 0; i < 8; i++) await capture();

console.log(`[frames] ${frame} frames written to ${frames}`);

ws.close();
chrome.kill();
process.exit(0);
