using System.IO;
using MarkSmith.Services;
using Xunit;
using S = MarkSmith.Services.InstallerTrust.SignatureState;

namespace MarkSmith.Tests;

// The updater used to launch any downloaded file that merely looked like an executable. It now
// requires Authenticode continuity with the running app (see InstallerTrust for the policy).
public class InstallerTrustTests
{
    private const string Pub = "CN=MarkSmith";

    [Theory]
    // A broken signature is never run, whatever the running app is.
    [InlineData("Invalid", "Unsigned", null, null, false)]
    [InlineData("Invalid", "Valid", null, Pub, false)]
    // Unsigned app (pre-certificate releases): accepts unsigned or validly signed installers.
    [InlineData("Unsigned", "Unsigned", null, null, true)]
    [InlineData("Valid", "Unsigned", Pub, null, true)]
    // Signed app: same publisher only — never "updates" to unsigned or someone else's binary.
    [InlineData("Valid", "Valid", Pub, Pub, true)]
    [InlineData("Unsigned", "Valid", null, Pub, false)]
    [InlineData("Valid", "Valid", "CN=Someone Else", Pub, false)]
    [InlineData("Valid", "Valid", null, null, false)]
    public void Policy(string installer, string current, string? installerSigner, string? currentSigner, bool trusted)
    {
        Assert.Equal(trusted, InstallerTrust.Decide(Enum.Parse<S>(installer), Enum.Parse<S>(current), installerSigner, currentSigner));
    }

    // dotnet.exe carries an embedded Microsoft Authenticode signature on every machine with the SDK
    // (system binaries like cmd.exe are catalog-signed, which a file-only check doesn't see).
    private static string? SignedSample()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
            Environment.ProcessPath ?? "",
        };
        return candidates.FirstOrDefault(p => File.Exists(p) && InstallerTrust.GetSignatureState(p) == S.Valid);
    }

    [Fact]
    public void Real_signatures_are_classified()
    {
        if (!OperatingSystem.IsWindows()) return;
        var signed = SignedSample();
        if (signed is null) return; // no embedded-signed sample on this machine

        Assert.Equal(S.Valid, InstallerTrust.GetSignatureState(signed));
        Assert.Contains("Microsoft", InstallerTrust.SignerSubject(signed) ?? "");

        var dir = Directory.CreateTempSubdirectory("ms-trust-").FullName;
        try
        {
            // Tampered: flip one byte well inside the signed image — the hash no longer matches.
            var tampered = Path.Combine(dir, "tampered.exe");
            var bytes = File.ReadAllBytes(signed);
            bytes[bytes.Length / 3] ^= 0xFF;
            File.WriteAllBytes(tampered, bytes);
            Assert.Equal(S.Invalid, InstallerTrust.GetSignatureState(tampered));
            Assert.False(InstallerTrust.IsTrusted(tampered, runningExePath: null));

            // Unsigned: a minimal PE-shaped file with no certificate table.
            var unsigned = Path.Combine(dir, "unsigned.exe");
            var pe = new byte[512];
            pe[0] = (byte)'M'; pe[1] = (byte)'Z';
            BitConverter.GetBytes(0x80).CopyTo(pe, 0x3C);
            pe[0x80] = (byte)'P'; pe[0x81] = (byte)'E';
            File.WriteAllBytes(unsigned, pe);
            Assert.NotEqual(S.Valid, InstallerTrust.GetSignatureState(unsigned));

            // A signed "running app" refuses that unsigned installer; an unsigned one accepts it.
            Assert.False(InstallerTrust.IsTrusted(unsigned, runningExePath: signed));
            Assert.Equal(InstallerTrust.GetSignatureState(unsigned) == S.Unsigned,
                InstallerTrust.IsTrusted(unsigned, runningExePath: null));
            // Same publisher on both sides is accepted.
            Assert.True(InstallerTrust.IsTrusted(signed, runningExePath: signed));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Missing_file_is_unsigned_not_a_crash()
    {
        Assert.Equal(S.Unsigned, InstallerTrust.GetSignatureState(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));
    }
}
