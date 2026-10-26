using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

public class Run273FrontmatterCopyPolishTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string DesktopPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName, "MarkSmith.Desktop" }.Concat(parts).ToArray());
    }

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    [Fact]
    public void StatusBar_CopyFrontmatterButton_ExistsWithCorrectProperties()
    {
        var doc = XDocument.Load(DesktopPath("MainWindow.xaml"));
        var btn = Named(doc, "StatusCopyFrontmatterButton");
        Assert.NotNull(btn);
        Assert.Equal("OnStatusCopyFrontmatterButtonClick", (string?)btn.Attribute("Click"));
        Assert.Equal("OnStatusCopyFrontmatterButtonRightTapped", (string?)btn.Attribute("RightTapped"));
        Assert.Equal("Copy YAML frontmatter", (string?)btn.Attribute("AutomationProperties.Name"));

        var group = Named(doc, "StatusSecondaryCopyPills");
        var names = group.Descendants().Select(e => (string?)e.Attribute(X + "Name")).Where(n => n is not null).ToList();
        Assert.Contains("StatusCopyFrontmatterButton", names);
    }

    [Fact]
    public void StatusBar_CopyFrontmatterButton_IsWiredInCodeBehind()
    {
        var code = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("(StatusCopyFrontmatterButton, OnStatusCopyFrontmatterButtonRightTapped)", code);
        Assert.Contains("Add(\"Copy YAML frontmatter\",", code);
        Assert.Contains("private void OnStatusCopyFrontmatterButtonClick(", code);
        Assert.Contains("private void OnStatusCopyFrontmatterButtonRightTapped(", code);
    }
}
