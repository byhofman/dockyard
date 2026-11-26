using Dockyard.Core;
using Xunit;

namespace Dockyard.Tests;

public class SettingsTests
{
    [Fact]
    public void A_stale_copy_saves_only_what_it_changed()
    {
        using var temp = new TempSettings();
        temp.Settings.Save();
        var setup = Settings.Load(temp.File);
        var toolbox = Settings.Load(temp.File);

        toolbox.KubectlShell = true;
        toolbox.Save();
        setup.Source = ToolSource.Dockyard;
        setup.Save();

        var now = Settings.Load(temp.File);
        Assert.True(now.KubectlShell);
        Assert.Equal(ToolSource.Dockyard, now.Source);
        Assert.True(setup.KubectlShell);
    }

    [Fact]
    public void The_Dockyard_source_round_trips()
    {
        using var temp = new TempSettings();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.File)!);
        File.WriteAllText(temp.File, """{ "Source": "dockyard" }""");
        var settings = Settings.Load(temp.File);
        Assert.Equal(ToolSource.Dockyard, settings.Source);

        settings.Source = ToolSource.PackageManager;
        settings.Save();
        Assert.Contains("\"Source\": \"package-manager\"", File.ReadAllText(temp.File));
    }

    [Fact]
    public void The_source_defaults_to_the_package_manager()
    {
        using var temp = new TempSettings();
        Assert.Equal(ToolSource.PackageManager, Settings.Load(temp.File).Source);
    }
}
