using Dockyard.Core;

namespace Dockyard.Tests;

public class ComponentRequirementTests
{
    static Updates.ComponentEntry Needing(string? toolbox) =>
        new("dashboard/0.10.0", new Version(0, 10, 0), toolbox is null ? new Dictionary<string, Version>() : new() { ["toolbox"] = Version.Parse(toolbox) });

    [Fact]
    public void A_component_needing_a_newer_toolbox_waits_for_it()
    {
        Assert.False(Updates.RequirementsMet(Needing("0.10.0"), new Version(0, 9, 0)));
        Assert.True(Updates.RequirementsMet(Needing("0.10.0"), new Version(0, 10, 0)));
        Assert.True(Updates.RequirementsMet(Needing(null), new Version(0, 9, 0)));
        Assert.True(Updates.RequirementsMet(Needing("0.10.0"), new Version(0, 0, 0)));
    }
}

public class ShellBlockTests
{
    [Fact]
    public void A_block_without_its_end_is_recognised_and_not_added_to()
    {
        var broken = $"before\n{ShellSetup.Begin}\nalias k=kubectl\n";
        Assert.True(ShellSetup.HasBrokenBlock(broken));
        Assert.False(ShellSetup.HasBrokenBlock(ShellSetup.With("before\n", ShellSetup.Kind.Bash)));
        Assert.False(ShellSetup.HasBrokenBlock("nothing of Dockyard's\n"));
    }

    [Fact]
    public void Applying_leaves_a_profile_with_a_broken_block_as_it_is()
    {
        var home = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var bashrc = Path.Combine(home, ".bashrc");
            var broken = $"before\n{ShellSetup.Begin}\nalias k=kubectl\n";
            File.WriteAllText(bashrc, broken);
            var lines = new List<string>();
            ShellSetup.Apply(true, lines.Add, home, Path.Combine(home, "Documents"));
            Assert.Equal(broken, File.ReadAllText(bashrc));
            Assert.Contains(lines, l => l.Contains("without its end"));
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
