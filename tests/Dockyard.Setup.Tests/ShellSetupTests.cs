using Dockyard.Core;

namespace Dockyard.Setup.Tests;

public sealed class ShellSetupTests : IDisposable
{
    readonly string home = Directory.CreateTempSubdirectory("dockyard-shell-home").FullName;

    public void Dispose() => Directory.Delete(home, recursive: true);

    [Fact]
    public void The_bash_block_is_the_setup_people_know_from_the_CKAD()
    {
        var block = ShellSetup.Block(ShellSetup.Kind.Bash);

        Assert.Contains("alias k=kubectl\n", block);
        Assert.Contains("alias dockctx='dockyard ctx'\n", block);
        Assert.Contains("source <(kubectl completion bash)\n", block);
        Assert.Contains("complete -o default -F __start_kubectl k\n", block);
        Assert.Contains("export do=\"--dry-run=client -o yaml\"\n", block);
        Assert.Contains("export now=\"--force --grace-period 0\"\n", block);
    }

    [Fact]
    public void Zsh_and_PowerShell_get_do_and_now_as_lists_so_they_become_separate_arguments()
    {
        Assert.Contains("alias dockctx='dockyard ctx'\n", ShellSetup.Block(ShellSetup.Kind.Zsh));
        Assert.Contains("function dockctx { dockyard ctx @args }\n", ShellSetup.Block(ShellSetup.Kind.PowerShell));
        Assert.Contains("typeset -ga do=(--dry-run=client -o yaml)", ShellSetup.Block(ShellSetup.Kind.Zsh));
        Assert.Contains("$now = @('--force', '--grace-period', '0')", ShellSetup.Block(ShellSetup.Kind.PowerShell));
    }

    [Fact]
    public void The_block_goes_at_the_end_once_and_comes_out_leaving_the_rest_as_it_was()
    {
        const string profile = "export PATH=$HOME/bin:$PATH\nalias ll='ls -l'\n";

        var with = ShellSetup.With(profile, ShellSetup.Kind.Bash);
        var again = ShellSetup.With(with, ShellSetup.Kind.Bash);

        Assert.StartsWith(profile.TrimEnd('\n') + "\n\n" + ShellSetup.Begin, with);
        Assert.Equal(with, again);
        Assert.Equal(profile, ShellSetup.Without(with));
        Assert.Equal(ShellSetup.Block(ShellSetup.Kind.Bash), ShellSetup.With(null, ShellSetup.Kind.Bash));
    }

    [Fact]
    public void A_block_between_other_lines_comes_out_and_crlf_files_stay_crlf()
    {
        var profile = "first\r\n\r\n" + ShellSetup.Block(ShellSetup.Kind.PowerShell).Replace("\n", "\r\n") + "\r\nlast\r\n";

        Assert.Equal("first\r\n\r\nlast\r\n", ShellSetup.Without(profile));
        Assert.DoesNotContain("\r\r", ShellSetup.With("Import-Module posh-git\r\n", ShellSetup.Kind.PowerShell));
        Assert.Contains("\r\nSet-Alias -Name k -Value kubectl\r\n", ShellSetup.With("Import-Module posh-git\r\n", ShellSetup.Kind.PowerShell));
    }

    [Fact]
    public void Applying_writes_each_profile_and_off_takes_only_the_block_out()
    {
        var documents = Directory.CreateDirectory(Path.Combine(home, "Documents")).FullName;
        var profiles = ShellSetup.Profiles(home, documents).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(profiles[0].File)!);
        File.WriteAllText(profiles[0].File, "# mine\n");
        List<string> log = [];

        ShellSetup.Apply(true, log.Add, home, documents);
        Assert.All(profiles, p => Assert.Contains(ShellSetup.Begin, File.ReadAllText(p.File)));
        Assert.Equal(profiles.Count, log.Count);

        ShellSetup.Apply(false, log.Add, home, documents);
        Assert.Equal("# mine\n", File.ReadAllText(profiles[0].File));
    }
}
