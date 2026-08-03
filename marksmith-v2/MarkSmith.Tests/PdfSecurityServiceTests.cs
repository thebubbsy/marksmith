using System;
using System.IO;
using MarkSmith.Models;
using MarkSmith.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>Unit tests for the Task 18 PDF password protection + access-control engine.</summary>
public sealed class PdfSecurityServiceTests
{
    /// <summary>Creates a tiny valid single-page PDF in memory (no drawing needed).</summary>
    private static byte[] CreateMinimalPdf()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static AppSettings Settings(bool encrypt, string user = "", string owner = "",
        bool print = true, bool copy = true, bool modify = true)
        => new()
        {
            PdfEncrypt = encrypt,
            PdfUserPassword = user,
            PdfOwnerPassword = owner,
            PdfAllowPrinting = print,
            PdfAllowCopying = copy,
            PdfAllowModifying = modify,
        };

    // ---- BuildPolicy ---------------------------------------------------------------

    [Fact]
    public void BuildPolicy_ReturnsNull_WhenEncryptionDisabled()
    {
        Assert.Null(PdfSecurityService.BuildPolicy(Settings(encrypt: false, owner: "x")));
    }

    [Fact]
    public void BuildPolicy_ReturnsNull_ForNullSettings()
    {
        Assert.Null(PdfSecurityService.BuildPolicy(null));
    }

    [Fact]
    public void BuildPolicy_CarriesPasswords()
    {
        var p = PdfSecurityService.BuildPolicy(Settings(encrypt: true, user: "u", owner: "o"));
        Assert.NotNull(p);
        Assert.Equal("u", p!.UserPassword);
        Assert.Equal("o", p.OwnerPassword);
    }

    [Fact]
    public void BuildPolicy_AllAllowed_MapsToAllPermissions()
    {
        var p = PdfSecurityService.BuildPolicy(Settings(encrypt: true, owner: "o"));
        Assert.Equal(PdfPermissions.All, p!.Permissions);
    }

    [Fact]
    public void BuildPolicy_AllDisabled_MapsToNone()
    {
        var p = PdfSecurityService.BuildPolicy(Settings(encrypt: true, owner: "o", print: false, copy: false, modify: false));
        Assert.Equal(PdfPermissions.None, p!.Permissions);
    }

    [Fact]
    public void BuildPolicy_PrintingToggle_GrantsHighQualityToo()
    {
        var p = PdfSecurityService.BuildPolicy(Settings(encrypt: true, owner: "o", print: true, copy: false, modify: false));
        Assert.True(p!.Permissions.HasFlag(PdfPermissions.Printing));
        Assert.True(p.Permissions.HasFlag(PdfPermissions.HighQualityPrinting));
        Assert.False(p.Permissions.HasFlag(PdfPermissions.CopyContents));
    }

    // ---- ValidatePolicy ------------------------------------------------------------

    [Fact]
    public void ValidatePolicy_NoErrors_ForValidPolicy()
    {
        var p = new PdfSecurityPolicy { OwnerPassword = "secret" };
        Assert.Empty(PdfSecurityService.ValidatePolicy(p));
    }

    [Fact]
    public void ValidatePolicy_Errors_WhenNoPasswords()
    {
        var errors = PdfSecurityService.ValidatePolicy(new PdfSecurityPolicy());
        Assert.Single(errors);
        Assert.Contains("password is required", errors[0]);
    }

    [Fact]
    public void ValidatePolicy_Errors_WhenUserPasswordTooLong()
    {
        var p = new PdfSecurityPolicy { UserPassword = new string('a', 33) };
        Assert.Contains(PdfSecurityService.ValidatePolicy(p), e => e.Contains("user password"));
    }

    [Fact]
    public void ValidatePolicy_Errors_WhenOwnerPasswordTooLong()
    {
        var p = new PdfSecurityPolicy { OwnerPassword = new string('b', 33) };
        Assert.Contains(PdfSecurityService.ValidatePolicy(p), e => e.Contains("owner password"));
    }

    [Fact]
    public void ValidatePolicy_CountsBytes_NotCharacters()
    {
        // 20 multibyte chars = 40 UTF-8 bytes > the 32-byte PDF limit, despite being only 20 chars.
        var p = new PdfSecurityPolicy { OwnerPassword = new string('\u00e9', 20) };
        Assert.Contains(PdfSecurityService.ValidatePolicy(p), e => e.Contains("owner password"));
    }

    [Fact]
    public void ValidatePolicy_Accepts32BytePassword()
    {
        var p = new PdfSecurityPolicy { OwnerPassword = new string('a', 32) };
        Assert.Empty(PdfSecurityService.ValidatePolicy(p));
    }

