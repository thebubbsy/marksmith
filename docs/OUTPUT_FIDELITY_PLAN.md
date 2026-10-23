# Output Fidelity Plan: polisher runs R1 to R50

**Owner's call (2026-10-23):** the next 50 runs of the scheduled polisher are about one thing: **what
the Word document looks like when the user opens it.** The Markdown Torture Test output looks worse
today than it did 12 weeks ago, which means the product is failing at its only job.

**Run numbering:** R1 is the first polisher run after this plan lands on `main`, and R50 is the 50th. Record the absolute run number alongside it, e.g. "run #237 (R1)".

This file **overrides** the latest "Next up" list in `PLANNING.md` and the run #220
standing rules wherever they conflict. Every run from R1 to R50 reads this file first.

---

## 1. Why we are here (what went wrong)

- **The polisher drifted off the product.** Runs #150 to #240 (about 90 runs) went into desktop
  chrome: Audit menus, then Diagnostics submenus, then Shift+F10 menus, accelerator columns and
  keyboard reachability. In that time almost nothing touched `DocxExportService`. The last real
  DOCX-look work was the 2026-10-10 batch (`polish(docx): Word documents that print properly`, and
  others).
- **The governance rules were deleted and never came back.** `AGENTS.md` held the hard-won
  Markdown to Word rules (Word/OOXML truths, the normalizer order, "fidelity is the product", "both
  pipelines, always", "verify twice"). It was removed in `e3bdc2f0` (2026-07-22, "before public
  release"). Its replacement, `docs/MD_ENGINE_GOVERNANCE.md`, is gitignored, so it has never been in
  the repo: `GovernanceDocsSyncTests` looks for it, and those tests have been failing ever since. Each run has written them off as "known
  pre-existing failures".
- **Nobody looks at the output.** Run notes report test counts and UIA menu readouts. No run in the
  last 80 rendered the torture test in Word and looked at it.

### Side-by-side evidence (gathered 2026-10-23)

The torture test source (`tests/fidelity/corpus/word-torture-2026-07.md`, recovered from git history)
and `examples/gauntlet.md` were rendered with today's engine (`MarkSmith.Core`, `39033e4`) and with the
v2.9.0 engine (`MdToPdf.Core`, `9839b380`, 2026-08-01). The DOCX files were then rasterised with
LibreOffice. **LibreOffice is not Word**, so treat this as a lead, not a verdict. Phase 0 redoes it in
real Word.

Worse today than in July:
1. **Kitchen-sink blockquote (torture §10):** the task list inside a quote collapses onto one line
   ("☒ Zero downtime deploy ☒ feature-flags cleaned up"). The `diff` fence inside the quote loses its
   red/green line colouring and its own block styling.
2. **GitHub alerts inside table cells (gauntlet §3):** the Warning/Tip callout boxes spill past the
   right edge of the table, and the table splits into stacked sub-tables with different column grids.
3. **Page flow:** "4. Multi-Tab Options Layout" jumps to a new page and leaves a third of page 2 blank.
4. **Running header:** the torture test's page header reads "⚡ QUARTERLY NUMBERS ⚡". The engine took
   a mid-document H2 as the document title.

Never right, in July or today:
5. **Mermaid `stateDiagram-v2`** falls back to raw source text. In July it was near-invisible grey
   text; today it is a plain code dump. Either way, a reader sees source code where a diagram should be.
6. **Equations:** `m:oMath` is present (7 in the gauntlet), but nothing shows in the LibreOffice
   render. Check in Word before assuming either way.
7. **Blocks nested in list items (gauntlet §6):** the quote and SQL block that belong to a level-3
   item fall back to the left margin instead of indenting under that item. The numbering itself
   holds.

Better today (keep these and don't regress them): real Word footnotes (they used to be `[n]` text);
task lists without a stray bullet glyph in front of the checkbox; definition lists indented; inline
code without boxed borders; real callout boxes outside tables.

---

## 2. What "looks right" means (the bar every run is judged against)

The product promise: **the output looks like a careful person built it in Word by hand.** In practice:

| Area | Looks right when… |
|---|---|
| Typography | One body face and one mono face, consistent everywhere. Body 10.5–11pt, line spacing about 1.15. Heading sizes step down clearly (H1 > H2 > H3), with no two levels that look alike. |
| Rhythm | Space before a heading is greater than space after it. Paragraph spacing stays even. No double gaps around HRs, quotes or tables. No cramped runs. |
| Page flow | A heading never sits alone at the bottom of a page (keep-with-next). There are no half-empty pages unless the author wrote a page break. Widow/orphan control is on. Table header rows repeat. |
| Headers/footers | The running header shows the document title (front-matter `title:` or the first H1), never a mid-document heading. The footer is quiet. |
| Code blocks | A shaded block with padding and a thin rule or border, and syntax colours matching the preview. Diff fences colour `+` and `-` lines. The block never splits a short snippet across pages. Inline code is a subtle mono run, not a boxed chip. |
| Tables | Column widths follow the content. Cell padding is comfortable. The header row is bold and shaded. Alignment from `:---:` is honoured. Nothing spills past the table edge. Block content in cells (lists, alerts) stays inside its cell. |
| Callouts / alerts | A Word-native look: a left bar, a tint and a title with an icon. Inside a table cell they fit the cell. Inside a blockquote they nest cleanly. |
| Lists | Native Word numbering. Correct restarts. Nesting indents step evenly. Task items are clickable `w14:checkbox` controls with no bullet glyph. Lists interrupted by quotes or code resume at the right level and number. |
| Blockquotes | A left bar and indent. Nested quotes step in. Any block inside a quote (table, list, code, heading) keeps its own formatting. |
| Math | Native OMML that Word renders and lets you edit. Inline math sits on the baseline. Display math is centred. |
| Diagrams | Native ShapeForge shapes. A diagram family the engine can't draw falls back to a clean picture with a caption, never to raw source text. |
| Footnotes | Real `footnotes.xml` notes. Multi-paragraph notes keep their lists and code. |
| Emoji | Rendered consistently (Segoe UI Emoji in Word). `NoEmoji` strips them without leaving double spaces. |

---

## 3. The governance rules (restored; still binding)

Restored from the deleted `AGENTS.md` (`git show e3bdc2f0^:AGENTS.md`). Run R2 writes them into
`docs/MD_ENGINE_GOVERNANCE.md`.

1. **Fidelity is the product.** Output that is 95% right is a document the user has to fix.
2. **Both pipelines, always.** The HTML preview/PDF and the OpenXML/DOCX output must agree. A DOCX fix
   that diverges from the preview is not a fix. Check `MarkdownHtmlService` for the same construct.
3. **The DOCX walk drops what it has no `case` for.** Historically this has been the main source of
   fidelity bugs. Every Markdown construct needs explicit handling in every container: body,
   blockquote, list item, table cell, callout and footnote. Most of the regressions in §1 are a
   block inside another block.
4. **Normalizer order is load-bearing:** Newlines → Admonition → Dialect → DiagramFenceSniffer →
   EmojiStripper → DashReplacer → Formatting. Any new normalizer goes in all call sites, in the same
   position.
5. **Word/OOXML truths:** task lists are `w14:checkbox` SDTs. Folded callouts are outlineLvl 4 +
   `w15:collapsed`. SVG is a PNG blip plus `asvg:svgBlip`. 1pt = 12700 EMU. The printable width is
   about 460pt. ShapeForge scales with a 75% floor, then falls back to web view. `GEdge.Arrow`
   defaults to true. The 66-node network benchmark gives 66 nodes / 76 edges / 208 `wps:wsp`. Ignore
   the two known `w14:ligatures` validator warnings.
6. **Verify empirically, twice, with different inputs.** "Verified" means it was seen in Word. One
   observation is not confirmation.
7. **Read the output, don't trust the exporter:** unzip, inspect `document.xml`, and run the OpenXml
   validator.

---

## 4. The per-run loop (every run, R1 to R50)

Each run fixes **one visible defect class** end to end. Keep the run notes short.

1. **Render the reference set in real Word.** The routine runs on the owner's Windows machine, so use
   Word itself (COM `Documents.Open` → `SaveAs2(..., 17)` for PDF), then rasterise each page to PNG
   (`pdftoppm`, or `Windows.Data.Pdf`). Do not judge from LibreOffice or the HTML preview.
   - `C:\Users\Tony\OneDrive\Documents\Markdown Torture Test.docx`: the owner's torture test.
     **This is the document that gets judged.** Run R1 finds its Markdown source. If no `.md` sits
     beside it, extract the text and rebuild the source faithfully into
     `tests/fidelity/corpus/torture-test.md`, then confirm it against the docx section by section.
   - `tests/fidelity/corpus/word-torture-2026-07.md`, `examples/gauntlet.md`,
     `examples/chatgpt-export.md`, `examples/product-spec.md`.
2. **Score it.** Fill in the §6 scorecard for each torture-test section, from 0 (broken) to 3 (looks
   hand-made in Word). Compare against the previous run's PNGs.
3. **Pick the worst-scoring defect** that the §5 schedule allows. Name it as the user would see it:
   "the task list in the quote is on one line", not "BlockQuote child dispatch".
4. **Find the root cause in `MarkSmith.Core`.** Fix the class of bug, not the one instance. A fix for
   lists inside quotes should also cover lists inside callouts and table cells.
5. **Pin it with a structural test** that asserts on the produced XML (paragraph count, `w:numPr`,
   `w:shd`, `w:tcW`, run colours, and so on). Add it to `tests/fidelity` coverage in `MarkSmith.Tests`.
   A test that only checks "non-null" or "contains text" does not count.
6. **Check the preview.** Render the same input through `MarkdownHtmlService`. If the preview is
   wrong too, fix both. If only the DOCX was wrong, say so.
7. **Re-render in Word** and put before/after PNGs in the run scratchpad. Then check a second, different
   input that exercises the same construct (rule 6).
8. **Run the whole test project** and report the real count. Fixing `GovernanceDocsSyncTests` is part
   of Phase 0, after which "known failures" stops being an excuse.
9. **Commit** with a message that names the visible change. The run note in `PLANNING.md` is **at most
   25 lines**: the defect, the root cause, the fix, the before/after scores, the test count and the next
   defect.

**Rejected outright:**
- Desktop/WinUI menu, shortcut or keyboard-reachability work. That track is paused for these 50 runs.
  The only UI changes allowed are ones that alter export output, such as an export option that does
  nothing.
- New Audit, Score or Diagnostics features.
- Changes to theme defaults with no before/after Word renders.
- Claims based on a LibreOffice render, an HTML preview or a grep alone.
- Regenerating `docs/media` or `docs/images` (the owner captures those by hand).

---

## 5. Schedule

Runs may finish early or slip within a phase. Each run still ships exactly one defect class. If a
phase runs out of defects, pull from the next phase. Never pull desktop work.

### Phase 0: Baseline and governance (R1 to R3)
- **R1: Word render harness and baseline.** Write `tools/fidelity/render-word.ps1`: md → docx (CLI)
  → Word PDF → PNG per page, and an HTML contact sheet. Locate or rebuild the torture-test source.
  Render it with today's engine **and** v2.9.0 (`git worktree add … 9839b380`, harness against
  `marksmith-v2/MdToPdf.Core`). Score both on the §6 sheet. Commit the scorecard and the harness. The
  PNGs stay out of the repo, or go in `tests/fidelity/baseline/` only if they are small.
- **R2: Restore governance.** Write `docs/MD_ENGINE_GOVERNANCE.md` from §3 plus the AGENTS.md
  §3/§4 material that still applies to `MarkSmith.Core`: the dual-pipeline contract, the block-wrapper
  catalogue (`:::smartart`, `:::workflow`, `:::tabs`, `:::columns`, and others), the inline syntaxes,
  Markdig AST / HtmlSanitizer, AmbiguityDetector / AppSettings. `GovernanceDocsSyncTests` goes green
  **on content, not by keyword stuffing.**
  **That path is gitignored** (`.gitignore`, in the list of internal docs kept out before the public
  release). This is why the tests fail on every clean checkout. If the doc is only written locally,
  the next run won't have it. Remove the `docs/MD_ENGINE_GOVERNANCE.md` line from `.gitignore` in the
  same commit and confirm with `git ls-files docs/MD_ENGINE_GOVERNANCE.md`. The content is the engine
  contract, with no secrets or business plans. If the owner has said it must stay private, ask them
  instead of shipping a local-only doc. Never skip the tests.
- **R3: Fidelity regression gate.** Add a `TortureFidelityTests` class that exports the torture
  corpus and asserts the structural invariants from §2. It should fail today on the §1 regressions,
  marked as `Skip` with the run number that will fix each one. Never skip anything else, and remove each
  `Skip` when that defect is fixed.

### Phase 1: Undo the regressions (R4 to R13)
Targets, in this order unless the Word scorecard says otherwise:
1. Task lists inside blockquotes and callouts keep one item per paragraph, with checkboxes.
2. Fenced code inside blockquotes keeps its block styling and syntax/diff colouring.
3. GitHub alerts inside table cells fit the cell. The table keeps one grid instead of splitting.
4. The running header uses front-matter `title:` or the first H1, never a mid-document H2. Without
   either, leave it blank.
5. Page flow: find out why `Multi-Tab` forced a page jump. Audit `keepNext` and `pageBreakBefore` usage.
6. Quotes and code blocks inside list items indent under their item at every nesting level.
7. Unsupported Mermaid families (stateDiagram-v2 first) render as a picture instead of raw source.
   Then raise ShapeForge coverage family by family in Phase 4.
8. Any further regression the R1 July/today comparison shows.

### Phase 2: Typography and rhythm (R14 to R23)
Theme defaults as a whole: body and mono fonts, sizes, line spacing, heading scale and spacing,
space before and after every block type, HR treatment, keep-with-next on headings and captions,
widow/orphan control. Check one block type per run against the §2 table: headings, paragraphs and
HRs, code blocks, inline code, blockquotes, callouts, tables, lists, captions and figures, front
matter and title block.

### Phase 3: Structure fidelity (R24 to R33)
Tables: content-based column widths, header repeat, alignment, wide tables, merged and empty cells,
block content in cells. Lists: deep mixed nesting, restarts, task lists, loose versus tight. Footnotes:
multi-paragraph, code inside a footnote, numbering. Definition lists. Deeply nested blockquotes.
`:::tabs` / `:::columns` / `:::smartart` / `:::workflow` in Word.

### Phase 4: Math and diagrams in Word (R34 to R41)
OMML rendering as Word displays it: matrices, `\begin{aligned}`, mhchem, inline baseline. ShapeForge
across every Mermaid family the gauntlet uses: flowchart with subgraphs, state, sequence, class, ER,
gantt and pie. Each family is either native shapes or a captioned picture, never source text.
Re-check the 66/76/208 benchmark after every ShapeForge change.

### Phase 5: Real AI-paste corpus (R42 to R47)
Exports from ChatGPT, Claude, Gemini and Copilot (`examples/chatgpt-export.md`, plus fresh real pastes
the owner supplies): artifact repair, KaTeX copy duplication, emoji and `NoEmoji`, and RTL. Judge
whether the page looks like a person made it.

### Phase 6: Lock it in (R48 to R50)
- Freeze the Word-render baseline. Every remaining `Skip` in `TortureFidelityTests` is either fixed or
  explained.
- Compare the final scorecard with the R1 baseline and with v2.9.0. Every section must score at
  least as well as July.
- Write a short summary of all 50 runs for the owner: before/after contact sheets and what is still
  weak. Cut a release.

---

## 6. Scorecard (update every run)

Score 0 to 3 as seen in Word. Columns: July (v2.9.0), the R1 baseline, then the latest run.

| Torture section | July | R1 | Latest | Notes |
|---|---|---|---|---|
| 1. Aligned table, mixed formatting | | | | |
| 2. Task list with nesting | | | | |
| 3. Nested blockquote conversation | | | | |
| 4. Syntax-highlighted code + header line | | | | |
| 5. Inline code in dense prose | | | | |
| 6. Definition-style list | | | | |
| 7. HR sandwich + centred heading | | | | |
| 8. Mixed ordered/unordered deep nesting | | | | |
| 9. Table with code, emoji, `<br>` | | | | |
| 10. Kitchen sink (quote + table + tasks + diff) | | | | |
| Owner's torture test: remaining sections | | | | add rows in R1 |
| Page flow / headers / footers | | | | |

---

## 7. If the routine prompt says something different

The scheduled task (`commit-to-main-ship-improvement--r4efinement`) still asks for a "3-block cycle"
across UI surfaces. For runs R1 to R50, this file wins. If you are the routine and you read this:
follow §4 and §5 and record which phase you are in.
