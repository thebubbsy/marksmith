using System;
using System.Collections.Generic;
using System.Linq;

namespace MdToPdf.Core.Services
{
    public class TemplatePreset
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string MarkdownContent { get; set; } = string.Empty;
    }

    public class DocumentTemplateGalleryService
    {
        private readonly List<TemplatePreset> _presets = new List<TemplatePreset>();

        public DocumentTemplateGalleryService()
        {
            _presets.Add(new TemplatePreset
            {
                Id = "tech_spec",
                Name = "Technical Specification",
                Description = "Standard technical design document with architecture, requirements, and API definitions.",
                Category = "Engineering",
                MarkdownContent = @"# Technical Specification: {{title}}

**Author**: {{author}}  
**Date**: {{date}}  
**Status**: Draft  

## 1. Overview
High-level description of {{title}}.

## 2. Requirements
- Requirement 1
- Requirement 2

## 3. Architecture & Design
```mermaid
graph TD
    A[Client] --> B[API Server]
    B --> C[(Database)]
```

## 4. API Specification
`GET /api/v1/resource` - Retrieves system resources.
"
            });

            _presets.Add(new TemplatePreset
            {
                Id = "exec_brief",
                Name = "Executive Brief",
                Description = "Concise project status brief with KPIs, risks, and next steps.",
                Category = "Management",
                MarkdownContent = @"# Executive Brief: {{title}}

**Prepared By**: {{author}}  
**Date**: {{date}}  

> [!IMPORTANT]
> Key Milestone Delivery targeted for {{date}}.

## Key Highlights
- Milestone 1 completed ahead of schedule.
- Resource allocation optimized.

## Summary Table
| Metric | Status | Owner |
|:---|:---:|---:|
| Budget | On Track | {{author}} |
| Timeline | On Track | PMO |
"
            });

            _presets.Add(new TemplatePreset
            {
                Id = "meeting_minutes",
                Name = "Meeting Minutes",
                Description = "Structured meeting notes with attendees, discussion items, and action items.",
                Category = "Operations",
                MarkdownContent = @"# Meeting Minutes: {{title}}

**Date**: {{date}}  
**Attendees**: {{author}}, Team  

## Agenda
1. Project Status Update
2. Blockers & Risks

## Discussion Items
- Reviewed architecture diagrams.
- Approved Q3 deliverables.

## Action Items
- [ ] Task 1 - Assigned to {{author}}
- [ ] Task 2 - Assigned to Team
"
            });
        }

        public List<TemplatePreset> GetTemplates() => _presets.ToList();

        public TemplatePreset? GetTemplateById(string id) =>
            _presets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        public string ApplyTemplate(string templateId, Dictionary<string, string> variables)
        {
            var template = GetTemplateById(templateId);
            if (template == null) return string.Empty;

            string result = template.MarkdownContent;
            if (variables != null)
            {
                foreach (var kvp in variables)
                {
                    result = result.Replace("{{" + kvp.Key + "}}", kvp.Value);
                }
            }

            return result;
        }
    }
}
