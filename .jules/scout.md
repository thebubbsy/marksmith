## 2026-08-03 - Fixed Ollama Local Models path
**Learning:** The previous path for Ollama exports contained an extra "Outputs" subfolder which did not match the actual `~/Documents/Ollama` path.
**Action:** Updated the preset in `FolderIngestService.cs` to correctly point to `Path.Combine(documents, "Ollama")`.
