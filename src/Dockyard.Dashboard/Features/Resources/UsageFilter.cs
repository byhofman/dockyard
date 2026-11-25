using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Features.Resources;

// All, only what a container uses, or only what none does: for images and volumes.
sealed class UsageFilter : Observable
{
    public enum Kind { All, Used, Unused }

    Kind current;
    int used;
    int unused;

    public UsageFilter() => ShowCommand = new Command<string>(k => { Current = Enum.Parse<Kind>(k); return Task.CompletedTask; });

    public Command<string> ShowCommand { get; }
    public event Action? Changed;

    public Kind Current
    {
        get => current;
        set
        {
            if (!Set(ref current, value)) return;
            Raise(nameof(IsAll), nameof(IsUsed), nameof(IsUnused));
            Changed?.Invoke();
        }
    }

    public bool IsAll => Current == Kind.All;
    public bool IsUsed => Current == Kind.Used;
    public bool IsUnused => Current == Kind.Unused;
    public string AllText => $"All {used + unused}";
    public string UsedText => $"In use {used}";
    public string UnusedText => $"Unused {unused}";

    public bool Shows(bool inUse) => Current switch
    {
        Kind.Used => inUse,
        Kind.Unused => !inUse,
        _ => true,
    };

    public void Count(IEnumerable<bool> inUse)
    {
        var list = inUse.ToList();
        used = list.Count(u => u);
        unused = list.Count - used;
        Raise(nameof(AllText), nameof(UsedText), nameof(UnusedText));
    }
}
