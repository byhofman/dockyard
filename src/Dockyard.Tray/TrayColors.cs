using Avalonia.Media;

namespace Dockyard.Tray;

// A blue from the logo container's own gradient, amber, grey, and the green of the running dot, each at least 3:1 on a light and a dark menu bar or taskbar.
static class TrayColors
{
    public static readonly Color Running = Color.Parse("#2E86DA");
    public static readonly Color Stopped = Color.Parse("#8A8A8A");
    public static readonly Color Busy = Color.Parse("#B8800E");
    public static readonly Color RunningDot = Color.Parse("#18924A");
}
