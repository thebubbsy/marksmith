using System.Text.Json;

namespace MarkSmith.Services;

/// <summary>
/// The MarkSmith Connector browser extension, as it ships inside the app. The build copies the
/// repo's <c>extension</c> folder to <see cref="FolderName"/> beside Marksmith.exe, so the
/// installer, the portable zip and the update feed all carry it, and "Get the extension" can point
/// at a real folder for Load unpacked. Before this, every "Get the extension" button opened the
/// extension's source folder on GitHub, which an installed copy of MarkSmith had no way to load.
/// </summary>
public static class BrowserExtensionPackage
{
    /// <summary>The folder beside Marksmith.exe that holds the unpacked extension.</summary>
    public const string FolderName = "BrowserExtension";

    /// <summary>The full written guide (permissions, options, troubleshooting).</summary>
    public const string GuideUrl = "https://github.com/thebubbsy/MarkSmith/tree/main/extension";

    /// <summary>The address of each browser's extensions page. Browsers refuse to open these from
    /// another app, so the setup guide offers them to copy into the address bar.</summary>
    public static readonly IReadOnlyList<(string Browser, string Address)> ExtensionPages = new[]
    {
        ("Edge", "edge://extensions"),
        ("Chrome", "chrome://extensions"),
    };

    /// <summary>The bundled extension folder under <paramref name="appDirectory"/>, or null when this
    /// copy of MarkSmith doesn't include one (a folder without a manifest doesn't count).</summary>
    public static string? Locate(string appDirectory)
    {
        if (string.IsNullOrEmpty(appDirectory)) return null;
        var folder = Path.Combine(appDirectory, FolderName);
        return File.Exists(Path.Combine(folder, "manifest.json")) ? folder : null;
    }

    /// <summary>The extension's version from its manifest, or null if it can't be read.</summary>
    public static string? Version(string folder)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Where the extension's connection stands, as the setup guide shows it.</summary>
    public enum ConnectionPhase { ApiOff, PortBusy, Waiting, Connected }

    /// <summary>apiEnabled is the setting; apiRunning whether the server actually came up. On but
    /// not running means the port is taken, which "Turn on" can't fix.</summary>
    public static ConnectionPhase Phase(bool apiEnabled, bool apiRunning, bool extensionConnected) =>
        !apiRunning ? (apiEnabled ? ConnectionPhase.PortBusy : ConnectionPhase.ApiOff)
        : extensionConnected ? ConnectionPhase.Connected
        : ConnectionPhase.Waiting;

    /// <summary>The status line for each phase. The extension checks in every 30 seconds (its
    /// command-poll alarm), so "waiting" says how long that can take rather than looking stuck.</summary>
    public static string Describe(ConnectionPhase phase, int port) => phase switch
    {
        ConnectionPhase.ApiOff =>
            "MarkSmith's connection for the extension is off, so the extension can't reach it yet.",
        ConnectionPhase.PortBusy =>
            $"Another program is using port {port}, so the extension can't reach MarkSmith. Pick a different port in Settings ▸ Automation, and the same one in the extension's options.",
        ConnectionPhase.Connected =>
            "Connected. Look for the Copy as Markdown and Email buttons under each reply in ChatGPT, Claude, Gemini or Copilot.",
        _ =>
            $"Waiting for the extension on port {port}. Once it's loaded it checks in within 30 seconds; open a chat page to wake it.",
    };
}
