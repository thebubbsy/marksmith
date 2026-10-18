using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class CanaryTokenTests
{
    [Fact]
    public void IsOwnerMachine_RecognizesWhitelistedLaptop_AndRejectsAllOthers()
    {
        // 1. Exact owner laptop identity (DESKTOP-R0H12DJ + Tony + MachineGuid)
        Assert.True(CanaryTokenService.IsOwnerMachine(
            "DESKTOP-R0H12DJ",
            "Tony",
            "f7712729-7f6c-4307-9628-a026d9718782"));

        // Case-insensitivity check
        Assert.True(CanaryTokenService.IsOwnerMachine(
            "desktop-r0h12dj",
            "TONY",
            "F7712729-7F6C-4307-9628-A026D9718782"));

        // Fallback when MachineGuid is unavailable on the owner laptop
        Assert.True(CanaryTokenService.IsOwnerMachine(
            "DESKTOP-R0H12DJ",
            "Tony",
            ""));

        // 2. Spoofed hostname + username on a different PC with a different MachineGuid MUST fail (proc canary)
        Assert.False(CanaryTokenService.IsOwnerMachine(
            "DESKTOP-R0H12DJ",
            "Tony",
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        // 3. Different username on the same machine MUST fail (proc canary)
        Assert.False(CanaryTokenService.IsOwnerMachine(
            "DESKTOP-R0H12DJ",
            "Administrator",
            "f7712729-7f6c-4307-9628-a026d9718782"));

        // 4. Same username on any other machine MUST fail (proc canary)
        Assert.False(CanaryTokenService.IsOwnerMachine(
            "WORKSTATION-99",
            "Tony",
            "f7712729-7f6c-4307-9628-a026d9718782"));
    }

    [Fact]
    public async Task TriggerAsync_SuppressesOnOwnerMachine_AndProcsForExternalUser()
    {
        CanaryTokenService.ResetStateForTesting();
        CanaryTriggerReport? captured = null;
        CanaryTokenService.TransportOverride = (report, _) =>
        {
            captured = report;
            return Task.FromResult(true);
        };

        try
        {
            // 1. Owner identity -> suppressed, transport never called
            var ownerIdentity = new CanaryIdentity(
                MachineName: "DESKTOP-R0H12DJ",
                UserName: "Tony",
                UserDomainName: "DESKTOP-R0H12DJ",
                MachineGuid: "f7712729-7f6c-4307-9628-a026d9718782",
                OsDescription: "Windows 11",
                ProcessName: "Marksmith",
                ProcessId: 1234,
                CommandLine: "Marksmith.exe",
                WorkingDirectory: @"C:\MarkSmith");

            var ownerResult = await CanaryTokenService.TriggerAsync("Desktop.OnLaunched", "OwnerSession", ownerIdentity);
            Assert.True(ownerResult.WasSuppressedAsOwner);
            Assert.False(ownerResult.DispatchedSuccessfully);
            Assert.Null(captured);

            // 2. External user identity -> procs canary and dispatches alert for mbubbtech@gmail.com
            var externalIdentity = new CanaryIdentity(
                MachineName: "EXTERNAL-LAPTOP",
                UserName: "intruder",
                UserDomainName: "CORP",
                MachineGuid: "11111111-2222-3333-4444-555555555555",
                OsDescription: "Microsoft Windows 10.0.22631",
                ProcessName: "marksmith-cli",
                ProcessId: 9876,
                CommandLine: "marksmith convert secret.md",
                WorkingDirectory: @"C:\Users\intruder\Documents");

            var externalResult = await CanaryTokenService.TriggerAsync("Cli.Main", "convert secret.md", externalIdentity);
            Assert.False(externalResult.WasSuppressedAsOwner);
            Assert.True(externalResult.DispatchedSuccessfully);
            Assert.NotNull(captured);
            Assert.Equal("mbubbtech@gmail.com", captured!.RecipientEmail);
            Assert.Equal("ffpmaf6z0nn6ide3d9tv4yntp", captured.TokenId);
            Assert.Equal("EXTERNAL-LAPTOP", captured.MachineName);
            Assert.Equal("intruder", captured.UserName);
            Assert.Equal("Cli.Main", captured.Surface);
            Assert.Equal("convert secret.md", captured.Detail);
        }
        finally
        {
            CanaryTokenService.TransportOverride = null;
            CanaryTokenService.ResetStateForTesting();
        }
    }
}
