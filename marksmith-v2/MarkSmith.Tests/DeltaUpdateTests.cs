using System.Text.Json;
using MarkSmith.Services.DeltaUpdate;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>Pins the delta-update engine: manifest validation, the hash-diff (only changed files
/// get downloaded), and JSON round-tripping. Download/apply orchestration is covered by review.</summary>
public class DeltaUpdateTests
{
    private static DeltaManifest SampleManifest() => new()
    {
        Release = "2.17.0",
        Arch = "x64",
        Files =
        {
            new DeltaFileEntry("MarkSmith.exe", "A".PadLeft(64, '0'), 100),
            new DeltaFileEntry("MarkSmith.Core.dll", "B".PadLeft(64, '0'), 200),
            new DeltaFileEntry("Assets/logo.png", "C".PadLeft(64, '0'), 50),
        }
    };

    [Fact]
    public void Parse_ValidManifest_Ok()
    {
        var json = JsonSerializer.Serialize(SampleManifest(), DeltaJson.Options);
        var m = DeltaManifest.Parse(json);
        Assert.Equal("2.17.0", m.Release);
        Assert.Equal(3, m.Files.Count);
    }

    [Fact]
    public void Parse_RejectsUnknownFormat()
    {
        var m = SampleManifest();
        m.Format = "some-other-format";
        Assert.Throws<InvalidDataException>(() => DeltaManifest.Parse(JsonSerializer.Serialize(m, DeltaJson.Options)));
    }

    [Theory]
    [InlineData("..\\evil.dll")]
    [InlineData("../evil.dll")]
    [InlineData("/abs/path.dll")]
    [InlineData("C:/windows/system32/evil.dll")]
    [InlineData("")]
    [InlineData("a/../../evil.dll")]
    public void Parse_RejectsUnsafePaths(string path)
    {
        var m = SampleManifest();
        m.Files.Add(new DeltaFileEntry(path, "D".PadLeft(64, '0'), 1));
        Assert.Throws<InvalidDataException>(() => DeltaManifest.Parse(JsonSerializer.Serialize(m, DeltaJson.Options)));
    }

    [Fact]
    public void ComputeDelta_ChangedFile_DetectedByHash()
    {
        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MarkSmith.exe"] = "A".PadLeft(64, '0'),      // unchanged
            ["MarkSmith.Core.dll"] = "Z".PadLeft(64, '0'), // changed hash
            ["Assets/logo.png"] = "C".PadLeft(64, '0'),    // unchanged
        };
        var delta = DeltaPlan.ComputeDelta(SampleManifest(), local);
        Assert.Single(delta.ChangedOrAdded);
        Assert.Equal("MarkSmith.Core.dll", delta.ChangedOrAdded[0].Path);
        Assert.Empty(delta.Removed);
        Assert.Equal(2, delta.Unchanged);
    }

    [Fact]
    public void ComputeDelta_NewFile_Added()
    {
        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MarkSmith.exe"] = "A".PadLeft(64, '0'),
        };
        var delta = DeltaPlan.ComputeDelta(SampleManifest(), local);
        Assert.Equal(2, delta.ChangedOrAdded.Count); // the two files not present locally
        Assert.Contains(delta.ChangedOrAdded, f => f.Path == "MarkSmith.Core.dll");
        Assert.Contains(delta.ChangedOrAdded, f => f.Path == "Assets/logo.png");
    }

    [Fact]
    public void ComputeDelta_RemovedFile_Listed()
    {
        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MarkSmith.exe"] = "A".PadLeft(64, '0'),
            ["MarkSmith.Core.dll"] = "B".PadLeft(64, '0'),
            ["Assets/logo.png"] = "C".PadLeft(64, '0'),
            ["obsolete.dll"] = "F".PadLeft(64, '0'), // not in the new release
        };
        var delta = DeltaPlan.ComputeDelta(SampleManifest(), local);
        Assert.Empty(delta.ChangedOrAdded);
        Assert.Single(delta.Removed);
        Assert.Equal("obsolete.dll", delta.Removed[0]);
    }

    [Fact]
    public void ComputeDelta_Unchanged_Skipped()
    {
        var local = SampleManifest().Files.ToDictionary(f => DeltaPlan.Normalize(f.Path), f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        var delta = DeltaPlan.ComputeDelta(SampleManifest(), local);
        Assert.Empty(delta.ChangedOrAdded);
        Assert.Empty(delta.Removed);
        Assert.Equal(3, delta.Unchanged);
    }

    [Fact]
    public void ComputeDelta_EmptyManifest_AllLocalMarkedRemoved()
    {
        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MarkSmith.exe"] = "A".PadLeft(64, '0'),
        };
        var empty = new DeltaManifest { Release = "2.17.0", Arch = "x64" };
        var delta = DeltaPlan.ComputeDelta(empty, local);
        Assert.Empty(delta.ChangedOrAdded);
        Assert.Single(delta.Removed);
        Assert.Equal(0, delta.Unchanged);
    }

    [Fact]
    public void RoundTrip_Manifest_Json()
    {
        var original = SampleManifest();
        var json = JsonSerializer.Serialize(original, DeltaJson.Options);
        var parsed = DeltaManifest.Parse(json);
        Assert.Equal(original.Release, parsed.Release);
        Assert.Equal(original.Arch, parsed.Arch);
        Assert.Equal(original.Files.Count, parsed.Files.Count);
        Assert.Equal(original.Files[0].Sha256, parsed.Files[0].Sha256);
    }
}
