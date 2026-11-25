using System.Collections.ObjectModel;
using Avalonia.Threading;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Resources;

abstract class SectionViewModel : Observable, ISearchable
{
    string? busy;
    bool reloadQueued;

    protected SectionViewModel(ShellViewModel shell, EngineSession session)
    {
        Shell = shell;
        Session = session;
    }

    protected ShellViewModel Shell { get; }
    public EngineSession Session { get; }
    protected string Filter { get; private set; } = "";
    protected bool Rebuilding { get; private set; }

    protected void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        Rebuilding = true;
        try
        {
            target.Clear();
            foreach (var item in items) target.Add(item);
        }
        finally { Rebuilding = false; }
    }

    public string? Busy { get => busy; private set { if (Set(ref busy, value)) Raise(nameof(IsBusy)); } }
    public bool IsBusy => Busy is not null;

    public void ApplySearch(string text)
    {
        Filter = text;
        ApplyFilter();
    }

    public virtual void OnShown() { }

    protected abstract void ApplyFilter();

    public abstract Task Reload();

    protected static bool Matches(string filter, params string?[] values) =>
        filter.Length == 0 || values.Any(v => v?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true);

    protected void Ask(string title, string message, string confirmText, Func<Dialog, Task> action, string? option = null) =>
        Shell.Open(new Dialog(title, message, confirmText, action, danger: true, optionText: option));

    protected static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    protected Task Run(string what, Func<DockerClient, Task> action) =>
        Session.Client is { } client ? Run(what, () => action(client)) : Task.CompletedTask;

    protected async Task Run(string what, Func<Task> action)
    {
        Busy = what;
        using var working = Shell.Activity.Working(what);
        try { await action(); }
        catch (Exception ex) { Shell.ShowError(ex.Message); }
        finally { Busy = null; }
    }

    protected DockerClient Client => Session.Client ?? throw new InvalidOperationException("The engine is not connected.");

    protected void ScheduleReload()
    {
        if (reloadQueued) return;
        reloadQueued = true;
        DispatcherTimer.RunOnce(async () =>
        {
            reloadQueued = false;
            await Reload();
        }, TimeSpan.FromMilliseconds(300));
    }

    protected async Task SafeReload(Func<DockerClient, Task> load)
    {
        if (Session.Client is not { } client || !Session.IsConnected) return;
        try { await load(client); }
        catch (Exception ex) when (ex is DockerException or EngineUnavailableException or HttpRequestException or IOException) { }
    }
}

// A list where any number of rows can be picked: with Ctrl or Shift, or the row's box. One picked row shows its detail;
// more than one shows the selection bar with what can be done to all of them.
abstract class SelectableSection<TRow> : SectionViewModel, ISearchable where TRow : class
{
    TRow? selected;
    bool syncing;

    protected SelectableSection(ShellViewModel shell, EngineSession session) : base(shell, session)
    {
        SelectedItems.CollectionChanged += (_, _) => { if (!Rebuilding && !syncing) OnPicked(); };
        ClearSelectionCommand = new Command(() => { Selected = null; return Task.CompletedTask; });
    }

    public ObservableCollection<TRow> Rows { get; } = [];
    public ObservableCollection<object> SelectedItems { get; } = [];
    public Command ClearSelectionCommand { get; }
    public IReadOnlyList<TRow> Picked => SelectedItems.OfType<TRow>().ToList();
    public int SelectionCount => SelectedItems.Count;
    public bool HasMany => SelectionCount > 1;
    public string SelectionText => $"{SelectionCount} selected";

    public TRow? Selected
    {
        get => selected;
        set
        {
            if (Rebuilding) return;
            Pick(value is null ? [] : [value]);
        }
    }

    public bool HasSelection => Selected is not null;

    public bool? AllSelected
    {
        get => SelectionCount == 0 ? false : SelectionCount == Rows.Count ? true : null;
        set => Pick(value == true ? Rows : []);
    }

    protected abstract object Key(TRow row);

    protected virtual void OnSelectionChanged(TRow? before) { }

    protected void Pick(IEnumerable<TRow> rows)
    {
        syncing = true;
        try
        {
            SelectedItems.Clear();
            foreach (var row in rows) SelectedItems.Add(row);
        }
        finally { syncing = false; }
        OnPicked();
    }

    protected abstract IEnumerable<TRow> All { get; }
    protected abstract string?[] Words(TRow row);
    protected abstract SearchResult Describe(TRow row);

    protected override void ApplyFilter() => ReplaceRows(All.Where(r => Matches(Filter, Words(r))));

    public IEnumerable<SearchResult> Find(string text) => All.Where(r => Matches(text, Words(r))).Select(Describe);

    public void Reveal(object row)
    {
        if (row is not TRow wanted) return;
        var key = Key(wanted);
        Pick(Rows.Where(r => Equals(Key(r), key)).ToList());
    }

    // New rows for the same things after a reload keep what was picked.
    protected void ReplaceRows(IEnumerable<TRow> rows)
    {
        var keys = Picked.Select(Key).ToHashSet();
        Replace(Rows, rows);
        Pick(Rows.Where(r => keys.Contains(Key(r))).ToList());
    }

    void OnPicked()
    {
        var before = selected;
        selected = SelectedItems.Count == 1 ? SelectedItems[0] as TRow : null;
        Raise(nameof(Selected), nameof(HasSelection), nameof(SelectionCount), nameof(HasMany), nameof(SelectionText), nameof(AllSelected));
        OnSelectionChanged(before);
    }
}
