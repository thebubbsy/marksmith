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
await evaluate("document.getElementById('sampleBtn').click()");
await sleep(1200);
await shoot('express-loaded');

// 3. Frame sequence for the animated demo. Express is a converter, not an editor, so the demo
// walks what it actually does: load a document, set the output profile, watch the option list
// react to the chosen format, convert.
const frames = `${outDir}/frames`;
mkdirSync(frames, { recursive: true });

let frame = 0;
const capture = async () => {
  const { data } = await send('Page.captureScreenshot', { format: 'png' });
  writeFileSync(`${frames}/f${String(frame++).padStart(4, '0')}.png`, Buffer.from(data, 'base64'));
};
const hold = async n => { for (let i = 0; i < n; i++) await capture(); };
const click = async sel => {
  await evaluate(`document.querySelector(${JSON.stringify(sel)})?.click()`);
  await sleep(220);
};
const openGroup = async id => {
  await evaluate(`(() => {
    const d = document.querySelector('details.group[data-id="${id}"]');
    document.querySelectorAll('details.group').forEach(x => { x.open = x === d; });
  })()`);
  await sleep(260);
};

await hold(10);

// Set a document profile.
await openGroup('document');
await hold(4);
await click('#o_includeToc');
await hold(4);
await click('#o_pageBorder');
await hold(6);

// Text processing.
await openGroup('text');
await hold(5);
await click('#o_noEmoji');
await hold(4);
await evaluate(`(() => {
  const s = document.getElementById('o_dashMode');
  s.value = '1'; s.dispatchEvent(new Event('change', { bubbles: true }));
})()`);
await hold(6);

// Diagrams — the Word-specific settings.
await openGroup('diagrams');
await hold(8);

// Switching format re-evaluates which settings the chosen exporter honours.
for (const fmt of ['html', 'pptx', 'epub', 'docx']) {
  await evaluate(`(() => {
    const b = [...document.querySelectorAll('.fmt')]
      .find(x => x.querySelector('.ext').textContent === '.${fmt}');
    if (b) b.click();
  })()`);
  await sleep(200);
  await hold(7);
}

await openGroup('document');
await hold(10);

console.log(`[frames] ${frame} frames written to ${frames}`);

ws.close();
chrome.kill();
process.exit(0);
