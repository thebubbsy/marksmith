## 2026-08-10 - Discovering local paths
**Learning:** Evaluated requested paths for local AI agents such as Ollama. The path `~/Documents/Ollama` is requested rather than `~/Documents/Ollama/Outputs`. GPT-Engineer path `~/.local/share/gpt-engineer` was already present.
**Action:** Update `AiAgentFolderPresets` in `FolderIngestService.cs` so Ollama path uses just `Ollama`. Ensure `.jules/scout.md` incorporates the specific required format and check for completeness.
