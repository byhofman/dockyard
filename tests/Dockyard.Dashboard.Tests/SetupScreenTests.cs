using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dockyard.Dashboard.Features.Setup;
using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Tests;

public sealed class SetupScreenTests
{
    [Fact]
    public async Task Setup_shows_until_Dockyard_is_installed_and_then_the_sections()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SetupScreenTests).Assembly);
        await session.Dispatch(() =>
        {
            var setup = new RecordingSetup(installed: false);
            var shell = new ShellViewModel(setup);
            var window = new MainWindow { DataContext = shell, Width = 1240, Height = 760 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(shell.ShowsSetup);
            Assert.Contains(window.GetVisualDescendants(), v => v is SetupView && v.IsEffectivelyVisible);

            shell.Setup!.InstallCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("setup ", setup.Calls.Single());
            Assert.True(shell.ShowsSetup);

            setup.Release();
            Dispatcher.UIThread.RunJobs();
            Assert.False(shell.ShowsSetup);
            Assert.DoesNotContain(window.GetVisualDescendants(), v => v is SetupView);
            Assert.Equal("Containers", shell.CurrentSection.Title);

            // Asked for by name, as --section setup does, it shows on an installed Dockyard too.
            shell.ShowSection("setup");
            Assert.True(shell.ShowsSetup);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }
}
