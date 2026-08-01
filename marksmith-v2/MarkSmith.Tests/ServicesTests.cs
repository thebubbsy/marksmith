using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class AtomicFileTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "MarkSmith-atomic-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact] public void Writes_new_file()
    {
        var p = TempPath();
        try { AtomicFile.WriteAllText(p, "hello"); Assert.Equal("hello", File.ReadAllText(p)); }
        finally { File.Delete(p); }
    }
    [Fact] public void Overwrites_existing_file()
    {
        var p = TempPath();
        try { File.WriteAllText(p, "old"); AtomicFile.WriteAllText(p, "new"); Assert.Equal("new", File.ReadAllText(p)); }
        finally { File.Delete(p); }
    }
    [Fact] public void Creates_missing_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MarkSmith-atomic-dir-" + Guid.NewGuid().ToString("N"));
        var p = Path.Combine(dir, "a.json");
        try { AtomicFile.WriteAllText(p, "x"); Assert.True(File.Exists(p)); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact] public void Leaves_no_tmp_file_behind()
    {
        var p = TempPath();
        try { AtomicFile.WriteAllText(p, "x"); Assert.False(File.Exists(p + ".tmp")); }
        finally { File.Delete(p); }
    }
    [Fact] public void Concurrent_writes_do_not_corrupt()
    {
        var p = TempPath();
        try
        {
            Parallel.For(0, 50, i => AtomicFile.WriteAllText(p, "value-" + (i % 5)));
            var final = File.ReadAllText(p);
            Assert.StartsWith("value-", final); // a whole value, never a torn fragment
        }
        finally { File.Delete(p); }
    }
    [Fact] public void Roundtrips_unicode()
    {
        var p = TempPath();
        try { AtomicFile.WriteAllText(p, "café → 日本語"); Assert.Equal("café → 日本語", File.ReadAllText(p)); }
        finally { File.Delete(p); }
    }
}

public class UpdateVersionCompareTests
{
    private static int C(string a, string b) => UpdateService.Compare(a, b);

    [Fact] public void Equal_versions() => Assert.Equal(0, C("1.2.3", "1.2.3"));
    [Fact] public void Major_greater() => Assert.True(C("2.0.0", "1.9.9") > 0);
    [Fact] public void Minor_greater() => Assert.True(C("1.3.0", "1.2.9") > 0);
    [Fact] public void Patch_greater() => Assert.True(C("1.2.3", "1.2.2") > 0);
    [Fact] public void Ten_greater_than_nine_not_string_compare() => Assert.True(C("1.10.0", "1.9.0") > 0);
    [Fact] public void Fourth_component_bump_detected() => Assert.True(C("1.0.0.9", "1.0.0.5") > 0);
    [Fact] public void Prerelease_lower_than_stable() => Assert.True(C("1.2.0-beta", "1.2.0") < 0);
    [Fact] public void Stable_higher_than_prerelease() => Assert.True(C("1.2.0", "1.2.0-rc1") > 0);
    [Fact] public void Two_prereleases_same_core_equal() => Assert.Equal(0, C("1.2.0-a", "1.2.0-b"));
    [Fact] public void V_prefix_ignored() => Assert.Equal(0, C("v1.2.3", "1.2.3"));
    [Fact] public void Older_is_negative() => Assert.True(C("1.0.0", "1.0.1") < 0);
    [Fact] public void Missing_components_treated_as_zero() => Assert.True(C("1.2", "1.2.0") == 0);
    [Fact] public void Whitespace_tolerated() => Assert.Equal(0, C(" 1.2.3 ", "1.2.3"));
}

public class ApiServerBoundsTests
{
    [Fact] public void MaxBodyBytes_is_reasonable() => Assert.Equal(25 * 1024 * 1024, ApiServer.MaxBodyBytes);
}

public class LlmNormalizeTests
{
    private static readonly LlmSourceService Svc = new();
    private static string Repair(string md) { var (r, _) = Svc.RepairArtifacts(md, new MdToPdf.Models.LlmClassification()); return r; }

    // The "Copy" over-match fix: a lone "Copy" in prose survives; a "Copy" button above a fence and
    // "Copy code" are still removed.
    [Fact] public void Lone_Copy_in_prose_survives() => Assert.Contains("Copy", Repair("Steps:\n\nCopy\n\nPaste the text."));
    [Fact] public void Lone_Copy_as_list_item_survives() => Assert.Contains("Copy", Repair("Menu:\n\nCopy"));
    [Fact] public void Copy_code_always_removed() => Assert.DoesNotContain("Copy code", Repair("Copy code\n\nsome text"));
    [Fact] public void ContentReference_pip_removed() => Assert.DoesNotContain("contentReference", Repair("Answer :contentReference[oaicite:0]{index=0} here."));
    [Fact] public void Citation_pip_removed() => Assert.DoesNotContain("†source", Repair("Fact 【12†source】 stated."));
    [Fact] public void Real_text_preserved() => Assert.Contains("Real text", Repair("<thinking>x</thinking>Real text."));
    [Fact] public void Code_fence_artifacts_untouched()
    {
        var r = Repair("```\n:contentReference[oaicite:0]{index=0}\n```");
        Assert.Contains(":contentReference", r);
    }
    [Fact] public void Empty_input_does_not_throw() => Repair("");
    [Fact] public void Plain_prose_unchanged() => Assert.Contains("nothing to fix here", Repair("nothing to fix here"));
}

