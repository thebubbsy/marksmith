using System;
using System.IO;
using Xunit;

namespace MarkSmith.Tests;

public class Run279CopyHtmlPillTests
{
    private static string DesktopDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "MarkSmith.Desktop");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(DesktopDir(), relative)).Replace("\r\n", "\n");

    [Fact]
    public void MainWindowXaml_ContainsStatusCopyHtmlButton()
    {
        var xaml = Read("MainWindow.xaml");
        Assert.Contains("x:Name=\"StatusCopyHtmlButton\"", xaml);
        Assert.Contains("Click=\"OnStatusCopyHtmlButtonClick\"", xaml);
        Assert.Contains("RightTapped=\"OnStatusCopyHtmlButtonRightTapped\"", xaml);
        Assert.Contains("x:Name=\"StatusCopyHtmlText\" Text=\"Copy HTML\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Copy rendered HTML\"", xaml);
    }

    [Fact]
    public void MainWindowCs_WiredStatusCopyHtmlButtonAndHandlers()
    {
        var cs = Read("MainWindow.xaml.cs");
        Assert.Contains("(StatusCopyHtmlButton, OnStatusCopyHtmlButtonRightTapped),", cs);
        Assert.Contains("Add(\"Copy rendered HTML\", \"\\uE774\", OnStatusCopyHtmlButtonClick);", cs);
        Assert.Contains("private void OnStatusCopyHtmlButtonClick(object sender, RoutedEventArgs e)", cs);
        Assert.Contains("private void OnStatusCopyHtmlButtonRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)", cs);
        Assert.Contains("StandaloneHtml.Inline(raw)", cs);
    }
}
