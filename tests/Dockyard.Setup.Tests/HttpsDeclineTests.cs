using System.ComponentModel;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class HttpsDeclineTests : IDisposable
{
    readonly TempSettings settings = new();
    readonly FakeHost host = new() { Installed = true, TrustFailure = Elevation.Explain(new Win32Exception(1223, "The operation was canceled by the user")) };

    public void Dispose()
    {
        settings.Dispose();
        if (Directory.Exists(host.CaDirectory)) Directory.Delete(host.CaDirectory, recursive: true);
    }

    [Fact]
    public async Task In_setup_a_declined_trust_is_a_warning_and_leaves_no_certificate_authority()
    {
        List<string> lines = [];

        await Https.EnableInSetup(host, settings.Settings, lines.Add, silent: false);

        Assert.False(Settings.Load(settings.File).HttpsEnabled);
        Assert.False(Directory.Exists(host.CaDirectory) && File.Exists(LocalCa.CertificateFile(host.CaDirectory)));
        Assert.Contains(lines, l => InstallPhases.ReadWarning(l) is { } warning && warning.Contains(Elevation.Declined));
    }

    [Fact]
    public async Task A_setup_whose_trust_is_declined_still_exits_0_with_the_warning()
    {
        var progress = new ProgressLines();
        var setup = Facade.For(new FakeHost { InstallLines = [InstallPhases.Warning("Trusted HTTPS stays off: Administrator approval was declined")] }, settings);

        var code = await setup.Setup(new SetupRequest([], [], null, TrustHttps: true, false, Silent: false, Resume: false) { Start = false }, progress, CancellationToken.None);

        Assert.Equal(DockyardSetup.Ok, code);
        Assert.Contains(progress.All, p => p.Kind == SetupProgressKind.Warning && p.Line.Contains("Administrator approval was declined"));
    }

    [Fact]
    public async Task Turned_on_as_a_setting_a_declined_trust_still_fails_and_leaves_no_certificate_authority()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Https.Enable(host, settings.Settings, _ => { }, silent: false));
        Assert.False(File.Exists(LocalCa.CertificateFile(host.CaDirectory)));
    }
}
