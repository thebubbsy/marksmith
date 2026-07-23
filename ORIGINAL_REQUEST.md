# Original User Request

## Initial Request — 2026-07-23T08:37:48Z

Overhaul the existing Visual Mermaid Diagram Studio in Marksmith to achieve professional-grade usability (mimicking tools like draw.io or FigJam) and ensure the visual editor can seamlessly round-trip full Mermaid text capabilities without data loss.

Working directory: C:\Users\Tony\.gemini\antigravity\scratch\marksmith
Integrity mode: demo

## Requirements

### R1. Absolute Positioning via Metadata Injection
The visual editor must support free-form drag-and-drop of nodes to absolute coordinates. Because standard Mermaid text acts as an auto-layout engine, you must inject hidden metadata comments (e.g., `%% {"id":"A", "x":500, "y":200}`) into the generated Markdown to persist exact layouts.
**Crucial Constraint:** The generated Mermaid text must remain 100% standards-compliant so that if a user copies the code block into an external renderer (like GitHub or standard Mermaid Live), it still renders perfectly (the external renderer will simply ignore the layout comments).

### R2. Professional UI/UX Usability Enhancements
The visual editor must feel like a modern, premium diagramming tool (e.g., Miro). Implement the following capabilities:
1. **Multi-Select & Bulk Move:** Users can drag a selection box to grab multiple nodes and move them synchronously.
2. **Smart Orthogonal Routing:** Connection lines must path cleanly at 90-degree angles around other nodes, rather than drawing overlapping straight lines.
3. **Quick-Add Hover Menus:** Hovering over a node must reveal directional arrows that instantly spawn and connect a new node.

## Acceptance Criteria

### Programmatic Verification
- [ ] A C# unit test must be written that parses a Markdown block containing standard Mermaid syntax and metadata comments (e.g., `%% {"id":"A", "x":100}`).
- [ ] The test must verify that the metadata layout comments successfully load into XAML Canvas coordinates, and conversely, modifying XAML coordinates saves back cleanly into `%%` comments without corrupting the standard Mermaid syntax.
- [ ] Build succeeds clean without errors.

## Follow-up — 2026-07-23T10:01:56Z

# Teamwork Project Prompt

> Status: Launched
> Goal: Overhaul the Visual Mermaid Diagram Studio

Overhaul the existing Visual Mermaid Diagram Studio in Marksmith to achieve professional-grade usability (mimicking tools like draw.io or FigJam) and ensure the visual editor can seamlessly round-trip full Mermaid text capabilities without data loss.

Working directory: C:\Users\Tony\.gemini\antigravity\scratch\marksmith
Integrity mode: demo

## Requirements

### R1. Absolute Positioning via Metadata Injection
The visual editor must support free-form drag-and-drop of nodes to absolute coordinates. Because standard Mermaid text acts as an auto-layout engine, you must inject hidden metadata comments (e.g., `%% {"id":"A", "x":500, "y":200}`) into the generated Markdown to persist exact layouts.
**Crucial Constraint:** The generated Mermaid text must remain 100% standards-compliant so that if a user copies the code block into an external renderer (like GitHub or standard Mermaid Live), it still renders perfectly (the external renderer will simply ignore the layout comments).

### R2. Professional UI/UX Usability Enhancements
The visual editor must feel like a modern, premium diagramming tool (e.g., Miro). Implement the following capabilities:
1. **Multi-Select & Bulk Move:** Users can drag a selection box to grab multiple nodes and move them synchronously.
2. **Smart Orthogonal Routing:** Connection lines must path cleanly at 90-degree angles around other nodes, rather than drawing overlapping straight lines.
3. **Quick-Add Hover Menus:** Hovering over a node must reveal directional arrows that instantly spawn and connect a new node.

## Acceptance Criteria

### Programmatic Verification
- [ ] A C# unit test must be written that parses a Markdown block containing standard Mermaid syntax and metadata comments (e.g., `%% {"id":"A", "x":100}`).
- [ ] The test must verify that the metadata layout comments successfully load into XAML Canvas coordinates, and conversely, modifying XAML coordinates saves back cleanly into `%%` comments without corrupting the standard Mermaid syntax.
- [ ] Build succeeds clean without errors.

