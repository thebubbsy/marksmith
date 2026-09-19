using System.IO;
using Xunit;

namespace MarkSmith.Core.Tests;

public class BatchDirectoryResolutionTests
{
    public static string ResolveTargetDir(string searchDir, string file, string? outputDir, bool recursive)
    {
        string targetDir = outputDir ?? (Path.GetDirectoryName(file) ?? searchDir);
        if (recursive)
        {
            string baseDir = outputDir ?? searchDir;
            try
            {
                string relPath = Path.GetRelativePath(searchDir, file);
                string relDir = Path.GetDirectoryName(relPath) ?? "";
                if (!string.IsNullOrEmpty(relDir))
                {
                    targetDir = Path.Combine(baseDir, relDir);
                }
            }
            catch
            {
                targetDir = baseDir;
            }
        }
        return targetDir;
    }

    [Fact]
    public void Recursive_NullOutputDir_ColocatesInSubfolderWithoutThrowing()
    {
        string searchDir = Path.Combine(Path.GetTempPath(), "batch_test_in");
        string file = Path.Combine(searchDir, "subfolder", "nested", "doc.md");

        string targetDir = ResolveTargetDir(searchDir, file, null, recursive: true);

        Assert.Equal(Path.Combine(searchDir, "subfolder", "nested"), targetDir);
    }

    [Fact]
    public void Recursive_WithOutputDir_MirrorsSubfolderUnderOutputDir()
    {
        string searchDir = Path.Combine(Path.GetTempPath(), "batch_test_in");
        string outputDir = Path.Combine(Path.GetTempPath(), "batch_test_out");
        string file = Path.Combine(searchDir, "subfolder", "nested", "doc.md");

        string targetDir = ResolveTargetDir(searchDir, file, outputDir, recursive: true);

        Assert.Equal(Path.Combine(outputDir, "subfolder", "nested"), targetDir);
    }

    [Fact]
    public void NonRecursive_NullOutputDir_UsesFileParentDirectory()
    {
        string searchDir = Path.Combine(Path.GetTempPath(), "batch_test_in");
        string file = Path.Combine(searchDir, "subfolder", "doc.md");

        string targetDir = ResolveTargetDir(searchDir, file, null, recursive: false);

        Assert.Equal(Path.Combine(searchDir, "subfolder"), targetDir);
    }
}
