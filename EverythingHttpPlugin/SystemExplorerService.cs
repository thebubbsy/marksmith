using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace EverythingHttpPlugin
{
    public static class SystemExplorerService
    {
        public static List<DriveInfoModel> GetDrives()
        {
            var list = new List<DriveInfoModel>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        var isReady = d.IsReady;
                        list.Add(new DriveInfoModel
                        {
                            Name = d.Name,
                            VolumeLabel = isReady ? d.VolumeLabel : "",
                            DriveType = d.DriveType.ToString(),
                            TotalSize = isReady ? d.TotalSize : 0,
                            FreeSpace = isReady ? d.AvailableFreeSpace : 0,
                            IsReady = isReady
                        });
                    }
                    catch
                    {
                        list.Add(new DriveInfoModel
                        {
                            Name = d.Name,
                            DriveType = d.DriveType.ToString(),
                            IsReady = false
                        });
                    }
                }
            }
            catch { }

            return list;
        }

        public static bool RevealInExplorer(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;

            try
            {
                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{fullPath}\"",
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    return true;
                }
            }
            catch { }

            return false;
        }

        public static bool OpenFile(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;

            try
            {
                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = fullPath,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    return true;
                }
            }
            catch { }

            return false;
        }

        public static async Task<(bool success, string content, string error)> GetTextPreviewAsync(string fullPath, int maxBytes = 256 * 1024)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                return (false, "", "File not found");
            }

            try
            {
                var fi = new FileInfo(fullPath);
                if (fi.Length > 10 * 1024 * 1024) // 10MB limit for text preview
                {
                    return (false, "", "File too large for live text preview (>10MB)");
                }

                using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var length = (int)Math.Min(fi.Length, maxBytes);
                var buffer = new byte[length];
                int read = await fs.ReadAsync(buffer.AsMemory(0, length));

                // Check for null bytes (binary file detection)
                for (int i = 0; i < Math.Min(read, 1024); i++)
                {
                    if (buffer[i] == 0)
                    {
                        return (false, "", "Binary file detected");
                    }
                }

                var text = Encoding.UTF8.GetString(buffer, 0, read);
                if (fi.Length > maxBytes)
                {
                    text += $"\n\n--- [Truncated: showing first {maxBytes / 1024} KB of {fi.Length / 1024} KB] ---";
                }

                return (true, text, "");
            }
            catch (Exception ex)
            {
                return (false, "", ex.Message);
            }
        }

        public static GoogleDriveStatusModel GetGoogleDriveStatus()
        {
            var model = new GoogleDriveStatusModel();
            try
            {
                // Check running process
                var procs = Process.GetProcessesByName("GoogleDriveFS");
                model.Running = procs.Length > 0;

                // Check standard drive G:
                if (Directory.Exists(@"G:\My Drive") || Directory.Exists(@"G:\"))
                {
                    model.Installed = true;
                    model.Mounted = true;
                    model.MountPath = @"G:\";
                    model.Status = model.Running ? "Connected (G:\\)" : "Drive detected (offline)";
                    return model;
                }

                // Check other letters
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.IsReady && (d.VolumeLabel.Contains("Google", StringComparison.OrdinalIgnoreCase) || d.Name.Equals(@"G:\", StringComparison.OrdinalIgnoreCase)))
                    {
                        model.Installed = true;
                        model.Mounted = true;
                        model.MountPath = d.Name;
                        model.Status = $"Connected ({d.Name})";
                        return model;
                    }
                }

                // Check LocalAppData
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var gDriveData = Path.Combine(appData, "Google", "DriveFS");
                if (Directory.Exists(gDriveData))
                {
                    model.Installed = true;
                    model.Mounted = false;
                    model.Status = model.Running ? "Running (configuring mount)" : "Installed (not running)";
                }
                else
                {
                    model.Installed = false;
                    model.Mounted = false;
                    model.Status = "Not installed";
                }
            }
            catch
            {
                model.Status = "Error checking Google Drive";
            }

            return model;
        }

        public static bool LaunchGoogleDrive()
        {
            try
            {
                var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var gDriveDir = Path.Combine(progFiles, @"Google\Drive File Stream");
                if (Directory.Exists(gDriveDir))
                {
                    var exes = Directory.GetFiles(gDriveDir, "GoogleDriveFS.exe", SearchOption.AllDirectories);
                    if (exes.Length > 0)
                    {
                        Process.Start(new ProcessStartInfo { FileName = exes[0], UseShellExecute = true });
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
