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

### 2026-10-06 (scheduled routine run #2)

Reviewed: the 2026-10-06 run #1 entry above — picked up its priority-ordered list and worked
items 1 and 3.

**Shipped** (commit follows this entry):
- `Services/HoverPolish.cs`: added `ShowPolishedAsync(this ContentDialog)` — an extension method
  that hooks the dialog's `Opened` event to run `Apply` over the whole dialog (so its
  Primary/Secondary/Close buttons, plus any custom buttons placed in `Content`, get the same
  hover lift) before calling the real `ShowAsync()`. Also added `AttachOnOpen(Flyout)` for plain
  (non-`MenuFlyout`) flyouts, for future use — see "Known gaps" below for why it isn't wired up
  everywhere yet.
- Replaced all 22 `dialog.ShowAsync()`/`dlg.ShowAsync()` call sites across `MainWindow.xaml.cs`,
  `Views/History/HistoryWindow.xaml.cs`, `Views/SettingsView.xaml.cs`,
  `Views/MindMap/MindMapGalaxyWindow.xaml.cs`, and `Views/Mermaid/MermaidDiagramStudioWindow.cs`
  with `MarkSmith.Services.HoverPolish.ShowPolishedAsync(...)`. This closes known gap #1 from the
  prior entry for `ContentDialog` specifically — every confirmation dialog, plugin install/remove
  flow, and "create new X" prompt in the app now has motion feedback on its buttons, not just a
  color tint.
- Tooltip sweep (priority 3): wrote a small one-off PowerShell heuristic (icon-only button body —
  `FontIcon`/`SymbolIcon` with no sibling `TextBlock`/`Content` text — and no
  `ToolTipService.ToolTip` anywhere in its own tag) across every `.xaml` file, covering `Button`,
  `ToggleButton`, `RepeatButton`, and `HyperlinkButton`. Found exactly one real gap:
  `Views/MindMap/MindMapGalaxyWindow.xaml`'s node-preview-card close button (✕ glyph, no label)
  was missing a tooltip — added `ToolTipService.ToolTip="Close preview"`, matching the app's
  existing "Close (Escape)" wording convention from `MainWindow.xaml`. Everything else the
  heuristic flagged turned out to be a labelled button (icon + text) that the earlier regex
  mis-matched — the app's tooltip coverage was already solid, consistent with the prior entry's
  note that this was "already close."

**Verified:**
- `dotnet build` (Debug/x64): 0 warnings, 0 errors, both before and after the tooltip fix.
- Smoke-launched `Marksmith.exe` twice (once per build): started cleanly, no crash, ran ~6s,
  closed via `taskkill` both times.
- Still could not visually confirm the dialog hover motion or the new tooltip in this session —
  same constraint as run #1, no native Windows UI automation/screenshot tool available to this
  autonomous run. **Please eyeball a couple of `ContentDialog`s (e.g. "New Document", the plugin
  install/remove confirmation, the history/galaxy rename prompts) next time you have the app
  open** to confirm their buttons lift correctly and nothing looks stretched/clipped by the scale
  transform at dialog width.

