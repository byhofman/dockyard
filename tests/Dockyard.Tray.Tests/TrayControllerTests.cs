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
