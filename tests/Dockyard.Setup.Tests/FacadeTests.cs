using System.Runtime.Versioning;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

sealed class ProgressLines : IProgress<SetupProgress>
{
    public List<SetupProgress> All { get; } = [];
    public void Report(SetupProgress value) => All.Add(value);
}

public sealed class FacadeTests : IDisposable
{
    readonly TempSettings settings = new();
    readonly ProgressLines progress = new();

    public void Dispose() => settings.Dispose();

    DockyardSetup For(IEngineHost host) => Facade.For(host, settings);

    [Fact]
    public void The_library_has_no_UI_and_carries_its_resources()
    {
        var library = typeof(DockyardSetup).Assembly;
        Assert.DoesNotContain(library.GetReferencedAssemblies(), a => a.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Subset(library.GetManifestResourceNames().ToHashSet(), new HashSet<string> { "setup-linux.sh", "versions.env", "versions.sha256" });
    }

    [Fact]
    public async Task Every_setting_has_a_definition_and_round_trips()
    {
        var setup = For(new FakeHost { Installed = true });
        var definitions = setup.SettingDefinitions.Where(d => d.Kind is SettingKind.Toggle or SettingKind.Choice).ToList();
        Assert.NotEmpty(definitions);
        foreach (var definition in definitions)
        {
            var values = definition.Kind == SettingKind.Toggle ? ["off", "on"] : definition.Choices!.Concat(definition.Choices!).ToList();
            foreach (var value in values)
            {
                await setup.SetSetting(definition.Key, value, progress);
                Assert.Equal(value, setup.GetSetting(definition.Key));
            }
        }
    }

    [Fact]
    public async Task The_source_setting_takes_the_same_two_words_as_the_flag_and_shows_its_own()
    {
        var setup = For(new FakeHost { Installed = true });
        var source = setup.SettingDefinitions.Single(d => d.Key == SettingDefinitions.Source);
        Assert.Equal(["package-manager", "dockyard"], source.Choices);
        Assert.Equal([SettingDefinitions.PackageManager, SettingDefinitions.DockyardSource], source.ChoiceTitles);
        Assert.Equal("package-manager", setup.GetSetting(SettingDefinitions.Source));

        await setup.SetSetting(SettingDefinitions.Source, "dockyard", progress);

        Assert.Equal("dockyard", setup.GetSetting(SettingDefinitions.Source));
        Assert.Equal(ToolSource.Dockyard, Settings.Load(settings.File).Source);
        await Assert.ThrowsAsync<ArgumentException>(() => setup.SetSetting(SettingDefinitions.Source, "winget", progress));
    }

    [Fact]
    public void Every_definition_says_what_it_is()
    {
        var keys = For(new FakeHost()).SettingDefinitions.Select(d => d.Key).ToList();
        Assert.Contains("container-domains", keys);
        Assert.Contains("trusted-https", keys);
        Assert.Contains("start-engine-at-logon", keys);
        Assert.Contains("kubectl-shell", keys);
        Assert.Contains("source", keys);
        Assert.Contains("engine-file:linux", keys);
        Assert.All(SettingDefinitions.All, d => Assert.False(string.IsNullOrWhiteSpace(d.Description)));
        Assert.Equal(["container-domains", "trusted-https"], SettingDefinitions.All.Where(d => d.LinuxEngineOnly).Select(d => d.Key));
    }

    [Fact]
    public async Task An_unknown_setting_or_value_is_refused()
    {
        var setup = For(new FakeHost());
        Assert.Throws<ArgumentException>(() => setup.GetSetting("nonsense"));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.SetSetting("nonsense", "on", progress));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.SetSetting("kubectl-shell", "maybe", progress));
    }

    [Fact]
    public async Task Setup_reports_numbered_steps()
    {
        var host = new FakeHost
        {
            Installed = true,
            Components = [new("DockyardCli", "Dockyard CLI"), new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard"), new("Compose", "Docker Compose", Flag: "compose"), new("Buildx", "Docker Buildx", Flag: "buildx")],
        };
        host.Missing.Add("Compose");
        settings.Settings.RemovedComponents = ["Compose"];

        var code = await For(host).Setup(new SetupRequest(["compose"], ["buildx"], null, false, false, Silent: true, Resume: false) { Start = false }, progress, CancellationToken.None);

        Assert.Equal(DockyardSetup.Ok, code);
        var steps = progress.All.Where(p => p.Step is not null).ToList();
        Assert.Equal([1, 2, 3], steps.Select(p => p.Step!.Value));
        Assert.All(steps, p => Assert.Equal(3, p.Steps));
        Assert.Equal(["install dashboard=False silent=True", "add Compose", "remove Buildx"], host.Calls);
    }

    [Fact]
    public async Task Removing_what_is_required_is_refused_with_the_reason_and_changes_nothing()
    {
        var host = new FakeHost { Installed = true, Components = [new("DockyardCli", "Dockyard CLI", RequiredBecause: "it runs everything"), new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard")] };
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => For(host).Remove("DockyardCli", removeData: false, progress));
        Assert.Contains("it runs everything", refused.Message);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task A_component_is_found_by_its_id_or_its_flag_and_its_removal_is_remembered()
    {
        var host = new FakeHost { Installed = true, Components = [new("DockyardCli", "Dockyard CLI"), new("Compose", "Docker Compose", Flag: "compose")] };
        var setup = For(host);
        await setup.Remove("compose", removeData: false, progress);
        Assert.Equal(["Compose"], Settings.Load(settings.File).RemovedComponents);
        Assert.True(setup.Components().Single(c => c.Id == "Compose").CanAdd);

        await setup.Add("Compose", progress);
        Assert.Empty(Settings.Load(settings.File).RemovedComponents);
        Assert.Equal(["remove Compose", "add Compose"], host.Calls);
    }

    [Fact]
    public async Task The_rows_carry_the_installed_and_newest_versions_and_what_can_change()
    {
        var host = new FakeHost { Installed = true, Dashboard = true, Components = [new("DockyardCli", "Dockyard CLI", RequiredBecause: "it runs everything"), new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard", Note: "Optional.")] };
        var rows = await For(host).ComponentsAsync(CancellationToken.None);

        var app = rows.Single(r => r.Id == "DockyardCli");
        Assert.Equal(("Dockyard", "1.0.0", "1.0.0", "it runs everything"), (app.Group, app.Installed, app.Newest, app.RequiredBecause));
        Assert.False(app.CanRemove);
        Assert.False(app.CanUpdate);
        var dashboard = rows.Single(r => r.Id == ComponentRules.DashboardId);
        Assert.True(dashboard.CanRemove);
        Assert.False(dashboard.CanAdd);
        Assert.Equal("Optional.", dashboard.Needs);
        Assert.Equal("dashboard", dashboard.Flag);
        Assert.Null(app.Flag);
    }

    static IReadOnlyList<ComponentInfo> Apps => [new("DockyardCli", "Dockyard CLI", Flag: "cli"), new("Tray", "Dockyard tray", Flag: "tray"), new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard")];

    [Fact]
    public async Task The_dashboard_can_remove_the_CLI_and_the_CLI_the_dashboard_but_never_the_last_app()
    {
        var both = new FakeHost { Installed = true, Dashboard = true, SelfComponent = ComponentRules.DashboardId, Components = Apps };
        var rows = For(both).Components();
        Assert.True(rows.Single(r => r.Id == "DockyardCli").CanRemove);
        Assert.False(rows.Single(r => r.Id == ComponentRules.DashboardId).CanRemove);
        await For(both).Remove("cli", removeData: false, progress);
        Assert.Equal(["remove DockyardCli"], both.Calls);

        var cliOnly = new FakeHost { Installed = true, Dashboard = false, Components = Apps };
        Assert.False(For(cliOnly).Components().Single(r => r.Id == "DockyardCli").CanRemove);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => For(cliOnly).Remove("cli", removeData: false, progress));
        Assert.Equal(ComponentRules.LastAppReason, refused.Message);
    }

    [Theory]
    [InlineData(true, false, null, true)]
    [InlineData(false, true, null, true)]
    [InlineData(true, false, "dashboard", false)]
    [InlineData(false, false, null, false)]
    public async Task Setup_without_the_CLI_is_refused_when_no_dashboard_would_be_left(bool installed, bool headless, string? with, bool refused)
    {
        var host = new FakeHost { Installed = installed, Dashboard = false, Components = Apps };
        var request = new SetupRequest(with is null ? [] : [with], ["cli"], null, false, false, Silent: true, Resume: false) { Start = false, Headless = headless };
        var code = await For(host).Setup(request, progress, CancellationToken.None);
        if (refused)
        {
            Assert.Equal(DockyardSetup.WrongArguments, code);
            Assert.Contains(progress.All, p => p.Line == ComponentRules.LastAppReason);
        }
        else Assert.NotEqual(DockyardSetup.WrongArguments, code);
    }

    [Fact]
    public void Dockyard_itself_is_the_dockyard_command_on_every_system_and_not_the_tray() =>
        Assert.Equal("DockyardCli", For(new FakeHost { Installed = true }).SelfId);

    [Theory]
    [InlineData("Tray", "update DockyardCli,Dashboard,Tray")]
    [InlineData("DockyardCli", "update Tray,Dashboard,DockyardCli")]
    public async Task An_update_brings_dockyard_itself_too_last(string self, string expected)
    {
        var host = new FakeHost { Installed = true, SelfComponent = self, Newest = new Version(2, 0, 0) };
        var setup = For(host);
        await setup.UpdateAvailable(await setup.CheckUpdates(CancellationToken.None), new ProgressLines());
        Assert.Equal([expected], host.Calls);
    }

    const string DashboardExe = "/apps/dockyard-dashboard";

    // Both apps installed unless the test says otherwise; what is handed over is written down as "exe args".
    DockyardSetup FromApp(FakeHost host, string app, List<string> handedOff, params string[] installed)
    {
        host.DashboardExe = DashboardExe;
        string[] present = installed.Length > 0 ? installed : [Paths.DockyardCliExe, DashboardExe];
        return new(host, settings.Settings, new ShellProfiles((_, _) => { }, () => []))
        {
            Caller = app,
            HandOff = (exe, args) => handedOff.Add($"{exe} {string.Join(' ', args)}"),
            AppExists = present.Contains,
        };
    }

    [Fact]
    public async Task From_the_dashboard_its_own_update_and_dockyard_itself_go_to_the_dashboard_itself_and_it_exits()
    {
        var host = new FakeHost { Installed = true, Newest = new Version(2, 0, 0) };
        List<string> handedOff = [];
        var setup = FromApp(host, ComponentRules.DashboardId, handedOff);
        var exits = 0;
        setup.ExitRequested += () => exits++;

        await setup.Update(["DockyardCli", "Tray", ComponentRules.DashboardId], progress);

        Assert.Equal(["update Tray"], host.Calls);
        Assert.Equal([$"{DashboardExe} --update {ComponentRules.DashboardId} DockyardCli"], handedOff);
        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task From_the_dashboard_without_the_cli_everything_it_hands_over_goes_to_itself()
    {
        var host = new FakeHost { Installed = true, Newest = new Version(2, 0, 0) };
        List<string> handedOff = [];
        var setup = FromApp(host, ComponentRules.DashboardId, handedOff, DashboardExe);

        await setup.Update([ComponentRules.DashboardId], progress);

        Assert.Equal([$"{DashboardExe} --update {ComponentRules.DashboardId}"], handedOff);
    }

    [Fact]
    public async Task From_the_dashboard_removing_the_dashboard_goes_to_the_cli_which_stays()
    {
        var host = new FakeHost { Installed = true, Dashboard = true };
        List<string> handedOff = [];
        var setup = FromApp(host, ComponentRules.DashboardId, handedOff);
        var exits = 0;
        setup.ExitRequested += () => exits++;

        await setup.Remove("dashboard", removeData: false, progress);

        Assert.Empty(host.Calls);
        Assert.Equal([$"{Paths.DockyardCliExe} component remove dashboard"], handedOff);
        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task From_the_tray_removing_the_tray_goes_to_the_dashboard_else_the_cli_and_the_tray_exits()
    {
        var host = new FakeHost { Installed = true };
        List<string> handedOff = [];
        var setup = FromApp(host, "Tray", handedOff);
        var exits = 0;
        setup.ExitRequested += () => exits++;

        await setup.Remove("tray", removeData: false, progress);

        Assert.Empty(host.Calls);
        Assert.Equal([$"{DashboardExe} --component remove tray"], handedOff);
        Assert.Equal(1, exits);

        List<string> onlyCli = [];
        await FromApp(new FakeHost { Installed = true }, "Tray", onlyCli, Paths.DockyardCliExe).Remove("tray", removeData: false, progress);
        Assert.Equal([$"{Paths.DockyardCliExe} component remove tray"], onlyCli);
    }

    [Fact]
    public async Task An_app_whose_update_of_dockyard_itself_leaves_it_running_hands_the_update_over_and_stays()
    {
        var host = new FakeHost { Installed = true, SelfUpdateStopsApps = false };
        List<string> handedOff = [];
        var setup = FromApp(host, "Tray", handedOff);
        var exits = 0;
        setup.ExitRequested += () => exits++;

        await setup.Update(["DockyardCli"], progress);

        Assert.Equal([$"{DashboardExe} --update DockyardCli"], handedOff);
        Assert.Equal(0, exits);
    }

    [Fact]
    public async Task The_dockyard_command_does_every_step_itself()
    {
        var host = new FakeHost { Installed = true };
        List<IReadOnlyList<string>> handedOff = [];
        var setup = new DockyardSetup(host, settings.Settings, new ShellProfiles((_, _) => { }, () => [])) { Caller = null, HandOff = (_, args) => handedOff.Add(args) };

        await setup.Update(["DockyardCli", ComponentRules.DashboardId], progress);
        await setup.Remove("tray", removeData: false, progress);

        Assert.Equal(["update Dashboard,DockyardCli", "remove Tray"], host.Calls);
        Assert.Empty(handedOff);
    }

    [Fact]
    public async Task A_removal_keeps_what_another_front_end_removed_since_this_one_started()
    {
        var host = new FakeHost { Installed = true, Components = [new("DockyardCli", "Dockyard CLI"), new("Kind", "kind", Flag: "kind"), new("Helm", "Helm", Flag: "helm")] };
        var dashboard = For(host);
        var cli = Settings.Load(settings.File);
        cli.RemovedComponents.Add("Kind");
        cli.Save();

        await dashboard.Remove("helm", removeData: false, progress);

        Assert.Equal(["Helm", "Kind"], Settings.Load(settings.File).RemovedComponents.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_row_says_whether_a_setup_screen_starts_with_it_picked()
    {
        var host = new FakeHost { Components = TrayCatalog(desktop: false) };
        Assert.Equal([true, false, false], For(host).Components().Select(r => r.Default));

        host.Installed = true;
        host.Dashboard = true;
        settings.Settings.RemovedComponents = ["Tray"];
        settings.Settings.Save();
        Assert.Equal([true, false, true], For(host).Components().Select(r => r.Default));
    }

    static IReadOnlyList<ComponentInfo> TrayCatalog(bool desktop) =>
        [new("DockyardCli", "Dockyard CLI", RequiredBecause: "it installs everything"), new("Tray", "Dockyard tray", Flag: "tray", Default: desktop), new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard", Default: desktop)];

    [Fact]
    public async Task A_new_install_on_a_server_leaves_out_the_tray_and_the_dashboard_unless_asked()
    {
        var host = new FakeHost { Components = TrayCatalog(desktop: false) };
        Assert.Equal(DockyardSetup.Ok, await For(host).Setup(new SetupRequest([], [], null, false, false, Silent: true, Resume: false) { Start = false }, progress, CancellationToken.None));
        Assert.False(host.Requests.Single().Tray);
        Assert.False(host.Requests.Single().Dashboard);
        Assert.Contains("Tray", settings.Settings.RemovedComponents);

        var asked = new FakeHost { Components = TrayCatalog(desktop: false) };
        settings.Settings.RemovedComponents = [];
        Assert.Equal(DockyardSetup.Ok, await For(asked).Setup(new SetupRequest(["tray", "dashboard"], [], null, false, false, Silent: true, Resume: false) { Start = false }, progress, CancellationToken.None));
        Assert.True(asked.Requests.Single().Tray);
        Assert.True(asked.Requests.Single().Dashboard);
        Assert.DoesNotContain("Tray", settings.Settings.RemovedComponents);
    }

    [Fact]
    public async Task A_headless_request_leaves_out_the_tray_and_the_dashboard_on_a_new_install_unless_asked()
    {
        var headless = new FakeHost { Components = TrayCatalog(desktop: true) };
        Assert.Equal(DockyardSetup.Ok, await For(headless).Setup(new SetupRequest([], [], null, false, false, Silent: true, Resume: false) { Start = false, Headless = true }, progress, CancellationToken.None));
        Assert.False(headless.Requests.Single().Tray);
        Assert.False(headless.Requests.Single().Dashboard);

        var asked = new FakeHost { Components = TrayCatalog(desktop: true) };
        settings.Settings.RemovedComponents = [];
        Assert.Equal(DockyardSetup.Ok, await For(asked).Setup(new SetupRequest(["tray", "dashboard"], [], null, false, false, Silent: true, Resume: false) { Start = false, Headless = true }, progress, CancellationToken.None));
        Assert.True(asked.Requests.Single().Tray);
        Assert.True(asked.Requests.Single().Dashboard);
    }

    [Fact]
    public async Task A_new_install_on_a_desktop_has_the_tray_and_the_dashboard_unless_declined()
    {
        var host = new FakeHost { Components = TrayCatalog(desktop: true) };
        await For(host).Setup(new SetupRequest([], [], null, false, false, Silent: true, Resume: false) { Start = false }, progress, CancellationToken.None);
        Assert.True(host.Requests.Single().Tray);
        Assert.True(host.Requests.Single().Dashboard);

        var declined = new FakeHost { Components = TrayCatalog(desktop: true) };
        await For(declined).Setup(new SetupRequest([], ["tray"], null, false, false, Silent: true, Resume: false) { Start = false }, progress, CancellationToken.None);
        Assert.False(declined.Requests.Single().Tray);
    }

    [Fact]
    public async Task A_rerun_keeps_the_tray_as_it_is_and_the_tray_is_added_and_removed_like_any_optional_component()
    {
        var host = new FakeHost { Installed = true, Components = TrayCatalog(desktop: false) };
        settings.Settings.RemovedComponents = ["Tray"];
        settings.Settings.Save();
        await For(host).Setup(new SetupRequest([], [], null, false, false, Silent: true, Resume: false) { Start = false }, progress, CancellationToken.None);
        Assert.False(host.Requests.Single().Tray);

        var setup = For(host);
        await setup.Add("tray", progress);
        Assert.DoesNotContain("Tray", settings.Settings.RemovedComponents);
        await setup.Remove("tray", removeData: false, progress);
        Assert.Equal(["Tray"], Settings.Load(settings.File).RemovedComponents);
        Assert.Equal(["add Tray", "remove Tray"], host.Calls.Skip(1));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Container_domains_on_a_Windows_engine_only_machine_say_why_they_cannot_be_on()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        settings.Settings.RemovedComponents = [nameof(Component.LinuxEngine)];
        settings.Settings.Save();
        var setup = For(new WindowsHost(new FakeRunner()));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.SetSetting("container-domains", "on", progress));

        Assert.Contains("Linux engine", refused.Message);
    }
}