**Known gaps — deliberately not fixed this run:**
1. Plain `Flyout` content (as opposed to `MenuFlyout`, which intentionally keeps its stock
   highlight) still isn't wired to `HoverPolish` even though `AttachOnOpen(Flyout)` now exists.
   Audited every non-menu `Flyout` in the app: the lint/outline/history flyouts in
   `MainWindow.xaml` only contain `ListView`s (no buttons to polish), and the two node
   fill/stroke-color flyouts in `MermaidDiagramStudioControl.xaml` host a `ColorPicker`, which
   already has its own rich built-in interaction model — layering a scale lift onto its internal
   "more colors" toggle felt like a real risk of visual conflict for low reward, so left alone.
   If a *future* flyout gets added with plain buttons in its content, call
   `HoverPolish.AttachOnOpen(thatFlyout)` once (e.g. in the view's constructor).
2. `MenuFlyoutItem` (the item type inside every `MenuFlyout` in the app — the ⋯ menu, "Copy MCP
   Config", align/distribute, etc.) is explicitly *not* given the scale lift: it's not a
   `ButtonBase`, and a per-row scale animation inside a dropdown list would look broken next to
   WinUI's standard full-row highlight. This is a deliberate exclusion, not a gap.
3. Items 2 and 4 from the prior entry's priority list (per-studio empty-state pass; Suite
   Hub/Settings typography rhythm) weren't touched this run — still open, see below.

**Next up, in priority order, for whoever/whatever picks this routine up next:**
1. Pass over `MindMapGalaxyWindow`, `ShapeDesignStudioWindow`, `SmartArtDesignStudioWindow`
   individually: confirm each has a deliberate, polished empty/first-run state (not just a blank
   canvas) and that their own toolbars got the hover lift (carried over from run #1, still open).
2. Typography/spacing consistency sweep across the 6 Suite Hub cards and the Settings plugin
   cards — they're already close, just verify padding/line-height rhythm matches exactly (carried
   over from run #1, still open).
3. Next time the app is open in a GUI session (this routine only has non-interactive/headless
   runs so far): visually confirm (a) the original button hover/press lift from run #1 feels
   right — tune `HoverScale`/`PressScale`/`AnimationDuration` in `HoverPolish.cs` if not, and (b)
   this run's `ContentDialog` lift doesn't look odd on any dialog, especially ones with a
   `PrimaryButtonStyle="{StaticResource AccentButtonStyle}"` override.
4. Focus-visual consistency and keyboard navigation highlighting — still untouched by either run
   so far; both runs have been motion/mouse-only.
5. (Unrelated to this routine, left here only as a reminder — see memory
   `marksmith-examples-need-regen`): once PRs #85 and #86 are both merged, regenerate
   `examples/*.docx`/`*.pdf` once from a branch containing both fixes.

### 2026-10-06 11:07 AEST (scheduled routine run #3)

Reviewed: run #2's entry above. Started on its priority #1 (per-studio empty/first-run pass),
beginning with `SmartArtDesignStudioWindow`, and found two cross-cutting hover bugs along the way.

**Shipped** (commit follows this entry):
- **Bug fix — press feedback never fired.** `ButtonBase` marks `PointerPressed`/`PointerReleased`
  as handled for its own click logic, so `HoverPolish.AttachTo`'s plain `+=` subscriptions for
  those two events never ran: buttons lifted on hover but the 0.97x press "squish" from run #1
  never happened anywhere. Now subscribed via `AddHandler(..., handledEventsToo: true)`.
- **Bug fix — templated buttons had no hover lift.** `HoverPolish.Apply` runs at construction,
  before any `DataTemplate` content is realized, so every button inside an item template missed
  the polish (only `MermaidCanvasControl` had a hand-written workaround). Added a XAML-settable
  attached property, `services:HoverPolish.ApplyOnLoad="True"` (class is now `public` so the
  XAML compiler can see it), and set it on the template root at all 8 affected sites:
  `MainWindow.xaml` (1), `HistoryWindow.xaml` (1), `MindMapGalaxyWindow.xaml` (tag pills, palette
  swatches), `ShapeDesignStudioWindow.xaml` (preset list, palette items),
  `SmartArtDesignStudioWindow.xaml` (per-row add/delete buttons). Future item templates with
  buttons should use the same attribute.
- **SmartArt studio empty states:**
  - Deleting every node used to leave the centre outline panel completely blank. It now shows an
    icon, "No nodes yet", a one-line explanation, and an accent **Add first node** button that
    reuses the existing `AddChild` command and drops focus straight into the inline rename box.
  - A Layout Gallery search with zero matches used to show an empty list. It now shows "No
    layouts match your search" with a hint.
  - Backed by two computed VM properties in `MarkSmith.Core` (`IsOutlineEmpty`,
    `HasNoLayoutMatches`), raised from `RebuildOutline`/`FilterLayouts`. No behaviour change
    otherwise.

**Verified:**
- `dotnet build` (Debug/x64): 0 warnings, 0 errors. Confirmed `ApplyOnLoad` is registered in the
  generated `XamlTypeInfo.g.cs`.
- Smoke-launched `Marksmith.exe`: still running cleanly after 8s, then killed.
- Still no GUI automation in these headless runs, so neither the press squish nor the new empty
  states have been seen by eye. **Next time the app is open:** click-and-hold any button to check
  the press now feels right, and in SmartArt Studio delete every node, then search the gallery
  for "zzz", to see both empty states.

**Audit notes (no change needed):**
- `ShapeDesignStudioWindow` already has a deliberate empty-canvas hint ("SmartArt & Vector Shape
  Studio" + guidance), but it uses hardcoded hex colours (`#555555`/`#BBBBBB`/`#888888`, and
  `#232329` panels) rather than theme brushes, so it will look wrong in Light theme. Logged below.
- `MindMapGalaxyWindow` has an empty-*selection* hint in the inspector, but no check yet for an
  empty *galaxy* (zero nodes) canvas state.

**Next up, in priority order:**
1. `ShapeDesignStudioWindow`: swap its hardcoded dark hex colours for `ThemeResource` brushes so
   it respects Light/Dark like the rest of the app (check the whole window, not just the empty
   hint).
2. `MindMapGalaxyWindow`: confirm/add a polished empty-galaxy state for when there are zero nodes.
3. SmartArt studio's live preview hardcodes a `#18181c` page background in `BuildWrapperHtml`.
   Check how it reads in Light theme.
4. Typography/spacing rhythm sweep across Suite Hub cards and Settings plugin cards (carried over
   from run #1).
5. Focus-visual consistency and keyboard navigation highlighting (carried over).
6. (Reminder, unrelated to this routine: see memory `marksmith-examples-need-regen`.)

### 2026-10-06 (scheduled routine run #4)

Reviewed: run #3's entry above. Worked its priorities #1 and #2.

**Shipped** (commit follows this entry):
- **`ShapeDesignStudioWindow` now follows Light/Dark theme.** Every hardcoded hex colour in the
  XAML (root `#1B1B1F`, panels `#232329`/`#18181C`, inner sections `#1E1E24`/`#1C1C22`, toolbar
  `#1F1F25`, canvas `#141417`, borders `#33333B`/`#2A2A34`, the `#0078D4` badge, and ~50
  `Foreground="White"`/`#BBBBBB`/`#CCCCCC`/`#888888`/`#555555` text colours) is now a Fluent
  `ThemeResource` brush, using the same set `SmartArtDesignStudioWindow` already uses:
  `ApplicationPageBackgroundThemeBrush`, `CardBackgroundFillColorDefaultBrush` +
  `CardStrokeColorDefaultBrush` for panels, `LayerOnAcrylicFillColorDefaultBrush` for title/
  status/toolbar strips and inner sections, `SolidBackgroundFillColorBaseBrush` for the drawing
  canvas, and `TextFillColorPrimary/Secondary/Tertiary/Disabled` for text. Zero hex literals left
  in that file. Shape fills on the canvas are user content and untouched.
- **`MindMapGalaxyWindow` empty-galaxy state.** Deleting every node (or dismissing the tour with
  nothing of your own left) used to leave a blank dark void. It now shows a centred card: galaxy
  icon, "Your galaxy is empty", one line of guidance, an accent **Add first node** button (reuses
  the existing `AddRootNodeCommand`, which drops the node at the viewport centre and selects it)
  and an **Import Vault** button (same handler as the toolbar). Backed by a new
  `IsGalaxyEmpty` VM property raised from the existing `OnNodeCollectionChanged`. The card uses
  theme brushes so it reads correctly on the galaxy's fixed dark canvas in either theme.

**Verified:**
- `dotnet build` (Debug/x64): 0 warnings, 0 errors.
- Smoke-launched `Marksmith.exe`: still running after 8s, then killed.
- Not seen by eye (still headless). **Next time the app is open:** switch to Light theme and open
  Shape Studio — check every panel, the canvas, and the empty hint; then in Mind Map Galaxy,
  select-all + delete (or Dismiss Tour on a fresh galaxy) to see the new empty card.

**Notes:**
- The working tree had unrelated uncommitted edits in `MarkSmith.Core` (`HouseLayout`,
  `DocxExportService`, `TemplateThemeService`) and `MarkSmith.Tests/HouseLayoutTests.cs` when this
  run started. They aren't part of this routine and were deliberately left uncommitted.
- `MindMapGalaxyWindow` keeps two intentional hex colours (`#12131C` root/canvas) — the "galaxy"
  is a deliberate always-dark space aesthetic, and every overlay on it is a self-contained card
  with theme brushes, so it's coherent. Not changing it.

**Next up, in priority order:**
1. SmartArt studio's live preview hardcodes a `#18181c` page background in `BuildWrapperHtml`.
   Check how it reads in Light theme (carried over).
2. Hex-colour sweep of the remaining views: grep `"#[0-9A-Fa-f]{6}"` across
   `MarkSmith.Desktop/**/*.xaml` and convert any that break Light theme (same approach as Shape
   Studio this run). Skip the galaxy canvas (intentional, see above).
3. Typography/spacing rhythm sweep across Suite Hub cards and Settings plugin cards (carried over).
4. Focus-visual consistency and keyboard navigation highlighting (carried over).
5. (Reminder, unrelated to this routine: see memory `marksmith-examples-need-regen`.)

### 2026-10-06 (scheduled routine run #5)

Reviewed: run #4's entry above. Worked its priorities #1, #2 and #3.

**Shipped** (commit follows this entry):
- **SmartArt live preview follows the app theme.** `BuildWrapperHtml` hardcoded a `#18181c` page
  behind the rendered SmartArt, which showed as a near-black slab inside the Light-theme preview
  card. The page is now `background:transparent` and `PreviewWebView.DefaultBackgroundColor` is
  `Transparent`, so the preview card's own theme brush shows through. The rendered diagram itself
  is a self-contained light card (`#f8f9fa`) in `HtmlPreviewRenderer`, so it reads on either.
- **Mermaid Diagram Studio caption buttons.** The Studio is deliberately always-dark
  (`RequestedTheme="Dark"`, custom cyan palette), but its extended title bar's min/max/close glyphs
  follow the *OS* theme — on a Light-mode Windows they were black on `#1E1E2E`, i.e. invisible.
  Now pinned via `AppWindow.TitleBar.Button*ForegroundColor`/`HoverBackgroundColor`/
  `PressedBackgroundColor` in `MermaidDiagramStudioWindow.cs`.
- **Suite Hub card rhythm.** Audited all 6 cards: padding (14), corner radius (10), header (15px
  icon / 13 SemiBold), description (11.5 secondary), badges and button sizes were already
  identical. The one real inconsistency: each card body was a `StackPanel`, so in a row where one
  description wraps to more lines, the two cards' button rows sat at different heights. Bodies are
  now a 3-row `Grid` (Auto / * / Auto) so action buttons are bottom-aligned across each row.

**Hex-colour sweep result (priority #2) — no further conversions needed:**
- `Mermaid/MermaidDiagramStudioControl.xaml`, `MermaidCanvasControl.xaml`, `NodePaletteControl.xaml`
  (~130 hex literals): intentional. The whole Studio is forced `RequestedTheme="Dark"` with its own
  cyan accent overrides, so it's internally coherent in either app/OS theme. Same category as the
  galaxy canvas — don't "fix" it.
- `WelcomeTour.xaml` (8): per-theme `ThemeDictionaries` accent overrides (green), already
  Light/Dark aware.
- `MindMapGalaxyWindow.xaml` (2): the intentional `#12131C` galaxy canvas.
- There is no in-app theme switch — the app follows the OS theme — so windows other than the
  Mermaid Studio don't need caption-button pinning (checked SmartArt, Shape, Splash, Main).

**Verified:**
- `dotnet build` (Debug/x64): 0 warnings, 0 errors.
- Smoke-launched `Marksmith.exe`: still running after 8s, then killed.
- Not seen by eye (still headless). **Next time the app is open:** with Windows in Light mode,
  open Mermaid Studio and check the caption buttons; open SmartArt Studio and check the preview
  pane has no dark slab; open Suite Hub and confirm the button rows line up across each card row.

**Notes:**
- The same unrelated uncommitted `MarkSmith.Core`/`MarkSmith.Tests` edits from run #4 are still in
  the working tree; again left uncommitted.

**Next up, in priority order:**
1. Settings plugin cards: the same rhythm audit just done on Suite Hub (padding, type sizes,
   bottom-aligned actions).
2. Focus-visual consistency and keyboard navigation highlighting (carried over — still untouched
   by every run so far; all work has been mouse/visual).
3. GUI-session visual check of everything listed under "Next time the app is open" across runs
   #1–#5 (hover/press tuning in `HoverPolish.cs`, dialog lift, empty states, Light theme).
4. (Reminder, unrelated to this routine: see memory `marksmith-examples-need-regen`.)
