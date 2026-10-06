using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace MarkSmith.Services;

/// <summary>
/// Decides whether a downloaded update installer may be launched, by Authenticode signature
/// continuity with the running app. Release signing switches on only once a certificate exists
/// (see release.yml), so a fixed thumbprint pin would break every update from an unsigned build:
/// <list type="bullet">
/// <item>a signature that is present but invalid (tampered bytes, untrusted chain) is always refused;</item>
/// <item>a signed running app only accepts a validly signed installer from the same publisher —
///       it never "updates" to an unsigned or differently-signed binary;</item>
/// <item>an unsigned running app accepts an unsigned installer (nothing to continue from yet).</item>
/// </list>
/// </summary>
internal static class InstallerTrust
{
    internal enum SignatureState { Unsigned, Valid, Invalid }

    internal static bool IsTrusted(string installerPath, string? runningExePath)
    {
        var installer = GetSignatureState(installerPath);
        var current = string.IsNullOrEmpty(runningExePath) ? SignatureState.Unsigned : GetSignatureState(runningExePath);
        return Decide(installer, current,
            installer == SignatureState.Valid ? SignerSubject(installerPath) : null,
            current == SignatureState.Valid ? SignerSubject(runningExePath!) : null);
    }

    /// <summary>The policy, separated from the Win32 calls so it can be tested exhaustively.</summary>
    internal static bool Decide(SignatureState installer, SignatureState current, string? installerSigner, string? currentSigner)
    {
        if (installer == SignatureState.Invalid) return false;
        if (current != SignatureState.Valid) return true;
        if (installer != SignatureState.Valid) return false;
        return !string.IsNullOrEmpty(currentSigner) && string.Equals(installerSigner, currentSigner, StringComparison.Ordinal);
    }

    internal static string? SignerSubject(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile has no X509CertificateLoader equivalent.
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch
        {
            return null;
        }
    }

    // ---- WinVerifyTrust ----

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

    internal static SignatureState GetSignatureState(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return SignatureState.Unsigned;

        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = path,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var marshalled = false;
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            marshalled = true;
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE — no network round-trip mid-update
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 1,         // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x10,        // WTD_REVOCATION_CHECK_NONE
            };
            var action = GenericVerifyV2;
            int result;
            try
            {
                result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
            finally
            {
                data.dwStateAction = 2;    // WTD_STATEACTION_CLOSE — release the state either way
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }

            return result switch
            {
                0 => SignatureState.Valid,
                TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => SignatureState.Unsigned,
                _ => SignatureState.Invalid,
            };
        }
        catch
        {
            // wintrust unavailable — treat as unsigned; Decide() still refuses a signed app's
            // update to an unverifiable installer.
            return SignatureState.Unsigned;
        }
        finally
        {
            if (marshalled) Marshal.DestroyStructure<WinTrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
