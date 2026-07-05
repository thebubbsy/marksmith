# Examples

Real documents run through Marksmith. Each `.md` is the input; the matching `.pdf` is exactly what
Marksmith produced (GitHub Light theme, single continuous page).

| Input | Output | What it shows |
| --- | --- | --- |
| [`chatgpt-export.md`](chatgpt-export.md) | [`chatgpt-export.pdf`](chatgpt-export.pdf) | A raw ChatGPT reply — `\(LaTeX\)` delimiters, `【n†source】` citation pips, "Copy code" — cleaned up, with the math typeset |
| [`product-spec.md`](product-spec.md) | [`product-spec.pdf`](product-spec.pdf) | A Mermaid architecture diagram, GitHub‑style alert boxes, syntax‑highlighted code, and tables |
| [`math-cheatsheet.md`](math-cheatsheet.md) | [`math-cheatsheet.pdf`](math-cheatsheet.pdf) | KaTeX inline and display math |

To reproduce any `.pdf`: open the `.md` in Marksmith (or send it from the browser extension) and
export. Try a different theme, or turn on "Normalize AI formatting quirks" on the ChatGPT one to see
the artifacts disappear.
