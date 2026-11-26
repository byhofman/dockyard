using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

// What front ends show of the catalog and the settings, decided here once.
public sealed class SettingCopyTests
{
    [Fact]
    public void The_Linux_engine_only_badge_is_shown_on_Windows_only()
    {
        var linuxOnly = SettingDefinitions.For([]).First(d => d.Key == SettingDefinitions.ContainerDomains);
        var everywhere = SettingDefinitions.For([]).First(d => d.Key == SettingDefinitions.StartEngineAtLogon);
        Assert.True(linuxOnly.LinuxEngineOnly);
        Assert.True(SettingDefinitions.ShowsLinuxEngineOnly(linuxOnly, windows: true));
        Assert.False(SettingDefinitions.ShowsLinuxEngineOnly(linuxOnly, windows: false));
        Assert.False(SettingDefinitions.ShowsLinuxEngineOnly(everywhere, windows: true));
        Assert.Equal(OperatingSystem.IsWindows(), SettingDefinitions.ShowsLinuxEngineOnly(linuxOnly));
    }

#pragma warning disable CA1416 // the catalogs are data on every platform
    public static TheoryData<string, string?> Notes()
    {
        var all = new TheoryData<string, string?>();
        foreach (var c in MacHost.Catalog.Concat(WindowsHost.CatalogFor(desktop: true)).Concat(LinuxHost.CatalogFor(desktop: true)))
            all.Add(c.Title, c.Note);
        return all;
    }
#pragma warning restore CA1416

    // Clusters are rows in Containers; the dashboard has had no Kubernetes section since.
    [Theory]
    [MemberData(nameof(Notes))]
    public void No_component_note_names_a_dashboard_section_that_is_gone(string title, string? note) =>
        Assert.False(note?.Contains("Kubernetes section", StringComparison.Ordinal) == true, $"{title}: {note}");
}
