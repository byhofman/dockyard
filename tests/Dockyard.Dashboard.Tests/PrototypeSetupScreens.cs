using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dockyard.Dashboard.Controls;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Features.Setup;
using Dockyard.Dashboard.Shell;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Tests;

// Setup, Tools and Settings in the real window over a fictive Mac: the catalog's groups, two updates, tools from Homebrew
// next to Dockyard's own, and required rows. Each in light and dark, at the window's default and its minimum size.
public sealed partial class PrototypeScreens
{
    internal static IReadOnlyList<ComponentRow> MacRows(bool installed)
    {
        const string brew = "Homebrew", own = SettingDefinitions.DockyardSource;
        ComponentRow Row(string id, string title, string group, string? at, string? newest, string source, string? required, string? needs, string? flag, bool canRemove = false) =>
            new(id, title, group, installed ? at : null, installed ? newest : null, source, required, needs, flag,
                CanAdd: installed && at is null && flag is not null, CanRemove: installed && canRemove && at is not null,
                CanUpdate: installed && at is not null && newest is not null && at != newest);
        return
        [
            Row("DockyardCli", "Dockyard", "Dockyard", "0.14.2", "0.14.2", own, "it installs and updates everything else here. Uninstall removes it.", null, null),
            Row("Tray", "Dockyard menu bar app", "Dockyard", "0.14.2", "0.14.2", own, null, "Optional. Keeps the engine in the menu bar and starts at login.", "tray", canRemove: true),
            Row("Dashboard", "Dashboard", "Dockyard", "0.14.2", "0.14.2", own, null, "Optional. Opens from the menu bar.", "dashboard", canRemove: true),
            Row("LinuxEngine", "Docker Engine (Linux VM)", "Docker", "29.0.2", "29.0.2", own, "it runs your containers, and it is the only engine here. Uninstall removes it.", null, null),
            Row("DockerCli", "Docker CLI", "Docker", "28.5.1", "29.0.2", brew, "the engine, Compose and Buildx are used through it.", null, null),
            Row("Compose", "Docker Compose", "Docker", "2.40.3", "2.40.3", brew, null, "Needs the Docker CLI. The dashboard's Compose actions use it.", "compose", canRemove: true),
            Row("Buildx", "Docker Buildx", "Docker", "0.29.1", "0.29.1", brew, null, "Needs the Docker CLI. docker build uses it.", "buildx", canRemove: true),
            Row("Kind", "kind (Kubernetes in Docker)", "Kubernetes", "0.29.0", "0.30.0", brew, null, "Local Kubernetes clusters in containers, on the Linux engine. The dashboard makes and runs its clusters with it.", "kind", canRemove: true),
            Row("Kubectl", "kubectl", "Kubernetes", "1.34.2", "1.34.2", brew, null, "The Kubernetes command line, for kind's clusters and any other.", "kubectl", canRemove: true),
            Row("Helm", "Helm", "Kubernetes", null, "3.19.2", brew, null, "Kubernetes packages. The dashboard installs Traefik and metrics-server into new clusters with it.", "helm", canRemove: true),
        ];
    }

    const string DaemonJson = "{\n  \"dns\": [\"192.168.64.1\"],\n  \"log-driver\": \"json-file\",\n  \"log-opts\": { \"max-size\": \"10m\", \"max-file\": \"3\" },\n  \"features\": { \"containerd-snapshotter\": true }\n}\n";

    // Every run reports the given lines and then either hangs, which holds the screen in its running state, or ends.
    sealed class FakeSetup(bool installed) : ISetupSource
    {
        readonly Dictionary<string, string> values = new()
        {
            [global::Dockyard.Setup.SettingDefinitions.ContainerDomains] = global::Dockyard.Setup.SettingDefinitions.On,
            [global::Dockyard.Setup.SettingDefinitions.TrustedHttps] = global::Dockyard.Setup.SettingDefinitions.Off,
            [global::Dockyard.Setup.SettingDefinitions.StartEngineAtLogon] = global::Dockyard.Setup.SettingDefinitions.On,
            [global::Dockyard.Setup.SettingDefinitions.KubectlShell] = global::Dockyard.Setup.SettingDefinitions.On,
            [global::Dockyard.Setup.SettingDefinitions.Source] = "Homebrew",
        };

