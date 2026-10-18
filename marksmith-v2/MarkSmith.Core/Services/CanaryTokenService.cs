using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MarkSmith.Services;

/// <summary>
/// Multi-vector execution canary token service that triggers an email alert to
/// <c>mbubbtech@gmail.com</c> whenever MarkSmith (Desktop, CLI, Express, MCP, CloudApi,
/// or Core library) is used by anyone other than the owner on the whitelisted laptop.
/// </summary>
public static class CanaryTokenService
{
    /// <summary>
    /// Target notification email registered with the Thinkst Canarytoken and alert relays.
    /// </summary>
    public const string AlertRecipientEmail = "mbubbtech@gmail.com";

    /// <summary>
    /// Live Thinkst Canarytoken ID provisioned for <see cref="AlertRecipientEmail"/>.
    /// </summary>
    public const string CanaryTokenId = "ffpmaf6z0nn6ide3d9tv4yntp";

    /// <summary>
    /// HTTP web-bug canary trigger URL (automatically dispatches email alert to <see cref="AlertRecipientEmail"/>).
    /// </summary>
    public const string CanaryHttpEndpoint = "http://canarytokens.com/tags/terms/about/ffpmaf6z0nn6ide3d9tv4yntp/post.jsp";

    /// <summary>
    /// DNS canary token domain (triggers email alert to <see cref="AlertRecipientEmail"/> via DNS resolution even if outbound HTTP is blocked).
    /// </summary>
    public const string CanaryDnsDomain = "ffpmaf6z0nn6ide3d9tv4yntp.canarytokens.com";

    /// <summary>
    /// SHA-256 digest of the owner's primary laptop identity:
    /// <c>UPPER(MachineName)|LOWER(UserName)|LOWER(MachineGuid)</c>.
    /// Stored as a cryptographic hash so decompilation does not reveal the whitelisted identifiers.
    /// </summary>
    public const string OwnerMachineFullSha256 = "176c819d302bbbc48c55f97f7c912971017527690faf075eb4e9dd66d7330724";

    /// <summary>
    /// SHA-256 fallback digest of <c>UPPER(MachineName)|LOWER(UserName)</c> used only when
    /// the Windows Cryptography MachineGuid registry key is inaccessible.
    /// </summary>
    public const string OwnerMachineHostUserSha256 = "a2554eb3d143fbef7131e6ff173a625988428d0ce700c1f92dfac89b8e5f513d";

    /// <summary>
    /// Cooldown window per surface so rapid operations (e.g. live keystroke preview) fire immediately
    /// on first use without flooding duplicate alerts every millisecond.
    /// </summary>
    private static readonly TimeSpan SurfaceCooldown = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastTriggeredBySurface = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<CanaryTriggerReport> TriggerHistory = new();

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    /// <summary>
    /// Optional test seam: when set, intercepts network dispatch so automated unit tests can
    /// deterministically assert canary triggers without sending live emails during test runs.
    /// </summary>
    internal static Func<CanaryTriggerReport, CancellationToken, Task<bool>>? TransportOverride { get; set; }

    /// <summary>
    /// Recent trigger reports recorded during the current process lifetime.
    /// </summary>
    public static IReadOnlyCollection<CanaryTriggerReport> RecentTriggers => TriggerHistory.ToArray();

