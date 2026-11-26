using Avalonia;
using Dockyard.Dashboard.Features.Resources;

namespace Dockyard.Dashboard.Shell;

// Where the window's top row meets the system's title bar. On macOS and Windows the row is the title bar, and RowInset
// keeps its content clear of the window buttons. Linux window managers differ too much, so it keeps the system's bar.
sealed record WindowChrome(bool ExtendsIntoTitleBar, Thickness RowInset)
{
    public const double RowHeight = 52;

    // The traffic lights end about 70 px from the left edge. Avalonia draws Windows' minimize, maximize and close itself,
    // 46 px wide each, as Windows does.
    public static WindowChrome For(HostOs host) => host switch
    {
        HostOs.MacOS => new(true, new Thickness(72, 0, 0, 0)),
        HostOs.Windows => new(true, new Thickness(0, 0, 3 * 46, 0)),
        _ => new(false, default),
    };

    public static WindowChrome ForThisMachine => For(OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOS : HostOs.Linux);
}
