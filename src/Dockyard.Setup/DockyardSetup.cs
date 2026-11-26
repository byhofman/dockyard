using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dockyard.Core;
using Dockyard.Engine;
using Dockyard.Engine.Transport;
using Dockyard.Hosts;
using Dockyard.Kind;

namespace Dockyard.Setup;

// The kubectl block in the shell's profiles: written or taken out, and which profiles did not get it.
internal sealed record ShellProfiles(Action<bool, Action<string>> Apply, Func<IReadOnlyList<string>> Missing)
{
    public static ShellProfiles User { get; } = new(ShellSetup.TryApply, () => ShellSetup.Missing().ToList());
}

public sealed record UpdateStatus(ComponentRow Row, string? Error);

// Everything a front end can do with Dockyard's install, its components, its settings and its engines. A front end parses its
// input, calls this and shows the progress; what to install, in which order and what a setting does is decided here.
// An argument that names nothing here throws ArgumentException; a request this machine refuses, InvalidOperationException.
public sealed partial class DockyardSetup : IDisposable
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int WrongArguments = 2;
    public const int RebootRequired = 3010;

    readonly IEngineHost host;
    readonly Settings settings;
    readonly ShellProfiles shell;
    // The releases the last check found, which an update installs.
    readonly ConcurrentDictionary<string, Release?> newest = new();

    internal DockyardSetup(IEngineHost host, Settings settings, ShellProfiles? shell = null)
    {
        this.host = host;
        this.settings = settings;
        this.shell = shell ?? ShellProfiles.User;
        Updates.InstalledCli = () => host.Installed(nameof(Component.DockyardCli));
        Duties = new BackgroundDuties(this, host, settings);
        ClusterAgent = () => AgentClient.For(DockerEndpoint.Parse(host.Engines[0].Endpoint));
    }

    public static DockyardSetup ForThisMachine() => new(HostFactory.Create(), Settings.Load());

    // Dockyard itself is being replaced, and the process that asked should exit.
    public event Action? ExitRequested;

    // The elevated half of an install, which Windows starts as this program with --machine-*.
    public static bool IsElevatedWorker(string[] args) => OperatingSystem.IsWindows() && Elevation.IsWorker(args);
    public static int RunElevatedWorker(string[] args) =>
        OperatingSystem.IsWindows() ? Elevation.Worker(args) : throw new PlatformNotSupportedException("The elevated step is for Windows");

    public const string DashboardFlag = "dashboard";
    public const string TrayFlag = "tray";

    // A person can be asked here: the input and the output are a terminal.
    public static bool HasTerminal => Terminal.IsAttached;

    public bool IsInstalled => host.IsInstalled;
    public bool HasDashboard => host.HasDashboard;
    public string? DashboardExe => host.DashboardExe;
    public BackgroundDuties Duties { get; }

    // Releases what this process holds of an engine, such as the macOS VM it started.
    public void Dispose() => host.ReleaseResources();

    // Engines

    public IReadOnlyList<EngineInfo> Engines => host.Engines;
    public Task<bool> IsRunning(string id) => host.IsRunning(Engine(id).Id);

    // Outside the tray, which holds the Linux engine's relay on Windows, an engine runs when its endpoint answers.
    public async Task<bool> IsAnswering(string id)
    {
        var engine = Engine(id);
        if (await host.IsRunning(engine.Id)) return true;
        try
        {
            using var client = new Dockyard.Engine.DockerClient(Dockyard.Engine.Transport.DockerEndpoint.Parse(engine.Endpoint));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.PingAsync(timeout.Token);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or TimeoutException or Dockyard.Engine.EngineUnavailableException) { return false; }
    }
    public string ActiveEngine() => host.ActiveEngine();
    public Task StartEngine(string id) => host.Start(Engine(id).Id);
    public Task StopEngine(string id) => host.Stop(Engine(id).Id);

    // Starts it and makes it docker's context, and stops the others when the setting says so.
    public async Task UseEngine(string id)
    {
        var engine = Engine(id);
        await host.Start(engine.Id);
        await host.UseContext(engine.Id);
        if (settings.StopOtherOnSwitch)
            foreach (var other in host.Engines.Where(e => e.Id != engine.Id)) await host.Stop(other.Id);
    }

    EngineInfo Engine(string id) => host.Engines.FirstOrDefault(e => e.Id == id)
        ?? throw new ArgumentException($"No engine {id} here. Engines: {string.Join(", ", host.Engines.Select(e => e.Id))}");

    // Components

    IReadOnlyList<ComponentInfo> Catalog => host.Components;

    // From what is known without asking: no versions, and what is there as the settings and the files say.
    public IReadOnlyList<ComponentRow> Components() => Catalog.Select(c => Row(c, IsPresent(c.Id), null, null, null)).ToList();

    // With the installed versions and the newest releases from the index.
    public async Task<IReadOnlyList<ComponentRow>> ComponentsAsync(CancellationToken ct) =>
        (await Check(null, skipStoppedEngines: false, ct)).Select(c => c.Row).ToList();

    // Whether each component has an update, and why a check failed.
    public async Task<IReadOnlyList<UpdateStatus>> CheckUpdates(CancellationToken ct) =>
        (await Check(null, skipStoppedEngines: false, ct)).Select(c => new UpdateStatus(c.Row, c.Error)).ToList();

    public Task UpdateAvailable(IReadOnlyList<UpdateStatus> checks, IProgress<SetupProgress> progress)
    {
        var ids = checks.Where(c => c.Row.CanUpdate).Select(c => c.Row.Id).ToList();
        return ids.Count == 0 ? Task.CompletedTask : Update(ids, progress);
    }

    internal sealed record ComponentCheck(ComponentRow Row, bool IsPresent, string? Error, bool CouldNotCheck, Release? Latest = null);

    // ids null checks everything, which the update checks count as a check. skipStoppedEngines: a component whose check
    // would start a stopped engine, such as the WSL distro, is left out.
    internal async Task<IReadOnlyList<ComponentCheck>> Check(IReadOnlyCollection<string>? ids, bool skipStoppedEngines, CancellationToken ct)
    {
        List<ComponentInfo> targets = [];
        foreach (var c in Catalog.Where(c => ids is null || ids.Contains(c.Id)))
            if (!skipStoppedEngines || c.CheckNeedsEngine is not { } needs || await host.IsRunning(needs)) targets.Add(c);
        var checks = await Task.WhenAll(targets.Select(CheckOne));
        foreach (var check in checks.Where(c => c.Error is null)) newest[check.Row.Id] = check.Latest;
        ct.ThrowIfCancellationRequested();
        if (ids is null) Duties.Checked(all: checks.All(c => c.Error is null));
        return checks;
    }

    async Task<ComponentCheck> CheckOne(ComponentInfo c)
    {
        Version? installed = null;
        string? source = null;
        var present = false;
        try
        {
            installed = await host.Installed(c.Id);
            source = installed is null ? null : await host.Source(c.Id);
            present = c.Id == ComponentRules.DashboardId ? host.HasDashboard : installed is not null;
            var latest = await host.Latest(c.Id);
            return new ComponentCheck(Row(c, present, installed, latest, source), present, null, false, latest);
        }
        catch (Exception e)
        {
            return new ComponentCheck(Row(c, present, installed, null, source), present, e.Message, e is HttpRequestException http && Updates.IsRateLimited(http));
        }
    }

    // Without a check: what the settings say was removed on purpose, and the dashboard by its files.
    internal bool IsPresent(string id) => id == ComponentRules.DashboardId ? host.HasDashboard : !settings.RemovedComponents.Contains(id);

    internal string? Flag(string id) => Catalog.FirstOrDefault(c => c.Id == id)?.Flag;

    ComponentRow Row(ComponentInfo c, bool present, Version? installed, Release? latest, string? source) => new(
        c.Id,
        c.Title,
        c.Id is nameof(Component.DockyardCli) or ComponentRules.TrayId or ComponentRules.DashboardId ? "Dockyard" : Tools.Standalone.Any(t => t.Id == c.Id) ? "Kubernetes" : "Docker",
        installed?.ToString(),
        latest?.Version.ToString(),
        source ?? (Tools.Find(c.Id) is not null && settings.Source == ToolSource.PackageManager ? global::Dockyard.Setup.SettingDefinitions.PackageManager : global::Dockyard.Setup.SettingDefinitions.DockyardSource),
        c.RequiredBecause,
        c.Note,
        c.Flag,
        CanAdd: host.IsInstalled && c.IsOptional && !present,
        // Dockyard itself goes by Uninstall, which takes everything else along.
        CanRemove: host.IsInstalled && c.IsOptional && present && c.Id != SelfId && WhyNotRemovable(c.Id) is null,
        CanUpdate: installed is not null && latest is not null && latest.Version > installed)
    { HasData = c.IsEngine };

    string? WhyNotRemovable(string id) =>
        ComponentRules.WhyNotRemovable(Catalog, id, Catalog.Where(c => IsPresent(c.Id)).Select(c => c.Id).ToHashSet());

    // A component by its id or its flag, as the command line names it.
    ComponentInfo Find(string name) =>
        Catalog.FirstOrDefault(c => string.Equals(c.Id, name, StringComparison.OrdinalIgnoreCase))
        ?? ComponentRules.ByFlag(Catalog, name)
        ?? throw new ArgumentException($"There is no {name} here. Components: {string.Join(", ", Catalog.Select(c => c.Flag ?? c.Id))}");

    public Task Add(string id, IProgress<SetupProgress> progress)
    {
        var component = Find(id);
        if (!component.IsOptional) throw new InvalidOperationException($"{component.Title} is always installed: {component.RequiredBecause}");
        return Change(component, installed: true, removeData: false, progress);
    }

    // Removing an engine keeps its images unless removeData says otherwise.
    public Task Remove(string id, bool removeData, IProgress<SetupProgress> progress)
    {
        var component = Find(id);
        if (WhyNotRemovable(component.Id) is { } why) throw new InvalidOperationException(why);
        return Change(component, installed: false, removeData, progress);
    }

    async Task Change(ComponentInfo component, bool installed, bool removeData, IProgress<SetupProgress> progress)
    {
        var log = Log(progress);
        progress.Report(new SetupProgress($"{(installed ? "Adding" : "Removing")} {component.Title}", Step: 1, Steps: 1));
        if (HandsOver(component.Id))
        {
            HandOver(log, ["component", installed ? "add" : "remove", component.Flag ?? component.Id, .. removeData ? ["--remove-data"] : Array.Empty<string>()], [component.Id]);
            return;
        }
        // Another front end may have changed them since this one started.
        settings.Reload();
        if (await host.SetComponent(log, component.Id, installed, removeData))
        {
            ExitRequested?.Invoke();
            return;
        }
        // The dashboard has its own way of staying gone: whether its files are there.
        if (component.Id == ComponentRules.DashboardId) return;
        if (installed) settings.RemovedComponents.Remove(component.Id);
        else if (!settings.RemovedComponents.Contains(component.Id)) settings.RemovedComponents.Add(component.Id);
        settings.Save();
        if (component.Id == nameof(Component.Kubectl)) ApplyShell(log);
    }

    // ids null updates everything the index has a newer release of.
    public async Task Update(IReadOnlyList<string>? ids, IProgress<SetupProgress> progress)
    {
        var log = Log(progress);
        List<(string Component, Release? Release)> items = [];
        if (ids is null)
            items.AddRange((await Check(null, skipStoppedEngines: false, CancellationToken.None)).Where(c => c.Row.CanUpdate).Select(c => (c.Row.Id, c.Latest)));
        else
            foreach (var component in ids.Select(Find))
                items.Add((component.Id, newest.TryGetValue(component.Id, out var known) && known is not null ? known : await host.Latest(component.Id)));
        if (items.Count == 0)
        {
            log("Everything is up to date");
            return;
        }
        // Dockyard itself last: the command that does the rest is what it replaces.
        items = [.. items.OrderBy(i => i.Component == SelfId)];
        progress.Report(new SetupProgress($"Updating {string.Join(", ", items.Select(i => Find(i.Component).Title))}", Step: 1, Steps: 1));
        var mine = items.Where(i => HandsOver(i.Component)).ToList();
        var here = items.Except(mine).ToList();
        if (here.Count > 0 && await host.Update(log, here)) ExitRequested?.Invoke();
        if (mine.Count > 0) HandOver(log, ["update", .. mine.Select(i => i.Component)], mine.Select(i => i.Component).ToList());
    }

    // Which of Dockyard's own apps this process is, null for the dockyard command. An app does not change itself or replace
    // Dockyard: that step would stop or overwrite it halfway, so a dockyard command of its own does it.
    internal string? Caller { get; init; } = RunningApp();
    internal Action<IReadOnlyList<string>> HandOff { get; init; } = StartCommand;

    static string? RunningApp() => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name switch
    {
        "Dockyard.Tray" => ComponentRules.TrayId,
        "Dockyard.Dashboard" => ComponentRules.DashboardId,
        _ => null,
    };

    bool HandsOver(string id) => Caller is not null && (id == Caller || id == SelfId);

    void HandOver(Action<string> log, IReadOnlyList<string> args, IReadOnlyList<string> ids)
    {
        HandOff(args);
        var exits = ids.Any(id => id == Caller || id == SelfId && host.SelfUpdateStops(Caller!));
        log(exits ? $"dockyard {string.Join(' ', args)} finishes this, and this app closes so that it can" : $"dockyard {string.Join(' ', args)} finishes this in the background");
        if (exits) ExitRequested?.Invoke();
    }

    // Detached, and on macOS and Linux with its output in a log: the app that started it is gone before it is done.
    static void StartCommand(IReadOnlyList<string> args)
    {
        if (OperatingSystem.IsWindows())
        {
            Shell.Spawn(Paths.DockyardCliExe, [.. args]).Dispose();
            return;
        }
        Directory.CreateDirectory(Paths.SettingsDir);
        var log = Path.Combine(Paths.SettingsDir, "handed-over.log");
        Shell.Spawn("/bin/sh", ["-c", $"exec \"$0\" \"$@\" </dev/null >>{Shell.ShQuote(log)} 2>&1", Paths.DockyardCliExe, .. args]).Dispose();
    }

    // Clusters

    // The Linux engine's agent, which keeps kind's cloud provider, the forwarders and dockyard-nfs running; null for an engine
    // without one.
    internal Func<IClusterAgent?> ClusterAgent { get; init; }
    internal string ClusterFolder { get; init; } = ClusterDefinitions.Folder;

    // Tells the engine's agent what every cluster's definition wants, after each change to one. With no Linux engine
    // running, as when only the Windows engine is, nothing is told: the agent keeps what it was told last. An agent that
    // answers with an error throws, since then nothing keeps the cloud provider, the forwarders or NFS running.
    public async Task PushClusterWants()
    {
        if (ClusterAgent() is not { } agent) return;
        try { await agent.SetClusterWantsAsync(ClusterDefinitions.Wants(ClusterFolder)); }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or TimeoutException) { }
        catch (InvalidOperationException e) { throw new InvalidOperationException($"The engine's agent refused the cluster settings: {e.Message}", e); }
    }

    // Setup

    // Installs, or brings an install up to date and in line with the request, keeping images and containers. Every outcome is
    // in the exit code and the progress; nothing throws.
    public async Task<int> Setup(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        try
        {
            return await Apply(request, progress, ct);
        }
        catch (Exception ex)
        {
            progress.Report(new SetupProgress(ex.Message, SetupProgressKind.Error));
            return Failed;
        }
    }

    async Task<int> Apply(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        var log = Log(progress);
        if (request.Resume)
        {
            progress.Report(new SetupProgress("Finishing the setup", Step: 1, Steps: 1));
            await host.Resume(log);
            if (request.Start) host.StartApp();
            log(host.DoneMessage(reboot: false));
            return Ok;
        }

        settings.Reload();
        var with = request.With.Select(FlagFor).ToList();
        var without = request.Without.Select(FlagFor).ToList();
        List<string> problems = [.. with.Intersect(without).Select(both => $"{both} is both wanted and not wanted")];
        problems.AddRange(ComponentRules.Problems(Catalog, with, without, settings.RemovedComponents));
        if (problems.Count > 0)
        {
            foreach (var problem in problems) progress.Report(new SetupProgress(problem, SetupProgressKind.Error));
            return WrongArguments;
        }

        var removedBefore = settings.RemovedComponents.ToList();
        var removed = ComponentRules.Removed(Catalog, removedBefore, with, without);
        if (!host.IsInstalled)
            foreach (var c in Catalog.Where(c => c.Id != ComponentRules.DashboardId && c.Flag is not null && !with.Contains(c.Flag) && (!c.Default || request.Headless && c.Id == ComponentRules.TrayId)))
                if (!removed.Contains(c.Id)) removed.Add(c.Id);
        settings.RemovedComponents = removed;
        settings.Source = request.Source ?? settings.Source;
        if (request.KubectlShell) settings.KubectlShell = true;
        // The hosts read these from the file while they install.
        settings.Save();

        bool? dashboardAsked = with.Contains(DashboardFlag) ? true : without.Contains(DashboardFlag) || request.Headless && !host.IsInstalled ? false : null;
        var dashboard = DashboardChoice.Resolve(dashboardAsked, host.IsInstalled, host.HasDashboard, Catalog.FirstOrDefault(c => c.Id == ComponentRules.DashboardId)?.Default ?? true);
        List<ComponentInfo> adds = [];
        List<ComponentInfo> removes = [];
        // A new install leaves out what it is asked to by itself; an existing one adds and removes after bringing the rest up to date.
        if (host.IsInstalled)
        {
            foreach (var c in Changed(with))
                if (removedBefore.Contains(c.Id) || await host.Installed(c.Id) is null) adds.Add(c);
            foreach (var c in Changed(without))
                if (await host.Installed(c.Id) is not null) removes.Add(c);
        }
        var installRequest = new InstallRequest(dashboard, request.Image, request.Silent, request.TrustHttps) { Without = removed, Tray = !removed.Contains(ComponentRules.TrayId) };
        var phases = host.InstallPlan(installRequest);
        List<string> plan =
        [
            .. phases,
            .. adds.Select(c => $"Adding {c.Title}"),
            .. removes.Select(c => $"Removing {c.Title}"),
            .. request.Start ? [Hosts.InstallPhases.StartingUp] : Array.Empty<string>(),
        ];
        var steps = plan.Count;
        var step = 1;

        progress.Report(new SetupProgress(plan[0], Step: step, Steps: steps) { Plan = plan });
        // A host's phase marker moves the steps on; one it did not plan, or one it entered already, is only a line.
        void InstallLog(string line)
        {
            if (Hosts.InstallPhases.Read(line) is not { } phase) log(line);
            else if (plan.IndexOf(phase, step - 1) is var at and >= 0 && at < phases.Count)
            {
                if (at + 1 == step) return;
                step = at + 1;
                progress.Report(new SetupProgress(phase, Step: step, Steps: steps));
            }
            else log(phase);
        }
        var reboot = await host.Install(InstallLog, installRequest);
        step = phases.Count;
        // Setup turns trusted HTTPS on through the file.
        settings.Reload();
        ct.ThrowIfCancellationRequested();
        foreach (var c in adds)
        {
            progress.Report(new SetupProgress($"Adding {c.Title}", Step: ++step, Steps: steps));
            // Removed earlier counts too: something else on the machine may answer for it meanwhile, such as a plugin of another package.
            if (removedBefore.Contains(c.Id) || await host.Installed(c.Id) is null) await host.SetComponent(log, c.Id, installed: true);
        }
        foreach (var c in removes)
        {
            progress.Report(new SetupProgress($"Removing {c.Title}", Step: ++step, Steps: steps));
            if (await host.Installed(c.Id) is not null) await host.SetComponent(log, c.Id, installed: false, request.RemoveData);
        }
        if (request.Start) progress.Report(new SetupProgress(Hosts.InstallPhases.StartingUp, Step: ++step, Steps: steps));
        ApplyShell(log);
        // The definitions are the user's: a setup run with sudo would tell the agent there are none.
        if (Environment.UserName != "root")
            try { await PushClusterWants(); }
            catch (InvalidOperationException e) { progress.Report(new SetupProgress(e.Message, SetupProgressKind.Warning)); }
        // Installing never fails halfway unnoticed: a step that quietly did nothing would otherwise report success.
        if (!reboot && !host.IsInstalled) throw new InvalidOperationException("Setup finished, but Dockyard is not installed");
        if (host.HasDashboard != dashboard)
            throw new InvalidOperationException(dashboard ? "The dashboard was not installed" : "The dashboard was not removed");
        if (request.Start && !reboot) host.StartApp();
        log(host.DoneMessage(reboot));
        return reboot ? RebootRequired : Ok;
    }

    // The dashboard and the tray go with the install itself rather than as components of their own.
    IEnumerable<ComponentInfo> Changed(IEnumerable<string> flags) =>
        flags.Where(f => f is not (DashboardFlag or TrayFlag)).Select(f => ComponentRules.ByFlag(Catalog, f)).OfType<ComponentInfo>();

    string FlagFor(string name) =>
        Catalog.FirstOrDefault(c => c.Flag is not null && string.Equals(c.Id, name, StringComparison.OrdinalIgnoreCase))?.Flag ?? name;

    public async Task Uninstall(bool removeData, IProgress<SetupProgress> progress, bool silent = false)
    {
        var log = Log(progress);
        shell.Apply(false, log);
        await host.Uninstall(log, removeData, silent);
        log(removeData ? "Dockyard was removed, with its images and containers." : "Dockyard was removed. Images and containers were kept.");
    }

    // Images, volumes and containers from Docker Desktop, or from the engine at from (unix://, npipe:// or tcp://).
    public async Task Import(IProgress<SetupProgress> progress, string? from = null)
    {
        var log = Log(progress);
        await host.Start(host.Engines[0].Id);
        await DockerDesktop.Import(from, host.ImportTarget, host.OwnSocket, log);
    }

    // For a kind cluster's cert-manager: an intermediate CA for the cluster, its key, then the root, as PEM. The root's key
    // never leaves this machine.
    public async Task<string> ClusterCa(string cluster)
    {
        if (!ClusterName().IsMatch(cluster)) throw new ArgumentException($"{cluster} is not a cluster name");
        settings.Reload();
        if (!settings.HttpsEnabled || await LocalCa.Load(host.CaDirectory, host.CaKeys) is not { } ca || !LocalCa.IsConstrainedRoot(ca.Certificate))
            throw new InvalidOperationException("Trusted HTTPS is off, so there is no certificate authority to issue from: turn on trusted HTTPS first");
        var issued = LocalCa.ClusterIntermediate(ca, cluster, DateTimeOffset.UtcNow);
        return issued.Certificate.TrimEnd() + "\n" + issued.Key.TrimEnd() + "\n" + ca.Certificate.TrimEnd() + "\n";
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex ClusterName();

    // Checks the profiles afterwards: a profile that did not get the block (Controlled Folder Access, a read-only file) turns
    // the setting off again, so it never says on while k does not work. Never into root's profile from a setup run with sudo.
    internal void ApplyShell(Action<string> log)
    {
        if (Environment.UserName == "root") return;
        var enabled = settings.KubectlShell && !settings.RemovedComponents.Contains(nameof(Component.Kubectl));
        shell.Apply(enabled, log);
        if (!enabled) return;
        var missing = shell.Missing();
        if (missing.Count == 0) return;
        log($"kubectl could not be set up in {string.Join(" and ", missing)}, so the setting is off again");
        settings.KubectlShell = false;
        settings.Save();
    }

    static Action<string> Log(IProgress<SetupProgress> progress) => line => progress.Report(new SetupProgress(line));

    // Dockyard's own release files from this folder instead of downloading them, unchecked.
    public static string? LocalAssets { get => Updates.LocalAssets; set => Updates.LocalAssets = value; }
    internal bool RunsFromInstallLocation => host.RunsFromInstallLocation;
    // Dockyard itself: the row whose update replaces the running program, and whose version is what is installed.
    public string SelfId => host.SelfComponent;
    internal void StartApp() => host.StartApp();
}
