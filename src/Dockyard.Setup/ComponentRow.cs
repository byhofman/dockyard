namespace Dockyard.Setup;

// Installed and Newest are versions, null when it is not there or has no release. Source is where it comes from, as people
// know it: winget, Homebrew, apt, dnf or Dockyard. Needs is what it depends on or what uses it. Flag is its name for --with, --without and component add, null when it cannot be added or removed.
public sealed record ComponentRow(string Id, string Title, string Group, string? Installed, string? Newest, string Source, string? RequiredBecause, string? Needs, string? Flag, bool CanAdd, bool CanRemove, bool CanUpdate)
{
    // Removing it can take images and containers along, as for an engine.
    public bool HasData { get; init; }
}
