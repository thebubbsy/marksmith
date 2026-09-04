using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;

namespace MarkSmith.Cli.Commands;

/// <summary>
/// Platform Suite health inspection, integration diagnostics, and setup tooling.
/// Unifies Desktop Engine, Express Web, MCP AI Server, CLI, and Document Galaxy into a cohesive hub.
/// </summary>
public static class SuiteCommand
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(800) };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && (args[0].Equals("mcp-setup", StringComparison.OrdinalIgnoreCase) ||
                                args[0].Equals("setup-mcp", StringComparison.OrdinalIgnoreCase) ||
                                (args[0].Equals("mcp", StringComparison.OrdinalIgnoreCase) && args.Length > 1 && args[1].Equals("setup", StringComparison.OrdinalIgnoreCase))))
        {
            return await RunMcpSetupAsync(args);
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"  __  __            _    ____            _ _   _     ");
        Console.WriteLine(@" |  \/  | __ _ _ __| | _/ ___| _ __ ___ (_) |_| |__  ");
        Console.WriteLine(@" | |\/| |/ _` | '__| |/ /\___ \| '_ ` _ \| | __| '_ \ ");
        Console.WriteLine(@" | |  | | (_| | |  |   <  ___) | | | | | | | |_| | | |");
        Console.WriteLine(@" |_|  |_|\__,_|_|  |_|\_\|____/|_| |_| |_|_|\__|_| |_|");
        Console.ResetColor();

        string appVersion = GetAppVersion();
        Console.WriteLine($" Platform Suite & Integrations Diagnostics  •  v{appVersion}");
        Console.WriteLine(new string('─', 65));

        // 1. MarkSmith Core & Environment
        PrintSection("1. Core Runtime & Local Storage");
        PrintItem("CLI Binary", Path.GetFullPath(AppContext.BaseDirectory));
        PrintItem("OS / Arch", $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        PrintItem(".NET Version", Environment.Version.ToString());

        string configDir = AppPaths.ConfigDir;
        bool configExists = Directory.Exists(configDir);
        PrintStatus("Config Store", configExists ? $"Ready ({configDir})" : "Not initialized", configExists);

        string vaultDir = Path.Combine(configDir, "recovery_vault");
        int vaultSnapshots = Directory.Exists(vaultDir) ? Directory.GetFiles(vaultDir).Length : 0;
        PrintItem("Recovery Vault", Directory.Exists(vaultDir) ? $"{vaultSnapshots} snapshot(s) retained" : "Standby");

        string galaxyDir = Path.Combine(configDir, "mindmaps");
        int galaxyMaps = Directory.Exists(galaxyDir) ? Directory.GetFiles(galaxyDir, "*.json").Length : 0;
        PrintItem("Document Galaxy", Directory.Exists(galaxyDir) ? $"{galaxyMaps} knowledge graph(s) saved" : "Ready");

        // 2. Licensing & Entitlements
        PrintSection("2. License & Pro Entitlement");
        try
        {
            AppServices.License.Load();
            if (AppServices.License.CanExportDocx)
            {
                PrintStatus("Licensing", "MarkSmith Pro Active (Full Feature Suite Unlocked)", true);
            }
            else
            {
                int remaining = AppServices.License.State.TrialExportsRemaining;
                PrintStatus("Licensing", $"Free Tier / Trial ({remaining} trial export(s) remaining)", null);
            }
        }
        catch (Exception ex)
        {
            PrintStatus("Licensing", $"Check failed: {ex.Message}", false);
        }

        // 3. Desktop Companion API Server (Port 47821)
        PrintSection("3. Desktop Engine Companion (REST API)");
        string desktopApi = "http://127.0.0.1:47821";
        bool desktopLive = false;
        try
        {
            var res = await Http.GetAsync($"{desktopApi}/api/health");
            if (res.IsSuccessStatusCode)
            {
                desktopLive = true;
                string json = await res.Content.ReadAsStringAsync();
                PrintStatus("Desktop API (47821)", $"Online · Connected to MarkSmith Desktop", true);
            }
        }
        catch
        {
            // Desktop not currently running
        }

        if (!desktopLive)
        {
            PrintStatus("Desktop API (47821)", "Standby · Start MarkSmith.Desktop for live browser pairing", null);
        }

        // 4. Express Web Companion (Port 5000)
        PrintSection("4. Express Web Converter (HTTP)");
        string expressUrl = "http://127.0.0.1:5000";
        bool expressLive = false;
        try
        {
            var res = await Http.GetAsync($"{expressUrl}/api/health");
            if (res.IsSuccessStatusCode)
            {
                expressLive = true;
                PrintStatus("Express UI (5000)", $"Online · Listening at {expressUrl}", true);
            }
        }
        catch
        {
            // Express not currently running
        }

        if (!expressLive)
        {
            PrintStatus("Express UI (5000)", "Standby · Launch via 'marksmith-express' or 'dotnet run --project MarkSmith.Express'", null);
        }

        // 5. Model Context Protocol (MCP) Server
        PrintSection("5. Model Context Protocol (MCP) AI Tools");
        string mcpExePath = ResolveMcpServerPath();
        bool mcpBinaryExists = File.Exists(mcpExePath);
        PrintStatus("MCP Server Binary", mcpBinaryExists ? $"Found ({mcpExePath})" : "marksmith mcp (built-in CLI)", true);

        string claudeConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude",
            "claude_desktop_config.json"
        );
        bool claudeConfigExists = File.Exists(claudeConfigPath);
        bool claudeIntegrated = false;
        if (claudeConfigExists)
        {
            try
            {
                string text = File.ReadAllText(claudeConfigPath);
                if (text.Contains("marksmith", StringComparison.OrdinalIgnoreCase))
                {
                    claudeIntegrated = true;
                }
            }
            catch { }
        }

        if (claudeIntegrated)
        {
            PrintStatus("Claude Desktop", $"Integrated in {claudeConfigPath}", true);
        }
        else if (claudeConfigExists)
        {
            PrintStatus("Claude Desktop", $"Installed, but MarkSmith MCP not configured. Run 'marksmith mcp setup --write-claude'", null);
        }
        else
        {
            PrintItem("Claude Desktop", "Not detected or config file not present");
        }

        // 6. Browser Companion Integration
        PrintSection("6. Browser Companion & Native Ingestion");
        PrintItem("Native Pairing Port", "127.0.0.1:47821 (Automatic CORS authentication)");
        PrintItem("Supported Hosts", "ChatGPT, Claude.ai, Gemini, Copilot, Perplexity, DeepSeek");

        Console.WriteLine();
        Console.WriteLine(new string('─', 65));
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(" Platform Quick Actions:");
        Console.ResetColor();
        Console.WriteLine("  • Setup AI MCP tools in Claude/Cursor :  marksmith mcp setup [--write-claude]");
        Console.WriteLine("  • Batch render Markdown documents     :  marksmith batch ./docs --format docx");
        Console.WriteLine("  • High-DPI document snapshot image    :  marksmith render-image doc.md snap.png");
        Console.WriteLine("  • Run built-in stdio MCP server       :  marksmith mcp");
        Console.WriteLine();

        return 0;
    }

    public static async Task<int> RunMcpSetupAsync(string[] args)
    {
        bool writeClaude = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--write-claude", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--install-claude", StringComparison.OrdinalIgnoreCase))
            {
                writeClaude = true;
            }
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=== MarkSmith Model Context Protocol (MCP) Setup ===");
        Console.ResetColor();
        Console.WriteLine();

        string mcpPath = ResolveMcpServerPath();
        var configObj = new
        {
            mcpServers = new
            {
                marksmith = new
                {
                    command = mcpPath,
                    args = Array.Empty<string>()
                }
            }
        };

        string json = JsonSerializer.Serialize(configObj, new JsonSerializerOptions { WriteIndented = true });

        Console.WriteLine("Configuration snippet for Claude Desktop or Cursor:");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(json);
        Console.ResetColor();
        Console.WriteLine();

        string claudeConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude"
        );
        string claudeConfigFile = Path.Combine(claudeConfigDir, "claude_desktop_config.json");

        if (writeClaude)
        {
            try
            {
                if (!Directory.Exists(claudeConfigDir))
                {
                    Directory.CreateDirectory(claudeConfigDir);
                }

                JsonObject rootObj;
                if (File.Exists(claudeConfigFile))
                {
                    string existingText = await File.ReadAllTextAsync(claudeConfigFile);
                    // Backup original file
                    await File.WriteAllTextAsync($"{claudeConfigFile}.bak", existingText);
                    Console.WriteLine($"[Backup] Existing Claude config backed up to {claudeConfigFile}.bak");
                    var node = JsonNode.Parse(existingText);
                    rootObj = node as JsonObject ?? new JsonObject();
                }
                else
                {
                    rootObj = new JsonObject();
                }

                if (!rootObj.ContainsKey("mcpServers") || rootObj["mcpServers"] is not JsonObject)
                {
                    rootObj["mcpServers"] = new JsonObject();
                }

                var mcpServers = (JsonObject)rootObj["mcpServers"]!;
                var marksmithEntry = new JsonObject
                {
                    ["command"] = mcpPath,
                    ["args"] = new JsonArray()
                };

                mcpServers["marksmith"] = marksmithEntry;

                string updatedJson = rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(claudeConfigFile, updatedJson);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ Successfully updated Claude Desktop configuration: {claudeConfigFile}");
                Console.ResetColor();
                Console.WriteLine("Restart Claude Desktop to activate the MarkSmith MCP tools.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Failed to write Claude config: {ex.Message}");
                Console.ResetColor();
                return 1;
            }
        }
        else
        {
            Console.WriteLine($"To automatically register with Claude Desktop, run:");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  marksmith mcp setup --write-claude");
            Console.ResetColor();
            Console.WriteLine($"Or copy the JSON above into: {claudeConfigFile}");
            return 0;
        }
    }

    private static string ResolveMcpServerPath()
    {
        var appDir = AppContext.BaseDirectory;
        var mcpExe = Path.Combine(appDir, "marksmith-mcp.exe");
        if (File.Exists(mcpExe)) return mcpExe;

        var siblingDebug = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "MarkSmith.Mcp", "bin", "Debug", "net8.0", "marksmith-mcp.exe"));
        if (File.Exists(siblingDebug)) return siblingDebug;

        var siblingRelease = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "MarkSmith.Mcp", "bin", "Release", "net8.0", "marksmith-mcp.exe"));
        if (File.Exists(siblingRelease)) return siblingRelease;

        var cliExe = Path.Combine(appDir, "marksmith.exe");
        if (File.Exists(cliExe)) return cliExe;

        return "marksmith-mcp";
    }

    private static string GetAppVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(SuiteCommand).Assembly;
        var iv = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(iv))
        {
            var plus = iv.IndexOf('+');
            return plus > 0 ? iv[..plus] : iv;
        }
        return asm.GetName().Version?.ToString(3) ?? "3.0.0";
    }

    private static void PrintSection(string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[{title}]");
        Console.ResetColor();
    }

    private static void PrintItem(string label, string value)
    {
        Console.Write($"  {label,-22} : ");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(value);
        Console.ResetColor();
    }

    private static void PrintStatus(string label, string message, bool? isSuccess)
    {
        Console.Write($"  {label,-22} : ");
        if (isSuccess == true)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("✓ ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(message);
        }
        else if (isSuccess == false)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("✗ ");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(message);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("○ ");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(message);
        }
        Console.ResetColor();
    }
}
