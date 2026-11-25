using System.Collections.ObjectModel;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Containers;

sealed class ComposeDetailViewModel : Observable, IDisposable
{
    readonly ContainersViewModel owner;
    readonly EngineSession session;
    readonly Action<string> showError;
    readonly ActivityLog activity;
    readonly ToolCheck tools;
    ComposeProject project;
    string? busy;
    bool showLogs;
    int selectedTab;
    string? selectedFile;
    ComposeFile? file;
    string fileText = "";
    string? fileStatus;
    bool fileOk;
    IReadOnlyList<int> problemLines = [];
    CancellationTokenSource? check;

    public ComposeDetailViewModel(ContainersViewModel owner, EngineSession session, ComposeProject project, Action<string> showError, ActivityLog activity, ToolCheck? tools = null)
    {
        this.owner = owner;
        this.session = session;
        this.project = project;
        this.showError = showError;
        this.activity = activity;
        this.tools = tools ?? new ToolCheck();
        this.tools.PropertyChanged += (_, _) => RefreshCommands();

        UpCommand = new Command(Up, () => !IsBusy && this.tools.HasCompose && Project.CanUp);
        StopCommand = new Command(() => Cli("Stopping", "stop"), () => !IsBusy && this.tools.HasCompose && Project.Total > 0);
        RestartCommand = new Command(() => Cli("Restarting", "restart"), () => !IsBusy && this.tools.HasCompose && Project.Total > 0);
        PullCommand = new Command(() => Cli("Pulling", "pull"), () => !IsBusy && this.tools.HasCompose && Project.CanUp);
        DownCommand = new Command(() => { AskDown(); return Task.CompletedTask; }, () => !IsBusy && this.tools.HasCompose && Project.Total > 0);
        ForgetCommand = new Command(() => { owner.ForgetRecent(Project.Name); return Task.CompletedTask; });
        CloseCommand = new Command(() => { owner.CloseDetail(); return Task.CompletedTask; });
        ServiceStartCommand = new Command<ComposeService>(s => ForService(s, (c, id) => c.StartContainerAsync(id)));
        ServiceStopCommand = new Command<ComposeService>(s => ForService(s, (c, id) => c.StopContainerAsync(id)));
        ServiceRestartCommand = new Command<ComposeService>(s => ForService(s, (c, id) => c.RestartContainerAsync(id)));
        SaveCommand = new Command(() => { Save(); return Task.CompletedTask; }, () => IsDirty);
        SaveAndApplyCommand = new Command(async () => { if (Save()) await Up(); }, () => !IsBusy && file is not null && Project.CanUp);
        RevertCommand = new Command(() => { if (file is not null) FileText = file.Text; return Task.CompletedTask; }, () => IsDirty);
        selectedFile = project.ConfigFiles.FirstOrDefault();
    }

    public ComposeProject Project
    {
        get => project;
        private set { if (Set(ref project, value)) { Raise(nameof(IsRecentOnly), nameof(Files), nameof(HasManyFiles), nameof(HasNoFiles)); RefreshCommands(); } }
    }

    public bool IsRecentOnly => Project.Total == 0 && owner.IsRecent(Project.Name);
    public LogsViewModel Logs { get; } = new();

    public Command UpCommand { get; }
    public Command StopCommand { get; }
    public Command RestartCommand { get; }
    public Command PullCommand { get; }
    public Command DownCommand { get; }
    public Command ForgetCommand { get; }
    public Command CloseCommand { get; }
    public Command<ComposeService> ServiceStartCommand { get; }
    public Command<ComposeService> ServiceStopCommand { get; }
    public Command<ComposeService> ServiceRestartCommand { get; }

    public string? Busy
    {
        get => busy;
        private set { if (Set(ref busy, value)) { Raise(nameof(IsBusy)); RefreshCommands(); } }
    }

    public bool IsBusy => Busy is not null;

    public Command SaveCommand { get; }
    public Command SaveAndApplyCommand { get; }
    public Command RevertCommand { get; }

    public int SelectedTab
    {
        get => selectedTab;
        set
        {
            if (!Set(ref selectedTab, value)) return;
            if (value == 1 && !IsDirty) LoadFile();
            ShowLogs = value == 2;
        }
    }

    public IReadOnlyList<string> Files => Project.ConfigFiles;
    public bool HasManyFiles => Files.Count > 1;
    public bool HasNoFiles => Files.Count == 0;

    public string? SelectedFile
    {
        get => selectedFile;
        set { if (value is not null && Set(ref selectedFile, value)) LoadFile(); }
    }

    public bool IsFileLoaded => file is not null;
    public string? FileMissing { get; private set; }
    public bool HasFileMissing => FileMissing is not null;

    public string FileText
    {
        get => fileText;
        set
        {
            if (!Set(ref fileText, value)) return;
            Raise(nameof(IsDirty), nameof(CanPickFile));
            RefreshFileCommands();
            CheckFile();
        }
    }

    public bool IsDirty => file is not null && file.IsChangedBy(FileText);
    public bool CanPickFile => !IsDirty;

    public IReadOnlyList<int> ProblemLines
    {
        get => problemLines;
        private set => Set(ref problemLines, value);
    }

    public string? FileStatus
    {
        get => fileStatus;
        private set { if (Set(ref fileStatus, value)) Raise(nameof(HasFileStatus)); }
    }

