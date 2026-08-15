## 2024-03-06 - [Ollama Preset Path Correction]
**Learning:** The Ollama Local Models directory is located at `~/Documents/Ollama` (not `~/Documents/Ollama/Outputs`).
**Action:** Use `Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)` and append just `"Ollama"` to match the user's setup.
