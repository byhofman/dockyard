using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Tests;

sealed class FakeHost : IEngineHost
{
    public bool Installed { get; set; }
    public bool Dashboard { get; set; }
    public bool RebootNeeded { get; set; }
    public Exception? Failure { get; set; }
    public bool IgnoresDashboard { get; set; }
    public List<string> Calls { get; } = [];

    public IReadOnlyList<EngineInfo> Engines { get; } = [new("linux", "Linux", "Linux containers", "", "ctx", "unix:///x")];
    public IReadOnlyList<ComponentInfo> Components { get; } = [new("App", "Dockyard")];
    public bool IsInstalled => Installed;
    public bool RunsFromInstallLocation => false;
    public bool HasDashboard => Dashboard;

    public Task<bool> IsRunning(string engine) => Task.FromResult(false);
    public Task Start(string engine) => Task.CompletedTask;
    public Task Stop(string engine) => Task.CompletedTask;
    public string ActiveEngine() => "linux";
    public Task UseContext(string engine) => Task.CompletedTask;
    Task<Version?> IEngineHost.Installed(string component) => Task.FromResult<Version?>(Installed ? new Version(1, 0, 0) : null);
    public Task<Release?> Latest(string component) => Task.FromResult<Release?>(new Release(new Version(1, 0, 0), "v1.0.0", null));
    public Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components) => Task.FromResult(false);

    public Task<bool> Install(Action<string> log, InstallRequest request)
    {
        Calls.Add($"install dashboard={request.Dashboard} silent={request.Silent}");
        if (Failure is not null) throw Failure;
        Installed = true;
        if (!IgnoresDashboard) Dashboard = request.Dashboard;
        return Task.FromResult(RebootNeeded);
    }

    public Task Resume(Action<string> log)
    {
        Calls.Add("resume");
        return Task.CompletedTask;
    }

    public void StartApp() => Calls.Add("start");
    public string DoneMessage(bool reboot) => reboot ? "reboot" : "done";

    public Task<bool> SetDashboard(Action<string> log, bool installed)
    {
        Calls.Add(installed ? "add" : "remove");
        Dashboard = installed;
        return Task.FromResult(false);
    }

    public Task Uninstall(Action<string> log, bool removeData)
    {
        Calls.Add($"uninstall data={removeData}");
        Installed = false;
        Dashboard = false;
        return Task.CompletedTask;
    }

    public void ReleaseResources() { }
}

public sealed class InstallerTests
{
    readonly List<string> output = [];
    readonly List<string> errors = [];

    Task<int> Run(FakeHost host, params string[] args) =>
        Installer.RunSilent(host, SetupOptions.Parse(args), output.Add, errors.Add);

    [Fact]
    public async Task Fresh_silent_install_gets_the_dashboard_and_starts()
    {
        var host = new FakeHost();
        Assert.Equal(0, await Run(host, "--silent"));
        Assert.Equal(["install dashboard=True silent=True", "start"], host.Calls);
    }

    [Fact]
    public async Task No_dashboard_and_no_start_are_honoured()
    {
        var host = new FakeHost();
        Assert.Equal(0, await Run(host, "--silent", "--no-dashboard", "--no-start"));
        Assert.Equal(["install dashboard=False silent=True"], host.Calls);
        Assert.False(host.Dashboard);
    }

    [Fact]
    public async Task Rerun_over_an_install_without_the_dashboard_leaves_it_out()
    {
        var host = new FakeHost { Installed = true, Dashboard = false };
        Assert.Equal(0, await Run(host, "--silent", "--no-start"));
        Assert.Equal(["install dashboard=False silent=True"], host.Calls);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("uninstall", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rerun_over_an_install_with_the_dashboard_keeps_it()
    {
        var host = new FakeHost { Installed = true, Dashboard = true };
        Assert.Equal(0, await Run(host, "--silent", "--no-start"));
        Assert.True(host.Dashboard);
    }

    [Fact]
    public async Task A_skipped_dashboard_can_be_added_and_removed_again_silently()
    {
        var host = new FakeHost { Installed = true, Dashboard = false };
        Assert.Equal(0, await Run(host, "--silent", "--dashboard", "--no-start"));
        Assert.True(host.Dashboard);
        Assert.Equal(0, await Run(host, "--silent", "--no-dashboard", "--no-start"));
        Assert.False(host.Dashboard);
    }

    [Fact]
    public async Task Failure_exits_non_zero_with_the_reason()
    {
        var host = new FakeHost { Failure = new InvalidOperationException("Administrator approval was declined") };
        Assert.Equal(Installer.Failed, await Run(host, "--silent"));
        Assert.Equal(["ERROR: Administrator approval was declined"], errors);
        Assert.DoesNotContain("start", host.Calls);
    }

    [Fact]
    public async Task A_dashboard_that_did_not_arrive_is_a_failure()
    {
        var host = new FakeHost { IgnoresDashboard = true };
        Assert.Equal(Installer.Failed, await Run(host, "--silent", "--dashboard"));
        Assert.Contains("dashboard", errors[0]);
    }

    [Fact]
    public async Task A_needed_reboot_is_its_own_exit_code_and_does_not_start_the_app()
    {
        var host = new FakeHost { RebootNeeded = true };
        Assert.Equal(Installer.RebootRequired, await Run(host, "--silent"));
        Assert.DoesNotContain("start", host.Calls);
    }

    [Fact]
    public async Task Resume_finishes_without_installing_again()
    {
        var host = new FakeHost { Installed = true, Dashboard = true };
        Assert.Equal(0, await Run(host, "--silent", "--resume"));
        Assert.Equal(["resume", "start"], host.Calls);
    }

    [Fact]
    public async Task Uninstall_keeps_data_unless_asked()
    {
        var host = new FakeHost { Installed = true };
        Assert.Equal(0, await Run(host, "--uninstall"));
        Assert.Equal(["uninstall data=False"], host.Calls);
    }
}
