# UI Wireframes — Marksmith.WebApp v1 (low-fi)

> Deliverable 07 of 12. These are layout wireframes, not pixel mocks. The implementation follows
> the block structure here; styling is theme-driven via CSS variables (docs/06-sdk-api.md §5).

## 1. Standalone editor (default layout)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  [Normal ▾] | B I U S [color] | [Table] [Image] [Link] [Comment] | [Undo]     │
│  [Resync] [☑ Suggestions]                                          toolbar    │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  ▍ Heading 1                                                          ▲      │
│   A paragraph with *bold* and _italic_ text with a   ┌─────────────┐  │      │
│   [⏺ u2] remote cursor here.                        │  Comments   │  │      │
│                                                     │  u1: "see…" │  scroll │
│   • bullet item                                     │  [Resolve]  │  │      │
│   1. numbered item                                  └─────────────┘  │      │
│                                                     ┌─────────────┐  ▼      │
│   ┌────────┬────────┬────────┐                      │ Track Chg.  │         │
│   │ cell   │ cell   │ cell   │                      │  ins by u2  │         │
│   └────────┴────────┴────────┘                      │ [Accept][↺] │         │
│                                                     └─────────────┘         │
│                                                                              │
├──────────────────────────────────────────────────────────────────────────────┤
│  session doc-abc · seq 128 · ws open            [u1][u2@3:12]   status bar  │
└──────────────────────────────────────────────────────────────────────────────┘
```

Layout: toolbar (row 1) / editor body (flex-1, scrollable) / panels column (right, collapsible)
/ status bar (bottom, mono).

## 2. Comments panel

```
Comments
─────────
┌─────────────────────────────┐
│  u1                         │
│  "see the OT spec §4"       │
│  [Resolve]                  │
├─────────────────────────────┤
│  u2   (resolved)            │
│  "already done in #128"     │
│  [Reopen]                   │
└─────────────────────────────┘
```

## 3. Track changes panel

```
Track Changes
─────────────
┌─────────────────────────────┐
│  insert by u2               │
│  [Accept] [Reject]          │
├─────────────────────────────┤
│  delete by u1               │
│  [Accept] [Reject]          │
└─────────────────────────────┘
```

## 4. Presence

```
Editor surface (inline):
   …text with [⏺ u2] remote caret here…
Presence rail (bottom, floating pill):
   (u1) (u2 @3:12) (u3 @0:5)
```

Cursors are color-coded per user (deterministic hue from the client id). v1 shows the rail and
inline caret markers; full inline selection shading is Phase 2.

## 5. Loading / error states

```
Connecting… (connecting)          Waiting for server state…
        │                                   │
        ▼                                   ▼
┌──────────────────────┐   ┌──────────────────────────────┐
│ Connecting… (open)   │   │ batch_rejected: "deleteText:  │
│                      │   │ range out of bounds"          │
│ (editor appears once │   │ [Resync]                      │
│  welcome arrives)    │   └──────────────────────────────┘
└──────────────────────┘
```

## 6. iframe mode

```
Host page
┌──────────────────────────────────────────────┐
│  ┌────────────────────────────────────────┐  │
│  │ iframe (same editor layout,           │  │
│  │  sandboxed; no cross-frame selection, │  │
│  │  paste/drag-drop via host bridge)     │  │
│  └────────────────────────────────────────┘  │
└──────────────────────────────────────────────┘
```

## 7. Supported interactions (v1)

| Action | Gesture | Op |
|---|---|---|
| Type | keyboard | insertText |
| Delete | backspace/delete/selection delete | deleteText |
| New paragraph | Enter | insertParagraph |
| Style | toolbar select | insertParagraph (style) |
| Bold/Italic/Underline/Strike/Color | toolbar | applyFormatting |
| Table | toolbar → 3×3 | insertTable |
| Row ops | toolbar (on table block) | insertTableRow/deleteTableRow |
| Image | toolbar → file picker | insertImage |
| Link | toolbar → URL prompt | insertHyperlink |
| Comment | toolbar → text prompt | addComment |
| Resolve/Reopen | comment panel | resolveComment |
| Suggestions | toolbar toggle | applyTrackChange (edits become suggestions) |
| Accept/Reject | track changes panel | accept/rejectTrackChange |
| Undo | toolbar | undo (server-side) |
