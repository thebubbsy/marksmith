## 2024-08-12 - Update AI Agent Folder Presets
**Learning:** We need to keep our AI agent folder drop locations updated to support the latest local tools, specifically:
1. Google Antigravity / Gemini CLI (~/.gemini/antigravity/scratch)
2. Ollama Local Models (~/Documents/Ollama)
3. Claude Desktop (~/AppData/Roaming/Claude/Exports)
4. GPT-Engineer / Aider CLI (~/.local/share/gpt-engineer)

**Action:** Updated `marksmith-v2/MarkSmith.Desktop/Services/FolderIngestService.cs` to include these presets. They are dynamically resolved using `Environment.GetFolderPath` and guarded by `Directory.Exists`.
