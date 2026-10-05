# Marksmith WinUI3 Desktop — Polish Plan

This file tracks an ongoing routine with one mandate: **the WinUI3 desktop app
(`marksmith-v2/MarkSmith.Desktop`) is feature-complete — stop adding features and make every
existing one feel completely finished.** That means hover/press feedback, consistent spacing and
typography, tooltips, empty/loading/error states, and general interaction polish, applied
uniformly across the whole app, not new functionality.

**Out of scope for this routine:** `marksmith-webapp`, `MarkSmith.Cli`, `MarkSmith.Express`,
`MarkSmith.Mcp`, the browser `extension`, and the marketing `website`. Those are separate efforts
pivoting in their own direction — leave them exactly as they are.

**Standing rules for every run of this routine:**
- Build must stay green (`dotnet build marksmith-v2\MarkSmith.Desktop\MarkSmith.Desktop.csproj -c Debug -p:Platform=x64`) before committing.
- Smoke-launch `Marksmith.exe` after building to confirm it doesn't crash on startup.
- Commit straight to `main` — including planning-only sessions, before stopping.
- Review this file's latest entry before starting, so plans build on each other instead of
  restarting from scratch.

## Inventory of WinUI3 surfaces (as of 2026-10-06)

Windows: `MainWindow` (editor), `SplashWindow`, `Views/History/HistoryWindow`,
`Views/MindMap/MindMapGalaxyWindow`, `Views/ShapeStudio/ShapeDesignStudioWindow`,
`Views/SmartArtStudio/SmartArtDesignStudioWindow`.

UserControls hosted inside those: `Views/SuiteHubView` (the hub/launcher), `Views/SettingsView`,
`Views/WelcomeTour`, `Views/SmartArtInsertControl`, `Views/ImageInsertControl`,
`Controls/ExtensionHintBar`, `Controls/ExtensionTip`, `Views/Mermaid/MermaidDiagramStudioControl`,
`Views/Mermaid/MermaidCanvasControl`, `Views/Mermaid/NodePaletteControl`.

Before this session, `App.xaml` only defined static card/typography styles (`PipelineCardStyle`,
`StepBadgeStyle`, etc.) — every button across the whole app relied on the stock WinUI3
`PointerOver`/`Pressed` background tint with no motion, and no view had any hover-specific code.

## Session log

### 2026-10-06 (scheduled routine run)

Reviewed: no prior `PLANNING.md` existed — this is the first run.

**Shipped** (commit follows this entry):
- `Services/HoverPolish.cs` — a single reusable helper that walks a visual-tree root and attaches
  a subtle scale "lift" to every `ButtonBase` it finds: scales to 1.035x on hover, 0.97x on press,
  eases back on release, 140ms `CubicEase EaseOut`. Layers on top of (doesn't replace) WinUI's
  existing background tint, so buttons now have real motion feedback instead of just a color
  change. Idempotent via an attached marker property, so it's safe to call more than once on the
  same tree. Opt-out via `Tag="NoHoverPolish"` on any control that shouldn't get it.
- Wired `HoverPolish.Apply(...)` into the constructor of every Window/UserControl listed in the
  inventory above that hosts real buttons (all except `SplashWindow`, which has no buttons — it's
  click-anywhere-to-skip).
- `Views/Mermaid/MermaidCanvasControl`: the per-node "quick add" direction buttons live in an
  `ItemsControl.ItemTemplate` and don't exist at construction time, so those are polished
  individually as each node is realized via a new `Loaded="OnNodeTemplateLoaded"` hook on the
  template's root `Grid` instead.

**Verified:**
- `dotnet build` (Debug/x64): 0 warnings, 0 errors.
- Smoke-launched `Marksmith.exe`: started cleanly, no crash, closed after ~6s.
- Could not visually confirm the hover motion itself in this session — no desktop GUI
  automation/screenshot tool was available in this autonomous run (only web-browser automation
  tools were available, not native Windows UI ones). **Please eyeball the hover feel on the Suite
  Hub buttons next time you have the app open** and flag anything (too fast/slow/strong) so the
  next run can tune `HoverScale`/`PressScale`/`AnimationDuration` in `HoverPolish.cs`.

**Known gaps — deliberately not fixed this run:**
1. Flyout and `ContentDialog` content (e.g. the `MenuFlyout` on "Copy MCP Config", plugin
   install/remove confirmation flows) isn't in the visual tree until opened, so buttons inside
   those don't get the hover lift yet. Needs a `FlyoutBase.Opened`/`ContentDialog.Opened` hook.
2. `MindMapGalaxyWindow` builds a lot of its canvas visuals (`NodeVisual`/`EdgeVisual`) as raw
   Shapes in code, not buttons — not applicable to this pass, but worth a dedicated look for
   its own hover/selection affordances later.
3. This pass is motion-only. It doesn't touch focus-visual consistency, keyboard navigation
   highlighting, or add any elevation/shadow on hover — only scale.

**Next up, in priority order, for whoever/whatever picks this routine up next:**
1. Extend `HoverPolish` (or a sibling helper) to flyouts/dialogs via `Opened` events so every
   button in the app is covered, not just the ones statically in a window's initial tree.
2. Pass over `MindMapGalaxyWindow`, `ShapeDesignStudioWindow`, `SmartArtDesignStudioWindow`
   individually: confirm each has a deliberate, polished empty/first-run state (not just a blank
   canvas) and that their own toolbars got the hover lift (they should have, via this session's
   change — re-verify visually).
3. Tooltip sweep: several buttons already set `ToolTipService.ToolTip` (e.g. "Config Folder",
   "Dismiss this tip"); find any icon-only button across the app still missing one.
4. Typography/spacing consistency sweep across the 6 Suite Hub cards and the Settings plugin
   cards — they're already close, just verify padding/line-height rhythm matches exactly.
5. (Unrelated to this routine, left here only as a reminder — see memory
   `marksmith-examples-need-regen`): once PRs #85 and #86 are both merged, regenerate
   `examples/*.docx`/`*.pdf` once from a branch containing both fixes.
