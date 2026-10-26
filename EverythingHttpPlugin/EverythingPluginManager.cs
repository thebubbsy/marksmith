using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace EverythingHttpPlugin
{
    public static class EverythingPluginManager
    {
        public static string AppDataEverythingDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everything");

        public static string PluginsIniPath =>
            Path.Combine(AppDataEverythingDir, "Plugins-1.5a.ini");

        public static string EverythingIniPath =>
            Path.Combine(AppDataEverythingDir, "Everything-1.5a.ini");

        public static PluginConfig LoadFromEverythingIni()
        {
            var config = new PluginConfig();
            var iniPath = PluginsIniPath;
            if (!File.Exists(iniPath))
            {
                iniPath = Path.Combine(AppDataEverythingDir, "Plugins.ini");
            }

            if (!File.Exists(iniPath))
            {
                return config;
            }

            try
            {
                var lines = File.ReadAllLines(iniPath);
                bool inHttpSection = false;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';')) continue;

                    if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                    {
                        var section = trimmed[1..^1];
                        inHttpSection = section.Contains("http_server", StringComparison.OrdinalIgnoreCase) ||
                                        section.Contains("HTTP Server", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (inHttpSection)
                    {
                        var eq = trimmed.IndexOf('=');
                        if (eq > 0)
                        {
                            var key = trimmed[..eq].Trim().ToLowerInvariant();
                            var val = trimmed[(eq + 1)..].Trim();

                            switch (key)
                            {
                                case "port":
                                    if (int.TryParse(val, out var p)) config.EverythingPort = p;
                                    break;
                                case "username":
                                    config.Username = val;
                                    break;
                                case "password":
                                    config.Password = val;
                                    break;
                                case "home":
                                    config.HomeDirectory = val;
                                    break;
                                case "allow_file_download":
                                    config.AllowFileDownload = val == "1";
                                    break;
                            }
                        }
                    }
                }
            }
            catch { }

            return config;
        }

        public static (bool success, string message) DeployTemplates(string? targetDir = null)
        {
            try
            {
                var config = LoadFromEverythingIni();
                var deployPath = targetDir;

                if (string.IsNullOrEmpty(deployPath))
                {
                    deployPath = Path.Combine(AppDataEverythingDir, "HTTP Server");
                }

                Directory.CreateDirectory(deployPath);

                var assembly = Assembly.GetExecutingAssembly();
                var resourceNames = assembly.GetManifestResourceNames();

                int copied = 0;
                foreach (var rName in resourceNames)
                {
                    if (!rName.Contains(".Embedded.", StringComparison.OrdinalIgnoreCase)) continue;

                    var relative = rName[(rName.IndexOf(".Embedded.", StringComparison.OrdinalIgnoreCase) + ".Embedded.".Length)..];
                    string fileName;
                    if (relative.StartsWith("css.", StringComparison.OrdinalIgnoreCase))
                    {
                        var cssDir = Path.Combine(deployPath, "css");
                        Directory.CreateDirectory(cssDir);
                        fileName = Path.Combine(cssDir, relative["css.".Length..]);
                    }
                    else if (relative.StartsWith("js.", StringComparison.OrdinalIgnoreCase))
                    {
                        var jsDir = Path.Combine(deployPath, "js");
                        Directory.CreateDirectory(jsDir);
                        fileName = Path.Combine(jsDir, relative["js.".Length..]);
                    }
                    else
                    {
                        fileName = Path.Combine(deployPath, relative);
                    }

                    using var stream = assembly.GetManifestResourceStream(rName);
                    if (stream != null)
                    {
                        using var fs = new FileStream(fileName, FileMode.Create, FileAccess.Write);
                        stream.CopyTo(fs);
                        copied++;
                    }
                }

                // Update default_page in Plugins-1.5a.ini if found
                UpdateDefaultPageSetting("index.html");

                return (true, $"Deployed {copied} template files to '{deployPath}' and configured default_page=index.html");
            }
            catch (Exception ex)
            {
                return (false, "Deployment failed: " + ex.Message);
            }
        }

        private static void UpdateDefaultPageSetting(string pageName)
        {
            var iniPath = PluginsIniPath;
            if (!File.Exists(iniPath)) return;

            try
            {
                var lines = File.ReadAllLines(iniPath);
                bool modified = false;
                bool inHttp = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                    {
                        var section = trimmed[1..^1];
                        inHttp = section.Contains("http_server", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (inHttp && trimmed.StartsWith("default_page=", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"default_page={pageName}";
                        modified = true;
                        break;
                    }
                }

                if (modified)
                {
                    File.WriteAllLines(iniPath, lines, Encoding.UTF8);
                }
            }
            catch { }
        }

        public static bool SetHttpServerPluginEnabled(bool enabled)
        {
            var iniPath = PluginsIniPath;
            if (!File.Exists(iniPath)) return false;

            try
            {
                var lines = File.ReadAllLines(iniPath);
                bool modified = false;
                bool inHttp = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                    {
                        var section = trimmed[1..^1];
                        inHttp = section.Contains("http_server", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (inHttp && trimmed.StartsWith("enabled=", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"enabled={(enabled ? "1" : "0")}";
                        modified = true;
                        break;
                    }
                }

                if (modified)
                {
                    File.WriteAllLines(iniPath, lines, Encoding.UTF8);
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static bool IsHttpServerPluginEnabled()
        {
            var iniPath = PluginsIniPath;
            if (!File.Exists(iniPath)) return false;

            try
            {
                var lines = File.ReadAllLines(iniPath);
                bool inHttp = false;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                    {
                        var section = trimmed[1..^1];
                        inHttp = section.Contains("http_server", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (inHttp && trimmed.StartsWith("enabled=", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = trimmed["enabled=".Length..].Trim();
                        return val == "1";
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
