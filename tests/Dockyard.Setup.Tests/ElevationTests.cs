using System.ComponentModel;
using System.Runtime.Versioning;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

[SupportedOSPlatform("windows")]
public sealed class ElevationTests
{
    [Theory]
    [InlineData("Cli")]
    [InlineData("Dashboard")]
    public void Either_app_is_its_own_worker_installed_or_not(string app)
    {
        var running = Enum.Parse<RunningApp>(app);
        Assert.Equal(Paths.Self, Elevation.WorkerExe(running, _ => true));
        Assert.Equal(Paths.Self, Elevation.WorkerExe(running, _ => false));
    }

    [Theory]
    [InlineData("Tray")]
    [InlineData("Other")]
    public void Anything_else_asks_whichever_app_is_installed(string app)
    {
        var running = Enum.Parse<RunningApp>(app);
        Assert.Equal(Paths.DockyardCliExe, Elevation.WorkerExe(running, _ => true));
        Assert.Equal(Paths.DockyardCliExe, Elevation.WorkerExe(running, path => path == Paths.DockyardCliExe));
        Assert.Equal(Paths.DashboardExe, Elevation.WorkerExe(running, path => path == Paths.DashboardExe));
        Assert.Equal(Paths.Self, Elevation.WorkerExe(running, _ => false));
    }

    [Fact]
    public void The_worker_reads_the_source_and_the_assets_as_setup_does()
    {
        var assets = Directory.CreateTempSubdirectory("dockyard-worker-").FullName;
        try
        {
            var (source, folder) = Elevation.WorkerOptions(["--machine-install", "--source", "dockyard", "--assets", assets, "--log", "x", "--keep-pid", "1"]);
            Assert.Equal(ToolSource.Dockyard, source);
            Assert.Equal(Path.GetFullPath(assets), folder);
            Assert.Equal((ToolSource.PackageManager, (string?)null), Elevation.WorkerOptions(["--machine-uninstall", "--log", "x"]));
        }
        finally { Directory.Delete(assets); }
    }

    [Fact]
    public void The_installed_CLI_is_where_the_system_keeps_commands()
    {
        var expected = OperatingSystem.IsWindows() ? Path.Combine(Paths.AppDir, "bin", "dockyard.exe")
            : OperatingSystem.IsMacOS() ? "/usr/local/bin/dockyard"
            : "/usr/bin/dockyard";
        Assert.Equal(expected, Paths.DockyardCliExe);
    }

    [Fact]
    public async Task A_declined_prompt_reads_administrator_approval_was_declined()
    {
        var declined = Elevation.Explain(new Win32Exception(1223, "The operation was canceled by the user"));
        Assert.Equal("Administrator approval was declined", declined.Message);
        Assert.Contains("run this from an elevated shell", Elevation.Explain(new Win32Exception(5, "Access is denied")).Message);

        var host = new FakeHost { Installed = true, Components = [new("Compose", "Docker Compose", Flag: "compose")], FailureOnSetComponent = declined };
        using var settings = new TempSettings();
        var setup = Facade.For(host, settings);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Add("compose", new ProgressLines()));
        Assert.Equal("Administrator approval was declined", refused.Message);
    }

    [Fact]
    public async Task Trusting_the_certificate_throws_when_its_approval_is_declined_and_says_false_for_any_other_failure()
    {
        var declined = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsSetup.Trusting(() => throw Elevation.Explain(new Win32Exception(1223, "The operation was canceled by the user"))));
        Assert.Equal("Administrator approval was declined", declined.Message);

        Assert.False(await WindowsSetup.Trusting(() => throw new System.Security.Cryptography.CryptographicException("bad")));
        Assert.True(await WindowsSetup.Trusting(() => Task.CompletedTask));
    }

    [Fact]
    public async Task A_declined_trust_leaves_trusted_HTTPS_off_and_says_why()
    {
        var host = new FakeHost { Installed = true, TrustFailure = Elevation.Explain(new Win32Exception(1223, "The operation was canceled by the user")) };
        using var settings = new TempSettings();
        var setup = Facade.For(host, settings);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.SetSetting(SettingDefinitions.TrustedHttps, SettingDefinitions.On, new ProgressLines()));

        Assert.Equal("Administrator approval was declined", refused.Message);
        Assert.Equal(SettingDefinitions.Off, setup.GetSetting(SettingDefinitions.TrustedHttps));
        Assert.False(File.Exists(LocalCa.CertificateFile(host.CaDirectory)));
    }
}
