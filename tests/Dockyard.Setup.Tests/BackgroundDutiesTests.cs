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
}
