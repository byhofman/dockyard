using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

public sealed class InstallerTests : IDisposable
{
    readonly List<string> output = [];
    readonly List<string> errors = [];
    readonly TempSettings settings = new();

    public void Dispose() => settings.Dispose();

    static SetupRequest Silent(IReadOnlyList<string>? with = null, IReadOnlyList<string>? without = null, bool start = true, bool resume = false) =>
        new(with ?? [], without ?? [], null, TrustHttps: false, KubectlShell: false, Silent: true, resume) { Start = start };

    static readonly string[] Dashboard = ["dashboard"];

    async Task<int> Run(FakeHost host, SetupRequest request)
    {
        var progress = new ProgressLines();
        var code = await Facade.For(host, settings).Setup(request, progress, CancellationToken.None);
        output.AddRange(progress.All.Where(p => p.Kind != SetupProgressKind.Error).Select(p => p.Line));
        errors.AddRange(progress.All.Where(p => p.Kind == SetupProgressKind.Error).Select(p => p.Line));
        return code;
    }

    [Fact]
    public async Task Fresh_silent_install_gets_the_dashboard_and_starts()
    {
        var host = new FakeHost();
        Assert.Equal(0, await Run(host, Silent()));
        Assert.Equal(["install dashboard=True silent=True", "start"], host.Calls);
    }

    [Fact]
    public async Task No_dashboard_and_no_start_are_honoured()
    {
        var host = new FakeHost();
        Assert.Equal(0, await Run(host, Silent(without: Dashboard, start: false)));
        Assert.Equal(["install dashboard=False silent=True"], host.Calls);
        Assert.False(host.Dashboard);
    }

    [Fact]
    public async Task Rerun_over_an_install_without_the_dashboard_leaves_it_out()
    {
        var host = new FakeHost { Installed = true, Dashboard = false };
        Assert.Equal(0, await Run(host, Silent(start: false)));
        Assert.Equal(["install dashboard=False silent=True"], host.Calls);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("uninstall", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rerun_over_an_install_with_the_dashboard_keeps_it()
    {
        var host = new FakeHost { Installed = true, Dashboard = true };
        Assert.Equal(0, await Run(host, Silent(start: false)));
        Assert.True(host.Dashboard);
    }

    [Fact]
    public async Task A_skipped_dashboard_can_be_added_and_removed_again_silently()
    {
        var host = new FakeHost { Installed = true, Dashboard = false };
        Assert.Equal(0, await Run(host, Silent(with: Dashboard, start: false)));
        Assert.True(host.Dashboard);
        Assert.Equal(0, await Run(host, Silent(without: Dashboard, start: false)));
        Assert.False(host.Dashboard);
    }

    [Fact]
    public async Task Failure_exits_non_zero_with_the_reason()
    {
        var host = new FakeHost { Failure = new InvalidOperationException("Administrator approval was declined") };
        Assert.Equal(DockyardSetup.Failed, await Run(host, Silent()));
        Assert.Equal(["Administrator approval was declined"], errors);
        Assert.DoesNotContain("start", host.Calls);
    }

    [Fact]
    public async Task A_dashboard_that_did_not_arrive_is_a_failure()
    {
        var host = new FakeHost { IgnoresDashboard = true };
        Assert.Equal(DockyardSetup.Failed, await Run(host, Silent(with: Dashboard)));
        Assert.Contains("dashboard", errors[0]);
    }

    [Fact]
    public async Task A_needed_reboot_is_its_own_exit_code_and_does_not_start_the_app()
    {
        var host = new FakeHost { RebootNeeded = true };
        Assert.Equal(DockyardSetup.RebootRequired, await Run(host, Silent()));
        Assert.DoesNotContain("start", host.Calls);
    }

    [Fact]
    public async Task Resume_finishes_without_installing_again()
    {
        var host = new FakeHost { Installed = true, Dashboard = true };
        Assert.Equal(0, await Run(host, Silent(resume: true)));
        Assert.Equal(["resume", "start"], host.Calls);
    }

    [Fact]
    public async Task Uninstall_keeps_data_unless_asked()
    {
        var host = new FakeHost { Installed = true };
        await Facade.For(host, settings).Uninstall(false, new ProgressLines(), silent: true);
        Assert.Equal(["uninstall data=False"], host.Calls);
    }
}
