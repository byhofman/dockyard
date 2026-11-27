using Dockyard.Core;

namespace Dockyard.Tray.Tests;

public sealed class TrayMenuTests
{
    sealed class Actions : ITrayActions
    {
        public List<string> Calls { get; } = [];
        public void StartEngine(string id) => Calls.Add($"start {id}");
        public void StopEngine(string id) => Calls.Add($"stop {id}");
        public void UseEngine(string id) => Calls.Add($"use {id}");
        public void Container(TrayContainer container, ContainerCommand command) => Calls.Add($"{command} {container.Name}");
        public void OpenWeb(int port) => Calls.Add($"web {port}");
        public void OpenDashboard(string? section) => Calls.Add($"dashboard {section}");
        public void UseKubeContext(string name) => Calls.Add($"kube {name}");
        public void OpenShell(string context) => Calls.Add($"shell {context}");
        public void Quit() => Calls.Add("quit");
    }

    static readonly TrayEngine Linux = new("linux", "Linux", Running: true, Active: true, Available: true);
    static readonly TrayEngine Windows = new("win", "Windows", Running: false, Active: false, Available: true);

    static TrayState State(IReadOnlyList<TrayEngine>? engines = null, bool dashboard = true, IReadOnlyList<TrayContainer>? containers = null,
        string? busy = null, bool update = false, string? problem = null, string[]? kube = null, string? kubeCurrent = null) =>
        new(engines ?? [Linux], dashboard, containers ?? [], busy, update, problem, kube, kubeCurrent);

    static (TrayMenu Menu, Actions Actions) Menu(TrayState state)
    {
        var actions = new Actions();
        var menu = new TrayMenu(actions);
        menu.Update(state);
        return (menu, actions);
    }

    static TrayMenuItem Item(TrayMenu menu, string header) => menu.Visible.Single(i => i.Header == header);

    [Fact]
    public void Without_the_dashboard_there_is_no_open_dashboard_item()
    {
        var (menu, _) = Menu(State(dashboard: false));

        var headers = menu.Visible.Where(i => !i.IsSeparator).Select(i => i.Header).ToList();

        Assert.DoesNotContain("Open dashboard", headers);
        Assert.DoesNotContain("Tools…", headers);
        Assert.DoesNotContain("Settings…", headers);
        Assert.Contains("Quit", headers);
        Assert.False(menu.Visible[^2].IsSeparator && menu.Visible[^3].IsSeparator, "two separators in a row");
    }

    [Fact]
    public void The_kubernetes_context_submenu_has_a_radio_item_per_context_with_the_current_one_checked()
    {
        var (menu, _) = Menu(State(kube: ["docker-desktop", "kind-dev", "prod"], kubeCurrent: "kind-dev"));

        var submenu = Item(menu, "Kubernetes context");

        Assert.Equal(["docker-desktop", "kind-dev", "prod"], submenu.Items.Select(i => i.Header));
        Assert.All(submenu.Items, i => Assert.True(i.IsRadio));
        Assert.Equal(["kind-dev"], submenu.Items.Where(i => i.IsChecked == true).Select(i => i.Header));
    }

    [Fact]
    public void Clicking_a_context_switches_to_it_and_the_check_follows_the_next_update()
    {
        var (menu, actions) = Menu(State(kube: ["a", "b"], kubeCurrent: "a"));

        Item(menu, "Kubernetes context").Items[1].Click!();
        menu.Update(State(kube: ["a", "b"], kubeCurrent: "b"));

        Assert.Equal(["kube b"], actions.Calls);
        Assert.Equal([false, true], Item(menu, "Kubernetes context").Items.Select(i => i.IsChecked == true));
    }

    [Fact]
    public void The_shell_submenu_has_an_item_per_context_that_opens_a_shell_on_it()
    {
        var (menu, actions) = Menu(State(kube: ["a", "b"], kubeCurrent: "a"));

        var submenu = Item(menu, "Open a shell on…");
        submenu.Items[1].Click!();

        Assert.Equal(["a", "b"], submenu.Items.Select(i => i.Header));
        Assert.Equal(["shell b"], actions.Calls);
    }

    [Fact]
    public void Without_contexts_there_is_no_shell_submenu()
    {
        var (menu, _) = Menu(State(kube: []));

        Assert.DoesNotContain(menu.Visible, i => i.Header == "Open a shell on…");
    }

    [Fact]
    public void Without_contexts_there_is_no_submenu_and_no_stray_separator()
    {
        var (menu, _) = Menu(State(dashboard: false, kube: []));
        Assert.DoesNotContain(menu.Visible, i => i.Header == "Kubernetes context");
        Assert.False(menu.Visible[^2].IsSeparator && menu.Visible[^3].IsSeparator, "two separators in a row");

        menu.Update(State(dashboard: false, kube: ["a"], kubeCurrent: "a"));
        Assert.Contains(menu.Visible, i => i.Header == "Kubernetes context");

        menu.Update(State(dashboard: false));
        Assert.DoesNotContain(menu.Visible, i => i.Header == "Kubernetes context");
        Assert.Equal(["Quit"], menu.Visible.Where(i => !i.IsSeparator).Skip(2).Select(i => i.Header));
    }

    [Fact]
    public void With_the_dashboard_the_items_open_it_at_their_section()
    {
        var (menu, actions) = Menu(State());

        Item(menu, "Open dashboard").Click!();
        Item(menu, "Tools…").Click!();
        Item(menu, "Settings…").Click!();

        Assert.Equal(["dashboard ", "dashboard tools", "dashboard settings"], actions.Calls);
    }

