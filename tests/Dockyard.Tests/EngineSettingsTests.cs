using System.Text.Json.Nodes;
using Dockyard.Core;
using Dockyard.Hosts;
using Dockyard.Setup.Tests;
using Dockyard.Shared;

namespace Dockyard.Tests;

public sealed class EngineSettingsTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("dockyard-engine-settings").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    sealed class SettingsHost(string? problem = null) : IEngineHost
    {
        public string Stored = """{ "log-level": "warn" }""";
        public List<string> Applied { get; } = [];
        public IReadOnlyList<EngineInfo> Engines { get; } = [new("linux", "Linux", "Linux containers", "", "linux", "npipe://x"), new("win", "Windows", "Windows containers", "", "win", "npipe://y")];
        public IReadOnlyList<ComponentInfo> Components { get; } = [];
        public bool IsInstalled => true;
        public bool RunsFromInstallLocation => true;
        public bool HasDashboard => false;
        public Task<bool> IsRunning(string engine) => Task.FromResult(true);
        public Task Start(string engine) => Task.CompletedTask;
        public Task Stop(string engine) => Task.CompletedTask;
        public string ActiveEngine() => "linux";
        public Task UseContext(string engine) => Task.CompletedTask;
        public Task<Version?> Installed(string component) => Task.FromResult<Version?>(null);
        public Task<Release?> Latest(string component) => Task.FromResult<Release?>(null);
        public Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components) => Task.FromResult(false);
        public Task<bool> Install(Action<string> log, InstallRequest request) => Task.FromResult(false);
        public void StartApp() { }
        public string DoneMessage(bool reboot) => "";
        public Task<bool> SetDashboard(Action<string> log, bool installed) => Task.FromResult(false);
        public Task Uninstall(Action<string> log, bool removeData, bool silent) => Task.CompletedTask;
        public void ReleaseResources() { }

        public Task<string?> ReadEngineSettings(string engine) => Task.FromResult<string?>(engine + ":" + Stored);
        public Task<string?> ValidateEngineSettings(string engine, string json) => Task.FromResult(EngineSettings.Problem(json) ?? problem);

        public Task ApplyEngineSettings(Action<string> log, string engine, string json)
        {
            Applied.Add($"{engine} {json}");
            log("restarted");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void What_the_user_saved_is_what_comes_back_and_Dockyard_adds_its_dns_when_it_writes_the_engine_s_file()
    {
        EngineSettings.Save(EngineSettings.Linux, """{ "log-level": "warn", "features": { "containerd-snapshotter": true } }""", dir);

        var saved = EngineSettings.Load(EngineSettings.Linux, dir);
        var written = JsonNode.Parse(EngineConfig.WithDockyard(saved))!.AsObject();

        Assert.Equal("warn", written["log-level"]!.GetValue<string>());
        Assert.True(written["features"]!["containerd-snapshotter"]!.GetValue<bool>());
        Assert.Equal(EngineConfig.DnsAddress, written["dns"]![0]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{ \"debug\": true }", null)]
    [InlineData("[1, 2]", "one JSON object")]
    [InlineData("{ \"debug\": true, }", "line 1")]
    [InlineData("{\n  \"debug\": tru\n}", "line 2")]
    public void A_problem_says_where(string json, string? expected)
    {
        var problem = EngineSettings.Problem(json);
        if (expected is null) Assert.Null(problem);
        else Assert.Contains(expected, problem);
    }

    [Fact]
    public void Keys_Dockyard_writes_over_are_named()
    {
        Assert.Equal(["dns"], EngineSettings.Overridden("linux", """{ "dns": ["1.1.1.1"], "debug": true }"""));
        Assert.Equal(["hosts"], EngineSettings.Overridden("win", """{ "hosts": ["tcp://0.0.0.0:2375"] }"""));
        Assert.Empty(EngineSettings.Overridden("win", """{ "group": "admins" }"""));
    }

    [Fact]
    public void The_Windows_engine_keeps_its_pipe_and_its_users_whatever_the_edit_says()
    {
        var written = JsonNode.Parse(DaemonJson.WithAccessFor("""{ "hosts": ["tcp://0.0.0.0:2375"], "group": "admins", "debug": true }""", "npipe:////./pipe/docker_windows", "me"))!;

        Assert.Equal(["npipe:////./pipe/docker_windows"], written["hosts"]!.AsArray().Select(h => h!.GetValue<string>()));
        Assert.Equal("admins,docker-users,me", written["group"]!.GetValue<string>());
        Assert.True(written["debug"]!.GetValue<bool>());
    }

    readonly List<string> output = [];
    readonly List<string> errors = [];

    async Task<int> Run(IEngineHost host, params string[] args)
    {
        using var settings = new TempSettings();
        return await Cli.Execute(Facade.For(host, settings), args, output.Add, errors.Add);
    }

    [Fact]
    public async Task Config_show_prints_the_active_engine_s_settings_or_the_one_named()
    {
        var host = new SettingsHost();

        Assert.Equal(0, await Run(host, "config", "show"));
        Assert.Equal(0, await Run(host, "config", "show", "--engine", "win"));

        Assert.Equal(["linux:" + host.Stored, "win:" + host.Stored], output);
    }

    [Fact]
    public async Task Config_apply_checks_first_and_names_keys_Dockyard_replaces()
    {
        var file = Path.Combine(dir, "daemon.json");
        File.WriteAllText(file, """{ "dns": ["1.1.1.1"], "debug": true }""");
        var host = new SettingsHost();

        Assert.Equal(0, await Run(host, "config", "apply", "--file", file));

        Assert.Equal(["linux { \"dns\": [\"1.1.1.1\"], \"debug\": true }"], host.Applied);
        Assert.Contains(output, l => l.StartsWith("dns: Dockyard sets this one itself", StringComparison.Ordinal));
        Assert.Contains("restarted", output);
    }

    [Fact]
    public async Task Settings_the_engine_refuses_are_not_applied()
    {
        var file = Path.Combine(dir, "daemon.json");
        File.WriteAllText(file, """{ "log-level": "loud" }""");
        var host = new SettingsHost(problem: "invalid log level: loud");

        Assert.Equal(1, await Run(host, "config", "apply", "--file", file));

        Assert.Empty(host.Applied);
        Assert.Equal(["invalid log level: loud"], errors);
    }

    [Fact]
    public async Task Engine_ls_marks_the_active_engine_and_the_others_take_an_engine_there_is()
    {
        var host = new SettingsHost();

        Assert.Equal(0, await Run(host, "engine", "ls"));
        Assert.Equal(0, await Run(host, "engine", "use", "win"));
        Assert.Equal(2, await Run(host, "engine", "start", "mars"));
        Assert.Equal(2, await Run(host, "engine", "start"));

        Assert.Equal(["* linux\tLinux containers\trunning", "  win\tWindows containers\trunning", "docker uses win"], output.Take(3));
        Assert.Contains(errors, e => e.StartsWith("An engine is needed: linux, win", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_new_commands_are_commands_and_https_takes_on_or_off()
    {
        foreach (var command in new[] { "engine", "https", "update", "config" }) Assert.True(Cli.IsCommand([command, "x"]));
        Assert.False(Cli.IsCommand(["cluster", "ls"]));
        Assert.Equal(2, await Run(new SettingsHost(), "https", "maybe"));
    }

    [Fact]
    public async Task Wrong_arguments_exit_2_with_the_usage()
    {
        var host = new SettingsHost();

        Assert.Equal(2, await Run(host, "config", "apply"));
        Assert.Equal(2, await Run(host, "config", "show", "--engine", "mars"));
        Assert.Equal(2, await Run(host, "config", "frobnicate"));
        Assert.Contains(errors, e => e.Contains("Usage: dockyard", StringComparison.Ordinal));
    }

    [Fact]
    public void A_command_for_the_tray_survives_the_pipe_with_its_arguments_and_reply_name()
    {
        var reply = "dockyard-reply-" + Guid.NewGuid().ToString("N");
        string[] args = ["config", "apply", "--file", "inline:eyJkZWJ1ZyI6IHRydWV9", "--engine", "win"];

        var parsed = TrayCommand.Parse(new TrayCommand(TrayCommandKind.Run, Reply: reply, Args: args).ToString());

        Assert.Equal(TrayCommandKind.Run, parsed!.Value.Kind);
        Assert.Equal(reply, parsed.Value.Reply);
        Assert.Equal(args, parsed.Value.Args);
        Assert.Null(TrayCommand.Parse("run \\\\server\\pipe ZGVidWc="));
        Assert.True(Cli.IsCommand(["config", "show"]));
        Assert.False(Cli.IsCommand(["--silent"]));
    }
}
