## 2024-05-24 - Initial AI CLI Integration
**Learning:** Adding integration endpoints dynamically using Environment.GetFolderPath ensures correct paths across Windows user profiles.
**Action:** Always use Environment.GetFolderPath instead of hardcoding absolute user paths and check for directory existence before exposing as auto-watch presets.
