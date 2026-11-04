using System.Collections.ObjectModel;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Resources;

sealed record NetworkRow(string Id, string Name, string Driver, string Subnet, int Containers)
{
    public bool IsBuiltIn => NetworkDrivers.IsBuiltIn(Name);
    public string ContainersText => Containers == 1 ? "1 container" : $"{Containers} containers";
}

sealed record NetworkMember(string ContainerId, string Name, string Address);

sealed class NetworksViewModel : SectionViewModel
{
    List<NetworkRow> all = [];
    NetworkRow? selected;
    ContainerItem? connectCandidate;
    string newName = "";
    string newDriver = "";

    public NetworksViewModel(ShellViewModel shell, EngineSession session) : base(shell, session)
    {
        RemoveCommand = new Command(() =>
        {
            if (Selected is { IsBuiltIn: false } n)
                Ask($"Remove network {n.Name}?", "Remove", () => Run($"Removing {n.Name}", c => c.RemoveNetworkAsync(n.Id)));
            return Task.CompletedTask;
        }, () => Selected is { IsBuiltIn: false });
        PruneCommand = new Command(() =>
        {
            Ask("Remove all networks that no container uses?", "Prune", () => Run("Pruning networks", c => c.PruneNetworksAsync()));
            return Task.CompletedTask;
        });
        CreateCommand = new Command(Create, () => NewName.Trim().Length > 0);
        ConnectCommand = new Command(Connect, () => Selected is not null && ConnectCandidate is not null);
        DisconnectCommand = new Command<NetworkMember>(m => Selected is { } n
            ? Run($"Disconnecting {m.Name}", async c => { await c.DisconnectNetworkAsync(n.Id, m.ContainerId); await LoadMembers(n); })
            : Task.CompletedTask);

        session.Connected += () => _ = Reload();
        session.ResourceChanged += r => { if (r.Kind == RouteKind.Networks) ScheduleReload(); };
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EngineSession.State)) Raise(nameof(Drivers));
        };
    }

    public ObservableCollection<NetworkRow> Rows { get; } = [];
    public ObservableCollection<NetworkMember> Members { get; } = [];
    public ObservableCollection<ContainerItem> Candidates { get; } = [];
    public IReadOnlyList<string> Drivers => NetworkDrivers.For(Session.IsWindowsEngine);
    public Command RemoveCommand { get; }
    public Command PruneCommand { get; }
    public Command CreateCommand { get; }
    public Command ConnectCommand { get; }
    public Command<NetworkMember> DisconnectCommand { get; }

    public NetworkRow? Selected
    {
        get => selected;
        set
        {
            if (Rebuilding) return;
            if (!Set(ref selected, value)) return;
            Raise(nameof(HasSelection));
            RemoveCommand.Refresh();
            ConnectCommand.Refresh();
            Members.Clear();
            Candidates.Clear();
            if (value is not null) _ = LoadMembers(value);
        }
    }

    public bool HasSelection => Selected is not null;

    public ContainerItem? ConnectCandidate
    {
        get => connectCandidate;
        set { if (Set(ref connectCandidate, value)) ConnectCommand.Refresh(); }
    }

    public string NewName
    {
        get => newName;
        set { if (Set(ref newName, value ?? "")) CreateCommand.Refresh(); }
    }

    public string NewDriver
    {
        get => newDriver.Length > 0 && Drivers.Contains(newDriver) ? newDriver : Drivers[0];
        set => Set(ref newDriver, value ?? "");
    }

    public override Task Reload() => SafeReload(async c =>
    {
        var networks = await c.ListNetworksAsync();
        var counts = new Dictionary<string, int>();
        foreach (var n in networks)
            counts[n.Id] = (n.Containers?.Count ?? 0) > 0 ? n.Containers!.Count : (await c.InspectNetworkAsync(n.Id)).Containers?.Count ?? 0;
        all = networks
            .Select(n => new NetworkRow(n.Id, n.Name, n.Driver, n.IPAM?.Config?.FirstOrDefault()?.Subnet ?? "", counts[n.Id]))
            .OrderBy(n => n.IsBuiltIn).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ApplyFilter();
    });

    protected override void ApplyFilter()
    {
        var id = Selected?.Id;
        Replace(Rows, all.Where(r => Matches(Filter, r.Name, r.Driver, r.Subnet)));
        var keep = Rows.FirstOrDefault(r => r.Id == id);
        if (keep is not null)
        {
            selected = keep;
            Raise(nameof(Selected));
            _ = LoadMembers(keep);
        }
        else Selected = null;
    }

    async Task LoadMembers(NetworkRow network) => await SafeReload(async c =>
    {
        var details = await c.InspectNetworkAsync(network.Id);
        if (Selected?.Id != network.Id) return;
        Members.Clear();
        foreach (var (id, m) in details.Containers ?? []) Members.Add(new NetworkMember(id, m.Name, m.IPv4Address));
        Candidates.Clear();
        var attached = Members.Select(m => m.ContainerId).ToHashSet();
        foreach (var item in Session.Containers.Items.Where(i => !attached.Contains(i.Id)).OrderBy(i => i.Name)) Candidates.Add(item);
        ConnectCandidate = null;
    });

    Task Create()
    {
        var name = NewName.Trim();
        var driver = NewDriver;
        return Run($"Creating {name}", async c =>
        {
            await c.CreateNetworkAsync(name, driver);
            NewName = "";
        });
    }

    Task Connect()
    {
        if (Selected is not { } network || ConnectCandidate is not { } container) return Task.CompletedTask;
        return Run($"Connecting {container.Name}", async c =>
        {
            await c.ConnectNetworkAsync(network.Id, container.Id);
            await LoadMembers(network);
        });
    }
}
