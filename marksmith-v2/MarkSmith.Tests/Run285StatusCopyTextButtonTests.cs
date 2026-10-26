using System;
using System.IO;
using Xunit;

namespace MarkSmith.Tests;

public class Run285StatusCopyTextButtonTests
{
    private static string RepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoDir(), relative)).Replace("\r\n", "\n");

    [Fact]
    public void MainWindowXaml_DeclaresStatusCopyTextButton_WithProperBindings()
    {
        var xaml = Read("MarkSmith.Desktop/MainWindow.xaml");

        Assert.Contains("x:Name=\"StatusCopyTextButton\"", xaml);
        Assert.Contains("Click=\"OnStatusCopyTextButtonClick\"", xaml);
        Assert.Contains("RightTapped=\"OnStatusCopyTextButtonRightTapped\"", xaml);
        Assert.Contains("x:Name=\"StatusCopyTextText\" Text=\"Copy Text\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Copy plain text\"", xaml);
    }

    [Fact]
    public void MainWindowCodeBehind_ImplementsCopyTextHandlers_AndMenuActions()
    {
        var cs = Read("MarkSmith.Desktop/MainWindow.xaml.cs");

        Assert.Contains("(StatusCopyTextButton, OnStatusCopyTextButtonRightTapped)", cs);
        Assert.Contains("Add(\"Copy plain text\", \"\\uE8D2\", OnStatusCopyTextButtonClick);", cs);
        Assert.Contains("private void OnStatusCopyTextButtonClick(object sender, RoutedEventArgs e)", cs);
        Assert.Contains("private void OnStatusCopyTextButtonRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)", cs);
        Assert.Contains("ExtractPlainText(md)", cs);
        Assert.Contains("CountWords(plain)", cs);
        Assert.Contains("Copied plain text document", cs);
        Assert.Contains("Insert Text Metrics Summary at cursor", cs);
    }
}
