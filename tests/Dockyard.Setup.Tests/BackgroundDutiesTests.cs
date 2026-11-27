using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

public sealed class BackgroundDutiesTests : IDisposable
{
    readonly TempSettings settings = new();

    public void Dispose() => settings.Dispose();

    // Every component answers on the thread pool after a moment, so the checks of one run finish in parallel.
    sealed class ParallelHost : IEngineHost
    {
        public const int Count = 64;
        public List<(string Component, Release? Release)> Updated { get; } = [];
        public int LatestCalls;

        public IReadOnlyList<EngineInfo> Engines { get; } = [new("linux", "Linux", "Linux containers", "", "ctx", "unix:///x")];
        public IReadOnlyList<ComponentInfo> Components { get; } = Enumerable.Range(0, Count).Select(i => new ComponentInfo($"C{i}", $"Component {i}", Flag: $"c{i}")).ToList();
        public bool IsInstalled => true;
        public bool RunsFromInstallLocation => false;
        public bool HasDashboard => false;
        public Task<bool> IsRunning(string engine) => Task.FromResult(true);
        public Task Start(string engine) => Task.CompletedTask;
        public Task Stop(string engine) => Task.CompletedTask;
        public string ActiveEngine() => "linux";
        public Task UseContext(string engine) => Task.CompletedTask;
        public async Task<Version?> Installed(string component)
        {
            await Task.Delay(1).ConfigureAwait(false);
            return new Version(1, 0);
        }
        public async Task<Release?> Latest(string component)
        {
            Interlocked.Increment(ref LatestCalls);
            await Task.Delay(1).ConfigureAwait(false);
            var minor = int.Parse(component[1..]) + 1;
            return new Release(new Version(1, minor), $"v1.{minor}", null);
        }
        public Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components)
        {
            Updated.AddRange(components);
            return Task.FromResult(false);
        }
        public Task<bool> Install(Action<string> log, InstallRequest request) => Task.FromResult(false);
        public void StartApp() { }
        public string DoneMessage(bool reboot) => "";
        public Task<bool> SetDashboard(Action<string> log, bool installed) => Task.FromResult(false);
        public Task Uninstall(Action<string> log, bool removeData, bool silent) => Task.CompletedTask;
        public void ReleaseResources() { }
    }

    [Fact]
    public async Task Checks_running_at_once_keep_every_release_they_found()
    {
        var host = new ParallelHost();
        var setup = Facade.For(host, settings);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => setup.ComponentsAsync(CancellationToken.None))));
        var calls = host.LatestCalls;
        await setup.Update(host.Components.Select(c => c.Id).ToList(), new ProgressLines());

        Assert.Equal(calls, host.LatestCalls);
        Assert.Equal(ParallelHost.Count, host.Updated.Count);
        Assert.All(host.Updated, u => Assert.Equal(new Version(1, int.Parse(u.Component[1..]) + 1), u.Release!.Version));
    }

    [Fact]
    public async Task A_look_for_updates_that_fails_is_reported_and_the_next_one_still_runs()
    {
        var failures = 0;
        var host = new FakeHost
        {
            Installed = true,
            Components = [new("App", "Dockyard"), new("LinuxEngine", "Linux engine", CheckNeedsEngine: "linux")],
            Running = _ => Interlocked.Increment(ref failures) == 1 ? throw new IOException("the engine did not answer") : Task.FromResult(true),
        };
        settings.Settings.StartActiveAtLogon = false;
        settings.Settings.Save();
        var setup = Facade.For(host, settings);
        setup.Duties.Times = new BackgroundDuties.Timing(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        var progress = new ProgressLines();
        var looked = new TaskCompletionSource();
        setup.Duties.UpdatesFound += _ => looked.TrySetResult();
        using var stop = new CancellationTokenSource();

        await setup.Duties.Start(progress, stop.Token);
        await looked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        stop.Cancel();

        Assert.Contains(progress.All.ToList(), p => p.Kind == SetupProgressKind.Warning && p.Line.Contains("the engine did not answer"));
    }

    // Two processes are two facades over one lock name.
    (DockyardSetup Setup, FakeHost Host) Process(string lockName)
    {
        var host = new FakeHost { Installed = true };
        var setup = Facade.For(host, settings);
        setup.Duties.LockFile = lockName;
        setup.Duties.Times = new BackgroundDuties.Timing(TimeSpan.FromHours(6), TimeSpan.FromHours(6), TimeSpan.FromMilliseconds(10));
        return (setup, host);
    }

    string NewLockName() => Path.Combine(Path.GetTempPath(), "dockyard-tests-" + Guid.NewGuid().ToString("N") + ".lock");

    static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition was not met in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Only_one_process_runs_the_duties()
    {
        settings.Settings.StartActiveAtLogon = false;
        var name = NewLockName();
        var (first, firstHost) = Process(name);
        var (second, secondHost) = Process(name);
        using var stop = new CancellationTokenSource();

        var running = first.Duties.RunWhenFree(new ProgressLines(), startEngine: false, stop.Token);
        await Until(() => firstHost.TrayServicesStarted == 1);
        var waiting = second.Duties.RunWhenFree(new ProgressLines(), startEngine: false, stop.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(0, secondHost.TrayServicesStarted);
        Assert.Equal(1, firstHost.TrayServicesStarted);
        stop.Cancel();
        await Task.WhenAll(running, waiting);
    }

    [Fact]
    public async Task The_dashboard_takes_over_when_the_tray_quits()
    {
        settings.Settings.StartActiveAtLogon = false;
        var name = NewLockName();
        var (tray, trayHost) = Process(name);
        var (dashboard, dashboardHost) = Process(name);
        using var trayStop = new CancellationTokenSource();
        using var dashboardStop = new CancellationTokenSource();

        var trayRuns = tray.Duties.RunWhenFree(new ProgressLines(), startEngine: false, trayStop.Token);
        await Until(() => trayHost.TrayServicesStarted == 1);
        var dashboardRuns = dashboard.Duties.RunWhenFree(new ProgressLines(), startEngine: false, dashboardStop.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, dashboardHost.TrayServicesStarted);

        trayStop.Cancel();
        await trayRuns;
        await Until(() => dashboardHost.TrayServicesStarted == 1);

        dashboardStop.Cancel();
        await dashboardRuns;
    }

    [Fact]
    public async Task A_process_that_yields_to_the_tray_gives_the_duties_up_and_takes_them_back_when_the_tray_is_gone()
    {
        settings.Settings.StartActiveAtLogon = false;
        var name = NewLockName();
        var (dashboard, host) = Process(name);
        var trayRuns = false;
        using var stop = new CancellationTokenSource();

        var runs = dashboard.Duties.RunWhenFree(new ProgressLines(), startEngine: false, stop.Token, yieldTo: () => trayRuns);
        await Until(() => host.TrayServicesStarted == 1);
        trayRuns = true;
        await Until(() => ProcessLock.TryTake(name) is { } free && Release(free));
        trayRuns = false;
        await Until(() => host.TrayServicesStarted == 2);

        stop.Cancel();
        await runs;
    }

    static bool Release(ProcessLock taken)
    {
        taken.Dispose();
        return true;
    }

    [Fact]
    public async Task The_engine_starts_at_logon_only_where_the_duties_are_asked_to_start_it()
    {
        settings.Settings.StartActiveAtLogon = true;
        var (setup, host) = Process(NewLockName());
        var started = new TaskCompletionSource();
        host.StartedEngine = _ => started.TrySetResult();
        using var stop = new CancellationTokenSource();

        var runs = setup.Duties.RunWhenFree(new ProgressLines(), startEngine: false, stop.Token);
        await Until(() => host.TrayServicesStarted == 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(started.Task.IsCompleted);
        stop.Cancel();
        await runs;

        var (tray, trayHost) = Process(NewLockName());
        trayHost.StartedEngine = _ => started.TrySetResult();
        using var trayStop = new CancellationTokenSource();
        var trayRuns = tray.Duties.RunWhenFree(new ProgressLines(), startEngine: true, trayStop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        trayStop.Cancel();
        await trayRuns;
    }
}

public sealed class ProcessLockTests
{
    [Fact]
    public void A_lock_is_held_by_one_taker_until_it_is_let_go_and_the_tray_is_found_by_it()
    {
        var file = Path.Combine(Path.GetTempPath(), "dockyard-tests-" + Guid.NewGuid().ToString("N") + ".lock");
        Assert.False(ProcessLock.IsHeld(file));

        using (var first = ProcessLock.TryTake(file))
        {
            Assert.NotNull(first);
            Assert.True(ProcessLock.IsHeld(file));
            Assert.Null(ProcessLock.TryTake(file));
        }

        Assert.False(ProcessLock.IsHeld(file));
        using (var again = ProcessLock.TryTake(file)) Assert.NotNull(again);
        File.Delete(file);
    }
}

public sealed class DashboardCloseTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Closing_the_dashboard_hides_it_only_while_a_tray_runs(bool trayRunning, bool hides) =>
        Assert.Equal(hides, TrayInstance.ClosingHidesDashboard(trayRunning));
}
