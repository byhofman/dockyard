using Dockyard.Setup;

namespace Dockyard.Dashboard.Tests;

public sealed class DashboardSilentSetupTests
{
    sealed class FakeSetup : IHeadlessSetup
    {
        public List<string> Calls { get; } = [];
        public SetupRequest? Request { get; private set; }
        public int Code { get; set; } = DockyardSetup.Ok;
        public Exception? Failure { get; set; }
        public IReadOnlyList<SetupProgress> Lines { get; set; } = [];
        public bool Disposed { get; private set; }

        Task Record(string call, IProgress<SetupProgress> progress)
        {
            Calls.Add(call);
            foreach (var line in Lines) progress.Report(line);
            return Failure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }

        public async Task<int> Setup(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken ct)
        {
            Request = request;
            await Record("setup", progress);
            return Code;
        }
        public Task Update(IReadOnlyList<string>? ids, IProgress<SetupProgress> progress) => Record(ids is null ? "update all" : "update " + string.Join(",", ids), progress);
        public Task Add(string id, IProgress<SetupProgress> progress) => Record("add " + id, progress);
        public Task Remove(string id, bool removeData, IProgress<SetupProgress> progress) => Record($"remove {id} data={removeData}", progress);
        public Task Uninstall(bool removeData, IProgress<SetupProgress> progress, bool silent) => Record($"uninstall data={removeData} silent={silent}", progress);
        public void Dispose() => Disposed = true;
    }

    sealed class Harness
    {
        public FakeSetup Setup { get; } = new();
        public StringWriter Out { get; } = new();
        public StringWriter Error { get; } = new();
        public List<string?> Windows { get; } = [];
        public List<string[]> Workers { get; } = [];
        public int Attached { get; private set; }
        public bool SetupCreated { get; private set; }
        public bool HasDesktop { get; init; } = true;
        public bool HasTerminal { get; init; }

        public int Run(params string[] args) => Program.Run(args, new Launch(
            IsWorker: a => a.Contains("--machine-install"),
            Worker: a => { Workers.Add(a); return 42; },
            Setup: () => { SetupCreated = true; return Setup; },
            Out: Out,
            Error: Error,
            HasDesktop: HasDesktop,
            HasTerminal: () => HasTerminal,
            AttachConsole: () => Attached++,
            Window: section => { Windows.Add(section); return 0; }));
    }

    [Fact]
    public void A_worker_invocation_reaches_the_elevated_worker_before_anything_else()
    {
        var h = new Harness();

        Assert.Equal(42, h.Run("--machine-install", "--setup", "--silent", "--log", "x"));

        Assert.Equal(["--machine-install", "--setup", "--silent", "--log", "x"], Assert.Single(h.Workers));
        Assert.False(h.SetupCreated);
        Assert.Empty(h.Windows);
    }

    [Fact]
    public void On_this_machine_the_worker_route_is_the_libraries_and_only_on_windows()
    {
        var launch = Launch.ForThisMachine();
        Assert.Equal(OperatingSystem.IsWindows(), launch.IsWorker(["--machine-install"]));
        Assert.False(launch.IsWorker(["--setup", "--silent"]));
    }

    [Fact]
    public void Silent_setup_runs_the_installer_without_a_window_and_exits_with_its_code()
    {
        var h = new Harness { HasDesktop = false };
        h.Setup.Code = DockyardSetup.RebootRequired;
        h.Setup.Lines = [new("Installing the engine", Step: 1, Steps: 2), new("pulled"), new("slow network", SetupProgressKind.Warning), new("gone wrong", SetupProgressKind.Error)];

        Assert.Equal(DockyardSetup.RebootRequired, h.Run("--setup", "--silent", "--with", "kind", "--no-start"));

        Assert.Empty(h.Windows);
        Assert.Equal(1, h.Attached);
        Assert.True(h.Setup.Disposed);
        var request = h.Setup.Request!;
        Assert.True(request.Silent);
        Assert.True(request.Headless);
        Assert.False(request.Start);
        Assert.Equal(["kind"], request.With);
        Assert.Contains("[1/2] Installing the engine", h.Out.ToString());
        Assert.Contains("pulled", h.Out.ToString());
        Assert.DoesNotContain("slow network", h.Out.ToString());
        Assert.Contains("slow network", h.Error.ToString());
        Assert.Contains("gone wrong", h.Error.ToString());
    }

    [Fact]
    public void Silent_setup_on_a_desktop_is_not_headless()
    {
        var h = new Harness { HasDesktop = true };
        h.Run("--silent", "--setup");
        Assert.False(h.Setup.Request!.Headless);
    }

