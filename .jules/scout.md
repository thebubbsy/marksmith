## 2024-05-24 - Auto-detecting new local AI output directories
**Learning:** Hardcoded absolute paths (like C:\Users\John) will fail across different user profiles and operating systems, creating friction for auto-detect features.
**Action:** Always use `Environment.GetFolderPath` to dynamically resolve user profile paths (like `Environment.SpecialFolder.UserProfile`, `ApplicationData`, `MyDocuments`) combined with `Directory.Exists` guards to ensure the folders are only watched if they actually exist on the current user's machine.
