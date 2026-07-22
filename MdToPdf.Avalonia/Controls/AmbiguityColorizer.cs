using System;
using System.Collections.Generic;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using MdToPdf.Models;
using Avalonia.Collections;

namespace MdToPdf.Avalonia.Controls
{
    public class AmbiguityColorizer : DocumentColorizingTransformer
    {
        private readonly object _lock = new();
        private readonly Dictionary<int, AmbiguityCase> _ambiguousLines = new();

        public void UpdateAmbiguities(IEnumerable<AmbiguityCase> cases)
        {
            lock (_lock)
            {
                _ambiguousLines.Clear();
                foreach (var c in cases)
                {
                    // AmbiguityCase.SourceLine is 0-indexed
                    _ambiguousLines[c.SourceLine + 1] = c; // DocumentLine.LineNumber is 1-indexed
                }
            }
        }

        public AmbiguityCase? GetAmbiguityAtLine(int lineNumber)
        {
            lock (_lock)
            {
                return _ambiguousLines.TryGetValue(lineNumber, out var ambiguity) ? ambiguity : null;
            }
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            bool hasLine;
            lock (_lock) { hasLine = _ambiguousLines.ContainsKey(line.LineNumber); }
            if (hasLine)
            {
                ChangeLinePart(line.Offset, line.EndOffset, element =>
                {
                    // Fallback to a red background highlight to ensure visibility 
                    // in case TextDecorations don't render squiggles perfectly.
                    element.TextRunProperties.SetBackgroundBrush(new SolidColorBrush(Color.Parse("#33FF0000")));
                    
                    var squiggly = new TextDecoration
                    {
                        Location = TextDecorationLocation.Underline,
                        Stroke = Brushes.Red,
                        StrokeThickness = 2,
                        StrokeDashArray = new AvaloniaList<double> { 2, 2 }
                    };
                    
                    if (element.TextRunProperties.TextDecorations == null)
                    {
                        element.TextRunProperties.SetTextDecorations(new TextDecorationCollection { squiggly });
                    }
                    else
                    {
                        var collection = new TextDecorationCollection(element.TextRunProperties.TextDecorations);
                        collection.Add(squiggly);
                        element.TextRunProperties.SetTextDecorations(collection);
                    }
                });
            }
        }
    }
}
