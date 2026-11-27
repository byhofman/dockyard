namespace Dockyard.Hosts;

// CheckNeedsEngine: checking this component starts that engine (e.g. the WSL distro), so automatic checks skip it while it is stopped.
// RequiredBecause: it cannot be removed, and the row says why. Flag: its name for --with and --without, and it has Add and Remove.
// Note: what depends on it or what it depends on. IsEngine: the last one left cannot be removed. Default: whether a new
// install has it without being asked; the tray and the dashboard are off without a display.
sealed record ComponentInfo(string Id, string Title, string? CheckNeedsEngine = null, string? RequiredBecause = null, string? Flag = null, string? Note = null, bool IsEngine = false, string? RequiredFlag = null, bool Default = true)
{
    public bool IsOptional => Flag is not null;
}
