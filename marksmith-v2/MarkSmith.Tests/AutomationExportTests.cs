using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using MimeKit;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Run #28: one unattended export pipeline (<see cref="AutomationExportService"/>) behind the
/// clipboard / extension auto-export, the watch folder, batch convert and the local API, every
/// format on every path, and email-only automation free on every plan.
/// </summary>
public class OutputFormatsTests
{
    [Theory]
    [InlineData("pdf", "pdf")]
    [InlineData("DOCX", "docx")]
    [InlineData(" .epub ", "epub")]
    [InlineData("email", "eml")]
    [InlineData("msg", "msg")]
    [InlineData("html", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_knows_every_automation_format(string? input, string? expected) =>
        Assert.Equal(expected, OutputFormats.Normalize(input));

    [Fact]
    public void Kinds_and_labels_come_from_one_table()
    {
        Assert.Equal("Word document", OutputFormats.Label("docx"));
        Assert.Equal("email draft (.eml)", OutputFormats.Label("email"));
        Assert.Equal("Outlook message", OutputFormats.Kind("msg"));
        Assert.Equal("DOCX", OutputFormats.KindForPath(@"C:\out\Report.docx"));
        Assert.Equal("Email", OutputFormats.KindForPath(@"C:\out\Report.eml"));
        Assert.Equal("PDF", OutputFormats.Label("nonsense"));
        Assert.Equal(FeatureId.DocxExport, OutputFormats.ProFeature("docx"));
        Assert.Null(OutputFormats.ProFeature("eml"));
        Assert.True(OutputFormats.NeedsRenderHost("pdf"));
        Assert.False(OutputFormats.NeedsRenderHost("docx"));
    }

    [Fact]
    public void ParseFormats_accepts_the_email_formats()
    {
        Assert.Equal(new[] { "eml" }, ExportCoordinator.ParseFormats("email"));
        Assert.Equal(new[] { "pdf", "msg" }, ExportCoordinator.ParseFormats("pdf, msg, html"));
        Assert.Equal(new[] { "msg" }, ExportCoordinator.ParseFormats(null, "msg"));
        Assert.Equal(new[] { "pdf" }, ExportCoordinator.ParseFormats(null, "html"));
    }

    [Fact]
    public void Email_only_automation_is_free_and_nothing_else_is()
    {
        var free = new LicenseState { Edition = Edition.Free };
        var pro = new LicenseState { Edition = Edition.Pro };
        Assert.True(AutomationPolicy.Allows(free, "eml"));
        Assert.True(AutomationPolicy.Allows(free, "msg"));
        Assert.True(AutomationPolicy.Allows(free, new[] { "eml", "msg" }));
        Assert.False(AutomationPolicy.Allows(free, "pdf"));
        Assert.False(AutomationPolicy.Allows(free, new[] { "eml", "pdf" }));
        Assert.False(AutomationPolicy.Allows(free, Array.Empty<string>()));
        Assert.True(AutomationPolicy.Allows(pro, "pptx"));
    }

    [Fact]
    public void Batch_summary_names_the_count_the_first_failure_and_the_folder()
    {
        var r = new BatchConvertResult { Format = "docx", OutputFolder = @"C:\out", Total = 3 };
        r.Produced.Add(@"C:\out\a.docx");
        r.Produced.Add(@"C:\out\b.docx");
        r.Failures.Add("c.md: it's open in Word");
        Assert.Equal(@"Batch done: 2 of 3 converted to Word document, 1 failed · c.md: it's open in Word · in C:\out", r.Summary());

        var ok = new BatchConvertResult { Format = "eml", OutputFolder = @"C:\out", Total = 1 };
        ok.Produced.Add(@"C:\out\a.eml");
        Assert.Equal(@"Batch done: 1 file converted to email draft (.eml) · in C:\out", ok.Summary());
        Assert.Equal("There were no documents to convert.", new BatchConvertResult().Summary());
    }
}

[Collection("LicenseState")]
public class AutomationExportServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ms_auto_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static AppSettings Settings(string outFolder) => new() { OutputFolder = outFolder, EmailTo = "", EmailCc = "" };

    [Theory]
    [InlineData("eml")]
    [InlineData("msg")]
    [InlineData("docx")]
    [InlineData("pptx")]
    [InlineData("epub")]
    public async Task Every_format_but_pdf_is_written_without_the_preview_engine(string fmt)
    {
        AppServices.License.ResetToFree();
        AppServices.License.ToggleDevPro();
        try
        {
            var outPath = Path.Combine(_dir, "Report." + fmt);
            var written = await new AutomationExportService().ExportAsync(new AutomationExportJob
            {
                Markdown = "# Quarterly report\n\nRevenue is **up**.\n\n- one\n- two\n",
                Format = fmt, OutputPath = outPath, Settings = Settings(_dir), SourceLabel = "Report",
            });
            Assert.Equal(outPath, written);
            Assert.True(new FileInfo(outPath).Length > 200, $"{fmt} should be a real file");
        }
        finally { AppServices.License.ResetToFree(); }
    }

    [Fact]
    public async Task An_email_export_is_an_unsent_draft_named_after_the_document()
    {
        var outPath = Path.Combine(_dir, "Notes.eml");
        await new AutomationExportService().ExportAsync(new AutomationExportJob
        {
            Markdown = "# Launch notes\n\nShip it on **Friday**.\n", Format = "email", OutputPath = outPath,
            Settings = Settings(_dir), SourceLabel = "Notes",
        });
        var msg = MimeMessage.Load(outPath);
        Assert.Equal("1", msg.Headers["X-Unsent"]);
        Assert.Contains("Friday", msg.HtmlBody);
    }

    [Fact]
    public async Task A_pdf_without_the_preview_engine_fails_with_a_reason()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new AutomationExportService().ExportAsync(new AutomationExportJob
        {
            Markdown = "# x", Format = "pdf", OutputPath = Path.Combine(_dir, "x.pdf"), Settings = Settings(_dir),
        }));
        Assert.Contains("preview engine", ex.Message);
    }

    [Fact]
    public async Task An_unknown_format_is_refused_not_reported_as_done()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new AutomationExportService().ExportAsync(new AutomationExportJob
        {
            Markdown = "# x", Format = "html", OutputPath = Path.Combine(_dir, "x.html"), Settings = Settings(_dir),
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => new AutomationExportService().ConvertFilesAsync(
            new[] { "a.md" }, null, _dir, "rtf", Settings(_dir), null));
    }

    [Fact]
    public void Batch_sources_are_every_document_the_editor_opens_except_the_output_folder_and_lock_files()
    {
        var src = Directory.CreateDirectory(Path.Combine(_dir, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.md"), "# a");
        File.WriteAllText(Path.Combine(src, "b.txt"), "b");
        File.WriteAllText(Path.Combine(src, "c.html"), "<h1>c</h1>");
        File.WriteAllText(Path.Combine(src, "~$draft.docx"), "lock");
        File.WriteAllText(Path.Combine(src, "photo.png"), "png");
        var outDir = Directory.CreateDirectory(Path.Combine(src, "out")).FullName;
        File.WriteAllText(Path.Combine(outDir, "a.md"), "# an earlier result");
        var nested = Directory.CreateDirectory(Path.Combine(src, "nested")).FullName;
        File.WriteAllText(Path.Combine(nested, "d.markdown"), "# d");

        var top = AutomationExportService.FindBatchSources(src, recursive: false, outDir).Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { "a.md", "b.txt", "c.html" }, top);

        var all = AutomationExportService.FindBatchSources(src, recursive: true, outDir).Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { "a.md", "b.txt", "c.html", "d.markdown" }, all);
    }

    [Fact]
    public async Task A_batch_never_overwrites_its_sources_or_its_own_results()
    {
        AppServices.License.ResetToFree();
        AppServices.License.ToggleDevPro();
        try
        {
            File.WriteAllText(Path.Combine(_dir, "notes.md"), "# Notes\n\nFrom Markdown.");
            File.WriteAllText(Path.Combine(_dir, "notes.txt"), "Notes from text.");
            var existing = Path.Combine(_dir, "report.docx");
            await new DocxExportService().ExportAsync("# Report\n\nThe original.", existing, Settings(_dir));
            var before = File.ReadAllBytes(existing);

            var files = new[] { Path.Combine(_dir, "notes.md"), Path.Combine(_dir, "notes.txt"), existing };
            var result = await new AutomationExportService().ConvertFilesAsync(files, null, _dir, "docx", Settings(_dir), null);

            Assert.Empty(result.Failures);
            Assert.Equal(new[] { "notes.docx", "notes (converted).docx", "report (converted).docx" },
                result.Produced.Select(Path.GetFileName).ToArray());
            Assert.Equal(before, File.ReadAllBytes(existing));
        }
        finally { AppServices.License.ResetToFree(); }
    }

    [Fact]
    public async Task One_bad_file_doesnt_stop_the_batch_and_its_reason_is_kept()
    {
        File.WriteAllText(Path.Combine(_dir, "good.md"), "# Good");
        var missing = Path.Combine(_dir, "gone.md");
        var outDir = Path.Combine(_dir, "out");
        var result = await new AutomationExportService().ConvertFilesAsync(
            new[] { missing, Path.Combine(_dir, "good.md") }, _dir, outDir, "eml", Settings(outDir), null);

        Assert.Equal(2, result.Total);
        Assert.Single(result.Produced);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("gone.md: ", failure);
        Assert.True(File.Exists(Path.Combine(outDir, "good.eml")));
    }

    [Fact]
    public async Task A_cancelled_batch_stops_and_says_so()
    {
        File.WriteAllText(Path.Combine(_dir, "a.md"), "# A");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await new AutomationExportService().ConvertFilesAsync(
            new[] { Path.Combine(_dir, "a.md") }, null, Path.Combine(_dir, "out"), "eml", Settings(_dir), null, ct: cts.Token);
        Assert.True(result.Cancelled);
        Assert.Empty(result.Produced);
        Assert.StartsWith("Batch stopped: converted 0 of 1", result.Summary());
    }
}

