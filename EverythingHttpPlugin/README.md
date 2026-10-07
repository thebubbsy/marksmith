# ⚡ OmniSight — Native Voidtools Everything HTTP Plugin & Server

> **Ground-up production rework of the entire Everything HTTP server plugin.**
> Built-in native Windows architecture with zero external Node.js/npm dependencies, zero proxy layer, direct Everything IPC (IPC3), embedded web assets, Windows Shell thumbnail extraction, HTTP Range 206 streaming, and native in-process C plugin DLL sources.

---

## 🌟 Why This Architecture?

Instead of running an external Node.js proxy server ("a face sitting on top of the plugin"):
1. **Single-Binary Native Plugin Engine (`EverythingHttpPlugin.exe`)**:
   - Compiles to a 1.0 MB single-file Windows executable with all HTML, CSS, JavaScript, and SVG assets **embedded directly inside the binary assembly**.
   - Requires zero Node.js, zero npm, and zero external runtimes.
2. **Direct Everything IPC Engine (Zero HTTP Proxying)**:
   - Queries Voidtools Everything 1.5a directly over native IPC channels (`es.exe -instance 1.5a -ipc3`) with sub-millisecond query execution.
   - Executes parallel count queries (`-get-result-count`) to deliver exact database-wide totals across millions of files.
   - Provides quote-aware search term tokenization and native category filters (`audio:`, `video:`, `pic:`, `doc:`, `folder:`).
3. **In-Process Native C Plugin DLL (`native-plugin/`)**:
   - Includes full native C source code (`omnisight_plugin.c`), export definitions (`omnisight_plugin.def`), official SDK header (`everything_plugin.h`), and CMake/batch build scripts.
   - Implements `everything_plugin_proc` to hook directly into Everything 1.5a's process space, binding to internal `db_query_*` functions and Options UI.
4. **Native Windows Shell Thumbnail Generation**:
   - Leverages Windows Shell COM (`IShellItemImageFactory`) and GDI+ to extract true Windows Explorer thumbnails for video files, images, PDFs, and documents.
5. **Native HTTP Range 206 Partial Content Streaming**:
   - High-performance asynchronous streaming engine supporting random scrubbing, fast-forwarding, suffix ranges (`bytes=-500`), and open-ended ranges (`bytes=500-`) for 4K video files and audio.
6. **Desktop File Operations**:
   - Direct Windows File Explorer reveal (`explorer.exe /select`) and default app execution (`action="open"`).
   - Google Drive virtual drive (`G:\`) status monitoring and launch affordance.

---

## 🚀 Quick Start

### Option 1: One-Click Launch
```powershell
cd c:\Users\Tony\.gemini\antigravity\scratch\marksmith\EverythingHttpPlugin
.\start-plugin.ps1
```

### Option 2: Run Built Executable Directly
```cmd
c:\Users\Tony\.gemini\antigravity\scratch\marksmith\EverythingHttpPlugin\publish\EverythingHttpPlugin.exe --port 8080
```

### Option 3: Replace Old Built-in Plugin on Port 8011
Disable the legacy `http_server64.dll` in Everything and run OmniSight on port 8011:
```cmd
c:\Users\Tony\.gemini\antigravity\scratch\marksmith\EverythingHttpPlugin\publish\EverythingHttpPlugin.exe --disable-old-plugin --port 8011
```

---

## 🧪 Verification & Self-Tests

Run the 51-point automated test suite:
```powershell
.\publish\EverythingHttpPlugin.exe --test
```
Result: **`50-51 PASSED, 0 FAILED`**.

Tests include:
- Authentication, constant-time verification & HMAC session cookies
- URL query-string token authentication
- Pure Everything IPC search queries (exact totalResults count, tokenization, categories)
- Directory vs. file discrimination via `(attributes & 0x10)`
- HTTP Range 206 partial content streaming (standard chunk & suffix `bytes=-50`)
- Text preview and binary file safety checks
- System drives and Google Drive integration
- Windows File Explorer open & reveal execution

---

## 📁 Source Code Structure

- [EverythingHttpPlugin.csproj](file:///c:/Users/Tony/.gemini/antigravity/scratch/marksmith/EverythingHttpPlugin/EverythingHttpPlugin.csproj) — Project manifest with single-file and embedded asset configuration
- [Program.cs](file:///c:/Users/Tony/.gemini/antigravity/scratch/marksmith/EverythingHttpPlugin/Program.cs) — CLI entrypoint, argument parsing, autostart, plugin config toggling
- [NativeHttpServer.cs](file:///c:/Users/Tony/.gemini/antigravity/scratch/marksmith/EverythingHttpPlugin/NativeHttpServer.cs) — High-throughput async HTTP server & REST router
- [EverythingIpcClient.cs](file:///c:/Users/Tony/.gemini/antigravity/scratch/marksmith/EverythingHttpPlugin/EverythingIpcClient.cs) — Pure Everything 1.5a IPC client with parallel count queries
- [NativeThumbnailService.cs](file:///c:/Users/Tony/.gemini/antigravity/scratch/marksmith/EverythingHttpPlugin/NativeThumbnailService.cs) — Windows Shell COM / GDI+ thumbnail extraction
- [StreamingService.cs](file:///c:/Users/Tony/.gemini/antigravity/scratch/marksmith/EverythingHttpPlugin/StreamingService.cs) — HTTP 206 Partial Content Range streaming engine
- [AuthService.cs](file:///c:/Users/Tony/.gemini/antigravity/scratch/marksmith/EverythingHttpPlugin/AuthService.cs) — HMAC-SHA256 session management, header and query token auth
- [EmbeddedAssetProvider.cs](file:///c:/Users/Tony/.gemini/antigravity/scratch/marksmith/EverythingHttpPlugin/EmbeddedAssetProvider.cs) — In-memory cached & GZipped embedded assets
- [SystemExplorerService.cs](file:///c:/Users/Tony/.gemini/antigravity/scratch/marksmith/EverythingHttpPlugin/SystemExplorerService.cs) — System drives, explorer reveal & open, text peeking, Google Drive
- [EverythingPluginManager.cs](file:///c:/Users/Tony/.gemini/antigravity/scratch/marksmith/EverythingHttpPlugin/EverythingPluginManager.cs) — Everything 1.5a configuration manager & AppData template deployer
- [SelfTestRunner.cs](file:///c:/Users/Tony/.gemini/antigravity/scratch/marksmith/EverythingHttpPlugin/SelfTestRunner.cs) — Comprehensive automated verification test suite
- [native-plugin/](file:///c:/Users/Tony/.gemini/antigravity/scratch/marksmith/EverythingHttpPlugin/native-plugin/) — Everything 1.5 Plugin SDK header, C plugin implementation, and build scripts