    /// <summary>
    /// Evaluates whether the current execution environment matches the owner's whitelisted laptop.
    /// Returns <c>true</c> ONLY for Tony on the whitelisted laptop; returns <c>false</c> for everyone else.
    /// </summary>
    public static bool IsOwnerMachine(string? machineName = null, string? userName = null, string? machineGuid = null)
    {
        try
        {
            string? forceEnv = Environment.GetEnvironmentVariable("MARKSMITH_FORCE_CANARY");
            if (string.Equals(forceEnv, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(forceEnv, "true", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string host = (machineName ?? Environment.MachineName ?? string.Empty).Trim().ToUpperInvariant();
            string user = (userName ?? Environment.UserName ?? string.Empty).Trim().ToLowerInvariant();
            string guid = (machineGuid ?? ReadMachineGuid() ?? string.Empty).Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(guid))
            {
                string fullDigest = ComputeSha256Hex($"{host}|{user}|{guid}");
                return string.Equals(fullDigest, OwnerMachineFullSha256, StringComparison.OrdinalIgnoreCase);
            }

            string hostUserDigest = ComputeSha256Hex($"{host}|{user}");
            return string.Equals(hostUserDigest, OwnerMachineHostUserSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Non-blocking entry hook called across all MarkSmith surfaces (Desktop, CLI, Express, MCP, CloudApi, Core).
    /// Silently exits on the owner's laptop; fires the canary token in the background for any other user or machine.
    /// </summary>
    public static void NotifyUsage(string surface, string? detail = null)
    {
        try
        {
            if (IsOwnerMachine())
            {
                return;
            }

            string key = string.IsNullOrWhiteSpace(surface) ? "UnknownSurface" : surface.Trim();
            var now = DateTimeOffset.UtcNow;

            if (LastTriggeredBySurface.TryGetValue(key, out var lastTime) && (now - lastTime) < SurfaceCooldown)
            {
                return;
            }

            LastTriggeredBySurface[key] = now;
            _ = Task.Run(() => TriggerAsync(key, detail));
        }
        catch
        {
            // Canary must never crash or interrupt the host application.
        }
    }

    /// <summary>
    /// Builds and dispatches a canary alert report to <see cref="AlertRecipientEmail"/> via HTTP web-bug,
    /// DNS token resolution, and any configured webhook.
    /// </summary>
    public static async Task<CanaryTriggerReport> TriggerAsync(
        string surface,
        string? detail = null,
        CanaryIdentity? identityOverride = null,
        bool bypassOwnerCheck = false,
        CancellationToken ct = default)
    {
        var identity = identityOverride ?? CaptureCurrentIdentity();
        bool isOwner = !bypassOwnerCheck && IsOwnerMachine(identity.MachineName, identity.UserName, identity.MachineGuid);

        var report = new CanaryTriggerReport(
            TokenId: CanaryTokenId,
            RecipientEmail: AlertRecipientEmail,
            Surface: string.IsNullOrWhiteSpace(surface) ? "Unknown" : surface.Trim(),
            Detail: detail ?? string.Empty,
            MachineName: identity.MachineName,
            UserName: identity.UserName,
            UserDomainName: identity.UserDomainName,
            MachineGuidPrefix: string.IsNullOrEmpty(identity.MachineGuid)
                ? "unavailable"
                : identity.MachineGuid[..Math.Min(8, identity.MachineGuid.Length)],
            IdentitySha256: ComputeSha256Hex(
                $"{identity.MachineName.ToUpperInvariant()}|{identity.UserName.ToLowerInvariant()}|{identity.MachineGuid.ToLowerInvariant()}"),
            OsDescription: identity.OsDescription,
            ProcessName: identity.ProcessName,
            ProcessId: identity.ProcessId,
            CommandLine: identity.CommandLine,
            WorkingDirectory: identity.WorkingDirectory,
            TriggeredAtUtc: DateTimeOffset.UtcNow,
            WasSuppressedAsOwner: isOwner,
            DispatchedSuccessfully: false);

        if (isOwner)
        {
            return report;
        }

        bool dispatched = false;
        try
        {
            if (TransportOverride is not null)
            {
                dispatched = await TransportOverride(report, ct).ConfigureAwait(false);
            }
            else
            {
                bool httpOk = await DispatchHttpCanaryAsync(report, ct).ConfigureAwait(false);
                bool dnsOk = await DispatchDnsCanaryAsync(report).ConfigureAwait(false);
                bool webhookOk = await DispatchOptionalWebhookAsync(report, ct).ConfigureAwait(false);
                dispatched = httpOk || dnsOk || webhookOk;
            }
        }
        catch
        {
            dispatched = false;
        }

        var finalReport = report with { DispatchedSuccessfully = dispatched };
        TriggerHistory.Enqueue(finalReport);
        while (TriggerHistory.Count > 50 && TriggerHistory.TryDequeue(out _)) { }

        return finalReport;
    }

    private static async Task<bool> DispatchHttpCanaryAsync(CanaryTriggerReport report, CancellationToken ct)
    {
        try
        {
            string query = $"?app=MarkSmith" +
                           $"&alert={Uri.EscapeDataString(report.RecipientEmail)}" +
                           $"&surface={Uri.EscapeDataString(report.Surface)}" +
                           $"&host={Uri.EscapeDataString(report.MachineName)}" +
                           $"&user={Uri.EscapeDataString($"{report.UserDomainName}\\{report.UserName}")}" +
                           $"&guid={Uri.EscapeDataString(report.MachineGuidPrefix)}" +
                           $"&os={Uri.EscapeDataString(report.OsDescription)}" +
                           $"&proc={Uri.EscapeDataString(report.ProcessName)}" +
                           $"&detail={Uri.EscapeDataString(Truncate(report.Detail, 120))}" +
                           $"&ts={Uri.EscapeDataString(report.TriggeredAtUtc.ToString("O"))}";

            string url = CanaryHttpEndpoint + query;
            using var req = new HttpRequestMessage(HttpMethod.Post, url);

            string userAgent = $"MarkSmith-Canary/3.1 (Notify={report.RecipientEmail}; " +
                               $"Surface={SanitizeHeader(report.Surface)}; " +
                               $"Host={SanitizeHeader(report.MachineName)}; " +
                               $"User={SanitizeHeader(report.UserDomainName)}/{SanitizeHeader(report.UserName)}; " +
                               $"GUID={SanitizeHeader(report.MachineGuidPrefix)}; " +
                               $"OS={SanitizeHeader(report.OsDescription)}; " +
                               $"Proc={SanitizeHeader(report.ProcessName)}:{report.ProcessId}; " +
                               $"Detail={SanitizeHeader(Truncate(report.Detail, 100))})";

            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            req.Headers.TryAddWithoutValidation(
                "Referer",
                $"https://marksmith.canary.alert/{Uri.EscapeDataString(report.MachineName)}/{Uri.EscapeDataString(report.UserName)}/{Uri.EscapeDataString(report.Surface)}");

            var payloadJson = JsonSerializer.Serialize(report);
            req.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            using var resp = await SharedHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (int)resp.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> DispatchDnsCanaryAsync(CanaryTriggerReport report)
    {
        try
        {
            // DNS label safe characters (alphanumeric + hyphen, max 60 chars per label)
            string label1 = SanitizeDnsLabel($"{report.MachineName}-{report.UserName}", 50);
            string label2 = SanitizeDnsLabel(report.Surface, 30);
            string nonce = $"g{Random.Shared.Next(1000, 9999)}";
            string fqdn = $"{label1}.{label2}.{nonce}.{CanaryDnsDomain}";

            _ = await Dns.GetHostAddressesAsync(fqdn).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // Even if NXDOMAIN is returned by the canary authoritative nameserver, the DNS query itself tripped the token.
            return true;
        }
    }

    private static async Task<bool> DispatchOptionalWebhookAsync(CanaryTriggerReport report, CancellationToken ct)
    {
        try
        {
            string? webhook = Environment.GetEnvironmentVariable("MARKSMITH_CANARY_WEBHOOK_URL");
            if (string.IsNullOrWhiteSpace(webhook)) return false;

            var json = JsonSerializer.Serialize(report);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await SharedHttpClient.PostAsync(webhook.Trim(), content, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static CanaryIdentity CaptureCurrentIdentity()
    {
        string machineName = SafeGet(() => Environment.MachineName, "unknown-host");
        string userName = SafeGet(() => Environment.UserName, "unknown-user");
        string domainName = SafeGet(() => Environment.UserDomainName, "unknown-domain");
        string machineGuid = ReadMachineGuid() ?? string.Empty;
        string osDesc = SafeGet(() => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})", "unknown-os");
        string procName = SafeGet(() => Process.GetCurrentProcess().ProcessName, "marksmith");
        int procId = SafeGet(() => Environment.ProcessId, 0);
        string cmdLine = SafeGet(() => Truncate(Environment.CommandLine, 240), string.Empty);
        string workDir = SafeGet(() => Environment.CurrentDirectory, string.Empty);

        return new CanaryIdentity(
            MachineName: machineName,
            UserName: userName,
            UserDomainName: domainName,
            MachineGuid: machineGuid,
            OsDescription: osDesc,
            ProcessName: procName,
            ProcessId: procId,
            CommandLine: cmdLine,
            WorkingDirectory: workDir);
    }

    public static string? ReadMachineGuid()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = localMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                if (key?.GetValue("MachineGuid") is string guid && !string.IsNullOrWhiteSpace(guid))
                {
                    return guid.Trim();
                }
            }
            else if (OperatingSystem.IsLinux() && File.Exists("/etc/machine-id"))
            {
                return File.ReadAllText("/etc/machine-id").Trim();
            }
        }
        catch
        {
            // Ignore registry/file access errors in restricted sandboxes
        }

        return null;
    }

    public static string ComputeSha256Hex(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Clears cooldowns and recorded history (for testing).
    /// </summary>
    internal static void ResetStateForTesting()
    {
        LastTriggeredBySurface.Clear();
        while (TriggerHistory.TryDequeue(out _)) { }
    }

    private static string SanitizeHeader(string value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c >= 32 && c <= 126 && c != ';' && c != ')' && c != '(')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.ToString();
    }

    private static string SanitizeDnsLabel(string value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unk";
        var sb = new StringBuilder(Math.Min(value.Length, maxLen));
        foreach (char c in value)
        {
            if (sb.Length >= maxLen) break;
            if (char.IsAsciiLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
            else if (c == '-' || c == '_' || c == '.')
                sb.Append('-');
        }
        string res = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(res) ? "unk" : res;
    }

    private static string Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLen ? value : value[..maxLen];
    }

    private static T SafeGet<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }
}

public sealed record CanaryIdentity(
    string MachineName,
    string UserName,
    string UserDomainName,
    string MachineGuid,
    string OsDescription,
    string ProcessName,
    int ProcessId,
    string CommandLine,
    string WorkingDirectory);

public sealed record CanaryTriggerReport(
    string TokenId,
    string RecipientEmail,
    string Surface,
    string Detail,
    string MachineName,
    string UserName,
    string UserDomainName,
    string MachineGuidPrefix,
    string IdentitySha256,
    string OsDescription,
    string ProcessName,
    int ProcessId,
    string CommandLine,
    string WorkingDirectory,
    DateTimeOffset TriggeredAtUtc,
    bool WasSuppressedAsOwner,
    bool DispatchedSuccessfully);
