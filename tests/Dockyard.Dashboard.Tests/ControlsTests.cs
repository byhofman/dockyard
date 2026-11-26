using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dockyard.Dashboard.Controls;
using Dockyard.Dashboard.Features.Setup;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Tests;

public sealed class ControlsTests
{
    static T Shown<T>(T control) where T : Control
    {
        var window = new Window { Width = 800, Height = 400, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return control;
    }

    static bool Says(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

    [Fact]
    public async Task A_setting_row_shows_the_Linux_engine_only_badge_only_when_the_definition_says_so()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ControlsTests).Assembly);
        await session.Dispatch(() =>
        {
            var on = Shown(new SettingRow { Title = "Container domains", IsLinuxEngineOnly = true });
            var off = Shown(new SettingRow { Title = "Start the active engine at logon", IsLinuxEngineOnly = false });
            Assert.True(Says(on, "Linux engine only"));
            Assert.False(Says(off, "Linux engine only"));

            // The screen asks the facade's rule, which keeps the badge to Windows, where there is a second engine.
            var item = new SettingsViewModel(new RecordingSetup()).Items.First(i => i.Definition.Key == SettingDefinitions.ContainerDomains);
            Assert.True(item.Definition.LinuxEngineOnly);
            Assert.Equal(OperatingSystem.IsWindows(), item.ShowsLinuxEngineOnly);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_setting_editor_follows_the_setting_kind()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ControlsTests).Assembly);
        await session.Dispatch(() =>
        {
            var expected = new Dictionary<SettingKind, Type>
            {
                [SettingKind.Toggle] = typeof(ToggleSwitch),
                [SettingKind.Choice] = typeof(ComboBox),
                [SettingKind.Text] = typeof(TextBox),
                [SettingKind.EngineFile] = typeof(Button),
            };
            foreach (var (kind, type) in expected)
            {
                var editor = Shown(new SettingEditor { Kind = kind, Choices = ["Homebrew", "Dockyard"] });
                Assert.IsType(type, editor.Content);
            }

            var toggle = Shown(new SettingEditor { Kind = SettingKind.Toggle, Value = SettingDefinitions.Off });
            ((ToggleSwitch)toggle.Content!).IsChecked = true;
            Assert.Equal(SettingDefinitions.On, toggle.Value);

            var choice = Shown(new SettingEditor { Kind = SettingKind.Choice, Choices = ["Homebrew", "Dockyard"], Value = "Homebrew" });
            Assert.Equal("Homebrew", ((ComboBox)choice.Content!).SelectedItem);
            ((ComboBox)choice.Content!).SelectedItem = "Dockyard";
            Assert.Equal("Dockyard", choice.Value);

            var titled = Shown(new SettingEditor { Kind = SettingKind.Choice, Choices = ["package-manager", "dockyard"], ChoiceTitles = ["Homebrew", "Dockyard"], Value = "package-manager" });
            Assert.Equal("Homebrew", ((ComboBox)titled.Content!).SelectedItem);
            ((ComboBox)titled.Content!).SelectedItem = "Dockyard";
            Assert.Equal("dockyard", titled.Value);
        }, TestContext.Current.CancellationToken);
    }
}
