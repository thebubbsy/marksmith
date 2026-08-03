# TEST READY — NATIVE COLLAPSIBLE TOGGLES (MILESTONE 2)

## Status: READY & PASSING

The E2E and Unit test suite for **Native Collapsible Toggles** (Milestone 2) is fully set up, compiled, discovered, and passing.

---

## Coverage Summary

- **Total Toggle Tests**: 13
- **Passed**: 13 / 13 (100% Pass Rate)
- **Test File**: `tests/MarkSmith.Core.Tests/ToggleTests.cs`
- **Output Artifact**: `test_outputs/sample_toggle.docx` (Generated & DOM-Validated)

---

## Test Execution Command

To run the toggle test suite:
```bash
dotnet test --filter ToggleTests
```

To run the complete project test suite:
```bash
dotnet test
```

---

## Feature Coverage Matrix

| Tier | Test Case | Target Feature / Verification | Status |
|------|-----------|-------------------------------|--------|
| **Tier 1** | `Tier1_BracketedToggleSyntax_ParsesAndCreatesCollapsibleHeading` | `:::toggle [Title]` syntax parsing & OpenXML container conversion | **PASS** |
| **Tier 1** | `Tier1_UnbracketedToggleSyntax_ParsesAndCreatesCollapsibleHeading` | `:::toggle Title` syntax parsing & conversion | **PASS** |
| **Tier 1** | `Tier1_HtmlDetailsSummarySyntax_ParsesAndCreatesCollapsibleHeading` | `<details><summary>Title</summary>` syntax parsing & conversion | **PASS** |
| **Tier 2** | `Tier2_EmptyTitle_FallsBackToDefaultTitle` | Empty title fallback behavior | **PASS** |
| **Tier 2** | `Tier2_EmptyBody_RendersHeaderWithoutCrashing` | Header paragraph rendered with zero-length body | **PASS** |
| **Tier 2** | `Tier2_NestedToggles_RendersBothOuterAndInnerCollapsibleHeaders` | Nested toggle container hierarchy | **PASS** |
| **Tier 2** | `Tier2_SpecialCharactersInTitle_EscapesSafelyInOpenXml` | Special characters (`&`, `<`, `>`, `"`) in title string | **PASS** |
| **Tier 2** | `Tier2_MultipleSequentialToggles_RenderInSequence` | Multiple consecutive toggle blocks | **PASS** |
| **Tier 2** | `Tier2_ToggleContainingCodeBlockAndTable_RendersCodeAndTableInside` | Code blocks and tables embedded in toggle body | **PASS** |
| **Tier 3** | `Tier3_ToggleWithCalloutBox_RendersAlertInsideToggle` | Admonition callouts (`> [!WARNING]`) inside toggle body | **PASS** |
| **Tier 3** | `Tier3_ToggleWithInnerHeadings_RendersHeadingsInsideToggle` | Markdown headings inside toggle body | **PASS** |
| **Tier 3** | `Tier3_ToggleWithListItems_RendersListInsideToggle` | Bulleted/numbered list items inside toggle body | **PASS** |
| **Tier 4** | `Tier4_RealWorldScenario_GeneratesSampleToggleDocxAndValidatesDom` | Generates `test_outputs/sample_toggle.docx`, verifies `ParagraphProperties.OutlineLevel` = 8, `DefaultCollapsed` = true, run bold styling, and ECMA-376 schema validation | **PASS** |
