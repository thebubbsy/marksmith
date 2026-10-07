using System.Collections.Generic;
using System.Linq;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>
/// Run #20's Settings / side-panel audit: options whose labels or effects didn't match what the
/// app actually did.
/// </summary>
public class SidePanelPolishTests
{
    // The cleanup-rules editor saved its rules, but no caller ever passed them to NormalizeStyle,
    // so a rule had no effect anywhere. The preview path now applies them.
    [Fact]
    public void Custom_cleanup_rules_apply_to_the_preview_markdown()
    {
        var settings = AppServices.Settings.Current;
        var savedRules = settings.CustomNormalizationRules;
        var savedNormalize = settings.NormalizeLlm;
        try
        {
            settings.CustomNormalizationRules = new List<TextCleanupRule>
            {
                new() { Find = "delve into", Replace = "explore" },
            };
            var vm = new MainViewModel { NormalizeLlm = true };
            var prepared = vm.PrepareMarkdown("# Notes\n\nLet's delve into the data.");
            Assert.Contains("explore the data", prepared);
            Assert.DoesNotContain("delve", prepared);

            // The rules ride on the AI cleanup toggle, as the panel says.
            vm.NormalizeLlm = false;
            Assert.Contains("delve into", vm.PrepareMarkdown("# Notes\n\nLet's delve into the data."));
        }
        finally
        {
            settings.CustomNormalizationRules = savedRules;
            settings.NormalizeLlm = savedNormalize;
        }
    }

    [Fact]
    public void Editing_the_rule_list_raises_HasNormalizationRules_for_the_preview_refresh()
    {
        var settings = AppServices.Settings.Current;
        var savedRules = settings.CustomNormalizationRules;
        try
        {
            var vm = new MainViewModel();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.AddNormalizationRuleCommand.Execute(null);
            Assert.Contains(nameof(MainViewModel.HasNormalizationRules), raised);
            Assert.True(vm.HasNormalizationRules);

            raised.Clear();
            vm.NormalizationRules.Last().Find = "x";
            Assert.Contains(nameof(MainViewModel.HasNormalizationRules), raised);

            foreach (var r in vm.NormalizationRules.ToList()) vm.RemoveNormalizationRuleCommand.Execute(r);
            Assert.False(vm.HasNormalizationRules);
        }
        finally
        {
            settings.CustomNormalizationRules = savedRules;
        }
    }

    // The automation options all said "PDF" while every automatic export follows TargetFormat.
    [Theory]
    [InlineData("pdf", "PDF")]
    [InlineData("docx", "Word document")]
    [InlineData("pptx", "PowerPoint deck")]
    [InlineData("epub", "EPUB e-book")]
    public void Automation_note_names_the_real_default_format(string format, string label)
    {
        var saved = AppServices.Settings.Current.TargetFormat;
        try
        {
            var vm = new MainViewModel();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.TargetFormat = format == "pdf" ? "docx" : "pdf"; // force a change
            vm.TargetFormat = format;
            Assert.Equal(label, vm.TargetFormatLabel);
            Assert.Contains($"({label})", vm.AutomationFormatNote);
            Assert.Contains(nameof(MainViewModel.AutomationFormatNote), raised);
        }
        finally
        {
            AppServices.Settings.Current.TargetFormat = saved;
        }
    }

    [Fact]
    public void Api_status_defaults_to_off_not_blank()
    {
        var vm = new MainViewModel();
        Assert.Equal("Off", vm.ApiStatusText);
        Assert.False(vm.ApiStatusIsError);
    }

    // A port another program holds used to read "The process cannot access the file because it
    // is being used by another process"; it now names the port.
    [Fact]
    public void Api_port_in_use_is_reported_in_plain_words()
    {
        var blocker = new System.Net.HttpListener();
        var port = FreePort();
        blocker.Prefixes.Add($"http://127.0.0.1:{port}/");
        blocker.Start();
        bool savedEnabled = false; int savedPort = 0;
        try
        {
            var vm = new MainViewModel();
            savedEnabled = vm.ApiEnabled; savedPort = vm.ApiPort;
            vm.ApiPort = port;
            vm.ApiEnabled = true;
            string? status = null;
            using var manager = new AutomationManager(
                new LlmSourceService(),
                () => new List<string> { "GitHub Dark" },
                (md, orig, ovr) => { },
                (md, ovr) => System.Threading.Tasks.Task.FromResult(System.Array.Empty<byte>()),
                new GovernanceService(),
                () => "",
                () => new AppSettings(),
                _ => { },
                (folder, fmt, ovr) => System.Threading.Tasks.Task.FromResult<object>(new { done = 0 }));
            manager.ApplyAutomationSettings(vm, () => { }, () => { }, false, _ => { }, () => { }, false, s => status = s);
            Assert.NotNull(status);
            Assert.StartsWith("API failed to start:", status);
            Assert.Contains($"port {port} is already in use", status);
        }
        finally
        {
            blocker.Stop();
            if (savedPort != 0)
            {
                AppServices.Settings.Current.ApiEnabled = savedEnabled;
                AppServices.Settings.Current.ApiPort = savedPort;
            }
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
