using System.Collections.ObjectModel;
using Avalonia.Threading;
using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;
using Dockyard.Shared;

namespace Dockyard.Dashboard.Shell;

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
