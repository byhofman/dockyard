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
            Components = [new("App", "Dockyard"), new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard"), new("Compose", "Docker Compose", Flag: "compose"), new("Buildx", "Docker Buildx", Flag: "buildx")],
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
        var host = new FakeHost { Installed = true, Components = [new("App", "Dockyard", RequiredBecause: "it runs everything"), new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard")] };
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => For(host).Remove("App", removeData: false, progress));
        Assert.Contains("it runs everything", refused.Message);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task A_component_is_found_by_its_id_or_its_flag_and_its_removal_is_remembered()
    {
        var host = new FakeHost { Installed = true, Components = [new("App", "Dockyard"), new("Compose", "Docker Compose", Flag: "compose")] };
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
        var host = new FakeHost { Installed = true, Dashboard = true, Components = [new("App", "Dockyard", RequiredBecause: "it runs everything"), new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard", Note: "Optional.")] };
        var rows = await For(host).ComponentsAsync(CancellationToken.None);

        var app = rows.Single(r => r.Id == "App");
        Assert.Equal(("Dockyard", "1.0.0", "1.0.0", "it runs everything"), (app.Group, app.Installed, app.Newest, app.RequiredBecause));
        Assert.False(app.CanRemove);
        Assert.False(app.CanUpdate);
        var dashboard = rows.Single(r => r.Id == ComponentRules.DashboardId);
        Assert.True(dashboard.CanRemove);
        Assert.False(dashboard.CanAdd);
        Assert.Equal("Optional.", dashboard.Needs);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Container_domains_on_a_Windows_engine_only_machine_say_why_they_cannot_be_on()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        settings.Settings.RemovedComponents = [nameof(Component.LinuxEngine)];
        var setup = For(new WindowsHost(new FakeRunner()));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.SetSetting("container-domains", "on", progress));

        Assert.Contains("Linux engine", refused.Message);
    }
}
