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
        setup.UsePackageManager = false;
        setup.Save();

        var now = Settings.Load(temp.File);
        Assert.True(now.KubectlShell);
        Assert.False(now.UsePackageManager);
        Assert.True(setup.KubectlShell);
    }
}
