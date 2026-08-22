using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Core.Tests;

[Collection("LicenseState")]
public class BatchDocxLicensingTests : IDisposable
{
    private readonly string _licensePath;
    private readonly string _shadowPath;
    private readonly string? _backup;
    private readonly string? _shadowBackup;

    public BatchDocxLicensingTests()
    {
        var dir = AppPaths.ConfigDir;
        _licensePath = Path.Combine(dir, "license.json");
        _shadowPath = Path.Combine(dir, "trial.state");
        _backup = File.Exists(_licensePath) ? File.ReadAllText(_licensePath) : null;
        _shadowBackup = File.Exists(_shadowPath) ? File.ReadAllText(_shadowPath) : null;
    }

    public void Dispose()
    {
        try
        {
            if (_backup is null) { if (File.Exists(_licensePath)) File.Delete(_licensePath); }
            else File.WriteAllText(_licensePath, _backup);
            if (_shadowBackup is null) { if (File.Exists(_shadowPath)) File.Delete(_shadowPath); }
            else File.WriteAllText(_shadowPath, _shadowBackup);
            AppServices.License.Load();
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task FreeUser_BatchConvert_Docx_Throws_InvalidOperationException()
    {
        AppServices.License.ResetToFree();
        Assert.False(AppServices.License.CanExportDocx);

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"batch_lic_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"batch_lic_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "test.md"), "# Hello\nFree test.");

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ConvertDirectoryAsync(null, tempSrc, tempOut, "docx", new AppSettings()));

            Assert.Contains("DOCX export is a MarkSmith Pro feature", ex.Message);
            Assert.False(File.Exists(Path.Combine(tempOut, "test.docx")));
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    [Fact]
    public async Task MainViewModel_BatchConvertAsync_Docx_FreeUser_Blocks_And_Fires_ProFeatureAttempted()
    {
        AppServices.License.ResetToFree();
        Assert.False(AppServices.License.CanExportDocx);

        var vm = new MainViewModel();
        FeatureId? attemptedFeature = null;
        vm.ProFeatureAttempted += id => attemptedFeature = id;

        var tempSrc = Path.Combine(Path.GetTempPath(), $"vm_batch_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"vm_batch_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "file1.md"), "# Heading\nContent");

