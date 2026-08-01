using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MdToPdf.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace MdToPdf.Services;

/// <summary>
/// Access-control permissions that can be granted to a password-protected PDF (Task 18). Mirrors the
/// PDF 32000-1 standard security handler's permission categories; each flag maps to a PDFsharp
/// <c>Permit*</c> setting when the policy is applied.
/// </summary>
[Flags]
public enum PdfPermissions
{
    None = 0,
    Printing = 1 << 0,
    HighQualityPrinting = 1 << 1,
    ModifyContents = 1 << 2,
    CopyContents = 1 << 3,
    ModifyAnnotations = 1 << 4,
    FillForms = 1 << 5,
    ExtractForAccessibility = 1 << 6,
    Assemble = 1 << 7,

    /// <summary>Everything allowed — the effective default for an unprotected export.</summary>
    All = Printing | HighQualityPrinting | ModifyContents | CopyContents
        | ModifyAnnotations | FillForms | ExtractForAccessibility | Assemble,
}

/// <summary>
/// A resolved PDF security policy: who can open it (user password), who can change the rules (owner
/// password), and what readers may do (permissions).
/// </summary>
public sealed record PdfSecurityPolicy
{
    public string UserPassword { get; init; } = "";
    public string OwnerPassword { get; init; } = "";
    public PdfPermissions Permissions { get; init; } = PdfPermissions.All;

    /// <summary>True when a password is present (the only case encryption can actually be applied).</summary>
    public bool HasPassword => UserPassword.Length > 0 || OwnerPassword.Length > 0;

    /// <summary>True when any protection (password or restricted permission) is requested.</summary>
    public bool IsProtected => HasPassword || Permissions != PdfPermissions.All;
}

/// <summary>
/// Applies optional password protection and access-control permissions to a generated PDF (Task 18).
/// The Chromium print pipeline emits an unprotected PDF; this service post-processes those bytes with
/// PDFsharp's standard security handler so the exported file can require a password to open and can
/// forbid printing / copying / modifying.
/// </summary>
public static class PdfSecurityService
{
    // The PDF standard security handler truncates passwords to 32 bytes (padding with a fixed pad
    // string). Longer passwords silently lose their tail, so we reject them up front rather than
    // protect the document with a password the user can't reproduce.
    public const int MaxPasswordBytes = 32;

    /// <summary>
    /// Builds a policy from settings, or null when encryption is disabled. The three "allow" toggles
    /// map onto the granular permission flags (printing also grants high-quality printing, etc.).
    /// When permissions are restricted but no password is supplied, an owner password is auto-generated
    /// so the restrictions are actually enforced (the PDF opens freely but the permissions stick).
    /// </summary>
    public static PdfSecurityPolicy? BuildPolicy(AppSettings? settings)
    {
        if (settings == null || !settings.PdfEncrypt) return null;

        var perms = PdfPermissions.None;
        if (settings.PdfAllowPrinting) perms |= PdfPermissions.Printing | PdfPermissions.HighQualityPrinting;
        if (settings.PdfAllowCopying) perms |= PdfPermissions.CopyContents | PdfPermissions.ExtractForAccessibility;
        if (settings.PdfAllowModifying) perms |= PdfPermissions.ModifyContents | PdfPermissions.ModifyAnnotations | PdfPermissions.FillForms | PdfPermissions.Assemble;

        var user = settings.PdfUserPassword ?? "";
        var owner = settings.PdfOwnerPassword ?? "";

        // The PDF standard security handler requires an owner password to enforce permissions.
        // If the user restricted permissions but left both passwords blank, generate a random owner
        // password so the restrictions actually apply (the document still opens without a user password).
        if (user.Length == 0 && owner.Length == 0 && perms != PdfPermissions.All)
            owner = Guid.NewGuid().ToString("N")[..16];

        return new PdfSecurityPolicy
        {
            UserPassword = user,
            OwnerPassword = owner,
            Permissions = perms,
        };
    }

