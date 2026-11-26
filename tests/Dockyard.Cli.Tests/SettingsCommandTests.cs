using Dockyard.Setup;

namespace Dockyard.Cli.Tests;

public sealed class SettingsCommandTests
{
    [Fact]
    public void Settings_ls_lists_every_definition_and_marks_Linux_engine_only_where_there_is_another_engine()
    {
        using var h = new CliHarness();

        Assert.Equal(0, h.Run("settings", "ls"));

        foreach (var definition in h.Setup.SettingDefinitions) Assert.Contains(definition.Key, h.Output);
        var lines = h.Output.Split('\n');
        Assert.Equal(OperatingSystem.IsWindows(), Assert.Single(lines, l => l.Contains(SettingDefinitions.ContainerDomains)).Contains("Linux engine only"));
        Assert.DoesNotContain("Linux engine only", Assert.Single(lines, l => l.Contains(SettingDefinitions.StartEngineAtLogon)));
    }

    [Fact]
    public void Settings_get_prints_the_value_and_set_changes_it()
    {
        using var h = new CliHarness();

        Assert.Equal(0, h.Run("settings", "set", SettingDefinitions.StartEngineAtLogon, "off"));
        Assert.Equal(0, h.Run("settings", "get", SettingDefinitions.StartEngineAtLogon));

        Assert.Equal("off", h.Output.Trim());
    }

    [Fact]
    public void Settings_set_an_unknown_key_exits_2()
    {
        using var h = new CliHarness();

        Assert.Equal(2, h.Run("settings", "set", "no-such-setting", "on"));
        Assert.Contains("no-such-setting", h.Error);
    }

    [Fact]
    public void Settings_set_a_value_the_setting_does_not_take_exits_2()
    {
        using var h = new CliHarness();

        Assert.Equal(2, h.Run("settings", "set", SettingDefinitions.StartEngineAtLogon, "maybe"));
    }

    [Fact]
    public void Engine_ls_marks_the_active_engine()
    {
        using var h = new CliHarness();

        Assert.Equal(0, h.Run("engine", "ls"));

        Assert.Contains("* linux", h.Output);
    }

    [Fact]
    public void Engine_use_of_an_engine_that_is_not_here_exits_2()
    {
        using var h = new CliHarness();

        Assert.Equal(2, h.Run("engine", "use", "plan9"));
        Assert.Contains("plan9", h.Error);
    }

    [Fact]
    public void Https_off_when_it_is_already_off_changes_nothing()
    {
        using var h = new CliHarness();

        Assert.Equal(0, h.Run("https", "off"));
    }
}
