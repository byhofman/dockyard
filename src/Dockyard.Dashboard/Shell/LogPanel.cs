using System.Collections.ObjectModel;
using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Shell;

// One tab of the log panel: the dashboard's own activity, or the logs of a container or a compose project.
sealed class LogTab : Observable, IDisposable
{
    public LogTab(string key, string title, LogsViewModel? logs, ActivityLog? activity)
    {
        Key = key;
        Title = title;
        Logs = logs;
        Activity = activity;
    }

    bool isSelected;
    bool isShown = true;

    public string Key { get; }
    public string Title { get; }
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }

    // Whether it is one of the logs shown side by side.
    public bool IsShown
    {
        get => isShown;
        set { if (Set(ref isShown, value)) ShownChanged?.Invoke(); }
    }

    public event Action? ShownChanged;
    public LogsViewModel? Logs { get; }
    public ActivityLog? Activity { get; }
    public bool IsActivity => Activity is not null;
    public bool IsLogs => Logs is not null;
    public bool CanClose => !IsActivity;
    public string Text => Activity?.Text ?? string.Join(Environment.NewLine, Logs?.Rows.Select(r => r.Line.Text) ?? []);
    public override string ToString() => Title;

    public void Dispose() => Logs?.Dispose();
}

// The panel across the bottom of the window: the activity log, and logs opened from a container or compose project,
// as tabs, or side by side.
sealed class LogPanel : Observable
{
    readonly ActivityLog activity;
    LogTab selected;
    bool sideBySide;

    public LogPanel(ActivityLog activity)
    {
        this.activity = activity;
        selected = new LogTab("activity", "Activity", null, activity) { IsSelected = true };
        Tabs.Add(selected);
        SelectCommand = new Command<LogTab>(t => { Selected = t; return Task.CompletedTask; });
        CloseTabCommand = new Command<LogTab>(t => { Close(t); return Task.CompletedTask; });
        ToggleSideBySideCommand = new Command(() => { SideBySide = !SideBySide; return Task.CompletedTask; });
        Tabs.CollectionChanged += (_, _) => { Raise(nameof(HasLogTabs)); RefreshShown(); };
        selected.ShownChanged += RefreshShown;
        RefreshShown();
        height = DashboardSettings.Current.PanelSize(HeightKey, 300);
    }

    public ObservableCollection<LogTab> Tabs { get; } = [];
    public ObservableCollection<LogTab> ShownTabs { get; } = [];
    const string HeightKey = "logs.panel";
    double height;

    public double Height
    {
        get => height;
        set { if (Set(ref height, Math.Round(value))) DashboardSettings.Current.SetPanelSize(HeightKey, height); }
    }

    void RefreshShown()
    {
        ShownTabs.Clear();
        foreach (var tab in Tabs.Where(t => t.IsShown)) ShownTabs.Add(tab);
    }
    public Command<LogTab> SelectCommand { get; }
    public Command<LogTab> CloseTabCommand { get; }
    public Command ToggleSideBySideCommand { get; }
    public bool HasLogTabs => Tabs.Count > 1;

    public LogTab Selected
    {
        get => selected;
        set
        {
            if (value is null || !Set(ref selected, value)) return;
            foreach (var tab in Tabs) tab.IsSelected = tab == value;
            Raise(nameof(SelectedIsActivity));
        }
    }

    public bool SelectedIsActivity => Selected.IsActivity;

    public bool SideBySide
    {
        get => sideBySide;
        set { if (Set(ref sideBySide, value)) Raise(nameof(ShowsOne)); }
    }

    public bool ShowsOne => !SideBySide;

    // The same container's tab again rather than a second one.
    public void Open(string key, string title, DockerClient client, IReadOnlyList<LogSource> sources)
    {
        if (Tabs.FirstOrDefault(t => t.Key == key) is not { } tab)
        {
            var logs = new LogsViewModel();
            logs.Start(client, sources);
            tab = new LogTab(key, title, logs, null);
            tab.ShownChanged += RefreshShown;
            Tabs.Add(tab);
        }
        Selected = tab;
        activity.IsOpen = true;
    }

    // A group's containers, a tab each, side by side and by themselves.
    public void OpenSideBySide(DockerClient client, IReadOnlyList<(string Title, LogSource Source)> containers)
    {
        if (containers.Count == 0) return;
        foreach (var (title, source) in containers) Open(source.Id, title, client, [source]);
        var keys = containers.Select(c => c.Source.Id).ToHashSet();
        foreach (var tab in Tabs) tab.IsShown = keys.Contains(tab.Key);
        SideBySide = containers.Count > 1;
    }

    public void Close(LogTab tab)
    {
        if (!tab.CanClose || !Tabs.Remove(tab)) return;
        tab.Dispose();
        if (Selected == tab) Selected = Tabs[^1];
        if (!HasLogTabs) SideBySide = false;
    }
}
