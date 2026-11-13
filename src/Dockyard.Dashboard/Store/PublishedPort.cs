namespace Dockyard.Dashboard.Store;

sealed record PublishedPort(int HostPort, int ContainerPort, string Protocol)
{
    public string Url => $"http://127.0.0.1:{HostPort}";
    public string Text => $"{HostPort}→{ContainerPort}/{Protocol}";
}
