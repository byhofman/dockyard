namespace Dockyard.Hosts;

static class DesktopEntry
{
    public static string Autostart(string exe) =>
        $"[Desktop Entry]\nType=Application\nName=Dockyard\nComment=Docker engine tray\nExec=\"{exe}\" --tray\nIcon=dockyard\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";
}
