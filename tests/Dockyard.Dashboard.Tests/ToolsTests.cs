using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dockyard.Dashboard.Controls;
using Dockyard.Dashboard.Features.Setup;
using Dockyard.Dashboard.Shell;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Tests;

public sealed class ToolsTests
{
    static bool HasButton(Control row, string text) =>
        row.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == text && b.IsEffectivelyVisible && b.Opacity > 0);

    static (MainWindow Window, ShellViewModel Shell, ToolsViewModel Tools) Open(RecordingSetup setup)
    {
        var shell = new ShellViewModel(setup);
        shell.ShowSection("Tools");
        var window = new MainWindow { DataContext = shell, Width = 1240, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell, (ToolsViewModel)shell.CurrentSection.Content);
    }

    [Fact]
    public async Task Every_catalog_row_is_drawn_and_a_required_one_has_no_remove_button()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ToolsTests).Assembly);
        await session.Dispatch(() =>
        {
            var setup = new RecordingSetup();
            var (window, _, _) = Open(setup);
            var rows = window.GetVisualDescendants().OfType<ComponentRowView>().ToList();
            Assert.Equal(setup.Components().Select(r => r.Title), rows.Select(r => r.Title));
            foreach (var row in rows)
            {
                var data = setup.Components().Single(r => r.Title == row.Title);
                if (data.RequiredBecause is not null) Assert.False(HasButton(row, "Remove"), $"{row.Title} is required but can be removed");
                Assert.Equal(data.Id == setup.SelfId, HasButton(row, "Uninstall…"));
            }
            Assert.True(HasButton(rows.Single(r => r.Title == "Docker Compose"), "Remove"));
            Assert.True(HasButton(rows.Single(r => r.Title == "Helm"), "Add"));
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Add_runs_through_the_facade_and_shows_numbered_progress()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ToolsTests).Assembly);
        await session.Dispatch(() =>
        {
            var setup = new RecordingSetup
            {
                Lines = [new("Adding Helm", Step: 1, Steps: 2), new("brew install helm", SetupProgressKind.Command), new("Setting up kubectl in your .zshrc", Step: 2, Steps: 2)],
            };
            var (window, _, tools) = Open(setup);
            var helm = tools.Groups.SelectMany(g => g.Items).Single(i => i.Row.Id == "Helm");
            tools.AddCommand.Execute(helm);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["add Helm"], setup.Calls);
            Assert.Equal("Adding…", helm.BusyText);
            var panel = window.GetVisualDescendants().OfType<ProgressPanel>().Single(p => p.IsEffectivelyVisible);
            Assert.Equal((2, 2), (panel.Step, panel.Steps));
            Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Step 2 of 2");
            Assert.Contains(panel.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text == "brew install helm");

            setup.Release();
            Dispatcher.UIThread.RunJobs();
            Assert.False(tools.IsBusy);
            Assert.Null(tools.Run!.Error);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Update_all_takes_Dockyard_itself_along()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ToolsTests).Assembly);
        await session.Dispatch(() =>
        {
            var setup = new RecordingSetup
            {
                Rows = PrototypeScreens.MacRows(installed: true)
                    .Select(r => r.Id == "DockyardCli" ? r with { Newest = "0.15.0", CanUpdate = true } : r).ToList(),
            };
            var tools = new ToolsViewModel(setup);
            _ = tools.Load();
            Dispatcher.UIThread.RunJobs();

            tools.UpdateAllCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["update DockyardCli,DockerCli,Kind"], setup.Calls);
            setup.Release();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Download_source_is_set_in_Tools_and_not_in_Settings()
    {
        var setup = new RecordingSetup();
        Assert.Equal(SettingDefinitions.Source, new ToolsViewModel(setup).SourceSetting.Definition.Key);
        Assert.DoesNotContain(new SettingsViewModel(setup).Items, i => i.Definition.Key == SettingDefinitions.Source);
    }
}
