using System.Text.Json;
using MarkSmith.Models;
using Microsoft.JSInterop;

namespace MarkSmith.Wasm.Services;

/// <summary>
/// AppSettings persistence for the browser: the desktop app stores settings in %LOCALAPPDATA%,
/// which doesn't exist under WebAssembly — so we serialize the same AppSettings POCO to
/// localStorage via JS interop. Everything downstream (FormattingService, DialectNormalizer,
/// DocxExportService, EpubExportService, ThemeCatalog) consumes AppSettings unchanged.
/// </summary>
public sealed class WasmSettingsStore
{
    private const string StorageKey = "marksmith.settings.v1";
    private readonly IJSRuntime _js;

    public WasmSettingsStore(IJSRuntime js) => _js = js;

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            var raw = await _js.InvokeAsync<string>("marksmithStorageGet", StorageKey);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(raw);
                if (settings is not null) return settings;
            }
        }
        catch { /* first run / corrupt — defaults */ }
        return new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        try
        {
            var raw = JsonSerializer.Serialize(settings);
            await _js.InvokeVoidAsync("marksmithStorageSet", StorageKey, raw);
        }
        catch { /* storage unavailable — settings just won't persist */ }
    }
}
