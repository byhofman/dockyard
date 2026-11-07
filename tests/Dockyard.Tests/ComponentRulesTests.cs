using System.Net;
using Dockyard.Core;
using Dockyard.Hosts;
using Dockyard.ViewModels;

namespace Dockyard.Tests;

public sealed class ComponentRulesTests
{
    static readonly IReadOnlyList<ComponentInfo> Catalog =
    [
        new("Cli", "Docker CLI", RequiredBecause: "the rest uses it", RequiredFlag: "cli"),
        new("Engine", "Windows engine", Flag: "windows-engine", IsEngine: true),
        new("LinuxEngine", "Linux engine", Flag: "linux-engine", IsEngine: true),
        new("Compose", "Compose", Flag: "compose"),
        new("App", "Dockyard", RequiredBecause: "it runs everything"),
        new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard"),
    ];

    [Fact]
    public void What_is_required_is_refused_with_the_reason()
    {
        var why = ComponentRules.WhyNotRemovable(Catalog, "Cli", new HashSet<string> { "Cli", "Engine", "LinuxEngine" });
        Assert.Contains("the rest uses it", why);
        Assert.Contains("it runs everything", ComponentRules.WhyNotRemovable(Catalog, "App", new HashSet<string>()));
    }

    [Fact]
    public void One_of_two_engines_can_go_but_not_the_last()
    {
        Assert.Null(ComponentRules.WhyNotRemovable(Catalog, "Engine", new HashSet<string> { "Engine", "LinuxEngine" }));
        Assert.Contains("last engine", ComponentRules.WhyNotRemovable(Catalog, "LinuxEngine", new HashSet<string> { "LinuxEngine" }));
    }

    [Fact]
    public void A_silent_request_is_refused_before_anything_changes()
    {
        Assert.Empty(ComponentRules.Problems(Catalog, ["compose"], ["windows-engine"], []));
        Assert.Contains("required", ComponentRules.Problems(Catalog, [], ["cli"], [])[0]);
        Assert.Contains("does not exist", ComponentRules.Problems(Catalog, ["buildx"], [], [])[0]);
        Assert.Contains("engine", ComponentRules.Problems(Catalog, [], ["windows-engine", "linux-engine"], [])[0]);
        Assert.Contains("engine", ComponentRules.Problems(Catalog, [], ["linux-engine"], ["Engine"])[0]);
        Assert.Empty(ComponentRules.Problems(Catalog, ["windows-engine"], ["linux-engine"], ["Engine"]));
    }

    [Fact]
    public void What_was_removed_stays_removed_across_reruns_until_asked_for()
    {
        Assert.Equal(["Compose"], ComponentRules.Removed(Catalog, ["Compose"], [], []));
        Assert.Equal(["Compose", "Engine"], ComponentRules.Removed(Catalog, ["Compose"], [], ["windows-engine"]));
        Assert.Empty(ComponentRules.Removed(Catalog, ["Compose"], ["compose"], []));
    }

    sealed class RowsHost : IEngineHost
    {
        public Dictionary<string, Version?> Versions { get; } = new() { ["App"] = new(1, 0, 0), ["Compose"] = new(2, 0, 0), [ComponentRules.DashboardId] = new(1, 0, 0) };
        public Version Newest { get; set; } = new(1, 1, 0);
        public List<string> Updated { get; } = [];
        public List<string> Changed { get; } = [];

