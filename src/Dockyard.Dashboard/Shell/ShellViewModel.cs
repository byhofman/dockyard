using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;
using Dockyard.Shared;

namespace Dockyard.Dashboard.Shell;

sealed record Section(string Title, object Content, Avalonia.Media.Geometry? Icon)
{
    public static Avalonia.Media.Geometry? Resource(string key) =>
        Avalonia.Application.Current?.TryGetResource(key, null, out var value) == true ? value as Avalonia.Media.Geometry : null;
}

sealed record Toast(string Message, bool IsError);

sealed class ShellViewModel : Observable
{
    ContextWatcher? watcher;
    Section currentSection;
    string search = "";

    public ShellViewModel()
    {
        Containers = new ContainersViewModel(this, Session);
        Sections =
        [
            new Section("Containers", Containers, Section.Resource("IconContainers")),
            new Section("Images", new ImagesViewModel(this, Session), Section.Resource("IconImages")),
            new Section("Volumes", new VolumesViewModel(this, Session), Section.Resource("IconVolumes")),
            new Section("Networks", new NetworksViewModel(this, Session), Section.Resource("IconNetworks")),
            new Section("Clusters", new ClustersViewModel(this, Session), Section.Resource("IconClusters")),
            new Section("Disk", new DiskViewModel(this, Session), Section.Resource("IconDisk")),
        ];
        currentSection = Sections[0];

        HasEngineSwitch = OperatingSystem.IsWindows() && DockerContext.Exists("linux") && DockerContext.Exists("win");
        SwitchLinuxCommand = new Command(() => SendToTray(new TrayCommand(TrayCommandKind.Switch, "linux")));
        SwitchWindowsCommand = new Command(() => SendToTray(new TrayCommand(TrayCommandKind.Switch, "win")));
        SettingsCommand = new Command(() => SendToTray(new TrayCommand(TrayCommandKind.Settings)));
        StartEngineCommand = new Command(StartEngine);

        Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EngineSession.ContextName)) Raise(nameof(IsLinuxActive), nameof(IsWindowsActive));
            if (e.PropertyName == nameof(EngineSession.EngineId)) Raise(nameof(CanStartEngine));
        };
    }

    public static Avalonia.Input.KeyModifiers ShortcutModifier => OperatingSystem.IsMacOS() ? Avalonia.Input.KeyModifiers.Meta : Avalonia.Input.KeyModifiers.Control;
    public string SearchHint => OperatingSystem.IsMacOS() ? "Search  (\u2318K)" : "Search  (Ctrl+K)";

    public EngineSession Session { get; } = new();
    public ContainersViewModel Containers { get; }
    public IReadOnlyList<Section> Sections { get; }
    public ObservableCollection<Toast> Toasts { get; } = [];

    public Section CurrentSection
    {
        get => currentSection;
        set
        {
            if (value is null || !Set(ref currentSection, value)) return;
            ApplySearch();
            (value.Content as SectionViewModel)?.OnShown();
        }
    }

    public string Search
    {
        get => search;
        set { if (Set(ref search, value ?? "")) ApplySearch(); }
    }

    public bool HasEngineSwitch { get; }
    public bool IsLinuxActive => Session.ContextName == "linux";
    public bool IsWindowsActive => Session.ContextName == "win";
    public bool CanStartEngine => Session.EngineId is not null;

    public Command SwitchLinuxCommand { get; }
    public Command SwitchWindowsCommand { get; }
    public Command SettingsCommand { get; }
    public Command StartEngineCommand { get; }

    public void Start()
    {
        watcher = new ContextWatcher(Session.Start);
        Session.Start();
    }

    public void Stop()
    {
        watcher?.Dispose();
        Containers.Shutdown();
        Session.Stop();
    }

    public void ShowError(string message) => Show(new Toast(message, IsError: true));

    public void ShowInfo(string message) => Show(new Toast(message, IsError: false));

    void Show(Toast toast)
    {
        Toasts.Add(toast);
        DispatcherTimer.RunOnce(() => Toasts.Remove(toast), TimeSpan.FromSeconds(6));
    }

    public void DismissToast(Toast toast) => Toasts.Remove(toast);

    void ApplySearch()
    {
        if (CurrentSection.Content is ISearchable s) s.ApplySearch(Search);
    }

    Task StartEngine()
    {
        if (Session.EngineId is { } engine)
        {
            SendToTray(new TrayCommand(TrayCommandKind.Start, engine));
            DispatcherTimer.RunOnce(Session.Start, TimeSpan.FromSeconds(2));
        }
        return Task.CompletedTask;
    }

    Task SendToTray(TrayCommand command)
    {
        if (!TrayClient.Send(command)) ShowError("The Dockyard tray app is not running and could not be started.");
        return Task.CompletedTask;
    }
}

interface ISearchable
{
    void ApplySearch(string text);
}

static class TrayClient
{
    static string TrayExe => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Dockyard.exe" : "Dockyard");

    public static bool Send(TrayCommand command)
    {
        if (LocalPipe.TrySend(LocalPipe.TrayName, command.ToString())) return true;
        if (!File.Exists(TrayExe)) return false;

        string[] args = command.Kind switch
        {
            TrayCommandKind.Settings => ["--tray", "--settings"],
            TrayCommandKind.Start => ["--tray", "--start", command.Engine!],
            TrayCommandKind.Switch => ["--tray", "--switch", command.Engine!],
            _ => ["--tray"],
        };
        try
        {
            Process.Start(new ProcessStartInfo(TrayExe, args) { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

sealed class ContextWatcher : IDisposable
{
    readonly FileSystemWatcher? watcher;
    readonly Action onChange;
    string? current;
    bool pending;

    public ContextWatcher(Action onChange)
    {
        this.onChange = onChange;
        current = Read();
        var dir = DockerContext.ConfigDir(Environment.GetEnvironmentVariable);
        if (!Directory.Exists(dir)) return;
        watcher = new FileSystemWatcher(dir, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        watcher.Changed += (_, _) => Schedule();
        watcher.Created += (_, _) => Schedule();
        watcher.Renamed += (_, _) => Schedule();
        watcher.EnableRaisingEvents = true;
    }

    static string? Read()
    {
        try { return DockerContext.CurrentContextName(); }
        catch (Exception) { return null; }
    }

    void Schedule() => Dispatcher.UIThread.Post(() =>
    {
        if (pending) return;
        pending = true;
        DispatcherTimer.RunOnce(() =>
        {
            pending = false;
            var next = Read();
            if (next == current) return;
            current = next;
            onChange();
        }, TimeSpan.FromMilliseconds(200));
    });

    public void Dispose() => watcher?.Dispose();
}