    // ---- Policy flags --------------------------------------------------------------

    [Fact]
    public void Policy_HasPassword_ReflectsEitherPassword()
    {
        Assert.False(new PdfSecurityPolicy().HasPassword);
        Assert.True(new PdfSecurityPolicy { UserPassword = "u" }.HasPassword);
        Assert.True(new PdfSecurityPolicy { OwnerPassword = "o" }.HasPassword);
    }

    [Fact]
    public void Policy_IsProtected_TrueForRestrictedPermissionsAlone()
    {
        var p = new PdfSecurityPolicy { Permissions = PdfPermissions.Printing };
        Assert.False(p.HasPassword);
        Assert.True(p.IsProtected);
    }

    // ---- IsEncrypted ---------------------------------------------------------------

    [Fact]
    public void IsEncrypted_False_ForNullOrEmpty()
    {
        Assert.False(PdfSecurityService.IsEncrypted(null));
        Assert.False(PdfSecurityService.IsEncrypted(Array.Empty<byte>()));
    }

    [Fact]
    public void IsEncrypted_False_ForUnprotectedPdf()
    {
        Assert.False(PdfSecurityService.IsEncrypted(CreateMinimalPdf()));
    }

    // ---- Apply (round-trip via PDFsharp) -------------------------------------------

    [Fact]
    public void Apply_EncryptsPdf_AndMarkerAppears()
    {
        var pdf = CreateMinimalPdf();
        Assert.False(PdfSecurityService.IsEncrypted(pdf));

        var encrypted = PdfSecurityService.Apply(pdf, new PdfSecurityPolicy { UserPassword = "user", OwnerPassword = "owner" });

        Assert.True(PdfSecurityService.IsEncrypted(encrypted));
    }

    [Fact]
    public void Apply_OwnerPasswordReopens_WrongPasswordThrows()
    {
        var encrypted = PdfSecurityService.Apply(CreateMinimalPdf(),
            new PdfSecurityPolicy { UserPassword = "user", OwnerPassword = "owner" });

        using (var ok = new MemoryStream(encrypted))
        using (var doc = PdfReader.Open(ok, "owner", PdfDocumentOpenMode.ReadOnly))
        {
            Assert.Equal(1, doc.PageCount);
        }

        using var bad = new MemoryStream(encrypted);
        Assert.Throws<PdfReaderException>(() => PdfReader.Open(bad, "wrong", PdfDocumentOpenMode.ReadOnly));
    }

    [Fact]
    public void Apply_UserPasswordAlsoReopens()
    {
        var encrypted = PdfSecurityService.Apply(CreateMinimalPdf(),
            new PdfSecurityPolicy { UserPassword = "user", OwnerPassword = "owner" });

        using var ms = new MemoryStream(encrypted);
        using var doc = PdfReader.Open(ms, "user", PdfDocumentOpenMode.ReadOnly);
        Assert.Equal(1, doc.PageCount);
    }

    [Fact]
    public void Apply_RestrictsPrinting_WhenPermissionRemoved()
    {
        var policy = new PdfSecurityPolicy
        {
            OwnerPassword = "owner",
            Permissions = PdfPermissions.All & ~PdfPermissions.Printing & ~PdfPermissions.HighQualityPrinting,
        };
        var encrypted = PdfSecurityService.Apply(CreateMinimalPdf(), policy);

        using var ms = new MemoryStream(encrypted);
        using var doc = PdfReader.Open(ms, "owner", PdfDocumentOpenMode.ReadOnly);
        Assert.False(doc.SecuritySettings.PermitPrint);
        Assert.True(doc.SecuritySettings.PermitModifyDocument);
    }

    [Fact]
    public void Apply_Throws_ForEmptyBytes()
    {
        Assert.Throws<ArgumentException>(() =>
            PdfSecurityService.Apply(Array.Empty<byte>(), new PdfSecurityPolicy { OwnerPassword = "o" }));
    }

    [Fact]
    public void Apply_Throws_WhenNoPassword()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PdfSecurityService.Apply(CreateMinimalPdf(), new PdfSecurityPolicy()));
    }

    [Fact]
    public void ApplyToFile_EncryptsInPlace()
    {
        var path = Path.Combine(Path.GetTempPath(), "pdfsec_" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, CreateMinimalPdf());
            Assert.False(PdfSecurityService.IsEncrypted(File.ReadAllBytes(path)));

            PdfSecurityService.ApplyToFile(path, new PdfSecurityPolicy { OwnerPassword = "owner" });

            Assert.True(PdfSecurityService.IsEncrypted(File.ReadAllBytes(path)));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }
}