    /// <summary>
    /// Validates a policy, returning human-readable errors (empty when valid). A password is required
    /// to apply protection, and each password must fit the standard handler's 32-byte limit.
    /// </summary>
    public static IReadOnlyList<string> ValidatePolicy(PdfSecurityPolicy? policy)
    {
        var errors = new List<string>();
        if (policy == null) return errors;

        if (!policy.HasPassword)
            errors.Add("A user or owner password is required to protect the PDF.");
        if (Encoding.UTF8.GetByteCount(policy.UserPassword) > MaxPasswordBytes)
            errors.Add($"The user password exceeds the {MaxPasswordBytes}-byte PDF limit.");
        if (Encoding.UTF8.GetByteCount(policy.OwnerPassword) > MaxPasswordBytes)
            errors.Add($"The owner password exceeds the {MaxPasswordBytes}-byte PDF limit.");

        return errors;
    }

    /// <summary>
    /// Returns the protected PDF bytes for the given policy. Requires at least one password (see
    /// <see cref="PdfSecurityPolicy.HasPassword"/>); call <see cref="ValidatePolicy"/> first for
    /// friendly errors.
    /// </summary>
    public static byte[] Apply(byte[] pdfBytes, PdfSecurityPolicy policy)
    {
        if (pdfBytes == null || pdfBytes.Length == 0)
            throw new ArgumentException("No PDF bytes supplied.", nameof(pdfBytes));
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        if (!policy.HasPassword)
            throw new InvalidOperationException("A user or owner password is required to encrypt the PDF.");

        using var input = new MemoryStream(pdfBytes);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);

        var s = doc.SecuritySettings;
        if (policy.OwnerPassword.Length > 0) s.OwnerPassword = policy.OwnerPassword;
        if (policy.UserPassword.Length > 0) s.UserPassword = policy.UserPassword;
        s.PermitPrint = policy.Permissions.HasFlag(PdfPermissions.Printing);
        s.PermitFullQualityPrint = policy.Permissions.HasFlag(PdfPermissions.HighQualityPrinting);
        s.PermitModifyDocument = policy.Permissions.HasFlag(PdfPermissions.ModifyContents);
        // PDFsharp folds accessibility extraction into the general content-extraction bit.
        s.PermitExtractContent = policy.Permissions.HasFlag(PdfPermissions.CopyContents)
            || policy.Permissions.HasFlag(PdfPermissions.ExtractForAccessibility);
        s.PermitAnnotations = policy.Permissions.HasFlag(PdfPermissions.ModifyAnnotations);
        s.PermitFormsFill = policy.Permissions.HasFlag(PdfPermissions.FillForms);
        s.PermitAssembleDocument = policy.Permissions.HasFlag(PdfPermissions.Assemble);

        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }

    /// <summary>Encrypts a PDF file in place (reads, applies the policy, rewrites the same path).</summary>
    public static void ApplyToFile(string path, PdfSecurityPolicy policy)
    {
        var encrypted = Apply(File.ReadAllBytes(path), policy);
        File.WriteAllBytes(path, encrypted);
    }

    /// <summary>
    /// Cheap, password-free check for whether a PDF carries an encryption dictionary — the standard
    /// security handler adds an <c>/Encrypt</c> entry to the trailer, so scanning the raw bytes is
    /// enough to confirm protection was applied.
    /// </summary>
    public static bool IsEncrypted(byte[]? pdf)
    {
        if (pdf == null || pdf.Length == 0) return false;
        return IndexOfAscii(pdf, "/Encrypt") >= 0;
    }

    private static int IndexOfAscii(byte[] haystack, string needle)
    {
        var n = needle.Length;
        var last = haystack.Length - n;
        for (var i = 0; i <= last; i++)
        {
            var j = 0;
            while (j < n && haystack[i + j] == (byte)needle[j]) j++;
            if (j == n) return i;
        }
        return -1;
    }
}
