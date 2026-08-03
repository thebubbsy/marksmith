namespace MarkSmith.Services;

// Windows and macOS (default APFS) treat file paths as case-insensitive; Linux (ext4 etc.) is
// case-sensitive. Path *identity* comparisons (recent-files dedup, discovered-file dedup) should
// follow the OS's actual filesystem semantics — unlike file *extension* matching (".md" vs ".MD")
// or user-facing name matching (preset names), which are legitimately case-insensitive everywhere
// regardless of platform and should keep using StringComparison.OrdinalIgnoreCase directly.
public static class PathEquality
{
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
