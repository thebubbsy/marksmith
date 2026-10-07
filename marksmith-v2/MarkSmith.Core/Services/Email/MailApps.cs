using System.Runtime.InteropServices;
using System.Text;

namespace MarkSmith.Services.Email;

/// <summary>Which mail app Windows opens a file type with.</summary>
public enum MailAppKind
{
    /// <summary>Nothing is set to open the file type.</summary>
    None,
    /// <summary>Classic Outlook (OUTLOOK.EXE, Microsoft 365 / Office).</summary>
    ClassicOutlook,
    /// <summary>The new Outlook for Windows (olk.exe, a Store app).</summary>
    NewOutlook,
    /// <summary>Windows asks which app to use ("How do you want to open this file?"): more than
    /// one app registered for the type (both Outlooks, say) and none was picked as the default.</summary>
    AskEachTime,
    /// <summary>Some other app: Thunderbird, Windows Mail, a viewer.</summary>
    Other,
}

public sealed record MailHandler(MailAppKind Kind, string Name);

/// <summary>Draft file formats, and how "Automatic" picks one: the format your default apps open
/// in Outlook. .eml is the default (every mail app opens it, and Outlook opens an X-Unsent .eml as a
/// draft); .msg is chosen only when .eml files would go to something that isn't Outlook while .msg
/// files go to Outlook, so "Email draft" still lands in Outlook.</summary>
public static class MailApps
{
    public const string Auto = "auto", Eml = "eml", Msg = "msg";

    /// <summary>Looks up the handler for an extension (".eml"). Replaceable in tests.</summary>
    public static Func<string, MailHandler> Lookup { get; set; } = QueryHandler;

    /// <summary>"eml" or "msg" for the EmailFormat setting ("auto", "eml", "msg"; anything else
    /// counts as automatic).</summary>
    public static string Resolve(string? setting)
    {
        var s = setting?.Trim().ToLowerInvariant();
        if (s is Eml or Msg) return s;
        var eml = Lookup(".eml");
        if (IsOutlook(eml.Kind)) return Eml;
        var msg = Lookup(".msg");
        return IsOutlook(msg.Kind) ? Msg : Eml;
    }

    public static bool IsOutlook(MailAppKind kind) => kind is MailAppKind.ClassicOutlook or MailAppKind.NewOutlook;

    /// <summary>The app a draft of <paramref name="format"/> opens in, for the status line:
    /// "Outlook (classic)", "Thunderbird", or null when nothing is set.</summary>
    public static string? AppFor(string format)
    {
        var h = Lookup("." + format);
        return h.Kind == MailAppKind.None ? null : h.Name;
    }

    /// <summary>One line under the format choice saying what Automatic picks on this PC and why.</summary>
    public static string DescribeAutomatic()
    {
        var eml = Lookup(".eml");
        var msg = Lookup(".msg");
        if (IsOutlook(eml.Kind)) return $"Automatic uses .eml here: your .eml files open in {eml.Name}.";
        if (IsOutlook(msg.Kind))
            return eml.Kind switch
            {
                MailAppKind.None => $"Automatic uses .msg here: nothing opens .eml files, and .msg files open in {msg.Name}.",
                MailAppKind.AskEachTime => $"Automatic uses .msg here: Windows asks which app opens .eml files, but .msg files open in {msg.Name}.",
                _ => $"Automatic uses .msg here: .eml files open in {eml.Name}, but .msg files open in {msg.Name}.",
            };
        if (eml.Kind == MailAppKind.AskEachTime)
            return "Automatic uses .eml. Windows hasn't been told which app opens .eml files, so it will ask: pick Outlook and tick \"Always\".";
        if (eml.Kind != MailAppKind.None) return $"Automatic uses .eml here: your .eml files open in {eml.Name}.";
        return "No app on this PC opens .eml or .msg files yet. Drafts are saved as .eml; set Outlook as the app for them under Settings > Apps > Default apps.";
    }

    // ---- Windows ----

    private static MailHandler QueryHandler(string extension)
    {
        if (!OperatingSystem.IsWindows()) return new MailHandler(MailAppKind.None, "");
        var exe = Query(AssocStr.Executable, extension) ?? "";
        var friendly = Query(AssocStr.FriendlyAppName, extension) ?? "";
        return Classify(exe, friendly);
    }

    /// <summary>Names the app behind an association from its executable path and friendly name.</summary>
    public static MailHandler Classify(string executable, string friendlyName)
    {
        var exe = Path.GetFileName(executable ?? "").ToLowerInvariant();
        var name = (friendlyName ?? "").Trim();
        if (exe.Length == 0 && name.Length == 0) return new MailHandler(MailAppKind.None, "");
        if (exe == "openwith.exe") return new MailHandler(MailAppKind.AskEachTime, "the Windows app picker");
        if (exe == "outlook.exe") return new MailHandler(MailAppKind.ClassicOutlook, "Outlook (classic)");
        if (exe == "olk.exe" || (executable ?? "").Contains("OutlookForWindows", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Outlook", StringComparison.OrdinalIgnoreCase))
            return new MailHandler(MailAppKind.NewOutlook, "the new Outlook");
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(executable ?? "");
        return new MailHandler(MailAppKind.Other, name);
    }

    private enum AssocStr { Executable = 2, FriendlyAppName = 4 }

    private const int AssocFNoTruncate = 0x20, S_OK = 0;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, AssocStr str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);

    private static string? Query(AssocStr what, string extension)
    {
        try
        {
            uint size = 0;
            AssocQueryString(AssocFNoTruncate, what, extension, "open", null, ref size);
            if (size == 0) return null;
            var sb = new StringBuilder((int)size);
            return AssocQueryString(AssocFNoTruncate, what, extension, "open", sb, ref size) == S_OK ? sb.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
