using Dockyard.Setup;
using Dockyard.Setup.Tests;

namespace Dockyard.Tray.Tests;

public sealed class TrayControllerTests : IDisposable
{
    readonly TempSettings settings = new();

    public void Dispose() => settings.Dispose();

    TrayController Controller(FakeHost host, Action? quit = null, Func<string, bool>? send = null) =>
        new(Facade.For(host, settings), quit ?? (() => { }), send ?? (_ => true));

    [Fact]
    public async Task A_shell_item_opens_a_terminal_running_dockyard_ctx_for_that_context()
    {
        var kubeconfig = Path.Combine(Path.GetTempPath(), $"dockyard-tray-kube-{Guid.NewGuid():N}");
        File.WriteAllText(kubeconfig, "apiVersion: v1\nkind: Config\ncontexts:\n- name: dev\n  context:\n    cluster: dev\n");
        try
        {
            List<Dockyard.Core.ShellTerminal.Command> opened = [];
            var controller = new TrayController(Facade.For(new FakeHost { Installed = true }, settings, [kubeconfig]), () => { }, _ => true, opened.Add, onPath: _ => true);

            await controller.Refresh();
            controller.Menu.Visible.Single(i => i.Header == "Open a shell on…").Items.Single().Click!();

            var command = Assert.Single(opened);
            Assert.Contains("dockyard ctx", command.File + " " + string.Join(" ", command.Args));
            Assert.Contains("dev", string.Join(" ", command.Args));
        }
        finally { File.Delete(kubeconfig); }
    }

    [Fact]
    public void Opened_without_a_dashboard_shows_dockyard_help_in_a_terminal()
    {
        List<Dockyard.Core.ShellTerminal.Command> opened = [];
        var controller = new TrayController(Facade.For(new FakeHost { Installed = true }, settings), () => { }, _ => false, opened.Add, onPath: _ => true);

        controller.Opened();

        Assert.Equal(Dockyard.Core.ShellTerminal.Running(["--help"], Dockyard.Core.ShellTerminal.ThisSystem, _ => true), Assert.Single(opened), CommandComparer.Instance);
    }

    [Fact]
    public void Opened_with_a_dashboard_shows_it_and_opens_no_terminal()
    {
        var exe = Path.GetTempFileName();
        try
        {
            List<string> sent = [];
            List<Dockyard.Core.ShellTerminal.Command> opened = [];
            var controller = new TrayController(Facade.For(new FakeHost { Installed = true, Dashboard = true, DashboardExe = exe }, settings), () => { }, line => { sent.Add(line); return true; }, opened.Add, onPath: _ => true);

            controller.Opened();

            Assert.Equal(["show"], sent);
            Assert.Empty(opened);
        }
        finally { File.Delete(exe); }
    }

    sealed class CommandComparer : IEqualityComparer<Dockyard.Core.ShellTerminal.Command>
    {
        public static readonly CommandComparer Instance = new();
        public bool Equals(Dockyard.Core.ShellTerminal.Command? a, Dockyard.Core.ShellTerminal.Command? b) =>
            a?.File == b?.File && (a?.Args ?? []).SequenceEqual(b?.Args ?? []);
        public int GetHashCode(Dockyard.Core.ShellTerminal.Command c) => c.File.GetHashCode();
    }

    [Fact]
    public void Quit_tells_the_dashboard_to_quit_and_then_quits_the_tray()
    {
        List<string> events = [];
        var controller = Controller(new FakeHost { Installed = true }, quit: () => events.Add("tray quit"), send: line => { events.Add("sent " + line); return true; });

        controller.Menu.Visible.Single(i => i.Header == "Quit").Click!();

        Assert.Equal(["sent quit", "tray quit"], events);
    }

    [Fact]
    public async Task A_refresh_that_fails_keeps_the_tray_alive_and_the_next_one_works()
    {
        var failing = true;
        var host = new FakeHost { Installed = true, Running = _ => failing ? throw new DivideByZeroException("boom") : Task.FromResult(true) };
        var controller = Controller(host);
        var changes = 0;
        controller.Changed += () => changes++;

        await controller.Refresh();
        Assert.Equal(0, changes);

        failing = false;
        await controller.Refresh();
        Assert.Equal(1, changes);
        Assert.Equal(TrayLevel.Running, controller.Menu.Icon.Level);
    }
}
