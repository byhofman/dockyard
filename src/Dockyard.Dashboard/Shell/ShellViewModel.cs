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
    public static string Version { get; } = "v" + (System.Reflection.Assembly.GetEntryAssembly()?
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    ContextWatcher? watcher;
    Section currentSection;
    string search = "";
    bool isSearchOpen;
    int hitIndex = -1;
    Dialog? dialog;
    TaskCompletionSource<string?>? switchResult;

    public ShellViewModel()
    {
        Containers = new ContainersViewModel(this, Session);
        Sections =
        [
            new Section("Containers", Containers, Section.Resource("IconContainers")),
            new Section("Images", new ImagesViewModel(this, Session), Section.Resource("IconImages")),
            new Section("Volumes", new VolumesViewModel(this, Session), Section.Resource("IconVolumes")),
            new Section("Networks", new NetworksViewModel(this, Session), Section.Resource("IconNetworks")),
            new Section("Builds", new Features.Builds.BuildsViewModel(this, Session), Section.Resource("IconBuilds")),
            new Section("Clusters", new ClustersViewModel(this, Session), Section.Resource("IconClusters")),
            new Section("Disk", new DiskViewModel(this, Session), Section.Resource("IconDisk")),
        ];
        currentSection = Sections[0];
        _ = Tools.Refresh();

        HasEngineSwitch = OperatingSystem.IsWindows() && DockerContext.Exists("linux") && DockerContext.Exists("win");
        SwitchLinuxCommand = new Command(() => { AskSwitch("linux"); return Task.CompletedTask; });
        SwitchWindowsCommand = new Command(() => { AskSwitch("win"); return Task.CompletedTask; });
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

    public void ShowSection(string title)
    {
        if (Sections.FirstOrDefault(s => s.Title.Equals(title, StringComparison.OrdinalIgnoreCase)) is { } section) CurrentSection = section;
    }

    public static string? InitialSection { get; set; }

    public Section CurrentSection
    {
        get => currentSection;
        set
        {
            if (value is null || !Set(ref currentSection, value)) return;
            ApplySearch();
            (value.Content as SectionViewModel)?.OnShown();
            _ = Tools.Refresh();
        }
    }

    public string Search
    {
        get => search;
        set
        {
            if (!Set(ref search, value ?? "")) return;
            ApplySearch();
            FindEverywhere();
        }
    }

    // What the search box finds in every section, under it; the section on screen is filtered as well.
    public ObservableCollection<SearchHit> Hits { get; } = [];
    public bool IsSearchOpen { get => isSearchOpen; set => Set(ref isSearchOpen, value); }
    public int HitIndex { get => hitIndex; set => Set(ref hitIndex, value); }
    public bool HasNoHits => Hits.Count == 0 && search.Trim().Length > 0;

    void FindEverywhere()
    {
        Hits.Clear();
        foreach (var hit in SearchHit.From(search, Sections)) Hits.Add(hit);
        HitIndex = Hits.ToList().FindIndex(h => h.IsSelectable);
        IsSearchOpen = search.Trim().Length > 0;
        Raise(nameof(HasNoHits));
    }

    // Headers are passed over.
    public void MoveHit(int by)
    {
        if (!Hits.Any(h => h.IsSelectable)) return;
        var i = Math.Max(HitIndex, 0);
        do i = (i + by + Hits.Count) % Hits.Count;
        while (!Hits[i].IsSelectable);
        HitIndex = i;
    }

    public Features.Clusters.ClustersViewModel? Clusters => Sections.Select(s => s.Content).OfType<Features.Clusters.ClustersViewModel>().FirstOrDefault();

    public void Reveal(string sectionTitle, object row)
    {
        if (Sections.FirstOrDefault(s => s.Title == sectionTitle) is not { } section) return;
        CurrentSection = section;
        if (section.Content is ISearchable searchable) searchable.Reveal(row);
    }

    public void OpenHit(SearchHit? hit = null)
    {
        hit ??= HitIndex >= 0 && HitIndex < Hits.Count ? Hits[HitIndex] : null;
        if (hit is not { IsSelectable: true, Section: { } section, Row: { } row }) return;
        IsSearchOpen = false;
        Search = "";
        CurrentSection = section;
        if (section.Content is ISearchable searchable) searchable.Reveal(row);
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

    public ActivityLog Activity { get; } = new();
    public ToolCheck Tools { get; } = new();

    public Dialog? Dialog { get => dialog; private set { if (Set(ref dialog, value)) Raise(nameof(HasDialog)); } }
    public bool HasDialog => dialog is not null;

    public void Open(Dialog next)
    {
        if (dialog is { IsWorking: true }) return;
        next.ShowError = ShowError;
        IDisposable? working = null;
        next.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Dialog.Progress) && next.Progress is { } step) Activity.Note(step);
            else if (e.PropertyName == nameof(Dialog.IsWorking))
            {
                if (next.IsWorking) working ??= Activity.Working(next.Title);
                else { working?.Dispose(); working = null; }
            }
        };
        next.Closed += () => working?.Dispose();
        next.Closed += () => { if (Dialog == next) Dialog = null; };
        Dialog = next;
    }

    public void AskSwitch(string engine)
    {
        if (Session.ContextName == engine) return;
        var name = EngineSwitch.Name(engine);
        Open(new Dialog($"Switch to {name} containers?", EngineSwitch.Message(engine, EngineSwitch.StopsOther()), "Switch",
            d => Switch(engine, d, TimeSpan.FromMinutes(5))));
    }

    // Done when the dashboard is connected to the engine the context now names; the tray only says when it failed.
    internal async Task Switch(string engine, Dialog dialog, TimeSpan timeout)
    {
        var name = EngineSwitch.Name(engine);
        dialog.Report($"Starting the {name} engine and switching the docker context…");
        var result = switchResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (Session.ContextName != engine) return;
            if (Session.IsConnected) result.TrySetResult(null);
            else dialog.Report($"Connecting to the {name} engine…");
        }
        Session.PropertyChanged += Watch;
        try
        {
            if (!TrayClient.Send(new TrayCommand(TrayCommandKind.Switch, engine)))
                throw new InvalidOperationException("The Dockyard tray app is not running and could not be started.");
            Watch(null, new(nameof(EngineSession.State)));
            var finished = await Task.WhenAny(result.Task, Task.Delay(timeout));
            if (finished != result.Task)
                throw new TimeoutException($"The {name} engine did not come up within {timeout.TotalMinutes:0} minutes. Engines and settings shows what Dockyard is doing.");
            if (await result.Task is { } error) throw new InvalidOperationException(error);
            ShowInfo($"Switched to {name} containers");
        }
        finally
        {
            Session.PropertyChanged -= Watch;
            if (switchResult == result) switchResult = null;
        }
    }

    public void OnTrayMessage(string line)
    {
        if (EngineSwitch.ParseResult(line) is not { } r) return;
        if (r.Error is { } error) switchResult?.TrySetResult(error);
        else if (Session.ContextName == r.Engine && Session.IsConnected) switchResult?.TrySetResult(null);
    }

    public void ShowError(string message) => Show(new Toast(message, IsError: true));

    public void ShowInfo(string message) => Show(new Toast(message, IsError: false));

    void Show(Toast toast)
    {
        Activity.Note(toast.Message, toast.IsError ? ActivityKind.Error : ActivityKind.Info);
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
