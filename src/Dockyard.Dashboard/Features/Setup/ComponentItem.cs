using Dockyard.Dashboard.Shell;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Features.Setup;

// A facade row as a screen shows it: picked for the setup or not, and busy while something runs on it.
sealed class ComponentItem(ComponentRow row, bool isSelf) : Observable
{
    bool isPicked = true;
    string? busyText;

    public ComponentRow Row { get; } = row;
    public bool IsSelf { get; } = isSelf;
    public bool IsRequired => Row.RequiredBecause is not null;
    public bool CanRemove => Row.CanRemove && !IsSelf;

    public bool IsPicked { get => isPicked; set => Set(ref isPicked, value || IsRequired); }
    public string? BusyText { get => busyText; set { if (Set(ref busyText, value)) Raise(nameof(IsBusy)); } }
    public bool IsBusy => busyText is not null;
}

sealed record ComponentGroup(string Title, IReadOnlyList<ComponentItem> Items)
{
    public string Header => Title.ToUpperInvariant();

    // The facade's order of groups is its catalog's, which puts Dockyard first.
    public static IReadOnlyList<ComponentGroup> Of(IEnumerable<ComponentItem> items) =>
        items.GroupBy(i => i.Row.Group).Select(g => new ComponentGroup(g.Key, g.ToList())).ToList();
}
