using System.ComponentModel;
using System.Runtime.Versioning;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

[SupportedOSPlatform("windows")]
public sealed class ElevationTests
{
    [Fact]
    public void The_worker_is_the_installed_CLI_and_else_this_executable()
    {
        Assert.Equal(Paths.DockyardCliExe, Elevation.WorkerExe(path => path == Paths.DockyardCliExe));
        Assert.Equal(Paths.Self, Elevation.WorkerExe(_ => false));
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
}
