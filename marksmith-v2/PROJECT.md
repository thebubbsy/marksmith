# Project: Universal OpenXML SmartArt Compiler

## Architecture
The Universal OpenXML SmartArt Compiler is a programmable DrawingML graphics engine integrated into `MdToPdf.Core`.
It consists of 3 core subsystems:
1. **Parser & AST Pipeline (`MdToPdf.Core/SmartArt/Parser`)**:
   - Converts Markdown nested bullet/numbered lists and JSON hierarchy definitions into a unified `SmartArtAst` model.
2. **Layout Routing & URN Registry (`MdToPdf.Core/SmartArt/Routing`)**:
   - Maps AST semantics (hierarchy depth, cycle loops, sequential lists, Venn intersections) to standard Microsoft `.glox` URNs and layout headers.
3. **OpenXML DrawingML Graph Generator (`MdToPdf.Core/Services/UniversalSmartArtBuilder.cs` & `MdToPdf.Core/SmartArt/Generator`)**:
   - Synthesizes `diagramData.xml` containing compliant `dgm:ptLst` (nodes & trans points) and `dgm:cxnLst` (parent-child graph connection edges), along with accompanying `diagramLayoutHeader.xml`, `diagramColors.xml`, and `diagramStyle.xml`.

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| E2E | E2E Testing Track | Requirement-driven test suite & `TEST_READY.md` | none | DONE |
| 1 | AST Memory Model & Generic Parsers | Markdown list & JSON parsing to `SmartArtAst` (R1) | none | DONE |
| 2 | Layout Algorithm Router & URN Registry | AST semantic routing to `.glox` layout types & URN resolution (R2) | M1 | DONE |
| 3 | OpenXML DrawingML Graph Generator | `diagramData.xml` `ptLst`/`cxnLst` generation & DOCX packaging (R3) | M2 | DONE |
| 4 | Final E2E Pass & Hardening | Pass 100% E2E tests & Tier 5 white-box coverage hardening | M3, E2E | IN_PROGRESS |

## Interface Contracts

### AST & Memory Model
```csharp
namespace MdToPdf.Core.SmartArt.Model;

public enum SmartArtLayoutType
{
    Hierarchy,
    Cycle,
    List,
    Venn,
    Pyramid,
    Matrix
}

public class SmartArtNode
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int Depth { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = new();
    public List<SmartArtNode> Children { get; set; } = new();
}

public class SmartArtRelationship
{
    public string SourceId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Type { get; set; } = "parent-child";
}

public class SmartArtAst
{
    public string Title { get; set; } = string.Empty;
    public SmartArtLayoutType PreferredLayout { get; set; } = SmartArtLayoutType.Hierarchy;
    public List<SmartArtNode> RootNodes { get; set; } = new();
    public List<SmartArtRelationship> Relationships { get; set; } = new();
}
```

### Parser Interface
```csharp
namespace MdToPdf.Core.SmartArt.Parser;

public interface ISmartArtParser
{
    bool CanParse(string input);
    SmartArtAst Parse(string input);
}
```

### Router & Generator Interface
```csharp
namespace MdToPdf.Core.SmartArt.Routing;

public record SmartArtLayoutDefinition(
    string LayoutUrn,
    string HeaderXml,
    string ColorsXml,
    string StyleXml
);

public interface ISmartArtLayoutRouter
{
    SmartArtLayoutDefinition ResolveLayout(SmartArtAst ast);
}

namespace MdToPdf.Core.SmartArt.Generator;

public interface ISmartArtGenerator
{
    byte[] GenerateDiagramDataXml(SmartArtAst ast, SmartArtLayoutDefinition layoutDef);
    void EmbedSmartArtIntoDocx(DocumentFormat.OpenXml.Packaging.MainDocumentPart mainPart, SmartArtAst ast);
}
```

## Code Layout
- Core Project: `MdToPdf.Core/`
  - `SmartArt/Model/` -> AST data structures
  - `SmartArt/Parser/` -> Markdown & JSON parsers
  - `SmartArt/Routing/` -> Glox Layout Router & URN Registry
  - `SmartArt/Generator/` -> DrawingML Graph Generator & OpenXML Packaging
- Test Project: `tests/MdToPdf.Core.Tests/`
  - `SmartArt/` -> Unit & Integration tests for SmartArt
  - `SmartArtE2ETests.cs` -> E2E acceptance test suite