/// <summary>The view model and coordinator paths: batch from dropped files, the watch folder,
/// and the free email exception on the automation toggles.</summary>
[Collection("LicenseState")]
public class AutomationFlowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ms_autoflow_").FullName;
    private readonly AppSettings _saved = Snapshot();

    private static AppSettings Snapshot() { var s = new AppSettings(); s.UpdateFrom(AppServices.Settings.Current); return s; }

    public void Dispose()
    {
        var s = AppServices.Settings.Current;
        s.TargetFormat = _saved.TargetFormat;
        s.AutoClipboardIngest = _saved.AutoClipboardIngest;
        s.WatchFolderEnabled = _saved.WatchFolderEnabled;
        s.WatchFolderAutoConvert = _saved.WatchFolderAutoConvert;
        s.AutoConvertIngests = _saved.AutoConvertIngests;
        s.OutputFolder = _saved.OutputFolder;
        s.EmailTo = _saved.EmailTo;
        s.EmailCc = _saved.EmailCc;
        AppServices.License.ResetToFree();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private MainViewModel Vm(string format)
    {
        AppServices.Settings.Current.EmailTo = "";
        AppServices.Settings.Current.EmailCc = "";
        return new MainViewModel { TargetFormat = format, OutputFolder = Path.Combine(_dir, "out") };
    }

    [Fact]
    public async Task Free_plan_batches_email_drafts_from_dropped_files_with_a_summary_and_history()
    {
        AppServices.License.ResetToFree();
        var vm = Vm("eml");
        FeatureId? gate = null;
        vm.ProFeatureAttempted += id => gate = id;
        var a = Path.Combine(_dir, "Alpha.md");
        var b = Path.Combine(_dir, "Beta.txt");
        File.WriteAllText(a, "# Alpha\n\nFirst.");
        File.WriteAllText(b, "Second, as text.");

        await vm.BatchConvertFilesAsync(new[] { a, b }, vm.OutputFolder, "eml");

        Assert.Null(gate);
        Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
        Assert.StartsWith("Batch done: 2 files converted to email draft (.eml)", vm.StatusText);
        Assert.True(File.Exists(Path.Combine(vm.OutputFolder, "Alpha.eml")));
        Assert.True(File.Exists(Path.Combine(vm.OutputFolder, "Beta.eml")));
        // The history rows name each source, not whatever the editor had open.
        Assert.Contains(vm.History, h => h.SourceLabel == "Alpha.md" && h.Kind == "Email");
        Assert.Contains(vm.History, h => h.SourceLabel == "Beta.txt");
        Assert.Equal(vm.StatusOutputPath, vm.LastOutputPath);
    }

    [Fact]
    public async Task Free_plan_batch_to_pdf_hits_the_batch_gate_with_the_free_way_in()
    {
        AppServices.License.ResetToFree();
        var vm = Vm("pdf");
        FeatureId? gate = null;
        vm.ProFeatureAttempted += id => gate = id;
        var a = Path.Combine(_dir, "Alpha.md");
        File.WriteAllText(a, "# Alpha");

        await vm.BatchConvertFilesAsync(new[] { a }, vm.OutputFolder, "pdf");

        Assert.Equal(FeatureId.BatchConvert, gate);
        Assert.Contains(AutomationPolicy.EmailIsFreeHint, vm.StatusText);
        Assert.False(Directory.Exists(vm.OutputFolder));
    }

    [Fact]
    public async Task Batch_of_files_it_cant_convert_says_so()
    {
        AppServices.License.ResetToFree();
        var vm = Vm("eml");
        await vm.BatchConvertFilesAsync(Array.Empty<string>(), vm.OutputFolder, "eml");
        Assert.Equal(StatusSeverity.Warning, vm.StatusSeverity);
        Assert.Contains("nothing here MarkSmith can convert", vm.StatusText);
    }

    [Fact]
    public void Free_plan_may_switch_on_automation_for_email_and_loses_it_when_the_format_changes()
    {
        AppServices.License.ResetToFree();
        var vm = Vm("eml");
        vm.WatchFolderEnabled = true;
        vm.AutoClipboardIngest = true;
        Assert.True(vm.WatchFolderEnabled);
        Assert.True(vm.AutoClipboardIngest);

        vm.TargetFormat = "pdf";
        Assert.False(vm.WatchFolderEnabled);
        Assert.False(vm.AutoClipboardIngest);
        Assert.Equal(StatusSeverity.Warning, vm.StatusSeverity);
        Assert.Contains("only runs for email drafts", vm.StatusText);

        vm.AutoConvertIngests = true; // refused on PDF
        Assert.False(vm.AutoConvertIngests);
        Assert.Contains(AutomationPolicy.EmailIsFreeHint, vm.StatusText);
    }

    [Fact]
    public void Free_email_automation_survives_a_restart()
    {
        // Found live: the constructor checked the licence before it had read the default format,
        // so a free user's email watch folder was switched off at every start.
        AppServices.License.ResetToFree();
        var s = AppServices.Settings.Current;
        s.TargetFormat = "eml";
        s.WatchFolderEnabled = true;
        s.AutoClipboardIngest = true;

        var vm = new MainViewModel();

        Assert.True(vm.WatchFolderEnabled);
        Assert.True(vm.AutoClipboardIngest);
        Assert.True(s.WatchFolderEnabled);

        s.TargetFormat = "pdf";
        var onPdf = new MainViewModel();
        Assert.False(onPdf.WatchFolderEnabled);
        Assert.False(onPdf.AutoClipboardIngest);
    }

    [Fact]
    public async Task Watch_folder_converts_once_per_change_and_leaves_the_open_document_alone()
    {
        AppServices.License.ResetToFree();
        var vm = Vm("eml");
        vm.WatchFolderAutoConvert = true;
        AppServices.Settings.Current.OutputFolder = vm.OutputFolder;
        var coordinator = new ExportCoordinator();
        var watched = Path.Combine(_dir, "Standup.md");
        File.WriteAllText(watched, "# Standup\n\nAll green.");

        await coordinator.OnWatchedFileAsync(vm, watched, host: null);
        var outPath = Path.Combine(vm.OutputFolder, "Standup.eml");
        Assert.True(File.Exists(outPath), vm.StatusText);
        Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
        Assert.StartsWith("Watch folder: Standup.md → Standup.eml", vm.StatusText);
        var rows = vm.History.Count;

        // The burst of Changed events one save raises: same text, nothing new.
        await coordinator.OnWatchedFileAsync(vm, watched, host: null);
        Assert.Equal(rows, vm.History.Count);

        // A real change converts again.
        File.WriteAllText(watched, "# Standup\n\nOne thing is red.");
        await coordinator.OnWatchedFileAsync(vm, watched, host: null);
        Assert.Equal(rows + 1, vm.History.Count);

        // Open in the editor: the user's edits are theirs, not an ingest.
        // (Written first: a VM holding InputFilePath reads the file and can keep it open briefly.)
        File.WriteAllText(watched, "# Standup\n\nEdited in MarkSmith.");
        vm.UsePasteSource = false;
        vm.InputFilePath = watched;
        await coordinator.OnWatchedFileAsync(vm, watched, host: null);
        Assert.Equal(rows + 1, vm.History.Count);
    }

    [Fact]
    public async Task Watch_folder_on_free_pdf_reports_the_gate_and_keeps_the_document()
    {
        AppServices.License.ResetToFree();
        var vm = Vm("pdf");
        vm.WatchFolderAutoConvert = true;
        var watched = Path.Combine(_dir, "Plan.md");
        File.WriteAllText(watched, "# Plan\n\nSteps.");

        await new ExportCoordinator().OnWatchedFileAsync(vm, watched, host: null);

        Assert.Contains("Steps.", vm.PastedMarkdown);
        Assert.Equal(StatusSeverity.Warning, vm.StatusSeverity);
        Assert.Contains("Folder watching is a MarkSmith Pro feature", vm.StatusText);
        Assert.Contains(AutomationPolicy.EmailIsFreeHint, vm.StatusText);
    }
}

