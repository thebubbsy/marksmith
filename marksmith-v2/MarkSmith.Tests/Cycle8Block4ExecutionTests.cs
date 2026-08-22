using System;
using System.Linq;
using MarkSmith.Services;
using MarkSmith.Services.Mermaid;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle8Block4ExecutionTests
{
    [Fact]
    public void MermaidSequenceDiagramBuilder_ParsesAndReordersMessages()
    {
        string diagram = """
            sequenceDiagram
                participant User
                participant Server
                participant Database
                User->>Server: GET /data
                Server->>Database: Query()
                Database-->>Server: Result
                Server-->>User: 200 OK
            """;

        var builder = MermaidSequenceDiagramBuilder.Parse(diagram);
        Assert.Equal(3, builder.Participants.Count);
        Assert.Equal(4, builder.Messages.Count);

        // Reorder message 0 and 1
        bool ok = builder.MoveMessage(0, 1);
        Assert.True(ok);

        string serialized = builder.ToMermaidSyntax();
        Assert.Contains("sequenceDiagram", serialized);
        Assert.Contains("autonumber", serialized);
        Assert.Contains("Server->>Database: Query()", serialized);
    }

    [Fact]
    public void SmartArtHierarchyFoldingService_ParsesTreeAndInjectsSvgScript()
    {
        string list = """
            - CEO
              - VP Engineering
                - Lead Dev
                - Senior QA
              - VP Marketing
            """;

        var root = SmartArtHierarchyFoldingService.ParseHierarchyTree(list, "Executive Board");
        Assert.Equal("Executive Board", root.Title);
        Assert.Single(root.Children); // CEO
        Assert.Equal(2, root.Children[0].Children.Count); // VP Engineering & VP Marketing
        Assert.Equal(2, root.Children[0].Children[0].Children.Count); // Lead Dev & Senior QA

        string svg = "<svg width=\"500\" height=\"300\"><g></g></svg>";
        string interactive = SmartArtHierarchyFoldingService.InjectCollapsibleSvgInteractivity(svg);
        Assert.Contains("toggleSmartArtBranch", interactive);
    }

    [Fact]
    public void FootnoteReferenceGraphService_DetectsReferencesAndBrokenOrphans()
    {
        string md = """
            # Architecture Overview
            
            This is documented in [^spec] and mentioned by [@knuth1984].
            Also see [Next Chapter](#implementation-details).
            
            [^spec]: Technical specification document.
            [^orphan]: This footnote is never called.
            
            Refer to broken footnote [^missing].
            Refer to broken link [Broken Section](#non-existent).
            """;

        var graph = FootnoteReferenceGraphService.BuildGraph(md);
        Assert.Equal(1, graph.TotalCitations);
        Assert.Equal(2, graph.TotalFootnotes);
        Assert.Single(graph.OrphanDefinitions); // orphan
        Assert.Contains("orphan", graph.OrphanDefinitions[0]);
        Assert.Equal(3, graph.BrokenReferences.Count); // missing, implementation-details, non-existent
    }

    [Fact]
    public void DocxTypographyService_ValidatesProgrammingLigatureFonts()
    {
        Assert.True(DocxTypographyService.SupportsProgrammingLigatures("Cascadia Code"));
        Assert.True(DocxTypographyService.SupportsProgrammingLigatures("Fira Code"));
        Assert.True(DocxTypographyService.SupportsProgrammingLigatures("JetBrains Mono"));
        Assert.False(DocxTypographyService.SupportsProgrammingLigatures("Arial"));
    }

    [Fact]
    public void PdfFormFieldService_ExtractsFieldsAndGeneratesHtml()
    {
        string formMd = """
            Please complete the following:
            - Name: [text:user_name:Enter your full name]
            - Date: [date:submission_date]
            - Status: [choice:status:Pending|Approved|Rejected]
            - Accept Terms: [x]
            """;

        var fields = PdfFormFieldService.ExtractFormFields(formMd);
        Assert.Equal(4, fields.Count);
        Assert.Contains(fields, f => f.Name == "user_name" && f.FieldType == FormFieldType.TextInput);
        Assert.Contains(fields, f => f.Name == "submission_date" && f.FieldType == FormFieldType.DateInput);
        Assert.Contains(fields, f => f.Name == "status" && f.FieldType == FormFieldType.DropdownChoice && f.Options?.Count == 3);
        Assert.Contains(fields, f => f.FieldType == FormFieldType.Checkbox && f.DefaultValue == "true");

        string html = PdfFormFieldService.TransformToHtmlInputs(formMd);
        Assert.Contains("<input type=\"text\" name=\"user_name\"", html);
        Assert.Contains("<input type=\"date\" name=\"submission_date\"", html);
        Assert.Contains("<select name=\"status\"", html);
    }

    [Fact]
    public void DocumentAnnotationService_ExtractsCommentsAndStats()
    {
        string md = """
            # Project Plan
            <!-- comment:rev1 author="Alice" date="2026-08-16T12:00:00Z" text="Review milestone dates" resolved="false" -->
            Milestone 1 will launch in Q3.
            <!-- comment:rev2 author="Bob" date="2026-08-16T12:30:00Z" text="Verified budget allocation" resolved="true" -->
            Budget is approved.
            """;

        var report = DocumentAnnotationService.ExtractAnnotations(md);
        Assert.Equal(2, report.Comments.Count);
        Assert.Equal(1, report.OpenCommentsCount);
        Assert.Equal(1, report.ResolvedCommentsCount);
        Assert.Equal(1, report.AuthorContributionCounts["Alice"]);
        Assert.Equal(1, report.AuthorContributionCounts["Bob"]);
    }
}
