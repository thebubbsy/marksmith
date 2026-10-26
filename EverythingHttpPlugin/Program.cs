using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EverythingHttpPlugin
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.WriteLine(@"
===================================================================
  ⚡ OmniSight — Native Voidtools Everything HTTP Plugin & Server
  Ground-Up Production Rework • Built-in Zero-Proxy Architecture
===================================================================
");

            // Load base config from Everything.ini / Plugins-1.5a.ini
            var config = EverythingPluginManager.LoadFromEverythingIni();
            string? overrideAssetsDir = null;
            bool runTests = false;
            bool doDeploy = false;
            string? customDeployDir = null;

            // Parse CLI overrides
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelp();
                    return 0;
                }
                else if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var p)) config.Port = p;
                }
                else if (arg.Equals("--everything-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var ep)) config.EverythingPort = ep;
                }
                else if (arg.Equals("--username", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    config.Username = args[++i];
                }
                else if (arg.Equals("--password", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    config.Password = args[++i];
                }
                else if (arg.Equals("--no-auth", StringComparison.OrdinalIgnoreCase))
                {
                    config.AuthEnabled = false;
                }
                else if (arg.Equals("--deploy-templates", StringComparison.OrdinalIgnoreCase))
                {
                    doDeploy = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    {
                        customDeployDir = args[++i];
                    }
                }
                else if (arg.Equals("--assets-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    overrideAssetsDir = args[++i];
                }
                else if (arg.Equals("--disable-old-plugin", StringComparison.OrdinalIgnoreCase))
                {
                    bool ok = EverythingPluginManager.SetHttpServerPluginEnabled(false);
                    Console.WriteLine(ok ? "[Plugin Config] Disabled built-in http_server64.dll in Plugins-1.5a.ini" : "[Plugin Config] Could not find Plugins-1.5a.ini");
                }
                else if (arg.Equals("--enable-old-plugin", StringComparison.OrdinalIgnoreCase))
                {
                    bool ok = EverythingPluginManager.SetHttpServerPluginEnabled(true);
                    Console.WriteLine(ok ? "[Plugin Config] Enabled built-in http_server64.dll in Plugins-1.5a.ini" : "[Plugin Config] Could not find Plugins-1.5a.ini");
                }
                else if (arg.Equals("--test", StringComparison.OrdinalIgnoreCase))
                {
                    runTests = true;
                }
            }

            if (doDeploy)
            {
                Console.WriteLine("[Deploy] Deploying reworked modern template to Everything HTTP server...");
                var (success, msg) = EverythingPluginManager.DeployTemplates(customDeployDir);
                if (success)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[Deploy SUCCESS] {msg}");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Deploy ERROR] {msg}");
                    Console.ResetColor();
                }
            }

            if (runTests)
            {
                Console.WriteLine("[Test] Running native plugin self-verification test suite...");
                return await SelfTestRunner.RunAsync(config);
            }

            Console.WriteLine($"[Config] Bound Port:          http://127.0.0.1:{config.Port}/");
            Console.WriteLine($"[Config] Everything Port:     {config.EverythingPort}");
            Console.WriteLine($"[Config] Authentication:      {(config.AuthEnabled ? $"Enabled (User: '{config.Username}')" : "Disabled")}");
            if (!string.IsNullOrEmpty(config.HomeDirectory))
            {
                Console.WriteLine($"[Config] Everything Home:     {config.HomeDirectory}");
            }

            var server = new NativeHttpServer(config, overrideAssetsDir);
            server.Start();

            var tcs = new TaskCompletionSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n[OmniSight] Shutting down gracefully...");
                tcs.TrySetResult();
            };

            Console.WriteLine("\n[OmniSight] Server is ready. Press Ctrl+C to terminate.");
            await tcs.Task;
            await server.StopAsync();
            return 0;
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"Usage: EverythingHttpPlugin.exe [options]

Options:
  --port <number>              Port for OmniSight server to listen on (default: 8080)
  --everything-port <number>   Port of Voidtools Everything HTTP server (default: 8011)
  --username <name>            Username for authentication
  --password <pass>            Password for authentication
  --no-auth                    Disable authentication requirements
  --deploy-templates [dir]     Deploy modern UI files to Everything home/HTTP Server dir
  --assets-dir <dir>           Directory to serve static assets from (overrides embedded)
  --test                       Run automated self-tests and exit
  --help, -h                   Show this help message
");
        }
    }
}
