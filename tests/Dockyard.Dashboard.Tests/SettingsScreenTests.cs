using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dockyard.Dashboard.Controls;
using Dockyard.Dashboard.Features.Setup;
using Dockyard.Dashboard.Shell;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Tests;

public sealed class SettingsScreenTests
{
    [Fact]
    public async Task Every_definition_has_a_row_and_a_change_calls_SetSetting()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SettingsScreenTests).Assembly);
        await session.Dispatch(() =>
        {
            var setup = new RecordingSetup();
            var shell = new ShellViewModel(setup);
            shell.SettingsCommand.Execute(null);
            Assert.Equal("Settings", shell.CurrentSection.Title);
            var window = new MainWindow { DataContext = shell, Width = 1240, Height = 760 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var rows = window.GetVisualDescendants().OfType<SettingRow>().ToList();
            var shown = setup.SettingDefinitions.Where(d => d.Key != SettingDefinitions.Source).Select(d => d.Title).ToList();
            Assert.Equal(shown, rows.Select(r => r.Title));

            var https = rows.Single(r => r.Title == setup.SettingDefinitions.Single(d => d.Key == SettingDefinitions.TrustedHttps).Title);
            https.GetVisualDescendants().OfType<ToggleSwitch>().Single().IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([$"set {SettingDefinitions.TrustedHttps} on"], setup.Calls);
            Assert.Equal("Waiting for your approval…", https.Status);

            setup.Release();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(https.Status);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }
}
