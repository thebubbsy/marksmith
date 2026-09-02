// Render a Marksmith HTML export in headless Chrome and screenshot it.
//
// The desktop app serves its bundled KaTeX / Mermaid / highlight.js from the
// `marksmith.assets` virtual host; this stands the same files up on loopback
// and rewrites the export to point at it, so the capture is the app's real
// preview output rather than an approximation.
//
//   node tools/capture/render-doc.mjs <input.html> <output.png> [--width 1100]
//        [--full] [--clip y0,y1] [--from "Heading text" --to "Next heading"] [--pad 24]

import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { readFileSync, writeFileSync, existsSync, statSync, mkdtempSync } from 'node:fs';
import { extname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { setTimeout as sleep } from 'node:timers/promises';

// Browser discovery. CHROME_PATH wins so a CI runner or container can point at
// whatever it has; otherwise try the usual per-platform locations. This used to be
// two hardcoded Windows paths, which meant the README media could only ever be
// regenerated on a Windows box even though the render itself is just headless
// Chrome over CDP and works anywhere.
const CHROME = [
  process.env.CHROME_PATH,
  // Linux / containers (including the Playwright browser bundle).
  '/opt/pw-browsers/chromium',
  '/usr/bin/chromium',
  '/usr/bin/chromium-browser',
  '/usr/bin/google-chrome',
  // macOS
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
  // Windows
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
].find(p => p && existsSync(p));

if (!CHROME) {
  console.error('No Chrome/Chromium found. Set CHROME_PATH to a browser binary.');
  process.exit(1);
}

const ASSETS = resolve('marksmith-v2/MarkSmith.Desktop/Assets/web');
const args = process.argv.slice(2);
const [input, output] = args;
const flag = (name, fallback) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 ? args[i + 1] : fallback;
};

const WIDTH = Number(flag('width', 1100));
const SCALE = Number(flag('scale', 2));
const FULL = args.includes('--full');
const clip = flag('clip', null);
const PORT = 8799;
const DEV = 9334;

const MIME = {
  '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2',
  '.woff': 'font/woff', '.ttf': 'font/ttf', '.html': 'text/html; charset=utf-8',
};

const html = readFileSync(input, 'utf8')
  .replaceAll('https://marksmith.assets', `http://127.0.0.1:${PORT}`);

const server = createServer((req, res) => {
  const url = req.url.split('?')[0];
  if (url === '/' || url === '/doc.html') {
    res.writeHead(200, { 'Content-Type': MIME['.html'] });
    return res.end(html);
  }
  const file = join(ASSETS, url.replace(/^\/+/, ''));
  if (!file.startsWith(ASSETS) || !existsSync(file) || !statSync(file).isFile()) {
    res.writeHead(404); return res.end();
  }
  res.writeHead(200, { 'Content-Type': MIME[extname(file)] ?? 'application/octet-stream' });
  res.end(readFileSync(file));
});
await new Promise(r => server.listen(PORT, '127.0.0.1', r));

const chrome = spawn(CHROME, [
  '--headless=new', `--remote-debugging-port=${DEV}`, '--disable-gpu',
  '--hide-scrollbars', '--no-first-run', '--force-color-profile=srgb',
  `--user-data-dir=${mkdtempSync(join(tmpdir(), 'marksmith-render-'))}`,
  // Containers usually run as root without a user namespace, where the sandbox
  // cannot start; the page we load is our own local file either way.
  ...(process.getuid?.() === 0 ? ['--no-sandbox'] : []),
  'about:blank',
], { stdio: 'ignore' });

const pending = new Map();
let msgId = 0;

async function endpoint() {
  for (let i = 0; i < 80; i++) {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${DEV}/json/list`)).json();
      const page = targets.find(t => t.type === 'page');
      if (page?.webSocketDebuggerUrl) return page.webSocketDebuggerUrl;
    } catch { /* not up yet */ }
    await sleep(250);
  }
  throw new Error('Chrome never exposed a DevTools endpoint');
}

const ws = new WebSocket(await endpoint());
await new Promise(r => ws.addEventListener('open', r, { once: true }));
ws.addEventListener('message', ev => {
  const msg = JSON.parse(ev.data);
  const resolveFn = pending.get(msg.id);
  if (resolveFn) { pending.delete(msg.id); resolveFn(msg.result ?? {}); }
});
const send = (method, params = {}) => new Promise(res => {
  const id = ++msgId;
  pending.set(id, res);
  ws.send(JSON.stringify({ id, method, params }));
});

await send('Page.enable');
await send('Runtime.enable');
await send('Emulation.setDeviceMetricsOverride', {
  width: WIDTH, height: 1400, deviceScaleFactor: SCALE, mobile: false,
});
await send('Page.navigate', { url: `http://127.0.0.1:${PORT}/doc.html` });
await sleep(1500);

// Honour the page's own export-readiness contract (mermaid + image decode + layout).
await send('Runtime.evaluate', {
  expression: `window.marksmithWaitForExportReady ? window.marksmithWaitForExportReady(true) : Promise.resolve()`,
  awaitPromise: true,
});
await sleep(2500);

const { result } = await send('Runtime.evaluate', {
  expression: `JSON.stringify({ h: document.documentElement.scrollHeight })`,
  returnByValue: true,
});
const pageHeight = JSON.parse(result.value).h;

// Clipping by heading text keeps the feature close-ups anchored to content
// rather than to pixel offsets that shift whenever a document is edited.
const from = flag('from', null);
const to = flag('to', null);
const pad = Number(flag('pad', 24));

let bounds = null;
if (from) {
  const probe = await send('Runtime.evaluate', {
    expression: `(() => {
      const find = t => [...document.querySelectorAll('h1,h2,h3,h4,p,table,div')]
        .find(el => el.textContent.trim().startsWith(t));
      const a = find(${JSON.stringify(from)});
      const b = ${to ? `find(${JSON.stringify(to)})` : 'null'};
      if (!a) return JSON.stringify({ error: 'from not found' });
      const top = a.getBoundingClientRect().top + window.scrollY;
      const bottom = b
        ? b.getBoundingClientRect().top + window.scrollY
        : top + a.getBoundingClientRect().height;
      return JSON.stringify({ top, bottom });
    })()`,
    returnByValue: true,
  });
  bounds = JSON.parse(probe.result.value);
  if (bounds.error) throw new Error(`--from "${from}": ${bounds.error}`);
}

let params = { format: 'png', captureBeyondViewport: true };
if (bounds) {
  const y = Math.max(0, bounds.top - pad);
  params.clip = { x: 0, y, width: WIDTH, height: bounds.bottom - y + pad, scale: 1 };
} else if (FULL) {
  params.clip = { x: 0, y: 0, width: WIDTH, height: pageHeight, scale: 1 };
} else if (clip) {
  const [y0, y1] = clip.split(',').map(Number);
  params.clip = { x: 0, y: y0, width: WIDTH, height: y1 - y0, scale: 1 };
} else {
  params.clip = { x: 0, y: 0, width: WIDTH, height: Math.min(pageHeight, 1400), scale: 1 };
}

const shot = await send('Page.captureScreenshot', params);
writeFileSync(output, Buffer.from(shot.data, 'base64'));
console.log(`[render] ${output}  ${WIDTH}x${params.clip.height} @${SCALE}x  (page height ${pageHeight})`);

ws.close();
chrome.kill();
server.close();
process.exit(0);
