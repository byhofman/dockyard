using Dockyard.Dashboard.Shell;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Features.Setup;

// The first run: which components, where the tools come from and trusted HTTPS, then the setup itself, step by step.
sealed class SetupViewModel : Observable
{
    readonly ISetupSource source;
    readonly ShellViewModel? shell;
    SetupRun? run;
    CancellationTokenSource? cancel;

    public SetupViewModel(ISetupSource source, ShellViewModel? shell = null)
    {
        this.source = source;
        this.shell = shell;
        Groups = ComponentGroup.Of(source.Components().Select(r => new ComponentItem(r, r.Id == source.SelfId)));
        SettingItem Local(string key) =>
            new(source.SettingDefinitions.First(d => d.Key == key), source.GetSetting(key));
        Options = [Local(SettingDefinitions.Source), Local(SettingDefinitions.TrustedHttps), Local(SettingDefinitions.KubectlShell)];
        InstallCommand = new Command(Start, () => !IsRunning);
        TryAgainCommand = new Command(Start, () => !IsRunning);
        BackCommand = new Command(() => { Run = null; return Task.CompletedTask; }, () => !IsRunning);
        CancelCommand = new Command(() => { cancel?.Cancel(); return Task.CompletedTask; }, () => IsRunning);
        CloseCommand = new Command(() => { Closed?.Invoke(); return Task.CompletedTask; }, () => CanClose && !IsRunning);
        foreach (var item in Items)
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ComponentItem.IsPicked)) Raise(nameof(PickedText)); };
    }

    public IReadOnlyList<ComponentGroup> Groups { get; }
    public IReadOnlyList<SettingItem> Options { get; }
    public string Description => OperatingSystem.IsWindows()
        ? "Installs the Windows Docker engine (dockerd service), the docker CLI with compose and buildx, a dedicated 'dockyard' WSL distro for Linux containers, and Dockyard itself, which runs in the tray."
        : OperatingSystem.IsMacOS()
            ? "Installs the docker CLI with compose and buildx, a small Linux VM for containers that starts when you need it, and Dockyard itself, which runs in the menu bar."
            : "Installs Docker CE with compose and buildx from Docker's repository, starts it on demand, and Dockyard itself, which runs in the tray.";

    IEnumerable<ComponentItem> Items => Groups.SelectMany(g => g.Items);
    public string PickedText => $"{Items.Count(i => i.IsPicked)} of {Items.Count()} components. You can add or remove them later, in Tools.";

    public SetupRun? Run
    {
        get => run;
        private set { if (Set(ref run, value)) Raise(nameof(ShowsChoices), nameof(ShowsRun), nameof(IsRunning), nameof(IsFailed), nameof(ShowsClose)); }
    }

    public bool ShowsChoices => run is null;
    public bool ShowsRun => run is not null;
    public bool IsRunning => run is { IsRunning: true };
    public bool IsFailed => run is { IsFailed: true };

    public Command InstallCommand { get; }
    public Command TryAgainCommand { get; }
    public Command BackCommand { get; }
    public Command CancelCommand { get; }

    // Opened on an installed Dockyard, as --section setup does, it can be left without running.
    public bool CanClose => source.IsInstalled;
    public bool ShowsClose => CanClose && !IsRunning;
    public Command CloseCommand { get; }
    public event Action? Closed;

    // The setup is done and Dockyard is installed: the shell shows its sections.
    public event Action? Installed;

    async Task Start()
    {
        var picked = Items.Where(i => i.Row.Flag is not null).ToList();
        var request = SetupRequest.FromSettings(
            picked.Where(i => i.IsPicked).Select(i => i.Row.Flag!).ToList(),
            picked.Where(i => !i.IsPicked).Select(i => i.Row.Flag!).ToList(),
            Options.ToDictionary(o => o.Definition.Key, o => o.Value));
        var now = new SetupRun("Installing Dockyard");
        cancel = new CancellationTokenSource();
        Run = now;
        Refresh();
        using var working = shell?.Activity.Working("Setting up Dockyard");
        try
        {
            var code = await source.Setup(request, now, cancel.Token);
            now.Finish(code == 0 ? null : "The setup did not finish. The lines above say where it stopped.");
        }
        catch (OperationCanceledException) { now.Finish("The setup was cancelled."); }
        catch (Exception ex) { now.Finish(ex.Message); }
        Raise(nameof(IsRunning), nameof(IsFailed), nameof(ShowsClose));
        Refresh();
        if (!now.IsFailed) Installed?.Invoke();
    }

    void Refresh()
    {
        InstallCommand.Refresh();
        TryAgainCommand.Refresh();
        BackCommand.Refresh();
        CancelCommand.Refresh();
        CloseCommand.Refresh();
    }
}
