using System.Collections.ObjectModel;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;

namespace Dockyard.Dashboard.Features.Resources;

sealed class NetworksViewModel : SelectableSection<NetworkRow>
{
    List<NetworkRow> all = [];
    ContainerItem? connectCandidate;

    public NetworksViewModel(ShellViewModel shell, EngineSession session) : base(shell, session)
    {
        RemoveCommand = new Command(() => { AskRemove(Picked); return Task.CompletedTask; }, () => Picked.Any(n => !n.IsBuiltIn));
        RemoveRowCommand = new Command<NetworkRow>(n => { AskRemove([n]); return Task.CompletedTask; });
        PruneCommand = new Command(() =>
        {
            Ask("Prune unused networks?", "Removes every network that no container uses. The built-in ones stay.", "Prune", async d =>
            {
                d.Report("Pruning networks…");
                await Client.PruneNetworksAsync();
            });
            return Task.CompletedTask;
        });
        CreateCommand = new Command(() => { AskCreate(); return Task.CompletedTask; });
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

    public ObservableCollection<NetworkMember> Members { get; } = [];
    public ObservableCollection<ContainerItem> Candidates { get; } = [];
    public IReadOnlyList<string> Drivers => NetworkDrivers.For(Session.IsWindowsEngine);
    public Command RemoveCommand { get; }
    public Command<NetworkRow> RemoveRowCommand { get; }
    public Command PruneCommand { get; }
    public Command CreateCommand { get; }
    public Command ConnectCommand { get; }
    public Command<NetworkMember> DisconnectCommand { get; }

    public ContainerItem? ConnectCandidate
    {
        get => connectCandidate;
        set { if (Set(ref connectCandidate, value)) ConnectCommand.Refresh(); }
    }

    protected override object Key(NetworkRow row) => row.Id;

    protected override void OnSelectionChanged(NetworkRow? before)
    {
        RemoveCommand.Refresh();
        ConnectCommand.Refresh();
        if (Selected?.Id == before?.Id && Selected is not null) return;
        Members.Clear();
        Candidates.Clear();
        if (Selected is { } network) _ = LoadMembers(network);
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
        if (Selected is { } network) await LoadMembers(network);
    });

    protected override IEnumerable<NetworkRow> All => all;
    public override string Summary => Count(all.Count, "network", "networks");
    protected override string?[] Words(NetworkRow row) => [row.Name, row.Driver, row.Subnet];
    protected override SearchResult Describe(NetworkRow row) => new(row.Name, row.Subnet.Length > 0 ? $"{row.Driver}  {row.Subnet}" : row.Driver, row);

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

    void AskRemove(IReadOnlyList<NetworkRow> rows)
    {
        var removable = rows.Where(n => !n.IsBuiltIn).ToList();
        if (removable.Count == 0) return;
        var title = removable.Count == 1 ? $"Remove network {removable[0].Name}?" : $"Remove {removable.Count} networks?";
        var connected = removable.Count(n => n.Containers > 0);
        var message = (connected > 0 ? "The engine refuses a network while containers are connected to it. " : "")
            + (rows.Count > removable.Count ? "Built-in networks stay." : "");
        Ask(title, message.Trim(), "Remove", d => d.ForEach(removable, "Removing", n => n.Name, n => Client.RemoveNetworkAsync(n.Id)));
    }

    void AskCreate()
    {
        var name = new DialogField("Name", placeholder: "my-network");
        var driver = new DialogField("Driver", choices: Drivers);
        Shell.Open(new Dialog("Create a network", "", "Create", async d =>
        {
            d.Report($"Creating {name.Value}…");
            await Client.CreateNetworkAsync(name.Value, driver.Value);
        }, fields: [name, driver]));
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
