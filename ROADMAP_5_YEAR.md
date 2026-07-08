# Marksmith: 5-Year Master Roadmap

This document outlines the long-term strategic vision for Marksmith. We are transitioning from a single-player desktop utility to a comprehensive B2B Enterprise Governance platform for AI-generated content.

---

## Year 1: Market Penetration & Platform Parity
*Focus: Capture the prosumer market, establish the brand, and break out of the Windows-only ecosystem.*

### Q1-Q2: Desktop Optimization & Marketing
- Launch the production landing page and waitlist.
- Deploy the SEO content engine (targeted articles on AI formatting pain points).
- Implement Batch Processing and Export Presets in the WinUI 3 app.
- Launch the "Branding Kit" feature (custom letterheads/logos for DOCX export).

### Q3-Q4: Cross-Platform Expansion
- **The WebAssembly Port**: Rewrite the core C# OpenXML generation engine to run in the browser via Blazor WebAssembly. 
- Launch **Marksmith Web**: A fully local, privacy-first web application ensuring macOS and Linux users can access the core product.
- Launch the **Browser Extension V2**: Deepen the integration with the desktop app's local REST API. The extension acts as a seamless funnel, grabbing chats and feeding them directly into the desktop app for the proprietary MD to Word conversion, reinforcing the desktop app as the indispensable engine.

---

## Year 2: B2B Transition & Teams
*Focus: Transition from one-off license sales to recurring SaaS revenue by targeting teams and agencies.*

### Q1-Q2: Marksmith Teams
- Launch the SaaS subscription model for teams.
- Centralized configuration: An agency can define a single company "Theme" (fonts, margins, branding kit) that applies to all team members' AI exports.
- Cloud Sync: Sync output profiles and watch-folder configurations across team devices.

### Q3-Q4: Delivery Connectors
- Direct integrations with corporate storage.
- Export directly to SharePoint, Google Drive, and Notion.
- Slack/Teams bot integration: "Hey Marksmith, format this Claude output and drop it in the #marketing channel."

---

## Year 3: Enterprise Governance & AI Compliance
*Focus: Become the compliance layer between raw AI output and corporate documentation.*

### Q1-Q4: The Consent & Tracking Dashboard
- Large enterprises are terrified of unchecked AI content entering their knowledge bases.
- Marksmith becomes the **choke point**: all AI content exported through the corporate network passes through Marksmith.
- Implement watermarking and metadata tagging to trace which LLM generated which document, and when.
- Provide a dashboard for IT/Legal to audit AI usage across the company (e.g., "Show me all documents generated via ChatGPT containing code blocks").

---

## Year 4-5: Ecosystem & Automation
*Focus: Ubiquity in the AI-to-Publishing pipeline.*

- **API as a Service**: Offer the Marksmith conversion engine (Markdown/LaTeX/Mermaid -> Native OpenXML) as a REST API for other developers to integrate into their own AI tools.
- **Deep LLM Partnerships**: Native integrations within major AI interfaces (OpenAI Enterprise, Google Workspace, Microsoft Copilot).
- **Exit Strategy / Expansion**: Evaluate acquisition by a major publisher or expand the engine to handle automated ingestion of multimodal AI outputs (video transcripts, generated audio logs) into living corporate knowledge bases.
