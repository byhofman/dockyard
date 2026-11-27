using Dockyard.Core;

namespace Dockyard.Tray;

enum ContainerCommand { Start, Stop, Restart }
enum TrayLevel { Stopped, Running, Busy }

// What the tray does when an item is chosen; the menu only says which.
interface ITrayActions
{
    void StartEngine(string id);
    void StopEngine(string id);
    void UseEngine(string id);
    void Container(TrayContainer container, ContainerCommand command);
    void OpenWeb(int port);
    // The dashboard at a section, or where it was left when null.
    void OpenDashboard(string? section);
    void UseKubeContext(string name);
    void Quit();
}

sealed record TrayEngine(string Id, string Name, bool Running, bool Active, bool Available);

// What the menu shows, all of it: Busy is what is running at the moment, Problem what failed last.
sealed record TrayState(IReadOnlyList<TrayEngine> Engines, bool HasDashboard, IReadOnlyList<TrayContainer> Containers, string? Busy, bool UpdateAvailable, string? Problem, IReadOnlyList<string>? KubeContexts = null, string? KubeContext = null);

// The icon: the engine's state as a colour, the active engine's initial when there is a choice of engines, and a dot for an update.
sealed record TrayImage(TrayLevel Level, string? Badge, bool Update);

sealed class TrayMenuItem
{
    public string Header { get; set; } = "";
    public bool IsVisible { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public bool IsSeparator { get; init; }
    public bool IsRadio { get; init; }
    public bool? IsChecked { get; set; }
    public Action? Click { get; init; }
    public List<TrayMenuItem> Items { get; } = [];
}

// The tray's menu without a window or an icon of the system's: the items, whether each shows and is enabled, and what each does.
// The system's menu is a thin copy of this.
sealed class TrayMenu
{
    readonly ITrayActions actions;
    readonly TrayMenuItem status = new() { IsEnabled = false };
    readonly TrayMenuItem toggle;
    readonly TrayMenuItem afterEngines = new() { IsSeparator = true };
    readonly TrayMenuItem betweenBlocks = new() { IsSeparator = true };
    readonly TrayMenuItem beforeQuit = new() { IsSeparator = true };
    readonly TrayMenuItem[] dashboardItems;
    IReadOnlyList<string> shownKubeContexts = [];
    IReadOnlyList<TrayContainer> shownContainers = [];
    TrayEngine? toggled;

    public TrayMenu(ITrayActions actions)
    {
        this.actions = actions;
        Containers = new TrayMenuItem { Header = "Containers", IsVisible = false };
        KubeContexts = new TrayMenuItem { Header = "Kubernetes context", IsVisible = false };
        dashboardItems =
        [
            new TrayMenuItem { Header = "Open dashboard", Click = () => actions.OpenDashboard(null) },
            new TrayMenuItem { Header = "Tools…", Click = () => actions.OpenDashboard("tools") },
            new TrayMenuItem { Header = "Settings…", Click = () => actions.OpenDashboard("settings") },
        ];
        toggle = new TrayMenuItem { Click = Toggle };
        Quit = new TrayMenuItem { Header = "Quit", Click = actions.Quit };
    }

    public TrayMenuItem Containers { get; }
    public TrayMenuItem KubeContexts { get; }
    TrayMenuItem Quit { get; }
    readonly List<TrayMenuItem> engines = [];

    // Every item in order, shown or not.
    public IReadOnlyList<TrayMenuItem> Items => [status, toggle, .. engines, afterEngines, Containers, KubeContexts, betweenBlocks, .. dashboardItems, beforeQuit, Quit];
    public IReadOnlyList<TrayMenuItem> Visible => Items.Where(i => i.IsVisible).ToList();
    public TrayImage Icon { get; private set; } = new(TrayLevel.Stopped, null, false);
    public string Tooltip { get; private set; } = "Dockyard";

    void Toggle()
    {
        if (toggled is null) return;
        if (toggled.Running) actions.StopEngine(toggled.Id);
        else actions.StartEngine(toggled.Id);
    }

