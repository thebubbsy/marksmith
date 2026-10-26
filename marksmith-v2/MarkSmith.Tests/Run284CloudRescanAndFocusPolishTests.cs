using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Tests;

// Run #284:
// - Settings ▸ Cloud storage "Rescan for cloud drives" no longer blanks the Provider picker and
//   saves no provider (the list was cleared under the picker's TwoWay SelectedValue binding);
// - the rescan line says when the chosen drive is no longer on this PC;
// - Shape Studio's inspector Layer buttons keep focus when a press disables them;
// - an Insert dialog's problem line is announced through LiveAnnounce (the raw peer was null);
// - the Provider picker's selected item is named after the drive, not the model type.
[Collection("MainViewModel settings")]
public class Run284CloudRescanAndFocusPolishTests
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

    private static string Body(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var open = src.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[start..(i + 1)];
        }
        return src[start..];
    }

    // What the WinUI ComboBox does when its ItemsSource is cleared: its SelectedValue binding
    // writes null back to the view model.
    private static void BindLikeThePicker(MainViewModel vm) =>
        vm.CloudProviders.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) vm.CloudProviderId = null!;
        };

    private static void WithProvider(Action body)
    {
        var settings = AppServices.Settings.Current;
        var saved = settings.CloudProviderId;
        try { body(); }
        finally { settings.CloudProviderId = saved; }
    }

    [Fact]
    public void Rescan_keeps_the_chosen_provider_and_never_saves_null()
    {
        WithProvider(() =>
        {
            var vm = new MainViewModel();
            vm.CloudProviderId = "dropbox";
            BindLikeThePicker(vm);
            var raised = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.CloudProviderId)) raised++; };

            vm.RefreshCloudProviders();

            Assert.Equal("dropbox", vm.CloudProviderId);
            Assert.Equal("dropbox", AppServices.Settings.Current.CloudProviderId);
            Assert.True(raised > 0); // the picker is told to reselect its item
            Assert.Contains(vm.CloudProviders, p => p.Id == "webdav");
        });
    }

    [Fact]
    public void Rescan_with_no_provider_chosen_stays_empty()
    {
        WithProvider(() =>
        {
            var vm = new MainViewModel();
            vm.CloudProviderId = "";
            BindLikeThePicker(vm);
            vm.RefreshCloudProviders();
            Assert.Equal("", vm.CloudProviderId);
            Assert.Equal("", AppServices.Settings.Current.CloudProviderId);
        });
    }

    [Fact]
    public void A_null_provider_from_the_picker_never_reaches_settings()
    {
        WithProvider(() =>
        {
            var vm = new MainViewModel();
            vm.CloudProviderId = "box";
            vm.CloudProviderId = null!;
            Assert.Equal("box", AppServices.Settings.Current.CloudProviderId);
        });
    }

    private static CloudProviderInfo P(string id, string name) => new() { Id = id, DisplayName = name, Detected = id != "webdav" };

    [Fact]
    public void Summary_counts_local_folders_without_webdav()
    {
        Assert.Equal("No local cloud sync folders found on this PC (WebDAV is available).",
            CloudRescanText.Summary(new[] { P("webdav", "WebDAV") }, ""));
        Assert.Equal("Found 1 local cloud sync folder on this PC.",
            CloudRescanText.Summary(new[] { P("onedrive", "OneDrive"), P("webdav", "WebDAV") }, "onedrive"));
        Assert.Equal("Found 2 local cloud sync folders on this PC.",
            CloudRescanText.Summary(new[] { P("onedrive", "OneDrive"), P("dropbox", "Dropbox"), P("webdav", "WebDAV") }, "webdav"));
    }

    [Fact]
    public void Summary_says_when_the_chosen_drive_is_gone()
    {
        var text = CloudRescanText.Summary(new[] { P("onedrive", "OneDrive"), P("webdav", "WebDAV") }, "dropbox");
        Assert.StartsWith("Found 1 local cloud sync folder on this PC. ", text);
        Assert.Contains("Your chosen drive (Dropbox) isn't on this PC any more", text);
        Assert.Contains("exports won't be copied", text);
        // Matched case-insensitively, and an unknown id is named as it is.
        Assert.DoesNotContain("chosen drive", CloudRescanText.Summary(new[] { P("onedrive", "OneDrive") }, "OneDrive"));
        Assert.Contains("(acme)", CloudRescanText.Summary(Array.Empty<CloudProviderInfo>(), "acme"));
    }

    [Fact]
    public void Provider_names_itself_by_its_display_name() =>
        Assert.Equal("Google Drive", P("googledrive", "Google Drive").ToString());

    [Fact]
    public void Settings_rescan_uses_the_shared_summary()
    {
        var body = Body(Read("MarkSmith.Desktop/Views/SettingsView.xaml.cs"), "private void OnRescanCloud(");
        Assert.Contains("CloudRescanText.Summary(App.ViewModel.CloudProviders, App.ViewModel.CloudProviderId)", body);
        Assert.DoesNotContain("count - 1", body);
    }

    [Fact]
    public void Shape_studio_layer_buttons_keep_focus()
    {
        var src = Read("MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs");
        var press = Body(src, "private void PressLayerButton(");
        // Focus state is read before the command runs, and only moved when the press disabled the button.
        Assert.True(press.IndexOf("var how = pressed?.FocusState", StringComparison.Ordinal)
            < press.IndexOf("command.Execute(null);", StringComparison.Ordinal));
        Assert.Contains("how == FocusState.Unfocused || pressed.IsEnabled) return;", press);
        Assert.Contains("FocusRescue.NextAvailable(layer, pressed) ?? CanvasScroller", press);
        foreach (var name in new[] { "BringToFront", "BringForward", "SendBackward", "SendToBack" })
            Assert.Contains($"PressLayerButton(sender as Button, ViewModel.{name}Command)", src);
    }

    [Fact]
    public void Insert_dialog_problem_goes_through_LiveAnnounce_and_resets_when_fixed()
    {
        var refresh = Body(Read("MarkSmith.Desktop/Views/InsertDialogControls.cs"), "protected void Refresh()");
        Assert.Contains("LiveAnnounce.RaiseIfChanged(_problem, problem);", refresh);
        Assert.Contains("LiveAnnounce.RaiseIfChanged(_problem, null);", refresh);
        Assert.DoesNotContain("FrameworkElementAutomationPeer.FromElement(_problem)", refresh);
    }
}
