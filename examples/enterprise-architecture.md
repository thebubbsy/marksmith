# Enterprise Distributed Architecture

The MarkSmith compiler transforms Markdown ASTs into native Office OpenXML packages with zero COM dependency:

```mermaid
flowchart LR
    A[Raw AI Prompt] --> B{MarkSmith Compiler}
    B -->|Native OMML| C[Editable Word Equations]
    B -->|ShapeForge| D[Vector DrawingML Shapes]
    B -->|ContrastGuard| E[WCAG 2.1 AA Document]
```

Unlike legacy tools that embed blurry raster images, MarkSmith translates Mermaid code fences into **native grouped vector shapes**:

> [!TIP]
> In Microsoft Word, you can click on each diagram box, change its fill color, drag connector lines, and edit label text. Real Office DrawingML vectors.
