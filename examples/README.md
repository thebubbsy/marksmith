# Examples

Real documents run through Marksmith. Each `.md` is the input; the matching `.pdf` and `.docx` are
exactly what Marksmith produced from it — GitHub Light theme, AI-quirk normalization on.

| Input | Outputs | What it shows |
| --- | --- | --- |
| [`chatgpt-export.md`](chatgpt-export.md) | [`.pdf`](chatgpt-export.pdf) · [`.docx`](chatgpt-export.docx) | A raw ChatGPT reply — `\(LaTeX\)` delimiters, `【n†source】` citation pips, "Copy code", the trailing disclaimer — cleaned up, with the math compiled into Word's native equation editor |
| [`product-spec.md`](product-spec.md) | [`.pdf`](product-spec.pdf) · [`.docx`](product-spec.docx) | Mermaid flowchart, sequence and state diagrams rebuilt as native Word shapes, plus GitHub alert boxes, syntax-highlighted code and tables |
| [`math-cheatsheet.md`](math-cheatsheet.md) | [`.pdf`](math-cheatsheet.pdf) · [`.docx`](math-cheatsheet.docx) | KaTeX inline and display math in the PDF; ten native OMML equations in the DOCX |

Three more sources have no committed output — they are fixtures for the engine and the README
media rather than showcase documents:

| Source | Purpose |
| --- | --- |
| [`gauntlet.md`](gauntlet.md) | The stress test: matrices, mhchem, nested tables, tabs, dialect callouts, deep lists, multi-line footnotes |
| [`mermaid-all-types.md`](mermaid-all-types.md) | Every Mermaid diagram type, native and harvested |
| [`niche-word-showcase.md`](niche-word-showcase.md) | Cover page, watermark, line numbering, drop caps, parallel columns, form controls, table formulas, concordance index |

## Reproducing these

Open a `.md` in Marksmith (or send it from the browser extension) and export, or regenerate the whole
set from a Release build:

```pwsh
python tools/capture/build_examples.py
```

That drives the desktop app over its local REST API — `/api/batch` for DOCX, `/api/convert` for PDF —
and refuses to write a PDF that rendered as a blank page. It deliberately does **not** use the CLI:
the CLI hands Markdown straight to the exporter, so it applies no AI-quirk normalization and leaves
`\(...\)` / `\[...\]` math as literal text instead of compiling it to OMML.
