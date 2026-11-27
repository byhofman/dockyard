using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

// Each host's install as steps a person recognises, leaving out what the request skips, and a first install walking
// through them in order.
public sealed class InstallPhaseTests : IDisposable
{
    readonly TempSettings settings = new();
    readonly ProgressLines progress = new();

    public void Dispose() => settings.Dispose();

    static InstallRequest Request(bool https = false, params string[] without) => new(Dashboard: true, TrustHttps: https) { Without = without };

#pragma warning disable CA1416 // the plans are data on every platform
    [Fact]
    public void Windows_plans_its_machine_half_then_the_user_s()
    {
        var host = new WindowsHost(new FakeRunner());
        Assert.Equal(
            ["Windows features and WSL", "The Windows engine", "docker CLI and plugins", "Kubernetes tools", "Dockyard", "Container domains", "The Linux engine", "Trusted HTTPS"],
            ((IEngineHost)host).InstallPlan(Request(https: true)));
        // Without the Linux engine: no WSL and none of its domains.
        Assert.Equal(
            ["Windows features", "The Windows engine", "docker CLI and plugins", "Kubernetes tools", "Dockyard"],
            ((IEngineHost)host).InstallPlan(Request(false, nameof(Component.LinuxEngine))));
        Assert.Equal(
            ["WSL", "docker CLI and plugins", "Dockyard", "Container domains", "The Linux engine"],
            ((IEngineHost)host).InstallPlan(Request(false, nameof(Component.Engine), "Kind", "Kubectl", "Helm")));
    }

    [Fact]
    public void A_Mac_plans_the_app_the_tools_the_administrator_step_and_the_VM()
    {
        var host = new MacHost(new FakeRunner());
        Assert.Equal(["Dockyard", "docker and the Kubernetes tools", "Administrator step", "The Linux VM"], ((IEngineHost)host).InstallPlan(Request()));
        Assert.Equal("Trusted HTTPS", ((IEngineHost)host).InstallPlan(Request(https: true))[^1]);
    }

    [Fact]
    public void Linux_plans_the_app_the_engine_and_the_tools()
    {
        var host = new LinuxHost(new FakeRunner());
        Assert.Equal(["Dockyard", "Docker Engine and its packages", "Kubernetes tools"], ((IEngineHost)host).InstallPlan(Request()));
        Assert.Equal(["Dockyard", "Docker Engine and its packages", "Trusted HTTPS"], ((IEngineHost)host).InstallPlan(Request(true, "Kind", "Kubectl", "Helm")));
    }
#pragma warning restore CA1416

    [Fact]
    public void A_phase_marker_reads_back_as_its_phase_and_nothing_else_does()
    {
        Assert.Equal("The Linux VM", InstallPhases.Read(InstallPhases.Start("The Linux VM")));
        Assert.Null(InstallPhases.Read("Installed /opt/homebrew/bin/docker (28.5.1)"));
    }

    [Fact]
    public async Task A_first_install_walks_through_the_host_s_steps_in_order()
    {
        var host = new FakeHost { Phases = ["Dockyard", "The Linux VM", "Administrator step"] };
        var setup = Facade.For(host, settings);

        Assert.Equal(DockyardSetup.Ok, await setup.Setup(new SetupRequest([], [], null, false, false, Silent: true, Resume: false), progress, CancellationToken.None));

        var steps = progress.All.Where(p => p.Step is not null).ToList();
        Assert.Equal(["Dockyard", "The Linux VM", "Administrator step", "Starting up"], steps[0].Plan);
        Assert.Equal(["Dockyard", "The Linux VM", "Administrator step", "Starting up"], steps.Select(s => s.Line));
        Assert.Equal([1, 2, 3, 4], steps.Select(s => s.Step!.Value));
        Assert.All(steps, s => Assert.Equal(4, s.Steps));
        // The host's own lines land under the step it was in, and no marker is shown as a line.
        var lines = progress.All.Select(p => p.Line).ToList();
        Assert.True(lines.IndexOf("doing The Linux VM") > lines.IndexOf("The Linux VM"));
        Assert.True(lines.IndexOf("doing The Linux VM") < lines.IndexOf("Administrator step"));
        Assert.DoesNotContain(progress.All, p => p.Line.StartsWith("[dockyard-step]", StringComparison.Ordinal));
    }
}
