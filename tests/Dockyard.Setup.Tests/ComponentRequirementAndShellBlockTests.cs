using Dockyard.Core;

namespace Dockyard.Setup.Tests;

public class ShellBlockTests
{
    [Fact]
    public void A_block_without_its_end_is_recognised_and_not_added_to()
    {
        var broken = $"before\n{ShellSetup.Begin(ShellSetup.Part.Kubectl)}\nalias k=kubectl\n";
        Assert.True(ShellSetup.HasBrokenBlock(broken, ShellSetup.Part.Kubectl));
        Assert.False(ShellSetup.HasBrokenBlock(ShellSetup.With("before\n", ShellSetup.Part.Kubectl, ShellSetup.Kind.Bash), ShellSetup.Part.Kubectl));
        Assert.False(ShellSetup.HasBrokenBlock("nothing of Dockyard's\n", ShellSetup.Part.Kubectl));
    }

    [Fact]
    public void Missing_names_each_profile_without_the_whole_block()
    {
        var home = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var documents = Path.Combine(home, "Documents");
            Assert.NotEmpty(ShellSetup.Missing(ShellSetup.Part.Kubectl, home, documents));
            ShellSetup.Apply(ShellSetup.Part.Kubectl, true, _ => { }, home, documents);
            Assert.Empty(ShellSetup.Missing(ShellSetup.Part.Kubectl, home, documents));
            var first = ShellSetup.Profiles(home, documents).First().File;
            File.WriteAllText(first, $"{ShellSetup.Begin(ShellSetup.Part.Kubectl)}\nalias k=kubectl\n");
            Assert.Equal([first], ShellSetup.Missing(ShellSetup.Part.Kubectl, home, documents));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void Applying_leaves_a_profile_with_a_broken_block_as_it_is()
    {
        var home = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var bashrc = Path.Combine(home, ".bashrc");
            var broken = $"before\n{ShellSetup.Begin(ShellSetup.Part.Kubectl)}\nalias k=kubectl\n";
            File.WriteAllText(bashrc, broken);
            var lines = new List<string>();
            ShellSetup.Apply(ShellSetup.Part.Kubectl, true, lines.Add, home, Path.Combine(home, "Documents"));
            Assert.Equal(broken, File.ReadAllText(bashrc));
            Assert.Contains(lines, l => l.Contains("without its end"));
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