        public IReadOnlyList<SetupProgress> Lines { get; init; } = [];
        public bool Fails { get; init; }

        public bool IsInstalled => installed;
        public string SelfId => "DockyardCli";
        // This computer's: a Mac's on the machine the prototype is made on.
        public IReadOnlyList<SettingDefinition> SettingDefinitions => global::Dockyard.Setup.SettingDefinitions.All;
        public IReadOnlyList<ComponentRow> Components() => MacRows(installed);
        public Task<IReadOnlyList<ComponentRow>> ComponentsAsync(CancellationToken ct) => Task.FromResult(Components());
        public string GetSetting(string key) => key.StartsWith(global::Dockyard.Setup.SettingDefinitions.EngineFilePrefix, StringComparison.Ordinal) ? DaemonJson : values.GetValueOrDefault(key, global::Dockyard.Setup.SettingDefinitions.Off);

        async Task<int> Play(IProgress<SetupProgress> progress)
        {
            foreach (var line in Lines) progress.Report(line);
            if (Fails) return DockyardSetup.Failed;
            await new TaskCompletionSource().Task;
            return DockyardSetup.Ok;
        }

        public Task SetSetting(string key, string value, IProgress<SetupProgress> progress) => Play(progress);
        public Task<int> Setup(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken ct) => Play(progress);
        public Task Add(string id, IProgress<SetupProgress> progress) => Play(progress);
        public Task Remove(string id, bool removeData, IProgress<SetupProgress> progress) => Play(progress);
        public Task Update(IReadOnlyList<string>? ids, IProgress<SetupProgress> progress) => Play(progress);
        public Task Uninstall(bool removeData, IProgress<SetupProgress> progress) => Play(progress);
        public Task Import(IProgress<SetupProgress> progress) => Play(progress);
        public Task StartEngine(string id) => Task.CompletedTask;
        public Task UseEngine(string id) => Task.CompletedTask;
        public Task<string> ClusterCa(string cluster) => Task.FromResult("");
        public Task PushClusterWants() => Task.CompletedTask;
    }

    static SetupProgress Info(string line) => new(line);
    static SetupProgress Cmd(string line) => new(line, SetupProgressKind.Command);

    // A Mac's install as the facade reports it: MacHost's phases, then each change, then starting up.
    static readonly string[] MacPhases = ["Dockyard", "docker and the Kubernetes tools", "Administrator step", "The Linux VM"];

    static SetupProgress[] MacInstall(string[] plan) =>
    [
        new(plan[0], Step: 1, Steps: plan.Length) { Plan = plan },
        Info("Installed /Users/me/Applications/Dockyard.app"),
        new(plan[1], Step: 2, Steps: plan.Length),
        Info("The tools come from Homebrew"),
        Cmd("brew install docker docker-compose docker-buildx kind kubectl"),
        Info("Installed /opt/homebrew/bin/docker (28.5.1)"),
        new(plan[2], Step: 3, Steps: plan.Length),
        Info("Administrator step: adding docker, kind, kubectl and helm to the PATH through /usr/local/bin"),
        new(plan[3], Step: 4, Steps: plan.Length),
        Info("Downloading the Linux engine image (212 MB)"),
    ];

    // A new Mac, in the middle of the VM.
    static readonly SetupProgress[] FirstRun = MacInstall([.. MacPhases, "Starting up"]);

    // Opened again on an installed Dockyard with Helm picked and Buildx left out: the install is brought up to date, then
    // each change is a step of its own.
    static readonly string[] Plan = [.. MacPhases, "Adding Helm", "Removing Docker Buildx", "Starting up"];

    static readonly SetupProgress[] Rerun =
    [
        .. MacInstall(Plan),
        Info("Docker context 'dockyard' → unix:///Users/me/.dockyard/run/docker.sock"),
        new("Adding Helm", Step: 5, Steps: Plan.Length),
        Cmd("brew install helm"),
        Info("==> Fetching helm 3.19.2"),
    ];

    static readonly SetupProgress[] FailedRerun =
    [
        .. Rerun,
        new("Warning: helm 3.19.2 is already downloaded", SetupProgressKind.Warning),
        new("brew install helm failed (exit code 1): Error: Permission denied @ apply2files - /opt/homebrew/share/zsh/site-functions/_helm", SetupProgressKind.Error),
    ];

