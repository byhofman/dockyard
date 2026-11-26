using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

sealed class FakeHost : IEngineHost
{
    public bool Installed { get; set; }
    public bool Dashboard { get; set; }
    public bool RebootNeeded { get; set; }
    public Exception? Failure { get; set; }
    public Exception? FailureOnSetComponent { get; set; }
    public bool IgnoresDashboard { get; set; }
    public string SelfComponent { get; set; } = "DockyardCli";
    public Version Newest { get; set; } = new(1, 0, 0);
    public List<string> Calls { get; } = [];
    public List<InstallRequest> Requests { get; } = [];

    public IReadOnlyList<EngineInfo> Engines { get; } = [new("linux", "Linux", "Linux containers", "", "ctx", "unix:///x")];
    public IReadOnlyList<ComponentInfo> Components { get; init; } = [new("DockyardCli", "Dockyard CLI"), new("Tray", "Dockyard tray", Flag: "tray"), new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard")];
    public HashSet<string> Missing { get; } = [];
    public bool IsInstalled => Installed;
    public bool RunsFromInstallLocation => false;
    public bool HasDashboard => Dashboard;
    // The certificate authority goes in a folder of the test's own, never the user's.
    public string CaDirectory { get; } = Path.Combine(Path.GetTempPath(), "dockyard-tests-ca-" + Guid.NewGuid().ToString("N"));
    public Task<bool> TrustCa(Action<string> log, string certificateFile, bool silent) => Task.FromResult(true);

    public Func<string, Task<bool>>? Running { get; set; }
    public Task<bool> IsRunning(string engine) => Running?.Invoke(engine) ?? Task.FromResult(false);
    public Action<string>? StartedEngine { get; set; }
    public Task Start(string engine)
    {
        StartedEngine?.Invoke(engine);
        return Task.CompletedTask;
    }
    public Task Stop(string engine) => Task.CompletedTask;
    public string ActiveEngine() => "linux";
    public Task UseContext(string engine) => Task.CompletedTask;
    Task<Version?> IEngineHost.Installed(string component) => Task.FromResult<Version?>(Installed && !Missing.Contains(component) ? new Version(1, 0, 0) : null);

    public Task<bool> SetComponent(Action<string> log, string component, bool installed, bool removeData = false)
    {
        if (component == ComponentRules.DashboardId) return SetDashboard(log, installed);
        if (FailureOnSetComponent is not null) throw FailureOnSetComponent;
        Calls.Add($"{(installed ? "add" : "remove")} {component}");
        if (installed) Missing.Remove(component); else Missing.Add(component);
        return Task.FromResult(false);
    }
    public Task<Release?> Latest(string component) => Task.FromResult<Release?>(new Release(Newest, $"v{Newest}", null));
    public Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components)
    {
        Calls.Add("update " + string.Join(",", components.Select(c => c.Component)));
        return Task.FromResult(false);
    }

    // An install in phases, as the real hosts run one: each phase's marker, then a line of its own.
    public IReadOnlyList<string>? Phases { get; init; }
    public IReadOnlyList<string> InstallPlan(InstallRequest request) => Phases ?? ["Installing Dockyard"];

    public Task<bool> Install(Action<string> log, InstallRequest request)
    {
        foreach (var phase in Phases ?? [])
        {
            log(InstallPhases.Start(phase));
            log($"doing {phase}");
        }
        Calls.Add($"install dashboard={request.Dashboard} silent={request.Silent}");
        Requests.Add(request);
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

    public int TrayServicesStarted;
    public void StartTrayServices() => Interlocked.Increment(ref TrayServicesStarted);

    public void StartApp() => Calls.Add("start");
    public string DoneMessage(bool reboot) => reboot ? "reboot" : "done";

    public Task<bool> SetDashboard(Action<string> log, bool installed)
    {
        Calls.Add(installed ? "add" : "remove");
        Dashboard = installed;
        return Task.FromResult(false);
    }

    public Task Uninstall(Action<string> log, bool removeData, bool silent)
    {
        Calls.Add($"uninstall data={removeData}");
        Installed = false;
        Dashboard = false;
        return Task.CompletedTask;
    }

    public void ReleaseResources() { }
}

static class Facade
{
    // Over the test's own settings, and with the shell's profiles left alone.
    public static DockyardSetup For(IEngineHost host, TempSettings settings) =>
        new(host, settings.Settings, new ShellProfiles((_, _) => { }, () => []));
}
