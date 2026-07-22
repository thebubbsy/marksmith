# Marksmith Rendering Engine — Architecture Diagram

## Pipeline Overview

```mermaid
flowchart TD
    subgraph INPUT["Raw Markdown Input"]
        RAW["Raw Markdown String\nfrom file, clipboard, or API"]
    end

    subgraph PREPROCESS["Pre-Processing Pipeline"]
        direction TB
        TN["TextNormalizer\nCR/CRLF to LF"]
        AN["AdmonitionNormalizer\n::: fences to GitHub alerts\nObsidian callouts to details"]
        DN["DialectNormalizer\nWiki-links, tags,\ncode titles, MkDocs tabs,\npage breaks, glued tables"]
        DR["DashReplacer\nEm-dashes to hyphens\noutside code fences"]
        ES["EmojiStripper\nRemove emoji/ZWJ\nif NoEmoji mode"]
        DFS["DiagramFenceSniffer\nInfer diagram types\nfrom bare fences"]
        TN --> AN --> DN --> DR --> ES --> DFS
    end

    subgraph MARKDIG["Markdig Parser"]
        PIPE["MarkdownPipelineBuilder\n.UseAdvancedExtensions\n.UseYamlFrontMatter\n.UseAlertBlocks\n.UseMathematics\n.UseEmojiAndSmiley"]
        AST["Markdig AST\nMarkdownDocument"]
        PIPE --> AST
    end

    subgraph DISPATCH["Block-Level Dispatch RenderBlock"]
        direction TB
        HD["HeadingBlock\nW.Paragraph Heading1-6\nbookmark anchors"]
        MB["MathBlock\nOMML equation\ncentered paragraph"]
        FCB_M["FencedCodeBlock mermaid\nShapeForge native shapes\nSnapshot PNG fallback\nCode block fallback"]
        CB["CodeBlock\nShaded paragraph\nConsolas font"]
        AB["AlertBlock\nSingle-cell table\nColored border and icon"]
        QB["QuoteBlock\nRecurse children\nApplyQuoteFormatting"]
        LB["ListBlock\nRenderList\nOrdered/unordered"]
        TB["MdTable\nRenderTable\nHeaders banding alignment"]
        HB["HtmlBlock\nRenderHtmlBlock\nSee HTML dispatch"]
        DL["DefinitionList/Item\nDefinitionTerm style\nDefinition hanging indent"]
        PB["ParagraphBlock\nW.Paragraph\nIntercepts TOC macros"]
        FN["Footnote\nFootnotesPart\nW.FootnoteReference"]
    end

    subgraph INLINE["Inline-Level Dispatch RenderInlines"]
        direction TB
        LIT["LiteralInline W.Text"]
        EMP["EmphasisInline Recurse\nBold Italic Strike Sub Sup"]
        COD["CodeInline W.Text\nConsolas font"]
        LNK["LinkInline RenderLink\nBookmark Image Hyperlink"]
        MTH["MathInline OMML equation"]
        HIN["HtmlInline ApplyHtmlInlineTag\nStack-based format toggle"]
    end

    subgraph HTML_DISPATCH["HTML Block Dispatch RenderHtmlBlock"]
        direction TB
        AF["MARKSMITH_FEATURE comment\nRenderAdvancedFeature\nColumns Tabs AI Chart Canvas"]
        HTB["table tag RenderHtmlTable\ncolspan rowspan support"]
        MED["iframe video svg\nHyperlinked placeholder"]
        DET["details summary\nCollapsible heading\noutline level 4"]
        CTA["Catch-All\nStripHtmlToText\nPlain text paragraph"]
    end

    subgraph OUTPUT["OpenXML Output"]
        DOCX["document.xml\nstyles.xml\nnumbering.xml\nfootnotes.xml\nsettings.xml\nmedia files"]
    end

    RAW --> PREPROCESS
    PREPROCESS --> MARKDIG
    AST --> DISPATCH
    DISPATCH --> INLINE
    DISPATCH --> HTML_DISPATCH
    INLINE --> OUTPUT
    HTML_DISPATCH --> OUTPUT
    DISPATCH --> OUTPUT
```

## Nesting and Recursion Rules

```mermaid
flowchart LR
    subgraph NESTING["Recursive Nesting Rules"]
        direction TB
        N1["QuoteBlock renders children\nTHEN applies formatting\nto all produced paragraphs"]
        N2["ListBlock RenderList\neach ListItem recurses\nback into RenderBlock"]
        N3["AlertBlock RenderAlert\ncreates table cell\nrecurses inner blocks into cell"]
        N4["DefinitionItem recurses\nfirst ParagraphBlock = Term\nrest = Definition"]
        N5["details HTML parses body\nthrough Markdig pipeline\nrecurses RenderBlock"]
        N6["Table cells RenderBlock\non each cell children"]
        N7["EmphasisInline recurses\nRenderInlines with toggled Fmt"]
        N8["HtmlInline push/pop\nformatting stack across\nsibling LiteralInlines"]
    end
```

## Format State Machine for Inline HTML

```mermaid
stateDiagram-v2
    [*] --> Default: Start rendering inlines
    Default --> Bold: b or strong tag
    Default --> Italic: i or em tag
    Default --> Underline: u or ins tag
    Default --> Strike: del or s tag
    Default --> Code: kbd or code tag
    Default --> Highlight: mark tag
    Default --> Sub: sub tag
    Default --> Sup: sup tag
    Default --> Colored: span with style color
    Bold --> Default: closing tag stack pop
    Italic --> Default: closing tag stack pop
    Underline --> Default: closing tag stack pop
    Strike --> Default: closing tag stack pop
    Code --> Default: closing tag stack pop
    Highlight --> Default: closing tag stack pop
    Sub --> Default: closing tag stack pop
    Sup --> Default: closing tag stack pop
    Colored --> Default: closing tag stack pop
```