        try
        {
            await vm.BatchConvertAsync(tempSrc, tempOut, "docx");

            Assert.Equal(FeatureId.DocxExport, attemptedFeature);
            Assert.Equal(StatusSeverity.Warning, vm.StatusSeverity);
            Assert.Contains("MarkSmith Pro feature", vm.StatusText);
            Assert.False(File.Exists(Path.Combine(tempOut, "file1.docx")));
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    [Fact]
    public async Task MainViewModel_BatchConvertAsync_Docx_ProUser_Allows_Conversion_Without_Prompt()
    {
        AppServices.License.ResetToFree();
        AppServices.License.ToggleDevPro();
        Assert.True(AppServices.License.CanExportDocx);

        var vm = new MainViewModel();
        FeatureId? attemptedFeature = null;
        vm.ProFeatureAttempted += id => attemptedFeature = id;

        var tempSrc = Path.Combine(Path.GetTempPath(), $"vm_batch_pro_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"vm_batch_pro_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "file1.md"), "# Pro Document\nPro content");

        try
        {
            await vm.BatchConvertAsync(tempSrc, tempOut, "docx");

            Assert.Null(attemptedFeature);
            Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
            Assert.True(File.Exists(Path.Combine(tempOut, "file1.docx")));
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    [Fact]
    public async Task PartialTrialQuotaExhaustion_MidBatch_ConvertsUntilExhausted_AndRevertsToFree()
    {
        AppServices.License.ResetToFree();
        var (started, msg) = AppServices.License.StartTrial();
        Assert.True(started, $"Trial failed to start: {msg}");
        Assert.Equal(3, AppServices.License.State.TrialExportsRemaining);

        // Spend 2 exports so exactly 1 trial credit remains
        AppServices.License.ConsumeDocxExport();
        AppServices.License.ConsumeDocxExport();
        Assert.Equal(1, AppServices.License.State.TrialExportsRemaining);
        Assert.Equal(Edition.Trial, AppServices.License.State.Edition);
        Assert.True(AppServices.License.CanExportDocx);

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"batch_partial_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"batch_partial_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        // Create 3 files with deterministic names
        File.WriteAllText(Path.Combine(tempSrc, "01_first.md"), "# First Doc\nShould convert successfully.");
        File.WriteAllText(Path.Combine(tempSrc, "02_second.md"), "# Second Doc\nShould be blocked by exhausted quota.");
        File.WriteAllText(Path.Combine(tempSrc, "03_third.md"), "# Third Doc\nShould also be blocked.");

        var progressMessages = new List<string>();

        try
        {
            await service.ConvertDirectoryAsync(
                null,
                tempSrc,
                tempOut,
                "docx",
                new AppSettings(),
                msg => progressMessages.Add(msg));

            // Exactly the first file should be converted
            Assert.True(File.Exists(Path.Combine(tempOut, "01_first.docx")), "01_first.docx should have been generated.");
            Assert.False(File.Exists(Path.Combine(tempOut, "02_second.docx")), "02_second.docx should NOT have been generated.");
            Assert.False(File.Exists(Path.Combine(tempOut, "03_third.docx")), "03_third.docx should NOT have been generated.");

            // License state should now be Free with 0 remaining
            Assert.Equal(0, AppServices.License.State.TrialExportsRemaining);
            Assert.Equal(Edition.Free, AppServices.License.State.Edition);
            Assert.False(AppServices.License.CanExportDocx);

            // Progress callbacks should have reported the quota exhaustion for 02 and 03
            Assert.Contains(progressMessages, m => m.Contains("02_second.md") && m.Contains("DOCX export trial quota exhausted"));
            Assert.Contains(progressMessages, m => m.Contains("03_third.md") && m.Contains("DOCX export trial quota exhausted"));
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    [Fact]
    public async Task ActiveTrialUser_WithSufficientQuota_ConvertsAllFiles_AndConsumesTokens()
    {
        AppServices.License.ResetToFree();
        var (started, _) = AppServices.License.StartTrial();
        Assert.True(started);
        Assert.Equal(3, AppServices.License.State.TrialExportsRemaining);

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"batch_trial_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"batch_trial_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "docA.md"), "# Document A\nContent A");
        File.WriteAllText(Path.Combine(tempSrc, "docB.md"), "# Document B\nContent B");

        try
        {
            await service.ConvertDirectoryAsync(null, tempSrc, tempOut, "docx", new AppSettings());

            Assert.True(File.Exists(Path.Combine(tempOut, "docA.docx")));
            Assert.True(File.Exists(Path.Combine(tempOut, "docB.docx")));

            // Started with 3, spent 2 -> 1 remaining, still in Trial edition
            Assert.Equal(1, AppServices.License.State.TrialExportsRemaining);
            Assert.Equal(Edition.Trial, AppServices.License.State.Edition);
            Assert.True(AppServices.License.CanExportDocx);
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    [Fact]
    public async Task ProUser_ConvertsAllFiles_WithoutDeductingTrialTokens()
    {
        AppServices.License.ResetToFree();
        AppServices.License.ToggleDevPro();
        Assert.Equal(Edition.Pro, AppServices.License.State.Edition);
        Assert.True(AppServices.License.CanExportDocx);

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"batch_pro_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"batch_pro_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "p1.md"), "# P1\nPro 1");
        File.WriteAllText(Path.Combine(tempSrc, "p2.md"), "# P2\nPro 2");
        File.WriteAllText(Path.Combine(tempSrc, "p3.md"), "# P3\nPro 3");

        try
        {
            await service.ConvertDirectoryAsync(null, tempSrc, tempOut, "docx", new AppSettings());

            Assert.True(File.Exists(Path.Combine(tempOut, "p1.docx")));
            Assert.True(File.Exists(Path.Combine(tempOut, "p2.docx")));
            Assert.True(File.Exists(Path.Combine(tempOut, "p3.docx")));

            Assert.Equal(Edition.Pro, AppServices.License.State.Edition);
            Assert.True(AppServices.License.CanExportDocx);
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }
}
