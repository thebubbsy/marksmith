## 2024-05-20 - Scout Initialization
**Learning:** Initializing Scout's journal for tracking AI integration presets.
**Action:** Always maintain records of newly integrated local AI tool paths.

## 2024-05-20 - Fix Ollama Local Models preset
**Learning:** The Ollama output path is typically `~/Documents/Ollama`, not `~/Documents/Ollama/Outputs`.
**Action:** Update the preset in `FolderIngestService.cs` to remove the "Outputs" subdirectory.
