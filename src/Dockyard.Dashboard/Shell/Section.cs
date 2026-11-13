namespace Dockyard.Dashboard.Shell;

sealed record Section(string Title, object Content, Avalonia.Media.Geometry? Icon)
{
    public static Avalonia.Media.Geometry? Resource(string key) =>
        Avalonia.Application.Current?.TryGetResource(key, null, out var value) == true ? value as Avalonia.Media.Geometry : null;
}
