namespace Dockyard.Core;

sealed record ProxySettings(string? Http, string? Https, IReadOnlyList<string> NoProxy)
{
    public bool IsEmpty => Http is null && Https is null;
}
