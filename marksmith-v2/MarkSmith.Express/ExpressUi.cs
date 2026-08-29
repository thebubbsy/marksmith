namespace MarkSmith.Express;

/// <summary>
/// The single-page Express UI, served verbatim at "/".
///
/// It is a converter, not an editor: source arrives by drop, file picker or paste and is shown
/// read-only. Every control is generated from the GROUPS table in the page script, which maps 1:1
/// onto <see cref="MarkSmith.Models.OutputOverride"/>, so the panel, the request body and the
/// documented curl command cannot drift apart — the previous page hard-coded its controls and
/// ended up with three checkboxes that were never sent and six themes the engine had never heard of.
/// </summary>
public static class ExpressUi
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Marksmith Express</title>
<style>
  :root {
    color-scheme: light dark;
    --bg:#f6f7f9; --panel:#fff; --sunk:#f1f3f7; --border:#e3e6ec; --border-strong:#d2d7e0;
    --text:#14161c; --muted:#666d7d; --accent:#3459e6; --accent-soft:#eaeeff; --on-accent:#fff;
    --danger:#c3372f; --ok:#12855a; --radius:10px;
    --font:ui-sans-serif,system-ui,-apple-system,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;
    --mono:ui-monospace,"Cascadia Code",Consolas,"SF Mono",Menlo,monospace;
  }
  html:not([data-theme="light"]) {
    --bg:#0b0d12; --panel:#12151c; --sunk:#171b23; --border:#232936; --border-strong:#2f3746;
    --text:#e7eaf1; --muted:#98a1b2; --accent:#6d90ff; --accent-soft:#1a2340; --on-accent:#0b0d12;
    --danger:#f2726a; --ok:#43c58d;
  }
  @media (prefers-color-scheme: light) {
    html:not([data-theme="dark"]) {
      --bg:#f6f7f9; --panel:#fff; --sunk:#f1f3f7; --border:#e3e6ec; --border-strong:#d2d7e0;
      --text:#14161c; --muted:#666d7d; --accent:#3459e6; --accent-soft:#eaeeff; --on-accent:#fff;
      --danger:#c3372f; --ok:#12855a;
    }
  }
  html[data-theme="light"] {
    --bg:#f6f7f9; --panel:#fff; --sunk:#f1f3f7; --border:#e3e6ec; --border-strong:#d2d7e0;
    --text:#14161c; --muted:#666d7d; --accent:#3459e6; --accent-soft:#eaeeff; --on-accent:#fff;
    --danger:#c3372f; --ok:#12855a;
  }
  * { box-sizing:border-box; margin:0; padding:0; }
  /* An author display rule outranks the UA stylesheet, so .drop{display:grid} kept the
     dropzone on screen even while [hidden] was set. */
  [hidden] { display:none !important; }
  body { background:var(--bg); color:var(--text); font-family:var(--font); font-size:14px;
         line-height:1.5; -webkit-font-smoothing:antialiased; }
  svg { width:16px; height:16px; flex:none; }

  .topbar { position:sticky; top:0; z-index:20; display:flex; align-items:center; gap:14px;
            padding:12px 20px; background:var(--panel);
            border-bottom:1px solid var(--border); }
  .brand { display:flex; align-items:center; gap:9px; font-weight:650; letter-spacing:-.01em; }
  .brand .mark { width:24px; height:24px; border-radius:7px; background:var(--accent);
                 color:var(--on-accent); display:grid; place-items:center; }
  .brand .mark svg { width:14px; height:14px; }
  .tag { font-size:11px; font-weight:600; color:var(--muted); border:1px solid var(--border);
         padding:2px 7px; border-radius:999px; }
  .spacer { flex:1; }
  .status { display:flex; align-items:center; gap:7px; font-size:12.5px; color:var(--muted); }
  .dot { width:7px; height:7px; border-radius:50%; background:var(--ok); }
  .dot.down { background:var(--danger); }
  .icon-btn { display:grid; place-items:center; width:32px; height:32px; border-radius:8px;
              border:1px solid var(--border); background:var(--panel); color:var(--muted); cursor:pointer; }
  .icon-btn:hover { color:var(--text); border-color:var(--border-strong); }

  main { max-width:1180px; margin:0 auto; padding:20px; display:grid;
         grid-template-columns:minmax(0,1fr) 372px; gap:20px; align-items:start; }
  @media (max-width:940px) { main { grid-template-columns:1fr; } }
  .panel { background:var(--panel); border:1px solid var(--border); border-radius:var(--radius); }
  .panel > header { display:flex; align-items:center; gap:8px; padding:12px 16px;
                    border-bottom:1px solid var(--border); }
  .panel h2 { font-size:13px; font-weight:650; }
  .panel .body { padding:16px; }

  .drop { border:1.5px dashed var(--border-strong); border-radius:var(--radius); background:var(--sunk);
          min-height:330px; display:grid; place-content:center; justify-items:center; gap:12px;
          text-align:center; padding:32px; transition:border-color .15s, background .15s; }
  .drop.over { border-color:var(--accent); background:var(--accent-soft); }
  .drop .big { width:34px; height:34px; color:var(--muted); }
  .drop h3 { font-size:15px; font-weight:600; }
  .drop p { color:var(--muted); font-size:13px; max-width:36ch; }
  .drop .row2 { display:flex; gap:8px; }
  .kbd { font-family:var(--mono); font-size:11px; border:1px solid var(--border-strong);
         border-bottom-width:2px; border-radius:5px; padding:1px 5px; }

  .filebar { display:flex; align-items:center; gap:10px; padding:10px 12px; background:var(--sunk);
             border:1px solid var(--border); border-radius:8px; margin-bottom:12px; }
  .filebar .name { font-weight:600; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .filebar .meta { color:var(--muted); font-size:12.5px; white-space:nowrap; }
  pre.source { font-family:var(--mono); font-size:12.5px; line-height:1.65; background:var(--sunk);
               border:1px solid var(--border); border-radius:8px; padding:14px; max-height:360px;
               overflow:auto; white-space:pre-wrap; word-break:break-word; color:var(--muted); }

  .btn { display:inline-flex; align-items:center; gap:7px; padding:7px 12px; border-radius:8px;
         border:1px solid var(--border-strong); background:var(--panel); color:var(--text);
         font:inherit; font-size:13px; font-weight:550; cursor:pointer; }
  .btn:hover { border-color:var(--accent); color:var(--accent); }
  .btn.primary { background:var(--accent); border-color:var(--accent); color:var(--on-accent);
                 width:100%; justify-content:center; padding:11px; font-size:14px; font-weight:650; }
  .btn.primary:hover { filter:brightness(1.08); color:var(--on-accent); }
  .btn:disabled { opacity:.5; cursor:not-allowed; filter:none; }
  .btn.ghost { border-color:transparent; background:transparent; color:var(--muted); padding:6px 8px; }
  .btn.ghost:hover { background:var(--sunk); color:var(--text); }

  .formats { display:grid; grid-template-columns:1fr 1fr; gap:8px; }
  .fmt { display:flex; align-items:center; gap:9px; padding:10px 11px; border-radius:9px;
         border:1px solid var(--border); background:var(--panel); color:var(--text);
         cursor:pointer; font:inherit; font-size:13px; font-weight:550; }
  .fmt:hover { border-color:var(--border-strong); }
  .fmt[aria-pressed="true"] { border-color:var(--accent); background:var(--accent-soft); color:var(--accent); }
  .fmt .ext { margin-left:auto; font-family:var(--mono); font-size:11px; color:var(--muted); }
  .fmt[aria-pressed="true"] .ext { color:var(--accent); }

  details.group { border-top:1px solid var(--border); margin-top:14px; }
  details.group > summary { display:flex; align-items:center; gap:8px; padding:12px 2px; cursor:pointer;
                            font-weight:600; font-size:13px; list-style:none; }
  details.group > summary::-webkit-details-marker { display:none; }
  details.group > summary .chev { margin-left:auto; color:var(--muted); transition:transform .15s; }
  details.group[open] > summary .chev { transform:rotate(90deg); }
  .group-body { padding-bottom:12px; display:flex; flex-direction:column; gap:12px; }

  .row { display:flex; align-items:center; gap:10px; min-height:28px; }
  .row .label { font-size:13px; }
  .row .hint { display:block; font-size:11.5px; color:var(--muted); margin-top:1px; max-width:30ch; }
  .row .ctl { margin-left:auto; flex:none; }
  .row.na { opacity:.42; }
  .na-tag { font-size:10px; font-weight:700; color:var(--muted); border:1px solid var(--border);
            border-radius:4px; padding:0 4px; margin-left:6px; text-transform:uppercase;
            letter-spacing:.03em; white-space:nowrap; }
  .row.stack { flex-direction:column; align-items:stretch; gap:6px; }
  .row.stack .ctl { margin-left:0; width:100%; }

  select, input[type=text], input[type=number] {
    font:inherit; font-size:13px; color:var(--text); background:var(--panel);
    border:1px solid var(--border-strong); border-radius:7px; padding:6px 8px; min-width:0; }
  select { min-width:148px; }
  input[type=text] { width:100%; }
  select:focus, input:focus { outline:2px solid var(--accent); outline-offset:-1px; }
  input[type=range] { width:132px; accent-color:var(--accent); }

  .switch { position:relative; display:inline-block; width:38px; height:22px; flex:none; }
  .switch input { opacity:0; width:0; height:0; position:absolute; }
  .switch span { position:absolute; inset:0; background:var(--border-strong); border-radius:999px;
                 transition:background .15s; cursor:pointer; }
  .switch span::after { content:""; position:absolute; top:3px; left:3px; width:16px; height:16px;
                        border-radius:50%; background:#fff; transition:transform .15s; }
  .switch input:checked + span { background:var(--accent); }
  .switch input:checked + span::after { transform:translateX(16px); }
  .switch input:focus-visible + span { outline:2px solid var(--accent); outline-offset:2px; }

  .inline { display:flex; align-items:center; gap:8px; }
  .swatch { width:22px; height:22px; border-radius:6px; border:1px solid var(--border-strong); flex:none; }
  .val { font-family:var(--mono); font-size:12px; color:var(--muted); min-width:22px; text-align:right; }

  .convert-wrap { padding:14px 16px; border-top:1px solid var(--border); }
  .convert-note { text-align:center; font-size:12px; color:var(--muted); margin-top:8px; }

  .api { max-width:1180px; margin:0 auto 24px; padding:0 20px; }
  .api .panel .body { padding:0; }
  .api pre { font-family:var(--mono); font-size:12px; line-height:1.7; padding:14px 16px;
             overflow-x:auto; color:var(--muted); }
  footer { max-width:1180px; margin:0 auto; padding:0 20px 32px; color:var(--muted); font-size:12.5px; }

  #toast { position:fixed; left:50%; bottom:24px; transform:translateX(-50%) translateY(12px);
           background:var(--panel); border:1px solid var(--border-strong); border-left:3px solid var(--danger);
           border-radius:9px; padding:11px 15px; font-size:13px; box-shadow:0 8px 28px rgba(0,0,0,.18);
           opacity:0; pointer-events:none; transition:opacity .18s, transform .18s; max-width:90vw; }
  #toast.show { opacity:1; transform:translateX(-50%) translateY(0); }
  #toast.ok { border-left-color:var(--ok); }
  .sr { position:absolute; width:1px; height:1px; overflow:hidden; clip:rect(0 0 0 0); }
</style>
</head>
<body>

<div class="topbar">
  <div class="brand">
    <span class="mark"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4"
      stroke-linecap="round" stroke-linejoin="round"><path d="M4 20V6l6 7 6-7v14"/></svg></span>
    <span>Marksmith Express</span>
  </div>
  <span class="tag" id="verTag">&#160;</span>
  <span class="spacer"></span>
  <div class="status"><span class="dot" id="dot"></span><span id="statusText">Checking&#8230;</span></div>
  <button class="icon-btn" id="themeToggle" title="Switch light / dark" aria-label="Switch light or dark">
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/></svg>
  </button>
</div>

<main>
  <section class="panel">
    <header>
      <h2>Source</h2>
      <span class="spacer"></span>
      <button class="btn ghost" id="pasteBtn">Paste</button>
      <button class="btn ghost" id="clearBtn" hidden>Clear</button>
    </header>
    <div class="body">
      <div class="drop" id="drop">
        <svg class="big" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"
          stroke-linecap="round" stroke-linejoin="round">
          <path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/>
        </svg>
        <h3>Drop a Markdown file</h3>
        <p>Or choose one, or paste Markdown straight from the clipboard with
           <span class="kbd">Ctrl</span> <span class="kbd">V</span>.</p>
        <div class="row2">
          <button class="btn" id="chooseBtn">Choose file</button>
          <button class="btn" id="sampleBtn">Load sample</button>
        </div>
        <input type="file" id="file" accept=".md,.markdown,.mdown,.mkd,.txt" class="sr">
      </div>
      <div id="loaded" hidden>
        <div class="filebar">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/></svg>
          <span class="name" id="fileName"></span>
          <span class="spacer"></span>
          <span class="meta" id="fileMeta"></span>
        </div>
        <pre class="source" id="preview" tabindex="0" aria-label="Source preview, read only"></pre>
      </div>
    </div>
  </section>

  <aside class="panel">
    <header><h2>Output</h2></header>
    <div class="body">
      <div class="formats" id="formats"></div>
      <div id="groups"></div>
    </div>
    <div class="convert-wrap">
      <button class="btn primary" id="convert" disabled>Convert &amp; download</button>
      <div class="convert-note" id="convertNote">Add a source file to begin</div>
    </div>
  </aside>
</main>

<div class="api">
  <div class="panel">
    <header>
      <h2>REST API</h2>
      <span class="spacer"></span>
      <button class="btn ghost" id="copyCurl">Copy</button>
    </header>
    <div class="body"><pre id="curl"></pre></div>
  </div>
</div>

<footer id="foot"></footer>
<div id="toast" role="status" aria-live="polite"></div>

<script>
// Every control is declared once, here. The panel, the request body and the curl snippet are all
// generated from this table, so a control cannot drift out of sync with what the server is sent.
// `only` narrows a setting to the formats whose exporter actually reads it; `when` shows a row
// only while another option holds a given value.
const GROUPS = [
  { id:'document', label:'Document', open:true, items:[
    { k:'theme', t:'theme', label:'Theme' },
    { k:'includeToc', t:'bool', def:false, label:'Table of contents', only:['docx','html'] },
    { k:'brandCoverPage', t:'bool', def:false, label:'Cover page', only:['docx','epub'] },
    { k:'pageBorder', t:'bool', def:false, label:'Page border', only:['docx'] },
    { k:'headingShift', t:'range', def:0, min:-5, max:5, label:'Heading level shift',
      hint:'Negative promotes, positive demotes' },
    { k:'themeLightInfluence', t:'bool', def:false, label:'Lighten theme background', only:['html'] },
    { k:'contentWidth', t:'number', def:800, min:320, max:2400, label:'Content width', only:['html'] },
    { k:'a4FixedWidth', t:'bool', def:true, label:'A4 page width', only:['docx','html'] },
    { k:'unlimitedHeight', t:'bool', def:true, label:'One continuous page', only:['docx','html'] },
  ]},
  { id:'text', label:'Text processing', items:[
    { k:'normalizeLlm', t:'bool', def:true, label:'Normalize AI writing quirks',
      hint:'Promotes bold pseudo-headings, drops assistant disclaimers, collapses blank runs' },
    { k:'noEmoji', t:'bool', def:false, label:'Strip emoji' },
    { k:'dashMode', t:'select', def:0, label:'Em dashes', num:true,
      choices:[[0,'Keep'],[1,'Hyphen'],[2,'Spaced hyphen'],[3,'Custom']] },
    { k:'dashCustom', t:'text', def:'', label:'Replacement text', when:['dashMode',3], stack:true },
    { k:'boldMode', t:'select', def:0, label:'Bold', num:true,
      choices:[[0,'Keep'],[1,'Remove'],[2,'Convert to italic']] },
    { k:'italicMode', t:'select', def:0, label:'Italic', num:true, choices:[[0,'Keep'],[1,'Remove']] },
  ]},
  { id:'diagrams', label:'Diagrams', items:[
    { k:'mermaidEnabled', t:'bool', def:true, label:'Render Mermaid diagrams' },
    { k:'mermaidDocxMode', t:'select', def:1, label:'Mermaid in Word', only:['docx'], num:true,
      choices:[[1,'Native Word shapes'],[0,'Embedded picture']] },
    { k:'smartConnectors', t:'bool', def:true, label:'Smart connectors', only:['docx'] },
    { k:'connectorRouting', t:'select', def:'default', label:'Routing', only:['docx'],
      choices:[['default','Automatic'],['straight','Straight'],['elbow','Elbow'],['curved','Curved']] },
    { k:'connectorArrowhead', t:'select', def:'default', label:'Arrowheads', only:['docx'],
      choices:[['default','Automatic'],['triangle','Triangle'],['open','Open'],['stealth','Stealth'],
               ['diamond','Diamond'],['oval','Oval'],['none','None']] },
  ]},
  { id:'meta', label:'Metadata and attribution', items:[
    { k:'authorName', t:'text', def:'', label:'Author', stack:true, only:['docx','pptx'] },
    { k:'fontPreset', t:'font', def:'System', label:'Font', only:['docx','html'] },
    { k:'sourceLanguage', t:'text', def:'', label:'Language tag', placeholder:'en-GB', stack:true,
      only:['docx','html'] },
    { k:'sourceDirection', t:'select', def:'', label:'Text direction', only:['docx','html'],
      choices:[['','Automatic'],['ltr','Left to right'],['rtl','Right to left']] },
    { k:'showWordCount', t:'bool', def:true, label:'Word count in footer', only:['html'] },
    { k:'showAttribution', t:'bool', def:true, label:'Attribution line', only:['docx','html'] },
    { k:'trackChanges', t:'bool', def:false, label:'Turn on Track Changes in Word', only:['docx'],
      hint:'Word records later edits as revisions' },
  ]},
];

const SAMPLE = [
  '# Quarterly Platform Review',
  '',
  '> [!NOTE]',
  '> Generated with Marksmith Express.',
  '',
  'The ingest pipeline sustained **99.98%** availability, and the 99th-percentile',
  'latency settled at $p_{99} = 240\\,\\mathrm{ms}$.',
  '',
  '## Throughput',
  '',
  '| Region | Peak req/s | Error rate |',
  '| --- | ---: | ---: |',
  '| eu-west | 18,400 | 0.02% |',
  '| us-east | 24,900 | 0.04% |',
  '',
  '## Architecture',
  '',
  '```mermaid',
  'flowchart LR',
  '  A[Client] --> B(API gateway)',
  '  B --> C[(Postgres)]',
  '  B --> D[[Worker pool]]',
  '```',
  ''
].join('\n');

const state = { markdown:'', name:'', format:'docx', opts:{}, themes:[], fonts:[], formats:[] };
const $ = s => document.querySelector(s);

const savedTheme = localStorage.getItem('ms-theme');
if (savedTheme) document.documentElement.setAttribute('data-theme', savedTheme);
$('#themeToggle').onclick = () => {
  const cur = document.documentElement.getAttribute('data-theme')
    || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
  const next = cur === 'dark' ? 'light' : 'dark';
  document.documentElement.setAttribute('data-theme', next);
  localStorage.setItem('ms-theme', next);
};

function toast(msg, ok) {
  const t = $('#toast');
  t.textContent = msg;
  t.classList.toggle('ok', !!ok);
  t.classList.add('show');
  clearTimeout(t._t);
  t._t = setTimeout(() => t.classList.remove('show'), 4200);
}
function bytes(n) {
  return n < 1024 ? n + ' B'
       : n < 1048576 ? (n / 1024).toFixed(1) + ' KB'
       : (n / 1048576).toFixed(1) + ' MB';
}
function applies(item) { return !item.only || item.only.includes(state.format); }
function visible(item) { return !item.when || state.opts[item.when[0]] === item.when[1]; }

function optionRow(item) {
  const row = document.createElement('div');
  row.className = 'row' + (item.stack ? ' stack' : '');

  const lab = document.createElement('label');
  lab.className = 'label';
  lab.htmlFor = 'o_' + item.k;
  lab.textContent = item.label;
  if (item.hint) {
    const h = document.createElement('span');
    h.className = 'hint';
    h.textContent = item.hint;
    lab.appendChild(h);
  }
  row.appendChild(lab);

  const ctl = document.createElement('div');
  ctl.className = 'ctl';
  const val = state.opts[item.k];

  if (item.t === 'bool') {
    const w = document.createElement('label');
    w.className = 'switch';
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.id = 'o_' + item.k;
    cb.checked = !!val;
    cb.onchange = () => set(item.k, cb.checked);
    w.appendChild(cb);
    w.appendChild(document.createElement('span'));
    ctl.appendChild(w);
  } else if (item.t === 'select' || item.t === 'font') {
    const sel = document.createElement('select');
    sel.id = 'o_' + item.k;
    const choices = item.t === 'font' ? state.fonts.map(f => [f.id, f.label]) : item.choices;
    for (const pair of choices) {
      const o = document.createElement('option');
      o.value = String(pair[0]);
      o.textContent = pair[1];
      if (String(pair[0]) === String(val)) o.selected = true;
      sel.appendChild(o);
    }
    sel.onchange = () => set(item.k, item.num ? Number(sel.value) : sel.value);
    ctl.appendChild(sel);
  } else if (item.t === 'theme') {
    const wrap = document.createElement('div');
    wrap.className = 'inline';
    const sw = document.createElement('span');
    sw.className = 'swatch';
    const sel = document.createElement('select');
    sel.id = 'o_' + item.k;
    for (const th of state.themes) {
      const o = document.createElement('option');
      o.value = th.name;
      o.textContent = th.name;
      if (th.name === val) o.selected = true;
      sel.appendChild(o);
    }
    const paint = () => {
      const th = state.themes.find(t => t.name === sel.value);
      if (th) { sw.style.background = th.background; sw.style.borderColor = th.accent; }
    };
    paint();
    sel.onchange = () => { set(item.k, sel.value); paint(); };
    wrap.appendChild(sw);
    wrap.appendChild(sel);
    ctl.appendChild(wrap);
  } else if (item.t === 'range') {
    const wrap = document.createElement('div');
    wrap.className = 'inline';
    const out = document.createElement('span');
    out.className = 'val';
    const r = document.createElement('input');
    r.type = 'range';
    r.id = 'o_' + item.k;
    r.min = item.min; r.max = item.max;
    r.value = val === undefined ? item.def : val;
    const show = () => { out.textContent = (Number(r.value) > 0 ? '+' : '') + r.value; };
    show();
    r.oninput = () => { show(); set(item.k, Number(r.value)); };
    wrap.appendChild(r);
    wrap.appendChild(out);
    ctl.appendChild(wrap);
  } else if (item.t === 'number') {
    const n = document.createElement('input');
    n.type = 'number';
    n.id = 'o_' + item.k;
    n.min = item.min; n.max = item.max;
    n.value = val === undefined ? item.def : val;
    n.style.width = '96px';
    n.onchange = () => set(item.k, Number(n.value));
    ctl.appendChild(n);
  } else {
    const i = document.createElement('input');
    i.type = 'text';
    i.id = 'o_' + item.k;
    i.value = val === undefined ? '' : val;
    if (item.placeholder) i.placeholder = item.placeholder;
    i.oninput = () => set(item.k, i.value);
    ctl.appendChild(i);
  }

  row.appendChild(ctl);

  // A setting this format's exporter never reads is shown, disabled, with the formats that do use
  // it. Hiding it would be tidier; saying so is more honest about what the engine supports.
  if (!applies(item)) {
    row.classList.add('na');
    row.querySelectorAll('input,select').forEach(e => { e.disabled = true; });
    const tag = document.createElement('span');
    tag.className = 'na-tag';
    tag.textContent = item.only.join(' / ');
    lab.appendChild(tag);
  }
  return row;
}

function renderOptions() {
  const host = $('#groups');
  const openState = {};
  host.querySelectorAll('details.group').forEach(d => { openState[d.dataset.id] = d.open; });
  host.textContent = '';

  for (const g of GROUPS) {
    const d = document.createElement('details');
    d.className = 'group';
    d.dataset.id = g.id;
    d.open = openState[g.id] === undefined ? !!g.open : openState[g.id];

    const sum = document.createElement('summary');
    sum.textContent = g.label;
    const chev = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    chev.setAttribute('class', 'chev');
    chev.setAttribute('viewBox', '0 0 24 24');
    chev.setAttribute('fill', 'none');
    chev.setAttribute('stroke', 'currentColor');
    chev.setAttribute('stroke-width', '2');
    const p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    p.setAttribute('d', 'M9 18l6-6-6-6');
    chev.appendChild(p);
    sum.appendChild(chev);
    d.appendChild(sum);

    const body = document.createElement('div');
    body.className = 'group-body';
    for (const item of g.items) if (visible(item)) body.appendChild(optionRow(item));
    d.appendChild(body);
    host.appendChild(d);
  }
}

function renderFormats() {
  const host = $('#formats');
  host.textContent = '';
  for (const f of state.formats) {
    const b = document.createElement('button');
    b.className = 'fmt';
    b.type = 'button';
    b.setAttribute('aria-pressed', String(f.id === state.format));
    const n = document.createElement('span');
    n.textContent = f.label;
    const e = document.createElement('span');
    e.className = 'ext';
    e.textContent = f.ext;
    b.appendChild(n);
    b.appendChild(e);
    b.onclick = () => { state.format = f.id; renderFormats(); renderOptions(); updateCurl(); };
    host.appendChild(b);
  }
}

function set(k, v) {
  state.opts[k] = v;
  try { localStorage.setItem('ms-opts', JSON.stringify(state.opts)); } catch (e) { /* private mode */ }
  if (GROUPS.some(g => g.items.some(i => i.when && i.when[0] === k))) renderOptions();
  updateCurl();
}

function buildOptions() {
  const out = {};
  for (const g of GROUPS) for (const item of g.items) {
    if (!applies(item) || !visible(item)) continue;   // never send what this format ignores
    const v = state.opts[item.k];
    if (v === undefined || v === '' || v === item.def) continue;
    out[item.k] = v;
  }
  return out;
}

function updateCurl() {
  const body = { markdown: '# Hello', format: state.format };
  const opts = buildOptions();
  if (Object.keys(opts).length) body.options = opts;
  $('#curl').textContent =
    'curl -X POST ' + location.origin + '/api/convert \\\n' +
    "  -H 'Content-Type: application/json' \\\n" +
    "  -d '" + JSON.stringify(body) + "' \\\n" +
    '  -o document.' + state.format;
}

function setSource(text, name) {
  state.markdown = text;
  state.name = name || '';
  const words = text.trim() ? text.trim().split(/\s+/).length : 0;
  $('#fileName').textContent = state.name || 'Pasted Markdown';
  $('#fileMeta').textContent =
    words.toLocaleString() + ' words · ' + bytes(new Blob([text]).size);
  $('#preview').textContent = text.length > 20000 ? text.slice(0, 20000) + '\n…' : text;
  $('#drop').hidden = true;
  $('#loaded').hidden = false;
  $('#clearBtn').hidden = false;
  $('#convert').disabled = false;
  $('#convertNote').textContent = 'Read-only preview — Express converts, it does not edit';
}

function clearSource() {
  state.markdown = '';
  state.name = '';
  $('#drop').hidden = false;
  $('#loaded').hidden = true;
  $('#clearBtn').hidden = true;
  $('#convert').disabled = true;
  $('#convertNote').textContent = 'Add a source file to begin';
}

async function readFile(f) {
  if (!f) return;
  if (f.size > 8 * 1024 * 1024) { toast('That file is larger than 8 MB.'); return; }
  setSource(await f.text(), f.name);
}

const drop = $('#drop');
['dragenter', 'dragover'].forEach(e => drop.addEventListener(e, ev => {
  ev.preventDefault();
  drop.classList.add('over');
}));
['dragleave', 'drop'].forEach(e => drop.addEventListener(e, ev => {
  ev.preventDefault();
  drop.classList.remove('over');
}));
drop.addEventListener('drop', ev => readFile(ev.dataTransfer.files[0]));
$('#chooseBtn').onclick = () => $('#file').click();
$('#file').onchange = e => readFile(e.target.files[0]);
$('#sampleBtn').onclick = () => setSource(SAMPLE, 'sample.md');
$('#clearBtn').onclick = clearSource;
$('#pasteBtn').onclick = async () => {
  try {
    const t = await navigator.clipboard.readText();
    if (t.trim()) setSource(t, ''); else toast('The clipboard is empty.');
  } catch (e) {
    toast('Clipboard access was blocked — press Ctrl+V instead.');
  }
};
document.addEventListener('paste', ev => {
  const t = ev.clipboardData.getData('text');
  if (t.trim()) { setSource(t, ''); ev.preventDefault(); }
});

$('#convert').onclick = async () => {
  const btn = $('#convert');
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = 'Converting…';
  try {
    const body = { markdown: state.markdown, format: state.format, options: buildOptions() };
    if (state.name) body.fileName = state.name;
    const res = await fetch('/api/convert', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    if (!res.ok) {
      const e = await res.json().catch(() => ({ error: res.statusText }));
      throw new Error(e.error || ('HTTP ' + res.status));
    }
    const cd = res.headers.get('Content-Disposition') || '';
    const m = cd.match(/filename="?([^"]+)"?/);
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = m ? m[1] : 'document.' + state.format;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    toast('Saved ' + a.download + ' · ' + bytes(blob.size), true);
  } catch (ex) {
    toast('Conversion failed: ' + ex.message);
  } finally {
    btn.disabled = false;
    btn.textContent = label;
  }
};

$('#copyCurl').onclick = async () => {
  try {
    await navigator.clipboard.writeText($('#curl').textContent);
    toast('Copied', true);
  } catch (e) {
    toast('Copy was blocked by the browser.');
  }
};

(async function boot() {
  try { state.opts = JSON.parse(localStorage.getItem('ms-opts') || '{}'); } catch (e) { state.opts = {}; }
  for (const g of GROUPS) for (const i of g.items) {
    if (state.opts[i.k] === undefined && i.def !== undefined) state.opts[i.k] = i.def;
  }

  try {
    const o = await (await fetch('/api/options')).json();
    state.themes = o.themes;
    state.fonts = o.fonts;
    state.formats = o.formats;
    if (state.opts.theme === undefined && state.themes.length) state.opts.theme = state.themes[0].name;
  } catch (e) {
    state.formats = [{ id:'docx', label:'Word', ext:'.docx' }];
    state.themes = [{ name:'GitHub Light', background:'#ffffff', accent:'#000000' }];
    state.fonts = [{ id:'System', label:'System Default' }];
  }

  try {
    const h = await (await fetch('/api/health')).json();
    $('#statusText').textContent = 'Connected on port ' + h.port;
    $('#verTag').textContent = 'v' + h.version;
    $('#foot').textContent = 'Marksmith Express v' + h.version +
      ' — the same conversion engine as the Windows app, over a loopback API. ' +
      'PDF and PNG export need the desktop build.';
  } catch (e) {
    $('#dot').classList.add('down');
    $('#statusText').textContent = 'Server unreachable';
  }

  renderFormats();
  renderOptions();
  updateCurl();
})();
</script>
</body>
</html>
""";
}