    [Fact]
    public void Silent_setup_takes_dockyards_files_from_the_assets_folder()
    {
        var folder = Directory.CreateTempSubdirectory("dockyard-assets-").FullName;
        try
        {
            new Harness().Run("--setup", "--silent", "--assets", folder);
            Assert.Equal(folder, DockyardSetup.LocalAssets);
        }
        finally
        {
            DockyardSetup.LocalAssets = null;
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void Wrong_arguments_exit_2_with_the_problem_and_never_open_a_window_or_the_installer()
    {
        var h = new Harness();

        Assert.Equal(DockyardSetup.WrongArguments, h.Run("--setup", "--silent", "--frob"));

        Assert.Contains("Unknown option --frob", h.Error.ToString());
        Assert.False(h.SetupCreated);
        Assert.Empty(h.Windows);
        Assert.Equal(1, h.Attached);
        Assert.Equal(DockyardSetup.WrongArguments, h.Run("--component", "frob", "kind"));
        Assert.Equal(DockyardSetup.WrongArguments, h.Run("--uninstall", "--with", "kind"));
        Assert.Empty(h.Windows);
    }

    [Fact]
    public void Setup_without_silent_opens_the_window_on_the_setup_screen()
    {
        var h = new Harness();

        Assert.Equal(0, h.Run("--setup"));

        Assert.Equal(["setup"], h.Windows);
        Assert.False(h.SetupCreated);
        Assert.Equal(0, h.Attached);
    }

    [Fact]
    public void Without_an_action_the_window_opens_at_the_section_asked_for()
    {
        var h = new Harness();
        h.Run();
        h.Run("--section", "tools");
        Assert.Equal([null, "tools"], h.Windows);
        Assert.False(h.SetupCreated);
    }

    [Theory]
    [InlineData("update all", "--update")]
    [InlineData("update Tray,DockyardCli", "--update", "Tray,DockyardCli")]
    [InlineData("add kind", "--component", "add", "kind")]
    [InlineData("remove dashboard data=True", "--component", "remove", "dashboard", "--remove-data")]
    [InlineData("uninstall data=False silent=True", "--uninstall")]
    [InlineData("uninstall data=True silent=True", "--uninstall", "--remove-data", "--silent")]
    public void Update_component_and_uninstall_run_headless_through_the_facade(string call, params string[] args)
    {
        var h = new Harness();

        Assert.Equal(DockyardSetup.Ok, h.Run(args));

        Assert.Equal([call], h.Setup.Calls);
        Assert.Empty(h.Windows);
        Assert.True(h.Setup.Disposed);
    }

    [Fact]
    public void Uninstall_from_a_terminal_may_ask_there()
    {
        var h = new Harness { HasTerminal = true };
        h.Run("--uninstall");
        Assert.Equal(["uninstall data=False silent=False"], h.Setup.Calls);
    }

    [Fact]
    public void A_name_the_facade_does_not_know_exits_2_and_a_refusal_or_failure_exits_1()
    {
        var unknown = new Harness();
        unknown.Setup.Failure = new ArgumentException("There is no frob here");
        Assert.Equal(DockyardSetup.WrongArguments, unknown.Run("--component", "add", "frob"));
        Assert.Contains("There is no frob here", unknown.Error.ToString());

        var refused = new Harness();
        refused.Setup.Failure = new InvalidOperationException("Dockyard needs an app");
        Assert.Equal(DockyardSetup.Failed, refused.Run("--component", "remove", "cli"));
        Assert.Contains("Dockyard needs an app", refused.Error.ToString());

        var failed = new Harness();
        failed.Setup.Failure = new HttpRequestException("offline");
        Assert.Equal(DockyardSetup.Failed, failed.Run("--update"));
        Assert.Contains("offline", failed.Error.ToString());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--setup", "--help")]
    [InlineData("--setup", "--silent", "--help")]
    [InlineData("--uninstall", "-h")]
    public void Help_prints_the_usage_and_exits_0_without_a_window(params string[] args)
    {
        var h = new Harness();

        Assert.Equal(0, h.Run(args));

        Assert.Equal(SetupArguments.DashboardUsage.TrimEnd(), h.Out.ToString().Trim());
        Assert.Empty(h.Windows);
        Assert.False(h.SetupCreated);
        Assert.Equal(1, h.Attached);
    }

    [Theory]
    [InlineData("Unknown option --silent", "--silent")]
    [InlineData("Unknown option --frob", "--frob")]
    [InlineData("Unexpected argument stray", "stray")]
    [InlineData("--section needs a value", "--section")]
    [InlineData("Unexpected argument extra", "--section", "tools", "extra")]
    [InlineData("Unknown option --frob", "--setup", "--frob")]
    public void Anything_else_exits_2_with_the_problem_and_no_window(string problem, params string[] args)
    {
        var h = new Harness();

        Assert.Equal(DockyardSetup.WrongArguments, h.Run(args));

        Assert.Contains(problem, h.Error.ToString());
        Assert.Empty(h.Windows);
        Assert.False(h.SetupCreated);
    }

    [Fact]
    public void Setup_with_fitting_options_but_without_silent_opens_the_setup_screen()
    {
        var h = new Harness();
        Assert.Equal(0, h.Run("--setup", "--with", "kind"));
        Assert.Equal(["setup"], h.Windows);
    }

    [Fact]
    public void Uninstall_with_silent_never_asks_even_in_a_terminal()
    {
        var h = new Harness { HasTerminal = true };
        h.Run("--uninstall", "--silent");
        Assert.Equal(["uninstall data=False silent=True"], h.Setup.Calls);
    }
}
