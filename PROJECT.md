# Project: Visual Mermaid Diagram Studio Overhaul

## Overview
Overhaul the Visual Mermaid Diagram Studio in Marksmith (`MdToPdf.Avalonia` and `MdToPdf.Core`) to achieve professional-grade usability (mimicking tools like draw.io or FigJam) and ensure the visual editor can seamlessly round-trip full Mermaid text capabilities without data loss.

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| 1 | Exploration & Architecture Analysis | Investigate existing Mermaid parser, visual canvas control, node/connector data structures, line routing, hover menu mechanisms, drag-and-drop, and unit test harness | None | DONE |
| 2 | Absolute Positioning via Metadata Injection | Implement parser/serializer for hidden metadata comments (`%% {"id":"nodeId", "x":100, "y":200}`) in Mermaid Markdown blocks; integrate canvas coordinate sync; write C# unit tests for round-trip verification without corrupting Mermaid syntax | M1 | DONE |
| 3 | Professional UI/UX Usability Enhancements | Implement Multi-Select & Bulk Move, Smart Orthogonal Routing (90-degree angle pathfinding around nodes), and Quick-Add Hover Menus (directional arrows to spawn and connect new nodes) | M2 | DONE |
| 4 | Verification & Forensic Integrity Audit | Execute full build & test suite, verify programmatic acceptance criteria, run Reviewers, Challengers, and Forensic Auditor | M3 | DONE |

## Code Layout
- `MdToPdf/` & `MdToPdf.Avalonia/` — Visual Mermaid Diagram Studio UI controls (`MermaidCanvasControl`), XAML canvas, interactive handlers, node rendering, hover menus.
- `MdToPdf.Core/` — Mermaid text parser, AST models, metadata comment injection/extraction logic (`MermaidMetadataService`, `NodePositionMetadata`, `OrthogonalRouter`).
- `tests/MdToPdf.Core.Tests/` — Unit test project verifying round-trip layout metadata parsing, multi-select, orthogonal routing, and quick-add creation.
- `.agents/` — Agent metadata and state files.

## Acceptance Criteria Status
1. Metadata layout comments (`%% {"id":"A", "x":100, "y":200}`) load into XAML Canvas coordinates: **PASSED (100% verified)**.
2. Modifying XAML Canvas coordinates saves back cleanly into `%%` comments without corrupting Mermaid syntax: **PASSED (100% verified)**.
3. Multi-Select & Bulk Move implemented: **PASSED (100% verified)**.
4. Smart Orthogonal Routing (90-degree angles around nodes) implemented: **PASSED (100% verified)**.
5. Quick-Add Hover Menus (directional arrows spawn & connect node) implemented: **PASSED (100% verified)**.
6. C# Unit Test written & passing for round-trip: **PASSED (100% passing)**.
7. Build succeeds clean without errors: **PASSED (0 Errors, 0 Warnings)**.
8. Forensic Integrity Audit VERDICT CLEAN: **PASSED (VERDICT CLEAN / VICTORY CONFIRMED)**.
