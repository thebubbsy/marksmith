## 2024-10-27 - Folder Ingest Paths
**Learning:** AI tool output paths often change or are misconfigured; Ollama actually outputs to `~/Documents/Ollama` directly, not the `Outputs` subfolder.
**Action:** Use `Environment.GetFolderPath` to dynamically resolve paths like `MyDocuments` and ensure preset paths match the actual application output folders.
