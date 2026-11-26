using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup;

// What has to run for as long as Dockyard does: the tray's services, the engine at logon, the HTTPS certificate the engine's
// agent keeps only in memory, and a look for updates every six hours.
public sealed class BackgroundDuties
{
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    readonly DockyardSetup setup;
    readonly IEngineHost host;
    readonly Settings settings;
    DateTime lastCheck = DateTime.MinValue;

    internal BackgroundDuties(DockyardSetup setup, IEngineHost host, Settings settings)
    {
        this.setup = setup;
        this.host = host;
        this.settings = settings;
    }

    // After every check of its own: the rows that have an update, empty when there are none.
    public event Action<IReadOnlyList<ComponentRow>>? UpdatesFound;
    // The same check in full, for the Toolbox's rows until it goes.
    internal event Action<IReadOnlyList<DockyardSetup.ComponentCheck>>? Checks;

    public bool CheckIsDue => DateTime.UtcNow - lastCheck > CheckEvery;

    // A check that could not reach everything is tried again sooner.
    internal void Checked(bool all) => lastCheck = all ? DateTime.UtcNow : DateTime.UtcNow - CheckEvery + RetryAfter;

    // Returns once the engine is started, when it starts at logon; the rest goes on until ct is cancelled.
    public async Task Start(IProgress<SetupProgress> progress, CancellationToken ct)
    {
        settings.Reload();
        host.StartTrayServices();
        // Setup often runs from Downloads, where Controlled Folder Access keeps it out of Documents and so out of the
        // PowerShell profile; the installed Dockyard may be allowed there, and writes only what is missing.
        if (settings.KubectlShell) setup.ApplyShell(line => progress.Report(new SetupProgress(line)));
        _ = Https.KeepCertificateCurrent(host, ct);
        _ = LookForUpdates(ct);
        if (!settings.StartActiveAtLogon) return;
        var engine = host.Engines.FirstOrDefault(e => e.Id == host.ActiveEngine()) ?? host.Engines[0];
        progress.Report(new SetupProgress($"Starting the {engine.Name} engine"));
        await host.Start(engine.Id);
    }

    async Task LookForUpdates(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                if (CheckIsDue)
                {
                    var checks = await setup.Check(null, skipStoppedEngines: true, ct);
                    Checks?.Invoke(checks);
                    UpdatesFound?.Invoke(checks.Where(c => c.Row.CanUpdate).Select(c => c.Row).ToList());
                }
                await Task.Delay(Tick, ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
