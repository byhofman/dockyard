using Dockyard.Dashboard.Shell;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Features.Setup;

// The package manager: every component by group, where the tools come from, updates, and the import from Docker Desktop.
sealed class ToolsViewModel : Observable
{
    readonly ISetupSource source;
    readonly ShellViewModel? shell;
    IReadOnlyList<ComponentGroup> groups = [];
    SetupRun? run;
    bool isLoading;

    public ToolsViewModel(ISetupSource source, ShellViewModel? shell = null)
    {
        this.source = source;
        this.shell = shell;
        var definition = source.SettingDefinitions.First(d => d.Key == SettingDefinitions.Source);
        SourceSetting = new SettingItem(definition, source.GetSetting(definition.Key), ChangeSource);
        RefreshCommand = new Command(Load, () => !IsBusy);
        UpdateAllCommand = new Command(UpdateAll, () => !IsBusy && HasUpdates);
        ImportCommand = new Command(AskImport, () => !IsBusy);
        CloseRunCommand = new Command(() => { Run = null; return Task.CompletedTask; });
        AddCommand = new Command<ComponentItem>(item => Act(item, $"Adding {item.Row.Title}", "Adding…", p => source.Add(item.Row.Id, p)));
        UpdateCommand = new Command<ComponentItem>(item => Act(item, $"Updating {item.Row.Title}", "Updating…", p => source.Update([item.Row.Id], p)));
        RemoveCommand = new Command<ComponentItem>(AskRemove);
        UninstallCommand = new Command<ComponentItem>(AskUninstall);
        Show(source.Components());
    }

    public SettingItem SourceSetting { get; }
    public IReadOnlyList<ComponentGroup> Groups { get => groups; private set { if (Set(ref groups, value)) Raise(nameof(HasUpdates), nameof(Summary), nameof(IsEmpty)); } }
    public bool IsEmpty => groups.Count == 0 && !isLoading;
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) Raise(nameof(IsEmpty)); } }
    IEnumerable<ComponentItem> Items => groups.SelectMany(g => g.Items);
    IReadOnlyList<ComponentItem> Updatable => Items.Where(i => i.Row.CanUpdate).ToList();
    public bool HasUpdates => Updatable.Count > 0;

    public string Summary
    {
        get
        {
            var installed = Items.Count(i => i.Row.Installed is not null);
            var updates = Items.Count(i => i.Row.CanUpdate);
            var counted = $"{installed} of {Items.Count()} installed";
            return updates switch { 0 => counted + ", all up to date", 1 => counted + ", 1 update available", _ => $"{counted}, {updates} updates available" };
        }
    }

    // What runs now or ran last; one at a time, as the facade does.
    public SetupRun? Run { get => run; private set { if (Set(ref run, value)) Raise(nameof(HasRun), nameof(IsBusy)); Refresh(); } }
    public bool HasRun => run is not null;
    public bool IsBusy => run is { IsRunning: true };

    public Command RefreshCommand { get; }
    public Command UpdateAllCommand { get; }
    public Command ImportCommand { get; }
    public Command CloseRunCommand { get; }
    public Command<ComponentItem> AddCommand { get; }
    public Command<ComponentItem> UpdateCommand { get; }
    public Command<ComponentItem> RemoveCommand { get; }
    public Command<ComponentItem> UninstallCommand { get; }

    public async Task Load()
    {
        IsLoading = true;
        try { Show(await source.ComponentsAsync(CancellationToken.None)); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException) { shell?.ShowError(ex.Message); }
        finally { IsLoading = false; }
    }

    void Show(IReadOnlyList<ComponentRow> rows)
    {
        var busy = Items.Where(i => i.IsBusy).ToDictionary(i => i.Row.Id, i => i.BusyText);
        Groups = ComponentGroup.Of(rows.Select(r => new ComponentItem(r, r.Id == source.SelfId) { BusyText = busy.GetValueOrDefault(r.Id) }));
    }

    void Refresh()
    {
        RefreshCommand.Refresh();
        UpdateAllCommand.Refresh();
        ImportCommand.Refresh();
    }

    Task UpdateAll()
    {
        var items = Updatable;
        return Act(items, items.Count == 1 ? $"Updating {items[0].Row.Title}" : $"Updating {items.Count} components", "Updating…",
            p => source.Update(items.Select(i => i.Row.Id).ToList(), p));
    }

    Task ChangeSource(SettingItem item, string value) => Act([], $"Moving the tools to {Shown(item.Definition, value)}", "", async p =>
    {
        item.Status = $"Moving docker, kind, kubectl and helm to {Shown(item.Definition, value)}…";
        try { await source.SetSetting(item.Definition.Key, value, p); }
        finally { item.Status = null; }
    });

    static string Shown(SettingDefinition definition, string value) =>
        definition is { Choices: { } choices, ChoiceTitles: { } titles } && choices.ToList().IndexOf(value) is var at and >= 0 ? titles[at] : value;

    Task AskRemove(ComponentItem item)
    {
        shell?.Open(new Dialog($"Remove {item.Row.Title}?", RemoveText(item.Row), "Remove",
            d => Act(item, $"Removing {item.Row.Title}", "Removing…", p => source.Remove(item.Row.Id, item.Row.HasData && d.Option, p)), danger: true,
            optionText: item.Row.HasData ? "Also remove its images and containers" : null));
        return Task.CompletedTask;
    }

    // What goes and what stays.
    internal static string RemoveText(ComponentRow row)
    {
        var how = row.Source == SettingDefinitions.DockyardSource ? "" : $" through {row.Source}";
        var stays = row.HasData ? "Its images and containers stay unless you choose otherwise below." : "Your images, containers and settings stay.";
        return $"{row.Title} is taken off this computer{how}. {stays} You can add it again here later.";
    }

    Task AskUninstall(ComponentItem item)
    {
        shell?.Open(new Dialog("Uninstall Dockyard?",
            "Removes Dockyard, its engines, the tools it downloaded and the docker contexts. Tools your package manager installed stay, to remove with it.",
            "Uninstall", d => Act(item, "Uninstalling Dockyard", "Uninstalling…", p => source.Uninstall(d.Option, p)), danger: true,
            optionText: "Also delete my images and containers"));
        return Task.CompletedTask;
    }

    Task AskImport()
    {
        shell?.Open(new Dialog("Import from Docker Desktop?", ImportText, "Import", _ => Act([], "Importing from Docker Desktop", "", source.Import)));
        return Task.CompletedTask;
    }

    public const string ImportText = "Copy your images, volumes and containers from Docker Desktop. It is started if it is not running, and keeps everything. Containers arrive stopped.";

    Task Act(ComponentItem item, string heading, string busyText, Func<IProgress<SetupProgress>, Task> act) => Act([item], heading, busyText, act);

    async Task Act(IReadOnlyList<ComponentItem> items, string heading, string busyText, Func<IProgress<SetupProgress>, Task> act)
    {
        if (IsBusy) return;
        var now = new SetupRun(heading);
        Run = now;
        foreach (var i in items) i.BusyText = busyText;
        using var working = shell?.Activity.Working(heading);
        try
        {
            await act(now);
            now.Finish();
        }
        catch (Exception ex) { now.Finish(ex.Message); }
        finally
        {
            foreach (var i in items) i.BusyText = null;
            Raise(nameof(IsBusy));
            Refresh();
        }
        if (!now.IsFailed) await Load();
    }
}
