# Project: Marksmith Engine Markdown Bug Fixes

## Overview
Fix markdown parsing/rendering bugs in the Marksmith engine:
- R1: Double hyphens (`--`) in text converted to native em-dashes (`—`).
- R2: `[[WikiLinks]]` parsed and styled correctly in DOCX export without spell-check errors or unmapped HTML tags.

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| 1 | Architecture & Codebase Exploration | Locate double-hyphen and WikiLink handling logic in Marksmith codebase | None | DONE |
| 2 | Automated OpenXML Verification Script | Create test harness to inspect `.docx` `word/document.xml` | M1 | DONE |
| 3 | Implementation Fixes | Fix em-dash conversion and WikiLink DOCX OpenXML rendering | M1 | DONE |
| 4 | Independent Review & Adversarial Testing | Verify fix correctness, test edge cases, and run integrity audit | M2, M3 | DONE |

## Code Layout
- `MdToPdf.Core/` — Core markdown parser, Markdig extensions, and OpenXML / HTML rendering logic.
- `MdToPdf/` — Main app / engine CLI / library.
- `tests/` — Automated test suites and verification scripts.
- `.agents/` — Agent working directories and metadata.

## Acceptance Criteria
1. Exporting markdown containing `text -- text` results in `text — text` in `.docx` OpenXML.
2. Exporting markdown containing `[[WikiLinks]]` results in properly styled OpenXML runs without unmapped HTML tags or spell-check errors.
3. Automated Python/PowerShell script verifies `document.xml` inside exported `.docx` file.
