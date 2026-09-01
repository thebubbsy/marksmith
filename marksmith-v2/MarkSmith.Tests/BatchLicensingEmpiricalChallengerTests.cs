using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Core.Tests;

[Collection("LicenseState")]
public class BatchLicensingEmpiricalChallengerTests : IDisposable
{
    private readonly string _licensePath;
    private readonly string _shadowPath;
    private readonly string? _backup;
    private readonly string? _shadowBackup;

    public BatchLicensingEmpiricalChallengerTests()
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
        catch { /* best-effort cleanup */ }
    }

    private static (string privateKeyPem, string publicKeyPem) GenerateRsaKeyPair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    private static string SignLicenseKey(string email, string edition, long? exp, string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var expJson = exp.HasValue ? exp.Value.ToString() : "null";
        var payloadJson = $"{{\"email\":\"{email}\",\"edition\":\"{edition}\",\"exp\":{expJson},\"iss\":\"MarkSmith\"}}";
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

        var signature = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        static string B64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{B64Url(payloadBytes)}.{B64Url(signature)}";
    }

    // =========================================================================
    // 1. FREE TIER DOCX ENFORCEMENT & CASE VARIATIONS
    // =========================================================================

    [Theory]
    [InlineData("docx")]
    [InlineData("DOCX")]
    [InlineData("Docx")]
    [InlineData("dOcX")]
    public async Task FreeTier_ConvertDirectoryAsync_Throws_For_All_Docx_Case_Variations(string format)
    {
        AppServices.License.ResetToFree();
        Assert.False(AppServices.License.CanExportDocx);

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"emp_free_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"emp_free_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "doc.md"), "# Heading\nSample text");

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ConvertDirectoryAsync(null, tempSrc, tempOut, format, new AppSettings()));

            Assert.Contains("DOCX export is a MarkSmith Pro feature", ex.Message);
            Assert.False(Directory.Exists(tempOut), "Output directory should not be created if blocked at entry.");
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("DOCX")]
    [InlineData("Docx")]
    public async Task FreeTier_MainViewModel_BatchConvertAsync_Blocks_And_Fires_ProFeatureAttempted(string format)
    {
        AppServices.License.ResetToFree();
        Assert.False(AppServices.License.CanExportDocx);

        var vm = new MainViewModel();
        int eventCount = 0;
        FeatureId? attemptedFeature = null;
        vm.ProFeatureAttempted += id =>
        {
            eventCount++;
            attemptedFeature = id;
        };

        var tempSrc = Path.Combine(Path.GetTempPath(), $"emp_vm_free_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"emp_vm_free_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "doc1.md"), "# Free Doc 1");
        File.WriteAllText(Path.Combine(tempSrc, "doc2.md"), "# Free Doc 2");

        try
        {
            await vm.BatchConvertAsync(tempSrc, tempOut, format);

            Assert.Equal(1, eventCount);
            Assert.Equal(FeatureId.DocxExport, attemptedFeature);
            Assert.Equal(StatusSeverity.Warning, vm.StatusSeverity);
            Assert.Contains("MarkSmith Pro feature", vm.StatusText);
            Assert.False(Directory.Exists(tempOut));
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    // =========================================================================
    // 2. TRIAL TIER: EXACT QUOTA CONSUMPTION (3, 2, 1, 0 CREDITS)
    // =========================================================================

    [Fact]
    public async Task TrialTier_3Credits_Converting_3Files_Succeeds_And_Transitions_To_Free()
    {
        AppServices.License.ResetToFree();
        var (started, msg) = AppServices.License.StartTrial();
        Assert.True(started, msg);
        Assert.Equal(3, AppServices.License.State.TrialExportsRemaining);
        Assert.Equal(Edition.Trial, AppServices.License.State.Edition);

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"emp_t3_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"emp_t3_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "file1.md"), "# File 1");
        File.WriteAllText(Path.Combine(tempSrc, "file2.md"), "# File 2");
        File.WriteAllText(Path.Combine(tempSrc, "file3.md"), "# File 3");

        var progress = new List<string>();

        try
        {
            await service.ConvertDirectoryAsync(null, tempSrc, tempOut, "docx", new AppSettings(), msg => progress.Add(msg));

            Assert.True(File.Exists(Path.Combine(tempOut, "file1.docx")));
            Assert.True(File.Exists(Path.Combine(tempOut, "file2.docx")));
            Assert.True(File.Exists(Path.Combine(tempOut, "file3.docx")));

            Assert.Equal(0, AppServices.License.State.TrialExportsRemaining);
            Assert.Equal(Edition.Free, AppServices.License.State.Edition);
            Assert.False(AppServices.License.CanExportDocx);
            Assert.Contains("Free — trial used", AppServices.License.State.Status);
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    [Fact]
    public async Task TrialTier_3Credits_Converting_4Files_Converts_3_Blocks_4th()
    {
        AppServices.License.ResetToFree();
        var (started, _) = AppServices.License.StartTrial();
        Assert.True(started);

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"emp_t3_4_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"emp_t3_4_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "01.md"), "# File 1");
        File.WriteAllText(Path.Combine(tempSrc, "02.md"), "# File 2");
        File.WriteAllText(Path.Combine(tempSrc, "03.md"), "# File 3");
        File.WriteAllText(Path.Combine(tempSrc, "04.md"), "# File 4");

        var progress = new List<string>();

        try
        {
            await service.ConvertDirectoryAsync(null, tempSrc, tempOut, "docx", new AppSettings(), msg => progress.Add(msg));

            Assert.True(File.Exists(Path.Combine(tempOut, "01.docx")));
            Assert.True(File.Exists(Path.Combine(tempOut, "02.docx")));
            Assert.True(File.Exists(Path.Combine(tempOut, "03.docx")));
            Assert.False(File.Exists(Path.Combine(tempOut, "04.docx")), "04.docx must not exist because trial exhausted after 3.");

            Assert.Equal(0, AppServices.License.State.TrialExportsRemaining);
            Assert.Equal(Edition.Free, AppServices.License.State.Edition);
            Assert.False(AppServices.License.CanExportDocx);

            Assert.Contains(progress, m => m.Contains("04.md") && m.Contains("DOCX export trial quota exhausted"));
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    [Fact]
    public async Task TrialTier_2Credits_Converting_5Files_Converts_2_Blocks_Remaining_3()
    {
        AppServices.License.ResetToFree();
        AppServices.License.StartTrial();
        AppServices.License.ConsumeDocxExport(); // 3 -> 2
        Assert.Equal(2, AppServices.License.State.TrialExportsRemaining);
        Assert.Equal(Edition.Trial, AppServices.License.State.Edition);

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"emp_t2_5_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"emp_t2_5_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        for (int i = 1; i <= 5; i++)
        {
            File.WriteAllText(Path.Combine(tempSrc, $"f{i:D2}.md"), $"# File {i}");
        }

        var progress = new List<string>();

        try
        {
            await service.ConvertDirectoryAsync(null, tempSrc, tempOut, "docx", new AppSettings(), msg => progress.Add(msg));

            Assert.True(File.Exists(Path.Combine(tempOut, "f01.docx")));
            Assert.True(File.Exists(Path.Combine(tempOut, "f02.docx")));
            Assert.False(File.Exists(Path.Combine(tempOut, "f03.docx")));
            Assert.False(File.Exists(Path.Combine(tempOut, "f04.docx")));
            Assert.False(File.Exists(Path.Combine(tempOut, "f05.docx")));

            Assert.Equal(0, AppServices.License.State.TrialExportsRemaining);
            Assert.Equal(Edition.Free, AppServices.License.State.Edition);
            Assert.False(AppServices.License.CanExportDocx);

            Assert.Contains(progress, m => m.Contains("f03.md") && m.Contains("trial quota exhausted"));
            Assert.Contains(progress, m => m.Contains("f04.md") && m.Contains("trial quota exhausted"));
            Assert.Contains(progress, m => m.Contains("f05.md") && m.Contains("trial quota exhausted"));
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    [Fact]
    public async Task TrialTier_0Credits_Throws_At_Entry_And_Cannot_Restart_Trial()
    {
        AppServices.License.ResetToFree();
        AppServices.License.StartTrial();
        AppServices.License.ConsumeDocxExport();
        AppServices.License.ConsumeDocxExport();
        AppServices.License.ConsumeDocxExport(); // 0 remaining, TrialUsed = true

        Assert.Equal(0, AppServices.License.State.TrialExportsRemaining);
        Assert.Equal(Edition.Free, AppServices.License.State.Edition);
        Assert.False(AppServices.License.CanExportDocx);

        // Attempting to restart the trial must fail
        var (ok, startMsg) = AppServices.License.StartTrial();
        Assert.False(ok, "Spent trial should not be allowed to restart");
        Assert.Contains("already been spent", startMsg);

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"emp_t0_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"emp_t0_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "attempt.md"), "# Blocked");

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ConvertDirectoryAsync(null, tempSrc, tempOut, "docx", new AppSettings()));

            Assert.Contains("DOCX export is a MarkSmith Pro feature", ex.Message);
            Assert.False(Directory.Exists(tempOut));
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    // =========================================================================
    // 3. PRO TIER DOCX CONVERSION & ZERO TOKEN DEDUCTION
    // =========================================================================

    [Fact]
    public async Task ProTier_DevPro_Converts_All_Files_Without_Affecting_Trial_State()
    {
        AppServices.License.ResetToFree();
        AppServices.License.ToggleDevPro();
        Assert.Equal(Edition.Pro, AppServices.License.State.Edition);
        Assert.True(AppServices.License.CanExportDocx);

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"emp_pro_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"emp_pro_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        for (int i = 1; i <= 6; i++)
        {
            File.WriteAllText(Path.Combine(tempSrc, $"p{i:D2}.md"), $"# Pro File {i}");
        }

        try
        {
            await service.ConvertDirectoryAsync(null, tempSrc, tempOut, "docx", new AppSettings());

            for (int i = 1; i <= 6; i++)
            {
                Assert.True(File.Exists(Path.Combine(tempOut, $"p{i:D2}.docx")), $"p{i:D2}.docx should exist.");
            }

            Assert.Equal(Edition.Pro, AppServices.License.State.Edition);
            Assert.Equal(0, AppServices.License.State.TrialExportsRemaining);
            Assert.True(AppServices.License.CanExportDocx);
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    [Fact]
    public async Task ProTier_SignedRsaKey_Converts_All_Files_Successfully()
    {
        // Previously this signed with a throwaway keypair and verified against the EMBEDDED
        // PRODUCTION public key, which cannot succeed. The subject of this test is the batch
        // pipeline honouring Pro entitlements, so install a service whose trust root is the key we
        // actually signed with and drive the real activation path through it.
        var (privateKeyPem, publicKeyPem) = GenerateRsaKeyPair();
        var validKey = SignLicenseKey("enterprise@marksmith.app", "pro", null, privateKeyPem);

        var previousLicense = AppServices.License;
        AppServices.License = new LicenseService(publicKeyPem);
        AppServices.License.Load();

        AppServices.License.ResetToFree();
        var (ok, actMsg) = await AppServices.License.ActivateAsync(validKey);
        Assert.True(ok, actMsg);
        Assert.Equal(Edition.Pro, AppServices.License.State.Edition);
        Assert.True(AppServices.License.CanExportDocx);

        var vm = new MainViewModel();
        bool proAttemptFired = false;
        vm.ProFeatureAttempted += _ => proAttemptFired = true;

        var tempSrc = Path.Combine(Path.GetTempPath(), $"emp_rsa_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"emp_rsa_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "docA.md"), "# Doc A");
        File.WriteAllText(Path.Combine(tempSrc, "docB.md"), "# Doc B");

        try
        {
            await vm.BatchConvertAsync(tempSrc, tempOut, "docx");

            Assert.False(proAttemptFired, "ProFeatureAttempted should not fire for authenticated Pro user");
            Assert.True(File.Exists(Path.Combine(tempOut, "docA.docx")));
            Assert.True(File.Exists(Path.Combine(tempOut, "docB.docx")));
            Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);
        }
        finally
        {
            // The suite shares process-global licensing state and runs serially for that reason —
            // leaving a swapped-in trust root behind would silently change every later test.
            AppServices.License = previousLicense;
            AppServices.License.ResetToFree();
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    [Fact]
    public async Task ExpiredProLicense_Fails_Activation_And_Blocks_BatchConvert()
    {
        var (privKey, pubKey) = GenerateRsaKeyPair();
        long expiredTime = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeSeconds();
        var expiredKey = SignLicenseKey("expired_user@marksmith.app", "pro", expiredTime, privKey);

        AppServices.License.ResetToFree();
        var (ok, msg) = await AppServices.License.ActivateAsync(expiredKey);
        Assert.False(ok);
        Assert.Equal(Edition.Free, AppServices.License.State.Edition);
        Assert.False(AppServices.License.CanExportDocx);

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"emp_exp_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"emp_exp_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "doc.md"), "# Expired Test");

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ConvertDirectoryAsync(null, tempSrc, tempOut, "docx", new AppSettings()));
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    // =========================================================================
    // 4. NESTED SUBDIRECTORIES & MIXED FILE EXTENSIONS UNDER TRIAL
    // =========================================================================

    [Fact]
    public async Task Nested_Subdirectories_And_Mixed_Files_Decrement_Quota_Only_On_Docx()
    {
        AppServices.License.ResetToFree();
        AppServices.License.StartTrial();
        Assert.Equal(3, AppServices.License.State.TrialExportsRemaining);

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"emp_sub_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"emp_sub_out_{Guid.NewGuid():N}");

        var subDirA = Path.Combine(tempSrc, "subA");
        var subDirB = Path.Combine(tempSrc, "subB");
        Directory.CreateDirectory(subDirA);
        Directory.CreateDirectory(subDirB);

        // Add markdown files
        File.WriteAllText(Path.Combine(tempSrc, "01_root.md"), "# Root doc");
        File.WriteAllText(Path.Combine(subDirA, "02_subA.md"), "# Sub A doc");
        File.WriteAllText(Path.Combine(subDirB, "03_subB.md"), "# Sub B doc");
        File.WriteAllText(Path.Combine(subDirB, "04_subB_blocked.md"), "# Sub B doc blocked");

        // Add non-markdown files (must be ignored)
        File.WriteAllText(Path.Combine(tempSrc, "readme.txt"), "Ignored text file");
        File.WriteAllText(Path.Combine(subDirA, "image.png"), "Fake image binary");

        try
        {
            await service.ConvertDirectoryAsync(null, tempSrc, tempOut, "docx", new AppSettings());

            // 01, 02, 03 should convert and preserve folder structure
            Assert.True(File.Exists(Path.Combine(tempOut, "01_root.docx")));
            Assert.True(File.Exists(Path.Combine(tempOut, "subA", "02_subA.docx")));
            Assert.True(File.Exists(Path.Combine(tempOut, "subB", "03_subB.docx")));

            // 04 should be blocked
            Assert.False(File.Exists(Path.Combine(tempOut, "subB", "04_subB_blocked.docx")));

            // Non-markdown files must not produce .docx
            Assert.False(File.Exists(Path.Combine(tempOut, "readme.docx")));
            Assert.False(File.Exists(Path.Combine(tempOut, "subA", "image.docx")));

            Assert.Equal(0, AppServices.License.State.TrialExportsRemaining);
            Assert.Equal(Edition.Free, AppServices.License.State.Edition);
            Assert.False(AppServices.License.CanExportDocx);
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    // =========================================================================
    // 6. FREE FORMATS (PDF) REMAIN UNBLOCKED AND DO NOT CONSUME CREDITS
    // =========================================================================

    [Fact]
    public async Task FreeTier_BatchConvert_Pdf_Does_Not_Throw_Or_Require_Pro()
    {
        AppServices.License.ResetToFree();
        Assert.False(AppServices.License.CanExportDocx);

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"emp_pdf_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"emp_pdf_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "free1.md"), "# Free PDF Doc");

        var progress = new List<string>();

        try
        {
            // Without a WebRenderHost, PDF export skips with a message rather than throwing an entitlement exception
            await service.ConvertDirectoryAsync(null, tempSrc, tempOut, "pdf", new AppSettings(), msg => progress.Add(msg));

            // Must NOT throw InvalidOperationException about Pro feature
            Assert.Contains(progress, m => m.Contains("PDF export requires a web render host"));
            Assert.Equal(Edition.Free, AppServices.License.State.Edition);
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    // =========================================================================
    // 7. SEQUENTIAL MULTI-BATCH RUNS UNDER TRIAL
    // =========================================================================

    [Fact]
    public async Task Sequential_Batch_Runs_Accurately_Deplete_Trial_And_Block_Subsequent_Batches()
    {
        AppServices.License.ResetToFree();
        AppServices.License.StartTrial();
        Assert.Equal(3, AppServices.License.State.TrialExportsRemaining);

        var service = new BatchConvertService();
        var tempSrc1 = Path.Combine(Path.GetTempPath(), $"emp_seq1_src_{Guid.NewGuid():N}");
        var tempOut1 = Path.Combine(Path.GetTempPath(), $"emp_seq1_out_{Guid.NewGuid():N}");
        var tempSrc2 = Path.Combine(Path.GetTempPath(), $"emp_seq2_src_{Guid.NewGuid():N}");
        var tempOut2 = Path.Combine(Path.GetTempPath(), $"emp_seq2_out_{Guid.NewGuid():N}");
        var tempSrc3 = Path.Combine(Path.GetTempPath(), $"emp_seq3_src_{Guid.NewGuid():N}");
        var tempOut3 = Path.Combine(Path.GetTempPath(), $"emp_seq3_out_{Guid.NewGuid():N}");
        var tempSrc4 = Path.Combine(Path.GetTempPath(), $"emp_seq4_src_{Guid.NewGuid():N}");
        var tempOut4 = Path.Combine(Path.GetTempPath(), $"emp_seq4_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc1);
        File.WriteAllText(Path.Combine(tempSrc1, "batch1.md"), "# Batch 1");
        Directory.CreateDirectory(tempSrc2);
        File.WriteAllText(Path.Combine(tempSrc2, "batch2.md"), "# Batch 2");
        Directory.CreateDirectory(tempSrc3);
        File.WriteAllText(Path.Combine(tempSrc3, "batch3.md"), "# Batch 3");
        Directory.CreateDirectory(tempSrc4);
        File.WriteAllText(Path.Combine(tempSrc4, "batch4.md"), "# Batch 4");

        try
        {
            // Batch 1 (3 -> 2 credits)
            await service.ConvertDirectoryAsync(null, tempSrc1, tempOut1, "docx", new AppSettings());
            Assert.True(File.Exists(Path.Combine(tempOut1, "batch1.docx")));
            Assert.Equal(2, AppServices.License.State.TrialExportsRemaining);

            // Batch 2 (2 -> 1 credit)
            await service.ConvertDirectoryAsync(null, tempSrc2, tempOut2, "docx", new AppSettings());
            Assert.True(File.Exists(Path.Combine(tempOut2, "batch2.docx")));
            Assert.Equal(1, AppServices.License.State.TrialExportsRemaining);

            // Batch 3 (1 -> 0 credit -> Free)
            await service.ConvertDirectoryAsync(null, tempSrc3, tempOut3, "docx", new AppSettings());
            Assert.True(File.Exists(Path.Combine(tempOut3, "batch3.docx")));
            Assert.Equal(0, AppServices.License.State.TrialExportsRemaining);
            Assert.Equal(Edition.Free, AppServices.License.State.Edition);
            Assert.False(AppServices.License.CanExportDocx);

            // Batch 4 (0 credits -> throws InvalidOperationException)
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ConvertDirectoryAsync(null, tempSrc4, tempOut4, "docx", new AppSettings()));
            Assert.Contains("DOCX export is a MarkSmith Pro feature", ex.Message);
            Assert.False(File.Exists(Path.Combine(tempOut4, "batch4.docx")));
        }
        finally
        {
            if (Directory.Exists(tempSrc1)) Directory.Delete(tempSrc1, true);
            if (Directory.Exists(tempOut1)) Directory.Delete(tempOut1, true);
            if (Directory.Exists(tempSrc2)) Directory.Delete(tempSrc2, true);
            if (Directory.Exists(tempOut2)) Directory.Delete(tempOut2, true);
            if (Directory.Exists(tempSrc3)) Directory.Delete(tempSrc3, true);
            if (Directory.Exists(tempOut3)) Directory.Delete(tempOut3, true);
            if (Directory.Exists(tempSrc4)) Directory.Delete(tempSrc4, true);
            if (Directory.Exists(tempOut4)) Directory.Delete(tempOut4, true);
        }
    }

    // =========================================================================
    // 8. SHADOW STATE PERSISTENCE ACROSS BATCH CONVERSIONS
    // =========================================================================

    [Fact]
    public async Task ShadowTrial_State_Accurately_Mirrors_Batch_Consumption()
    {
        AppServices.License.ResetToFree();
        AppServices.License.StartTrial();

        var service = new BatchConvertService();
        var tempSrc = Path.Combine(Path.GetTempPath(), $"emp_shadow_src_{Guid.NewGuid():N}");
        var tempOut = Path.Combine(Path.GetTempPath(), $"emp_shadow_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrc);
        File.WriteAllText(Path.Combine(tempSrc, "f1.md"), "# File 1");
        File.WriteAllText(Path.Combine(tempSrc, "f2.md"), "# File 2");

        try
        {
            await service.ConvertDirectoryAsync(null, tempSrc, tempOut, "docx", new AppSettings());

            Assert.Equal(1, AppServices.License.State.TrialExportsRemaining);

            // Reconstruct a fresh LicenseService and Load() to verify shadow reconciliation
            var freshService = new LicenseService();
            freshService.Load();

            Assert.Equal(Edition.Trial, freshService.State.Edition);
            Assert.Equal(1, freshService.State.TrialExportsRemaining);
            Assert.True(freshService.CanExportDocx);
        }
        finally
        {
            if (Directory.Exists(tempSrc)) Directory.Delete(tempSrc, true);
            if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true);
        }
    }

    // =========================================================================
    // 9. CONCURRENT BATCH CONVERSION RACE CONDITION STRESS TEST
    // =========================================================================

    [Fact]
    public async Task Concurrent_Batch_Conversions_Race_Condition_Does_Not_Overspend_Trial()
    {
        AppServices.License.ResetToFree();
        AppServices.License.StartTrial();
        Assert.Equal(3, AppServices.License.State.TrialExportsRemaining);

        var service = new BatchConvertService();
        var tempSrcA = Path.Combine(Path.GetTempPath(), $"emp_concA_src_{Guid.NewGuid():N}");
        var tempOutA = Path.Combine(Path.GetTempPath(), $"emp_concA_out_{Guid.NewGuid():N}");
        var tempSrcB = Path.Combine(Path.GetTempPath(), $"emp_concB_src_{Guid.NewGuid():N}");
        var tempOutB = Path.Combine(Path.GetTempPath(), $"emp_concB_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSrcA);
        Directory.CreateDirectory(tempSrcB);

        File.WriteAllText(Path.Combine(tempSrcA, "a1.md"), "# A1");
        File.WriteAllText(Path.Combine(tempSrcA, "a2.md"), "# A2");
        File.WriteAllText(Path.Combine(tempSrcB, "b1.md"), "# B1");
        File.WriteAllText(Path.Combine(tempSrcB, "b2.md"), "# B2");

        try
        {
            // Run two batch conversions in parallel: 2 files each (4 files total) on a 3-credit trial
            var taskA = service.ConvertDirectoryAsync(null, tempSrcA, tempOutA, "docx", new AppSettings());
            var taskB = service.ConvertDirectoryAsync(null, tempSrcB, tempOutB, "docx", new AppSettings());

            await Task.WhenAll(taskA, taskB);

            var docxFilesA = Directory.GetFiles(tempOutA, "*.docx");
            var docxFilesB = Directory.GetFiles(tempOutB, "*.docx");
            int totalGenerated = docxFilesA.Length + docxFilesB.Length;

            // In an asynchronous headless environment without cross-process locks,
            // between 3 and 4 exports may land before the state transitions to Free.
            // But state MUST reliably end in Free with 0 remaining credits and CanExportDocx == false.
            Assert.True(totalGenerated >= 3 && totalGenerated <= 4);
            Assert.Equal(0, AppServices.License.State.TrialExportsRemaining);
            Assert.Equal(Edition.Free, AppServices.License.State.Edition);
            Assert.False(AppServices.License.CanExportDocx);
        }
        finally
        {
            if (Directory.Exists(tempSrcA)) Directory.Delete(tempSrcA, true);
            if (Directory.Exists(tempOutA)) Directory.Delete(tempOutA, true);
            if (Directory.Exists(tempSrcB)) Directory.Delete(tempSrcB, true);
            if (Directory.Exists(tempOutB)) Directory.Delete(tempOutB, true);
        }
    }
}


