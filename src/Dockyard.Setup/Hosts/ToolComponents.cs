using Dockyard.Core;

namespace Dockyard.Hosts;

// The Dockyard source: Dockyard's pinned download of a tool, for where no package manager brings it, or when the source is Dockyard.
interface IDockyardSource
{
    Task<Version?> Installed(CliTool tool);
    Release? Pinned(CliTool tool);
    Task Install(Action<string> log, CliTool tool);
    Task Remove(Action<string> log, CliTool tool);
}

// A tool comes from the package manager wherever there is one, and then takes the place of a copy Dockyard put there
// earlier. Only without one, or when the user chose the source Dockyard, does the Dockyard source put its pinned copy in place.
// source is that choice, read each time so that a change applies at once.
// ledger holds what Dockyard itself installed through the package manager.
sealed class ToolComponents(IPackageManager? packages, IDockyardSource dockyard, Func<ToolSource>? source = null, ToolLedger? ledger = null)
{
    bool ChoseDockyard => source?.Invoke() == ToolSource.Dockyard;

    public IPackageManager? Packages => ChoseDockyard ? null : packages;

    bool Carries(CliTool tool) => Packages?.Carries(tool) == true;

    public async Task<bool> IsManaged(CliTool tool) => Carries(tool) && await packages!.Installed(tool) is not null;

    public async Task<string?> Source(CliTool tool) =>
        await IsManaged(tool) ? packages!.Name : await dockyard.Installed(tool) is not null ? "Dockyard" : null;

    public async Task<Version?> Installed(CliTool tool) => (Carries(tool) ? await packages!.Installed(tool) : null) ?? await dockyard.Installed(tool);

    // What the package manager would upgrade to, for a tool it manages; otherwise the pinned version.
    public async Task<Release?> Latest(CliTool tool)
    {
        if (!await IsManaged(tool)) return dockyard.Pinned(tool);
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
            if (await dockyard.Installed(tool) is not null) await dockyard.Remove(log, tool);
            return true;
        }
        if (dockyard.Pinned(tool) is not { } pinned) throw new InvalidOperationException($"{tool.Title} needs {packages?.Name ?? "a package manager"}");
        if (!onlyIfChanged || await dockyard.Installed(tool) != pinned.Version) await dockyard.Install(log, tool);
        if (ChoseDockyard) await RemovePackage(log, tool);
        return false;
    }

    // Moving to the source Dockyard: the package Dockyard installed goes, so only one copy is on the PATH. One the user
    // installed before Dockyard stays theirs.
    async Task RemovePackage(Action<string> log, CliTool tool)
    {
        if (packages is null || ledger?.Has(tool.Id) != true) return;
        try
        {
            if (packages.Carries(tool) && await packages.IsAvailable() && await packages.Installed(tool) is not null)
                await packages.Remove(log, tool);
            ledger.Remove(tool.Id);
        }
        catch (InvalidOperationException e) { log($"{tool.Title} from {packages.Name} stays ({e.Message}); Dockyard's copy is in place next to it"); }
    }

    // The package manager's half alone, for a host that takes away Dockyard's copies in a step of its own. Returns true when
    // the package manager has the tool; a failure is logged and leaves it to Dockyard's copy.
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
            log(kept ? $"{e.Message}; the installed version stays" : $"{e.Message}; Dockyard's copy is put in place instead");
            return kept;
        }
    }

    // For a tool left out of an install: only what Dockyard put there itself, since the package manager's may be the user's own.
    public async Task RemoveDockyardCopy(Action<string> log, CliTool tool)
    {
        if (await dockyard.Installed(tool) is not null) await dockyard.Remove(log, tool);
    }

    // Both, so that removing a tool leaves no copy behind whichever way it came.
    public async Task Remove(Action<string> log, CliTool tool)
    {
        if (await IsManaged(tool)) await packages!.Remove(log, tool);
        ledger?.Remove(tool.Id);
        if (await dockyard.Installed(tool) is not null) await dockyard.Remove(log, tool);
    }
}