    public bool HasFileStatus => FileStatus is not null;

    public bool FileOk
    {
        get => fileOk;
        private set => Set(ref fileOk, value);
    }

    public bool ShowLogs
    {
        get => showLogs;
        set
        {
            if (!Set(ref showLogs, value)) return;
            if (value) StartLogs();
            else Logs.Dispose();
        }
    }

    public void Update(ComposeProject updated) => Project = updated;

    public Task Up() => Cli("Starting", "up", "-d");

    public Task StartOrStop() => Project.IsRunning ? Cli("Stopping", "stop") : Project.CanUp ? Up() : Cli("Starting", "start");

    public void AskDown() => owner.AskDown(Project);

    void RefreshCommands()
    {
        foreach (var c in new[] { UpCommand, StopCommand, RestartCommand, PullCommand, DownCommand }) c.Refresh();
        RefreshFileCommands();
    }

    void RefreshFileCommands()
    {
        SaveCommand?.Refresh();
        SaveAndApplyCommand?.Refresh();
        RevertCommand?.Refresh();
    }

    void LoadFile()
    {
        check?.Cancel();
        FileStatus = null;
        ProblemLines = [];
        if (SelectedFile is not { } path) return;
        try
        {
            file = ComposeFile.Load(path);
            FileMissing = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            file = null;
            FileMissing = Path.IsPathFullyQualified(path) && !path.StartsWith('/')
                ? $"Could not read {path}: {ex.Message}"
                : $"{path} is not a path on this computer; the project was started from somewhere else, inside WSL for example.";
        }
        Raise(nameof(IsFileLoaded), nameof(FileMissing), nameof(HasFileMissing));
        fileText = file?.Text ?? "";
        Raise(nameof(FileText), nameof(IsDirty), nameof(CanPickFile));
        RefreshFileCommands();
        CheckFile();
    }

    bool Save()
    {
        if (file is null) return false;
        if (!IsDirty) return true;
        try
        {
            file.Save(FileText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            showError($"Could not save {file.Path}: {ex.Message}");
            return false;
        }
        Raise(nameof(IsDirty), nameof(CanPickFile));
        RefreshFileCommands();
        return true;
    }

    async void CheckFile()
    {
        check?.Cancel();
        if (file is null) return;
        if (ComposeFile.Problem(FileText) is { } problem)
        {
            ProblemLines = [problem.Line];
            FileStatus = $"Line {problem.Line}: {problem.Message}";
            FileOk = false;
            return;
        }
        ProblemLines = [];
        var path = file.Path;
        var text = FileText;
        var cancel = (check = new CancellationTokenSource()).Token;
        try
        {
            await Task.Delay(800, cancel);
            FileStatus = "Checking with docker compose…";
            var context = session.ContextName.Length > 0 ? session.ContextName : "default";
            var (exit, output) = await CliRunner.Capture(ComposeFile.ConfigArgs(context, Project, path), text, cancel, session.IsWindowsEngine);
            if (cancel.IsCancellationRequested) return;
            FileOk = exit == 0;
            FileStatus = exit == 0 ? "docker compose accepts this file" : output.Length > 0 ? output : $"docker compose config failed (exit code {exit})";
            if (exit != 0 && ComposeFile.LineOf(output) is { } line) ProblemLines = [line];
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            if (!cancel.IsCancellationRequested) FileStatus = null;
        }
    }

    async Task Cli(string what, params string[] verb)
    {
        if (IsBusy) return;
        var context = session.ContextName.Length > 0 ? session.ContextName : "default";
        var args = ComposeCli.Args(context, Project, verb);
        Busy = $"{what} {Project.Name}…";
        using var working = activity.Working(Busy);
        activity.Note($"docker {string.Join(' ', args)}", ActivityKind.Command);
        var last = "";
        try
        {
            var exit = await CliRunner.Run(args, line =>
            {
                activity.Note(line);
                if (line.Trim().Length > 0) last = line.Trim();
            }, session.IsWindowsEngine);
            if (exit != 0) showError($"docker compose {verb[0]} failed for {Project.Name}: {(last.Length > 0 ? last : $"exit code {exit}")}");
        }
        catch (Exception ex)
        {
            showError($"Could not run docker compose: {ex.Message}");
        }
        finally
        {
            Busy = null;
        }
    }

    async Task ForService(ComposeService service, Func<DockerClient, string, Task> action)
    {
        if (session.Client is not { } client) return;
        try
        {
            foreach (var container in service.Containers) await action(client, container.Id);
        }
        catch (Exception ex)
        {
            showError($"{service.Name}: {ex.Message}");
        }
    }

    async void StartLogs()
    {
        if (session.Client is not { } client) return;
        var name = Project.Name;
        var sources = new List<LogSource>();
        foreach (var service in Project.Services)
            foreach (var container in service.Containers)
            {
                try
                {
                    var details = await client.InspectContainerAsync(container.Id);
                    sources.Add(new LogSource(container.Id, details.Config.Tty, service.Name));
                }
                catch (DockerException) { }
            }
        if (ShowLogs && Project.Name == name) Logs.Start(client, sources);
    }

    public void Dispose()
    {
        check?.Cancel();
        Logs.Dispose();
    }
}
