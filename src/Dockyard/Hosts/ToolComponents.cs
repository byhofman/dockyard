using Dockyard.Core;

namespace Dockyard.Hosts;

// Dockyard's own pinned copy of a tool, for where no package manager brings it.
interface IOwnCopies
{
    Task<Version?> Installed(CliTool tool);
    Release? Pinned(CliTool tool);
    Task Install(Action<string> log, CliTool tool);
    Task Remove(Action<string> log, CliTool tool);
}

// A tool comes from the package manager wherever there is one, and then takes the place of a copy Dockyard put there
// earlier. Only without one, or when the user chose so, does Dockyard keep a pinned copy of its own. usePackages is that
// choice, read each time so that a change applies at once.
// ledger holds what Dockyard itself installed through the package manager.
sealed class ToolComponents(IPackageManager? packages, IOwnCopies own, Func<bool>? usePackages = null, ToolLedger? ledger = null)
{
    bool ChoseOwnCopies => usePackages?.Invoke() == false;

    public IPackageManager? Packages => usePackages?.Invoke() != false ? packages : null;

    bool Carries(CliTool tool) => Packages?.Carries(tool) == true;

    public async Task<bool> IsManaged(CliTool tool) => Carries(tool) && await packages!.Installed(tool) is not null;

    public async Task<string?> Source(CliTool tool) =>
        await IsManaged(tool) ? packages!.Name : await own.Installed(tool) is not null ? "Dockyard" : null;

    public async Task<Version?> Installed(CliTool tool) => (Carries(tool) ? await packages!.Installed(tool) : null) ?? await own.Installed(tool);

    // What the package manager would upgrade to, for a tool it manages; otherwise the pinned version.
    public async Task<Release?> Latest(CliTool tool)
    {
        if (!await IsManaged(tool)) return own.Pinned(tool);
        return await packages!.Available(tool) is { } version ? new Release(version, version.ToString(), null) : null;
    }

    // Returns true when the package manager has it.
    public Task<bool> Install(Action<string> log, CliTool tool) => Bring(log, tool, onlyIfChanged: false);

    // The wizard, on every run: only what has something newer changes.
    public Task<bool> Ensure(Action<string> log, CliTool tool) => Bring(log, tool, onlyIfChanged: true);

    async Task<bool> Bring(Action<string> log, CliTool tool, bool onlyIfChanged)
    {
        if (await FromPackageManager(log, tool, onlyIfChanged))
        {
            if (await own.Installed(tool) is not null) await own.Remove(log, tool);
            return true;
        }
        if (own.Pinned(tool) is not { } pinned) throw new InvalidOperationException($"{tool.Title} needs {packages?.Name ?? "a package manager"}");
        if (!onlyIfChanged || await own.Installed(tool) != pinned.Version) await own.Install(log, tool);
        if (ChoseOwnCopies) await RemoveOwnPackage(log, tool);
        return false;
    }

    // Moving to Dockyard's own copies: the package Dockyard installed goes, so only one copy is on the PATH. One the user
    // installed before Dockyard stays theirs.
    async Task RemoveOwnPackage(Action<string> log, CliTool tool)
    {
        if (packages is null || ledger?.Has(tool.Id) != true) return;
        try
        {
            if (packages.Carries(tool) && await packages.IsAvailable() && await packages.Installed(tool) is not null)
                await packages.Remove(log, tool);
            ledger.Remove(tool.Id);
        }
        catch (InvalidOperationException e) { log($"{tool.Title} from {packages.Name} stays ({e.Message}); Dockyard's own copy is in place next to it"); }
    }

    // The package manager's half alone, for a host that takes away its own copies in a step of its own. Returns true when
    // the package manager has the tool; a failure is logged and leaves it to Dockyard's own copy.
    public async Task<bool> FromPackageManager(Action<string> log, CliTool tool, bool onlyIfChanged = true)
    {
        if (!Carries(tool) || !await packages!.IsAvailable()) return false;
        try
        {
            var before = await packages.Installed(tool);
            if (!onlyIfChanged || before is null || await packages.Available(tool) is not { } available || available > before)
            {
                await packages.Install(log, tool);
                if (before is null) ledger?.Add(tool.Id);
            }
            return true;
        }
        catch (InvalidOperationException e)
        {
            // An upgrade that failed leaves the package that was there, which stays the one in use.
            var kept = await packages.Installed(tool) is not null;
            log(kept ? $"{e.Message}; the installed version stays" : $"{e.Message}; Dockyard puts its own copy in place instead");
            return kept;
        }
    }

    // For a tool left out of an install: only what Dockyard put there itself, since the package manager's may be the user's own.
    public async Task RemoveOwn(Action<string> log, CliTool tool)
    {
        if (await own.Installed(tool) is not null) await own.Remove(log, tool);
    }

    // Both, so that removing a tool leaves no copy behind whichever way it came.
    public async Task Remove(Action<string> log, CliTool tool)
    {
        if (await IsManaged(tool)) await packages!.Remove(log, tool);
        ledger?.Remove(tool.Id);
        if (await own.Installed(tool) is not null) await own.Remove(log, tool);
    }
}
