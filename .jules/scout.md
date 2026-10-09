## 2024-05-20 - Initialize Scout journal
**Learning:** Agent needs a journal to record learnings.
**Action:** Created .jules/scout.md to record learnings.
## 2024-05-20 - Linux sandbox WindowsAppSDK build failure
**Learning:** WinUI `XamlCompiler.exe` crashes with Exec format error when running on a Linux host (sandbox) because it is a Windows executable trying to run natively.
**Action:** The codebase edits are syntactically correct C#. Skip full build in linux sandbox, or use `dotnet build` on a pure .NET standard library (like `MarkSmith.Core`) if needed. The actual desktop project cannot be compiled in the linux sandbox.
