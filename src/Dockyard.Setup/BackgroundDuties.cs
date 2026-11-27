using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup;

// What has to run for as long as Dockyard does: the tray's services, the engine at logon, the HTTPS certificate the engine's
// agent keeps only in memory, and a look for updates every six hours.
public sealed class BackgroundDuties
{
    internal sealed record Timing(TimeSpan CheckEvery, TimeSpan RetryAfter, TimeSpan Tick)
    {
        public static Timing Default { get; } = new(TimeSpan.FromHours(6), TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10));
    }

    // Shorter in tests.
    internal Timing Times { get; set; } = Timing.Default;
    internal string LockFile { get; set; } = ProcessLock.DutiesFile;

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

    public bool CheckIsDue => DateTime.UtcNow - lastCheck > Times.CheckEvery;

    // A check that could not reach everything is tried again sooner.
    internal void Checked(bool all) => lastCheck = all ? DateTime.UtcNow : DateTime.UtcNow - Times.CheckEvery + Times.RetryAfter;

    // The duties in this process for as long as no other process runs them: the tray holds them while it runs, the dashboard
    // only while there is no tray. A process that does not get them tries again, so it takes over when the holder goes.
    // yieldTo: gives them up while it is true. Returns when ct is cancelled.
    public async Task RunWhenFree(IProgress<SetupProgress> progress, bool startEngine, CancellationToken ct, Func<bool>? yieldTo = null)
    {
        while (!ct.IsCancellationRequested)
        {
            if (yieldTo?.Invoke() != true && ProcessLock.TryTake(LockFile) is { } taken)
            {
                using (taken) await RunHolding(progress, startEngine, yieldTo, ct);
                // The engine starts at logon once, from the process that was there at logon.
                startEngine = false;
            }
            try { await Task.Delay(Times.Tick, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task RunHolding(IProgress<SetupProgress> progress, bool startEngine, Func<bool>? yieldTo, CancellationToken ct)
    {
        using var holding = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            try { await Start(progress, startEngine, holding.Token); }
            catch (Exception e) when (e is not OperationCanceledException) { progress.Report(new SetupProgress(e.Message, SetupProgressKind.Error)); }
            while (yieldTo?.Invoke() != true) await Task.Delay(Times.Tick, ct);
        }
        catch (OperationCanceledException) { }
        finally { holding.Cancel(); }
    }

    // Returns once the engine is started, when it starts at logon; the rest goes on until ct is cancelled.
    public Task Start(IProgress<SetupProgress> progress, CancellationToken ct) => Start(progress, startEngine: true, ct);

    async Task Start(IProgress<SetupProgress> progress, bool startEngine, CancellationToken ct)
    {
        settings.Reload();
        host.StartTrayServices();
        // Setup often runs from Downloads, where Controlled Folder Access keeps it out of Documents and so out of the
        // PowerShell profile; the installed Dockyard may be allowed there, and writes only what is missing.
        if (settings.KubectlShell || settings.Dockctx) setup.ApplyShell(line => progress.Report(new SetupProgress(line)));
        _ = Https.KeepCertificateCurrent(host, ct);
        _ = LookForUpdates(progress, ct);
        if (!startEngine || !settings.StartActiveAtLogon) return;
        var engine = host.Engines.FirstOrDefault(e => e.Id == host.ActiveEngine()) ?? host.Engines[0];
        progress.Report(new SetupProgress($"Starting the {engine.Name} engine"));
        await host.Start(engine.Id);
    }

    // One failed look, or a handler that throws, is reported and tried again later; the looking never stops by itself.
    async Task LookForUpdates(IProgress<SetupProgress> progress, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (CheckIsDue)
                {
                    var checks = await setup.Check(null, skipStoppedEngines: true, ct);
                    UpdatesFound?.Invoke(checks.Where(c => c.Row.CanUpdate).Select(c => c.Row).ToList());
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                Checked(all: false);
                progress.Report(new SetupProgress($"Looking for updates failed: {e.Message}", SetupProgressKind.Warning));
            }
            try { await Task.Delay(Times.Tick, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