    public static TheoryData<string> SetupScreens => new()
    {
        "setup-before", "setup-reopened", "setup-first-run", "setup-installing", "setup-error",
        "tools-default", "tools-focus", "tools-updating", "tools-remove", "tools-uninstall", "tools-windows",
        "settings-default", "settings-approving", "settings-engine-file",
    };

    static readonly (string Name, double Width, double Height)[] Sizes = [("default", 1240, 760), ("minimum", 720, 480)];

    [Theory]
    [MemberData(nameof(SetupScreens))]
    public async Task Setup_screen_renders(string screen)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PrototypeScreens).Assembly);
        await session.Dispatch(() =>
        {
            var app = Application.Current!;
            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                    foreach (var size in Sizes)
                    {
                        app.RequestedThemeVariant = theme;
                        Render(screen, $"{screen}-{theme.Key.ToString()!.ToLowerInvariant()}-{size.Name}", size.Width, size.Height);
                    }
            }
            finally { app.RequestedThemeVariant = ThemeVariant.Default; }
        }, TestContext.Current.CancellationToken);
    }

    static void Render(string screen, string file, double width, double height)
    {
        var (kind, state) = (screen[..screen.IndexOf('-')], screen[(screen.IndexOf('-') + 1)..]);
        var fake = new FakeSetup(installed: kind != "setup" || state is "reopened" or "installing" or "error")
        {
            Lines = state switch
            {
                "first-run" => FirstRun,
                "installing" => Rerun,
                "error" => FailedRerun,
                "updating" => [new("Updating Docker CLI", Step: 1, Steps: 1), Cmd("brew upgrade docker"), Info("==> Fetching docker 29.0.2"), Info("==> Pouring docker--29.0.2.arm64_tahoe.bottle.tar.gz")],
                _ => [],
            },
            Fails = state == "error",
        };
        var shell = new ShellViewModel(fake);
        if (kind == "setup") shell.Setup = new SetupViewModel(fake, shell);
        else shell.ShowSection(kind);

        // The top row as the title bar. Headless draws neither macOS's traffic lights nor Windows' caption buttons, so their
        // place in the row stays empty in the shots.
        var window = new MainWindow(WindowChrome.For(state == "windows" ? HostOs.Windows : HostOs.MacOS)) { DataContext = shell, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        switch (screen)
        {
            case "setup-first-run" or "setup-installing" or "setup-error":
                shell.Setup!.InstallCommand.Execute(null);
                break;
            case "tools-focus":
                var compose = window.GetVisualDescendants().OfType<ComponentRowView>().First(r => r.Title == "Docker Compose");
                compose.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Remove").Focus(Avalonia.Input.NavigationMethod.Tab);
                break;
            case "tools-remove":
                var kindTools = (ToolsViewModel)shell.CurrentSection.Content;
                kindTools.RemoveCommand.Execute(kindTools.Groups.SelectMany(g => g.Items).First(i => i.Row.Id == "Kind"));
                break;
            case "tools-updating":
                var tools = (ToolsViewModel)shell.CurrentSection.Content;
                tools.UpdateCommand.Execute(tools.Groups.SelectMany(g => g.Items).First(i => i.Row.Id == "DockerCli"));
                break;
            case "tools-uninstall":
                var self = (ToolsViewModel)shell.CurrentSection.Content;
                self.UninstallCommand.Execute(self.Groups.SelectMany(g => g.Items).First(i => i.IsSelf));
                break;
            case "settings-approving":
                ((SettingsViewModel)shell.CurrentSection.Content).Items.First(i => i.Definition.Key == SettingDefinitions.TrustedHttps).Value = SettingDefinitions.On;
                break;
            case "settings-engine-file":
                ((SettingsViewModel)shell.CurrentSection.Content).Items.First(i => i.Definition.Kind == SettingKind.EngineFile).EditCommand.Execute(null);
                break;
        }
        // Lets transitions, such as a switch's knob moving across, run to their end.
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(80);
        }

        Assert.True(window.GetVisualDescendants().OfType<Control>().Any(c => c is ComponentRowView or SettingRow), $"{screen} drew none of its rows");
        if (Environment.GetEnvironmentVariable("DOCKYARD_SCREENSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            window.CaptureRenderedFrame()?.Save(Path.Combine(folder, file + ".png"));
        }
        window.Close();
    }
}