    public void Update(TrayState state)
    {
        var installed = state.Engines.Where(e => e.Available).ToList();
        var choice = installed.Count > 1;
        var active = state.Engines.FirstOrDefault(e => e.Active) ?? state.Engines[0];
        toggled = active;
        var idle = state.Busy is null;

        status.Header = state.Busy is { } busy ? $"{busy}…" : $"{active.Name} engine {(active.Running ? "running" : "stopped")}";
        toggle.Header = (active.Running ? "Stop" : "Start") + (choice ? $" {active.Name} engine" : " engine");
        toggle.IsEnabled = idle;
        SyncEngines(state.Engines, choice, idle);

        var containers = state.Containers;
        if (!containers.SequenceEqual(shownContainers))
        {
            shownContainers = containers;
            Containers.Items.Clear();
            Containers.Items.AddRange(containers.Select(ContainerItem));
        }
        Containers.IsVisible = containers.Count > 0;

        SyncKubeContexts(state.KubeContexts ?? [], state.KubeContext);

        var hasBlock = containers.Count > 0 || KubeContexts.IsVisible;
        foreach (var item in dashboardItems) item.IsVisible = state.HasDashboard;
        betweenBlocks.IsVisible = hasBlock && state.HasDashboard;
        beforeQuit.IsVisible = hasBlock || state.HasDashboard;

        Icon = new TrayImage(!idle ? TrayLevel.Busy : active.Running ? TrayLevel.Running : TrayLevel.Stopped, choice ? active.Name[..1] : null, state.UpdateAvailable);
        Tooltip = !idle
            ? $"Dockyard: {state.Busy}…"
            : "Dockyard\n" + string.Join("\n", installed.Select(e => $"{e.Name} engine {(e.Running ? "running" : "stopped")}{(choice && e.Active ? " (active)" : "")}"))
                + (state.UpdateAvailable ? "\nUpdate available: run dockyard update" : "");
        if (state.Problem is { Length: > 0 } problem) Tooltip += "\n" + problem;
    }

    void SyncEngines(IReadOnlyList<TrayEngine> all, bool choice, bool idle)
    {
        if (engines.Count != all.Count)
        {
            engines.Clear();
            engines.AddRange(all.Select(e => new TrayMenuItem { IsRadio = true, Click = () => actions.UseEngine(e.Id) }));
        }
        for (var i = 0; i < all.Count; i++)
        {
            engines[i].Header = all[i].Name;
            engines[i].IsChecked = all[i].Active;
            engines[i].IsVisible = choice && all[i].Available;
            engines[i].IsEnabled = idle;
        }
        afterEngines.IsVisible = true;
    }

    void SyncKubeContexts(IReadOnlyList<string> contexts, string? current)
    {
        if (!contexts.SequenceEqual(shownKubeContexts))
        {
            shownKubeContexts = contexts;
            KubeContexts.Items.Clear();
            KubeContexts.Items.AddRange(contexts.Select(c => new TrayMenuItem { Header = c, IsRadio = true, Click = () => actions.UseKubeContext(c) }));
        }
        foreach (var item in KubeContexts.Items) item.IsChecked = item.Header == current;
        KubeContexts.IsVisible = contexts.Count > 0;
    }

    TrayMenuItem ContainerItem(TrayContainer container)
    {
        var item = new TrayMenuItem { Header = (container.Running ? "● " : "○ ") + (container.Project is { } project ? $"{project} / " : "") + container.Name };
        TrayMenuItem Action(string header, ContainerCommand command) => new() { Header = header, Click = () => actions.Container(container, command) };
        if (container.Running)
        {
            item.Items.Add(Action("Stop", ContainerCommand.Stop));
            item.Items.Add(Action("Restart", ContainerCommand.Restart));
            if (container.WebPort is { } port)
                item.Items.Add(new TrayMenuItem { Header = $"Open http://127.0.0.1:{port}", Click = () => actions.OpenWeb(port) });
        }
        else item.Items.Add(Action("Start", ContainerCommand.Start));
        return item;
    }
}
