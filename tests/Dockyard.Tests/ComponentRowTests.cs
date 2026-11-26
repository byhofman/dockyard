using System.Net;
using Dockyard.Core;
using Dockyard.Hosts;
using Dockyard.ViewModels;

using Dockyard.Setup.Tests;

namespace Dockyard.Tests;

public sealed class ComponentRowTests
{
    sealed class RowsHost : IEngineHost
    {
        public Dictionary<string, Version?> Versions { get; } = new() { ["DockyardCli"] = new(1, 0, 0), ["Compose"] = new(2, 0, 0), [ComponentRules.DashboardId] = new(1, 0, 0) };
        public Version Newest { get; set; } = new(1, 1, 0);
        public List<string> Updated { get; } = [];
        public List<string> Changed { get; } = [];

        public IReadOnlyList<EngineInfo> Engines { get; } = [new("linux", "Linux", "Linux containers", "", "ctx", "unix:///x")];
        public IReadOnlyList<ComponentInfo> Components { get; } =
        [
            new("DockyardCli", "Dockyard CLI", RequiredBecause: "it runs everything"),
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
        public Task Uninstall(Action<string> log, bool removeData, bool silent) => Task.CompletedTask;
        public void ReleaseResources() { }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_refused_check_is_could_not_check_and_never_claims_up_to_date(HttpStatusCode status)
    {
        using var settings = new TempSettings();
        var host = new RowsHost { LatestFailure = new HttpRequestException("limited", null, status) };
        var vm = new MainViewModel(Facade.For(host, settings), setupMode: true);
        await vm.LoadSetup();

        Assert.Equal(UpdateState.CouldNotCheck, vm.Dashboard.State);
        Assert.False(vm.Dashboard.IsProblem);
        Assert.DoesNotContain("Everything is up to date", vm.LogText);
    }

    [Fact]
    public async Task Any_other_failed_check_stays_an_error()
    {
        using var settings = new TempSettings();
        var host = new RowsHost { LatestFailure = new HttpRequestException("gone", null, HttpStatusCode.InternalServerError) };
        var vm = new MainViewModel(Facade.For(host, settings), setupMode: true);
        await vm.LoadSetup();

        Assert.Equal(UpdateState.Error, vm.Dashboard.State);
    }

    [Fact]
    public async Task The_dashboard_shows_its_versions_and_updates_alone_without_touching_anything_else()
    {
        using var settings = new TempSettings();
        var host = new RowsHost();
        var vm = new MainViewModel(Facade.For(host, settings), setupMode: true);
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
    public async Task A_removed_component_is_written_to_the_settings_the_window_was_given_and_nowhere_else()
    {
        using var mine = new TempSettings();
        var userFile = Paths.SettingsFile;
        var userBefore = File.Exists(userFile) ? File.ReadAllText(userFile) : null;
        var vm = new MainViewModel(Facade.For(new RowsHost(), mine), setupMode: true);
        await vm.LoadSetup();

        await vm.Components.First(c => c.Id == "Compose").RemoveCommand!.ExecuteAsync();

        Assert.Equal(["Compose"], Settings.Load(mine.File).RemovedComponents);
        Assert.Equal(userBefore, File.Exists(userFile) ? File.ReadAllText(userFile) : null);
    }

    [Fact]
    public async Task Rows_say_why_something_is_required_and_only_optional_ones_can_change()
    {
        using var settings = new TempSettings();
        var host = new RowsHost();
        var vm = new MainViewModel(Facade.For(host, settings), setupMode: true);
        await vm.LoadSetup();
        var app = vm.Components.First(c => c.Id == "DockyardCli");
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
