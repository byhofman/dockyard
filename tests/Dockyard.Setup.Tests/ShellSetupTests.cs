using Dockyard.Core;
using Dockyard.Hosts;
using static Dockyard.Core.ShellSetup;
using Shell = Dockyard.Core.ShellSetup.Kind;

namespace Dockyard.Setup.Tests;

public sealed class ShellSetupTests : IDisposable
{
    readonly string home = Directory.CreateTempSubdirectory("dockyard-shell-home").FullName;

    public void Dispose() => Directory.Delete(home, recursive: true);

    [Fact]
    public void The_bash_block_is_the_setup_people_know_from_the_CKAD()
    {
        var block = Block(Part.Kubectl, Shell.Bash);

        Assert.Contains("alias k=kubectl\n", block);
        Assert.Contains("source <(kubectl completion bash)\n", block);
        Assert.Contains("complete -o default -F __start_kubectl k\n", block);
        Assert.Contains("export do=\"--dry-run=client -o yaml\"\n", block);
        Assert.Contains("export now=\"--force --grace-period 0\"\n", block);
    }

    [Fact]
    public void Zsh_and_PowerShell_get_do_and_now_as_lists_so_they_become_separate_arguments()
    {
        Assert.Contains("typeset -ga do=(--dry-run=client -o yaml)", Block(Part.Kubectl, Shell.Zsh));
        Assert.Contains("$now = @('--force', '--grace-period', '0')", Block(Part.Kubectl, Shell.PowerShell));
    }

    [Fact]
    public void Dockctx_is_a_block_of_its_own_and_not_in_kubectls()
    {
        Assert.Equal("# >>> dockyard dockctx >>>\nalias dockctx='dockyard ctx'\n# <<< dockyard dockctx <<<\n", Block(Part.Dockctx, Shell.Zsh));
        Assert.Contains("alias dockctx='dockyard ctx'\n", Block(Part.Dockctx, Shell.Bash));
        Assert.Contains("function dockctx { dockyard ctx @args }\n", Block(Part.Dockctx, Shell.PowerShell));
        foreach (var kind in Enum.GetValues<Shell>()) Assert.DoesNotContain("dockctx", Block(Part.Kubectl, kind));
    }

    [Fact]
    public void The_block_goes_at_the_end_once_and_comes_out_leaving_the_rest_as_it_was()
    {
        const string profile = "export PATH=$HOME/bin:$PATH\nalias ll='ls -l'\n";

        var with = With(profile, Part.Kubectl, Shell.Bash);
        var again = With(with, Part.Kubectl, Shell.Bash);

        Assert.StartsWith(profile.TrimEnd('\n') + "\n\n" + Begin(Part.Kubectl), with);
        Assert.Equal(with, again);
        Assert.Equal(profile, Without(with, Part.Kubectl));
        Assert.Equal(Block(Part.Kubectl, Shell.Bash), With(null, Part.Kubectl, Shell.Bash));
    }

    [Fact]
    public void Each_block_comes_out_on_its_own()
    {
        var both = With(With("# mine\n", Part.Kubectl, Shell.Zsh), Part.Dockctx, Shell.Zsh);

        Assert.Equal(With("# mine\n", Part.Kubectl, Shell.Zsh), Without(both, Part.Dockctx));
        Assert.Equal(With("# mine\n", Part.Dockctx, Shell.Zsh), Without(both, Part.Kubectl));
    }

    [Fact]
    public void A_block_between_other_lines_comes_out_and_crlf_files_stay_crlf()
    {
        var profile = "first\r\n\r\n" + Block(Part.Kubectl, Shell.PowerShell).Replace("\n", "\r\n") + "\r\nlast\r\n";

        Assert.Equal("first\r\n\r\nlast\r\n", Without(profile, Part.Kubectl));
        Assert.DoesNotContain("\r\r", With("Import-Module posh-git\r\n", Part.Kubectl, Shell.PowerShell));
        Assert.Contains("\r\nSet-Alias -Name k -Value kubectl\r\n", With("Import-Module posh-git\r\n", Part.Kubectl, Shell.PowerShell));
    }

    [Fact]
    public void Applying_writes_each_profile_and_off_takes_only_the_block_out()
    {
        var documents = Directory.CreateDirectory(Path.Combine(home, "Documents")).FullName;
        var profiles = Profiles(home, documents).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(profiles[0].File)!);
        File.WriteAllText(profiles[0].File, "# mine\n");
        List<string> log = [];

        Apply(Part.Kubectl, true, log.Add, home, documents);
        Assert.All(profiles, p => Assert.Contains(Begin(Part.Kubectl), File.ReadAllText(p.File)));
        Assert.Equal(profiles.Count, log.Count);

        Apply(Part.Kubectl, false, log.Add, home, documents);
        Assert.Equal("# mine\n", File.ReadAllText(profiles[0].File));
    }
}

public sealed class ShellSettingTests : IDisposable
{
    readonly TempSettings settings = new();
    readonly List<(Part Part, bool Enabled)> applied = [];

    public void Dispose() => settings.Dispose();

    DockyardSetup Setup(FakeHost host) => new(host, settings.Settings, new ShellProfiles((part, on, _) => applied.Add((part, on)), _ => []));

    [Fact]
    public void Dockctx_is_written_with_the_dockyard_command_without_being_asked_for()
    {
        Setup(new FakeHost()).ApplyShell(_ => { });

        Assert.Contains((Part.Dockctx, true), applied);
        Assert.Contains((Part.Kubectl, false), applied);
    }

    [Fact]
    public void Dockctx_goes_when_it_is_turned_off_or_the_dockyard_command_is_removed()
    {
        settings.Settings.Dockctx = false;
        Setup(new FakeHost()).ApplyShell(_ => { });
        settings.Settings.Dockctx = true;
        settings.Settings.RemovedComponents.Add(nameof(Component.DockyardCli));
        Setup(new FakeHost()).ApplyShell(_ => { });

        Assert.Equal([(Part.Dockctx, false), (Part.Dockctx, false)], applied.Where(a => a.Part == Part.Dockctx));
    }

    [Fact]
    public void A_profile_that_did_not_get_dockctx_turns_its_setting_off()
    {
        var setup = new DockyardSetup(new FakeHost(), settings.Settings,
            new ShellProfiles((_, _, _) => { }, part => part == Part.Dockctx ? ["/home/me/.zshrc"] : []));
        List<string> log = [];

        setup.ApplyShell(log.Add);

        Assert.False(Settings.Load(settings.File).Dockctx);
        Assert.Contains("dockctx could not be set up in /home/me/.zshrc, so the setting is off again", log);
    }

    [Fact]
    public void An_existing_settings_file_without_the_setting_has_dockctx_on()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settings.File)!);
        File.WriteAllText(settings.File, """{ "KubectlShell": false }""");

        Assert.True(Settings.Load(settings.File).Dockctx);
    }
}