/// <summary>The local API's side of run #28: /api/batch is automation (Pro, email drafts free),
/// and /api/convert labels the bytes with the format it actually wrote.</summary>
[Collection("LicenseState")]
public class AutomationApiTests : IDisposable
{
    private readonly Func<LicenseService> _licenseSource = ApiServer.LicenseSource;

    // Other API tests point the gate at their own license; these read the shared one.
    public AutomationApiTests() => ApiServer.LicenseSource = () => AppServices.License;

    public void Dispose()
    {
        ApiServer.LicenseSource = _licenseSource;
        AppServices.License.ResetToFree();
    }

    private static ApiServer Server(string targetFormat, List<string> batched) => new(
        new LlmSourceService(),
        () => new List<string> { "GitHub Dark" },
        (md, orig, ovr) => { },
        (md, ovr) => Task.FromResult(new byte[] { 1, 2, 3 }),
        new GovernanceService(),
        () => "",
        () => new AppSettings { TargetFormat = targetFormat },
        s => { },
        (folder, fmt, ovr) => { batched.Add(fmt); return Task.FromResult<object>(new { done = 1 }); });

    private static async Task<(int Port, ApiServer Server)> StartAsync(ApiServer server)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            try { server.Start(port); return (port, server); }
            catch (System.Net.HttpListenerException) when (attempt < 5) { await Task.Delay(50); }
        }
    }

    private static async Task<System.Net.Http.HttpResponseMessage> Post(int port, string path, string json)
    {
        using var client = new System.Net.Http.HttpClient();
        return await client.PostAsync($"http://127.0.0.1:{port}{path}", new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"));
    }

    [Fact]
    public async Task Free_install_can_batch_email_drafts_but_not_pdfs()
    {
        AppServices.License.ResetToFree();
        var batched = new List<string>();
        var (port, server) = await StartAsync(Server("pdf", batched));
        using (server)
        {
            var pdf = await Post(port, "/api/batch", "{\"folder\":\"C:/docs\",\"format\":\"pdf\"}");
            Assert.Equal(System.Net.HttpStatusCode.PaymentRequired, pdf.StatusCode);
            Assert.Contains("Batch conversion is a MarkSmith Pro feature", await pdf.Content.ReadAsStringAsync());

            var eml = await Post(port, "/api/batch", "{\"folder\":\"C:/docs\",\"format\":\"email\"}");
            Assert.Equal(System.Net.HttpStatusCode.OK, eml.StatusCode);
            Assert.Equal(new[] { "eml" }, batched);

            var bad = await Post(port, "/api/batch", "{\"folder\":\"C:/docs\",\"format\":\"rtf\"}");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("unknown format", await bad.Content.ReadAsStringAsync());
            server.Stop();
        }
    }

    [Fact]
    public async Task Convert_without_a_format_names_the_default_format_it_wrote()
    {
        AppServices.License.ResetToFree();
        AppServices.License.ToggleDevPro();
        var (port, server) = await StartAsync(Server("docx", new List<string>()));
        using (server)
        {
            var resp = await Post(port, "/api/convert", "{\"markdown\":\"# X\"}");
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", resp.Content.Headers.ContentType?.MediaType);
            Assert.Contains("export.docx", resp.Content.Headers.ContentDisposition?.ToString() ?? string.Join(",", resp.Content.Headers.GetValues("Content-Disposition")));
            server.Stop();
        }
    }

    [Fact]
    public async Task Convert_without_a_format_on_a_free_word_default_is_gated()
    {
        AppServices.License.ResetToFree();
        var (port, server) = await StartAsync(Server("docx", new List<string>()));
        using (server)
        {
            var resp = await Post(port, "/api/convert", "{\"markdown\":\"# X\"}");
            Assert.Equal(System.Net.HttpStatusCode.PaymentRequired, resp.StatusCode);
            server.Stop();
        }
    }
}