        public IReadOnlyList<EngineInfo> Engines { get; } = [new("linux", "Linux", "Linux containers", "", "ctx", "unix:///x")];
        public IReadOnlyList<ComponentInfo> Components { get; } =
        [
            new("App", "Dockyard", RequiredBecause: "it runs everything"),
            new("Compose", "Compose", Flag: "compose", Note: "Needs the CLI"),
            new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard"),
        ];
        public bool IsInstalled => true;
        public bool RunsFromInstallLocation => false;
        public bool HasDashboard => Versions[ComponentRules.DashboardId] is not null;
        public Task<bool> IsRunning(string engine) => Task.FromResult(false);
        public Task Start(string engine) => Task.CompletedTask;
        public Task Stop(string engine) => Task.CompletedTask;
        public string ActiveEngine() => "linux";
        public Task UseContext(string engine) => Task.CompletedTask;
        public Task<Version?> Installed(string component) => Task.FromResult(Versions.GetValueOrDefault(component));
        public Exception? LatestFailure { get; set; }
        public Task<Release?> Latest(string component) => LatestFailure is { } failure
            ? Task.FromException<Release?>(failure)
            : Task.FromResult<Release?>(new Release(Newest, $"v{Newest}", null));
        public Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components)
        {
            foreach (var (component, release) in components)
            {
                Updated.Add(component);
                Versions[component] = release!.Version;
            }
            return Task.FromResult(false);
        }
        public Task<bool> Install(Action<string> log, InstallRequest request) => Task.FromResult(false);
        public void StartApp() { }
        public string DoneMessage(bool reboot) => "done";
        public Task<bool> SetDashboard(Action<string> log, bool installed) => Task.FromResult(false);
        public Task<bool> SetComponent(Action<string> log, string component, bool installed, bool removeData = false)
        {
            Changed.Add($"{component}={installed}");
            Versions[component] = installed ? new Version(2, 0, 0) : null;
            return Task.FromResult(false);
        }
        public Task Uninstall(Action<string> log, bool removeData) => Task.CompletedTask;
        public void ReleaseResources() { }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_refused_check_is_could_not_check_and_never_claims_up_to_date(HttpStatusCode status)
    {
        var host = new RowsHost { LatestFailure = new HttpRequestException("limited", null, status) };
        var vm = new MainViewModel(host, setupMode: true);
        await vm.LoadSetup();

        Assert.Equal(UpdateState.CouldNotCheck, vm.Dashboard.State);
        Assert.False(vm.Dashboard.IsProblem);
        Assert.DoesNotContain("Everything is up to date", vm.LogText);
    }

    [Fact]
    public async Task Any_other_failed_check_stays_an_error()
    {
        var host = new RowsHost { LatestFailure = new HttpRequestException("gone", null, HttpStatusCode.InternalServerError) };
        var vm = new MainViewModel(host, setupMode: true);
        await vm.LoadSetup();

        Assert.Equal(UpdateState.Error, vm.Dashboard.State);
    }

    [Fact]
    public async Task The_dashboard_shows_its_versions_and_updates_alone_without_touching_anything_else()
    {
        var host = new RowsHost();
        var vm = new MainViewModel(host, setupMode: true);
        await vm.LoadSetup();

        Assert.Equal("1.0.0", vm.Dashboard.Installed);
        Assert.Equal("1.1.0", vm.Dashboard.Latest);
        Assert.True(vm.Dashboard.IsAvailable);
        Assert.True(vm.Dashboard.CanRemove);

        await vm.Dashboard.UpdateCommand.ExecuteAsync();

        Assert.Equal([ComponentRules.DashboardId], host.Updated);
        Assert.Equal("1.1.0", vm.Dashboard.Installed);
        Assert.False(vm.Dashboard.IsAvailable);
        Assert.True(vm.Dashboard.CanRemove);
    }

    [Fact]
    public async Task Rows_say_why_something_is_required_and_only_optional_ones_can_change()
    {
        var host = new RowsHost();
        var vm = new MainViewModel(host, setupMode: true);
        await vm.LoadSetup();
        var app = vm.Components.First(c => c.Id == "App");
        var compose = vm.Components.First(c => c.Id == "Compose");

        Assert.Equal("Required: it runs everything", app.Note);
        Assert.False(app.CanAdd);
        Assert.False(app.CanRemove);
        Assert.Equal("Needs the CLI", compose.Note);
        Assert.True(compose.CanRemove);
        Assert.Equal("Remove-Compose", compose.RemoveId);

        await compose.RemoveCommand!.ExecuteAsync();

        Assert.Equal(["Compose=False"], host.Changed);
        Assert.True(compose.CanAdd);
        Assert.False(compose.CanRemove);
        Assert.False(compose.IsProblem);

        await compose.AddCommand!.ExecuteAsync();
        Assert.Equal(["Compose=False", "Compose=True"], host.Changed);
        Assert.True(compose.CanRemove);
    }
}
