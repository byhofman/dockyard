using System.Diagnostics;
using Dockyard.Core;
using Dockyard.Engine;
using Dockyard.Engine.Transport;
using Dockyard.Setup;
using Dockyard.Shared;

namespace Dockyard.Tray;

// The tray's state and what its menu does, through the facade. The dashboard's window is not here: the tray only asks for it.
sealed class TrayController : ITrayActions
{
    readonly DockyardSetup setup;
    readonly Action quit;
    string? busy;
    string? problem;
    bool updateAvailable;
    IReadOnlyList<TrayContainer> containers = [];
    KubeContexts kube = new([], null);
    bool refreshingContainers;

    readonly Func<string, bool> sendToDashboard;
    readonly Action<ShellTerminal.Command> openTerminal;
    readonly Func<string, bool> onPath;

    public TrayController(DockyardSetup setup, Action quit, Func<string, bool>? sendToDashboard = null, Action<ShellTerminal.Command>? openTerminal = null, Func<string, bool>? onPath = null)
    {
        this.setup = setup;
        this.quit = quit;
        this.sendToDashboard = sendToDashboard ?? (line => LocalPipe.TrySend(LocalPipe.DashboardName, line));
        this.openTerminal = openTerminal ?? (command => Start(new ProcessStartInfo(command.File, command.Args)));
        this.onPath = onPath ?? ShellTerminal.OnPath;
        Menu = new TrayMenu(this);
    }

    public TrayMenu Menu { get; }

    // After every change of what the menu shows.
    public event Action? Changed;

    public void UpdatesFound(IReadOnlyList<ComponentRow> rows)
    {
        updateAvailable = rows.Count > 0;
        _ = Refresh();
    }

    public void Report(SetupProgress progress)
    {
        if (progress.Kind != SetupProgressKind.Error) return;
        problem = progress.Line;
        _ = Refresh();
    }

    // Never throws: a tick that fails keeps what was shown, says so on stderr, and the next tick tries again.
    public async Task Refresh()
    {
        try { await RefreshCore(); }
        catch (Exception e) { Console.Error.WriteLine($"dockyard-tray: refresh failed: {e.Message}"); }
    }

    async Task RefreshCore()
    {
        List<TrayEngine> engines = [];
        var active = setup.ActiveEngine();
        foreach (var e in setup.Engines)
            engines.Add(new TrayEngine(e.Id, e.Name, await IsRunning(e.Id), e.Id == active, e.ComponentId is not { } id || setup.IsPresent(id)));
        kube = ReadKubeContexts();
        Menu.Update(new TrayState(engines, setup.HasDashboard, containers, busy, updateAvailable, problem, kube.Names, kube.Current));
        Changed?.Invoke();
        _ = RefreshContainers(engines.FirstOrDefault(e => e.Active && e.Running));
    }

    // A kubeconfig that cannot be read leaves the submenu out until it can.
    KubeContexts ReadKubeContexts()
    {
        try { return setup.KubeContexts(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new KubeContexts([], null); }
    }

    async Task<bool> IsRunning(string engine)
    {
        try { return await setup.IsRunning(engine); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { return false; }
    }

    async Task RefreshContainers(TrayEngine? running)
    {
        if (refreshingContainers) return;
        refreshingContainers = true;
        try
        {
            List<TrayContainer> found = [];
            if (running is not null)
            {
                using var docker = Docker(running.Id);
                found = TrayContainers.From(await docker.ListContainersAsync(all: true));
            }
            if (found.SequenceEqual(containers)) return;
            containers = found;
            await Refresh();
        }
        catch (Exception) { }
        finally { refreshingContainers = false; }
    }

    DockerClient Docker(string engine) => new(DockerEndpoint.Parse(setup.Engines.First(e => e.Id == engine).Endpoint));

    string Name(string engine) => setup.Engines.FirstOrDefault(e => e.Id == engine)?.Name ?? engine;

    // One thing at a time: while it runs the menu says so, and a failure stays in the tooltip until the next thing runs.
    async Task Run(string what, Func<Task> action)
    {
        if (busy is not null) return;
        busy = what;
        problem = null;
        await Refresh();
        try { await action(); }
        catch (Exception e) when (e is not OutOfMemoryException) { problem = e.Message; }
        finally
        {
            busy = null;
            await Refresh();
        }
    }

    public void StartEngine(string id) => _ = Run($"Starting {Name(id)} engine", () => setup.StartEngine(id));
    public void StopEngine(string id) => _ = Run($"Stopping {Name(id)} engine", () => setup.StopEngine(id));
    public void UseEngine(string id) => _ = Run($"Switching to {Name(id)} containers", () => setup.UseEngine(id));

    public void Container(TrayContainer container, ContainerCommand command) => _ = Run($"{command} {container.Name}", async () =>
    {
        using var docker = Docker(setup.ActiveEngine());
        switch (command)
        {
            case ContainerCommand.Start: await docker.StartContainerAsync(container.Id); break;
            case ContainerCommand.Stop: await docker.StopContainerAsync(container.Id); break;
            case ContainerCommand.Restart: await docker.RestartContainerAsync(container.Id); break;
        }
    });

    public void UseKubeContext(string name) => _ = Run($"Switching to {name}", () => Task.Run(() => setup.UseKubeContext(name)));

    // A terminal running dockyard ctx, so the shell is pinned to the context the same way as from a command line.
    public void OpenShell(string context)
    {
        try { openTerminal(ShellTerminal.For(context, ShellTerminal.ThisSystem, onPath)); }
        catch (InvalidOperationException e)
        {
            problem = e.Message;
            _ = Refresh();
        }
    }

    public void OpenWeb(int port) => Start(new ProcessStartInfo($"http://127.0.0.1:{port}") { UseShellExecute = true });

    // The dashboard is started first when it is not running, and then asked to show the section.
    public void OpenDashboard(string? section)
    {
        if (sendToDashboard(section is null ? "show" : $"show {section}")) return;
        if (setup.DashboardExe is { } exe && File.Exists(exe)) Start(new ProcessStartInfo(exe, section is null ? [] : ["--section", section]) { UseShellExecute = false });
    }

    void Start(ProcessStartInfo start)
    {
        try { Process.Start(start)?.Dispose(); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            problem = e.Message;
            _ = Refresh();
        }
    }

    // The dashboard goes with the tray: with no tray its window would have no way to be shown or ended.
    public void Quit()
    {
        sendToDashboard("quit");
        quit();
    }
}