    [Fact]
    public void The_dashboard_items_come_and_go_with_the_dashboard()
    {
        var (menu, _) = Menu(State(dashboard: false));
        menu.Update(State(dashboard: true));
        Assert.Contains(menu.Visible, i => i.Header == "Open dashboard");
        menu.Update(State(dashboard: false));
        Assert.DoesNotContain(menu.Visible, i => i.Header == "Open dashboard");
    }

    [Fact]
    public void A_running_engine_offers_Stop_and_a_stopped_one_Start()
    {
        var (menu, actions) = Menu(State());
        Assert.Equal("Linux engine running", menu.Visible[0].Header);
        Assert.False(menu.Visible[0].IsEnabled);
        Item(menu, "Stop engine").Click!();

        menu.Update(State([Linux with { Running = false }]));
        Assert.Equal("Linux engine stopped", menu.Visible[0].Header);
        Item(menu, "Start engine").Click!();

        Assert.Equal(["stop linux", "start linux"], actions.Calls);
    }

    [Fact]
    public void With_two_engines_the_choice_is_a_radio_that_switches_and_Start_Stop_is_for_the_active_one()
    {
        var (menu, actions) = Menu(State([Linux, Windows]));

        var linux = Item(menu, "Linux");
        var windows = Item(menu, "Windows");
        Assert.True(linux.IsRadio && windows.IsRadio);
        Assert.Equal(true, linux.IsChecked);
        Assert.Equal(false, windows.IsChecked);
        windows.Click!();
        Item(menu, "Stop Linux engine").Click!();

        Assert.Equal(["use win", "stop linux"], actions.Calls);
    }

    [Fact]
    public void An_engine_that_was_removed_is_not_offered_and_a_single_one_has_no_choice()
    {
        var (menu, _) = Menu(State([Linux, Windows with { Available = false }]));

        Assert.DoesNotContain(menu.Visible, i => i.IsRadio);
        Assert.Equal(TrayLevel.Running, menu.Icon.Level);
        Assert.Null(menu.Icon.Badge);
    }

    [Fact]
    public void The_icon_shows_the_state_and_on_two_engines_the_active_one()
    {
        var (menu, _) = Menu(State([Linux, Windows]));
        Assert.Equal(new TrayImage(TrayLevel.Running, "L", Update: false), menu.Icon);

        menu.Update(State([Linux with { Running = false }, Windows]));
        Assert.Equal(TrayLevel.Stopped, menu.Icon.Level);

        menu.Update(State([Linux with { Active = false }, Windows with { Active = true, Running = true }], update: true));
        Assert.Equal(new TrayImage(TrayLevel.Running, "W", Update: true), menu.Icon);

        menu.Update(State([Linux, Windows], busy: "Starting Linux engine"));
        Assert.Equal(TrayLevel.Busy, menu.Icon.Level);
    }

    [Fact]
    public void While_something_runs_the_engine_items_wait_and_the_status_says_what_runs()
    {
        var (menu, _) = Menu(State([Linux, Windows], busy: "Stopping Linux engine"));

        Assert.Equal("Stopping Linux engine…", menu.Visible[0].Header);
        Assert.All(menu.Visible.Where(i => i.IsRadio || i.Header.StartsWith("Stop", StringComparison.Ordinal)), i => Assert.False(i.IsEnabled));
        Assert.Contains("Stopping Linux engine", menu.Tooltip);
    }

    [Fact]
    public void The_tooltip_names_the_engines_an_update_and_the_last_problem()
    {
        var (menu, _) = Menu(State([Linux, Windows], update: true, problem: "the engine did not start"));

        Assert.Contains("Linux engine running (active)", menu.Tooltip);
        Assert.Contains("Windows engine stopped", menu.Tooltip);
        Assert.Contains("dockyard update", menu.Tooltip);
        Assert.Contains("the engine did not start", menu.Tooltip);
    }

    static TrayContainer Web(string name, bool running, int? port = null, string? project = null) => new(name + "-id", name, project, running, port);

    [Fact]
    public void Containers_have_the_quick_actions_of_their_state()
    {
        var (menu, actions) = Menu(State(containers: [Web("api", running: true, port: 8080, project: "shop"), Web("db", running: false)]));

        var containers = Item(menu, "Containers");
        Assert.Equal(["● shop / api", "○ db"], containers.Items.Select(i => i.Header));
        var api = containers.Items[0].Items;
        Assert.Equal(["Stop", "Restart", "Open http://127.0.0.1:8080"], api.Select(i => i.Header));
        Assert.Equal(["Start"], containers.Items[1].Items.Select(i => i.Header));
        foreach (var item in api) item.Click!();
        containers.Items[1].Items[0].Click!();

        Assert.Equal(["Stop api", "Restart api", "web 8080", "Start db"], actions.Calls);
    }

    [Fact]
    public void Without_containers_there_is_no_containers_item()
    {
        var (menu, _) = Menu(State());
        Assert.DoesNotContain(menu.Visible, i => i.Header == "Containers");
    }

    [Fact]
    public void The_containers_menu_is_rebuilt_only_when_what_it_lists_changes()
    {
        var (menu, _) = Menu(State(containers: [Web("api", running: true)]));
        var first = menu.Containers.Items[0];

        menu.Update(State(containers: [Web("api", running: true)]));
        Assert.Same(first, menu.Containers.Items[0]);

        menu.Update(State(containers: [Web("api", running: false)]));
        Assert.NotSame(first, menu.Containers.Items[0]);
    }

    [Fact]
    public void Quit_quits_the_tray()
    {
        var (menu, actions) = Menu(State());
        Item(menu, "Quit").Click!();
        Assert.Equal(["quit"], actions.Calls);
    }
}
