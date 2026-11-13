using System.ComponentModel;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Containers;

sealed class ContainerDetailViewModel : Observable, IDisposable
{
    readonly EngineSession session;
    readonly Action<string> showError;
    InspectViewModel? inspect;
    Features.Files.FileBrowserViewModel? files;
    string? domain;
    bool https;
    string domainPort = "";
    string domainPortNote = "";
    bool confirmRemove;
    bool logsExpanded;
    int selectedTab;
    bool? tty;
    bool disposed;

    public ContainerDetailViewModel(ContainersViewModel actions, EngineSession session, ContainerItem item, Action<string> showError)
    {
        Actions = actions;
        this.session = session;
        this.showError = showError;
        Item = item;
        AskRemoveCommand = new Command(() => { ConfirmRemove = true; return Task.CompletedTask; });
        CancelRemoveCommand = new Command(() => { ConfirmRemove = false; return Task.CompletedTask; });
        CloseCommand = new Command(() => { actions.CloseDetail(); return Task.CompletedTask; });
        ApplyDomainPortCommand = new Command(ApplyDomainPort);
        OpenDomainCommand = new Command(() =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DomainUrl) { UseShellExecute = true })?.Dispose();
            return Task.CompletedTask;
        });
        item.PropertyChanged += OnItemChanged;
        _ = Load();
        _ = LoadDomain();
    }

    public ContainersViewModel Actions { get; }
    public ContainerItem Item { get; }
    public LogsViewModel Logs { get; } = new();
    public Command AskRemoveCommand { get; }
    public Command CancelRemoveCommand { get; }
    public Command CloseCommand { get; }

    // Made when the tab is opened: Avalonia only sets a tab's content then, and that is what starts the first listing.
    public Features.Files.FileBrowserViewModel? Files => files ??= session.Client is { } client
        ? new Features.Files.FileBrowserViewModel(client, Item.Id, "/", showError)
        : null;

    public string? Domain { get => domain; private set { if (Set(ref domain, value)) Raise(nameof(HasDomain), nameof(DomainUrl)); } }
    public bool HasDomain => domain is not null;
    public string DomainUrl => $"{(https ? "https" : "http")}://{domain}";
    public string DomainPort { get => domainPort; set => Set(ref domainPort, value); }
    public string DomainPortNote { get => domainPortNote; private set => Set(ref domainPortNote, value); }
    public Command ApplyDomainPortCommand { get; }
    public Command OpenDomainCommand { get; }

    async Task LoadDomain()
    {
        if (session.Client is not { } client || AgentClient.For(client.Endpoint) is not { } agent) return;
        try { Show(await agent.DomainsAsync()); }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidOperationException) { }
    }

    void Show(DomainList list)
    {
        if (disposed) return;
        https = list.Https;
        Raise(nameof(DomainUrl));
        var mine = list.Domains.Where(d => d.ContainerId == Item.Id).ToList();
        var first = mine.OrderBy(d => d.Domain.Length).FirstOrDefault();
        Domain = first?.Domain;
        DomainPort = first?.Port?.ToString() ?? "";
        DomainPortNote = first switch
        {
            null => "",
            { PortIsChosen: true } => "Port chosen here. Empty it to detect it again.",
            { Port: null } => "No port found yet. Enter the one this container serves on.",
            _ => "Detected. Change it if this is the wrong port.",
        };
    }

    // Empty goes back to detecting, which is the way back from a wrong choice.
    async Task ApplyDomainPort()
    {
        if (session.Client is not { } client || AgentClient.For(client.Endpoint) is not { } agent) return;
        int? port = null;
        if (DomainPort.Trim().Length > 0)
        {
            if (!int.TryParse(DomainPort.Trim(), out var parsed) || parsed is < 1 or > 65535)
            {
                showError($"'{DomainPort}' is not a port");
                return;
            }
            port = parsed;
        }
        try { Show(await agent.ChoosePortAsync(Item.Id, port)); }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidOperationException) { showError($"{Item.Name}: {e.Message}"); }
    }

    public InspectViewModel? Inspect { get => inspect; private set => Set(ref inspect, value); }
    public bool ConfirmRemove { get => confirmRemove; set => Set(ref confirmRemove, value); }
    public bool LogsExpanded { get => logsExpanded; set => Set(ref logsExpanded, value); }
    public int SelectedTab { get => selectedTab; set => Set(ref selectedTab, value); }

    async Task Load()
    {
        if (session.Client is not { } client) return;
        try
        {
            var details = await client.InspectContainerAsync(Item.Id);
            if (disposed) return;
            tty = details.Config.Tty;
            Inspect = new InspectViewModel(details);
            Logs.Start(client, Item.Id, details.Config.Tty);
        }
        catch (Exception ex) when (!disposed)
        {
            showError($"{Item.Name}: {ex.Message}");
        }
    }

    void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ContainerItem.State) || disposed) return;
        if (Item.IsRunning && session.Client is { } client && tty is { } isTty)
        {
            Logs.Start(client, Item.Id, isTty);
            _ = RefreshInspect(client);
        }
    }

    async Task RefreshInspect(DockerClient client)
    {
        try
        {
            var details = await client.InspectContainerAsync(Item.Id);
            if (!disposed) Inspect = new InspectViewModel(details);
        }
        catch (DockerException) { }
    }

    public void Dispose()
    {
        disposed = true;
        Item.PropertyChanged -= OnItemChanged;
        Logs.Dispose();
        files?.Dispose();
    }
}
