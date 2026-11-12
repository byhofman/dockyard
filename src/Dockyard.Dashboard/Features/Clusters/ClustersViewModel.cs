using System.Collections.ObjectModel;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;

namespace Dockyard.Dashboard.Features.Clusters;

sealed class ClustersViewModel : SectionViewModel
{
    const int OutputLines = 200;
    List<KindCluster> all = [];
    KindCluster? selected;
    string newName = "";
    string? kind;

    public ClustersViewModel(ShellViewModel shell, EngineSession session) : base(shell, session)
    {
        CreateCommand = new Command(Create, () => !IsBusy && HasKind && !NeedsLinuxEngine && KindClusters.IsValidName(NewName.Trim()));
        DeleteCommand = new Command(() =>
        {
            if (Selected is { } c) Ask($"Delete cluster {c.Name}? Its nodes and everything in it go.", "Delete", () => RunKind($"Deleting {c.Name}", KindCli.Delete(c.Name)));
            return Task.CompletedTask;
        }, () => !IsBusy && HasKind && Selected is not null);
        CopyKubeconfigCommand = new Command(CopyKubeconfig, () => HasKind && Selected is { IsRunning: true });
        UseContextCommand = new Command(UseContext, () => !IsBusy && HasKind && Selected is { IsRunning: true });

        session.Connected += () => _ = Reload();
        session.Containers.RowsChanged += () => _ = Reload();
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EngineSession.ContextName) or nameof(EngineSession.State)) Raise(nameof(NeedsLinuxEngine), nameof(Hint));
        };
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsBusy)) RefreshCommands(); };
    }

    public ObservableCollection<KindCluster> Rows { get; } = [];
    public ObservableCollection<string> Output { get; } = [];
    public Command CreateCommand { get; }
    public Command DeleteCommand { get; }
    public Command CopyKubeconfigCommand { get; }
    public Command UseContextCommand { get; }
    public Command OpenSettingsCommand => Shell.SettingsCommand;

    // Set by the view, which has the clipboard.
    public Func<string, Task>? CopyText { get; set; }

    public bool HasKind => kind is not null;
    public bool NeedsLinuxEngine => Session.IsWindowsEngine;
    public bool HasOutput => Output.Count > 0;

    public string Hint =>
        !HasKind ? "kind is not installed. Add it under Engines and settings, or run the setup again with --with kind."
        : NeedsLinuxEngine ? "kind runs on the Linux engine. Switch to Linux to see and create clusters."
        : Rows.Count == 0 ? "No clusters yet. kind runs each node as a container on this engine."
        : "";

    public bool HasHint => Hint.Length > 0;

    public KindCluster? Selected
    {
        get => selected;
        set
        {
            if (Rebuilding || !Set(ref selected, value)) return;
            Raise(nameof(HasSelection), nameof(KubectlHint));
            RefreshCommands();
        }
    }

    public bool HasSelection => Selected is not null;
    public string KubectlHint => Selected is { } c ? $"kubectl --context {c.Context} get nodes" : "";

    public string NewName
    {
        get => newName;
        set { if (Set(ref newName, value ?? "")) CreateCommand.Refresh(); }
    }

    public override void OnShown()
    {
        kind = KindCli.Locate();
        Raise(nameof(HasKind), nameof(Hint), nameof(HasHint));
        RefreshCommands();
    }

    public override Task Reload()
    {
        kind = KindCli.Locate();
        all = NeedsLinuxEngine ? [] : KindClusters.From(Session.Containers.Items);
        ApplyFilter();
        Raise(nameof(HasKind), nameof(Hint), nameof(HasHint));
        RefreshCommands();
        return Task.CompletedTask;
    }

    protected override void ApplyFilter()
    {
        var name = Selected?.Name;
        Replace(Rows, all.Where(c => Matches(Filter, c.Name, c.Context, c.KubernetesVersion)));
        var keep = Rows.FirstOrDefault(c => c.Name == name);
        if (keep is not null)
        {
            selected = keep;
            Raise(nameof(Selected), nameof(KubectlHint));
        }
        else Selected = null;
    }

    void RefreshCommands()
    {
        CreateCommand.Refresh();
        DeleteCommand.Refresh();
        CopyKubeconfigCommand.Refresh();
        UseContextCommand.Refresh();
    }

    Task Create()
    {
        var name = NewName.Trim();
        return RunKind($"Creating {name}, which pulls the node image the first time", KindCli.Create(name), () => NewName = "");
    }

    Task RunKind(string what, string[] args, Action? onSuccess = null) => Run(what, async () =>
    {
        if (kind is not { } exe) return;
        Output.Clear();
        Raise(nameof(HasOutput));
        var result = await KindCli.Run(exe, Session.ContextName, args, line => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Output.Count == OutputLines) Output.RemoveAt(0);
            Output.Add(line);
            Raise(nameof(HasOutput));
        }));
        if (result.ExitCode != 0) throw new InvalidOperationException(LastLine(result.Error) ?? $"kind exited with {result.ExitCode}");
        onSuccess?.Invoke();
        await Reload();
    });

    async Task CopyKubeconfig()
    {
        if (Selected is not { } c || kind is not { } exe || CopyText is not { } copy) return;
        try
        {
            var result = await KindCli.Run(exe, Session.ContextName, KindCli.Kubeconfig(c.Name));
            if (result.ExitCode != 0) throw new InvalidOperationException(LastLine(result.Error) ?? $"kind exited with {result.ExitCode}");
            await copy(result.Output);
            Shell.ShowInfo($"The kubeconfig of {c.Name} is on the clipboard");
        }
        catch (Exception ex) { Shell.ShowError(ex.Message); }
    }

    Task UseContext()
    {
        if (Selected is not { } c) return Task.CompletedTask;
        return RunKind($"Making {c.Context} the current context", KindCli.UseContext(c.Name),
            () => Shell.ShowInfo($"kubectl now uses {c.Context}, from ~/.kube/config"));
    }

    static string? LastLine(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
}
