namespace Dockyard.Dashboard.Features.Resources;

sealed record NetworkRow(string Id, string Name, string Driver, string Subnet, int Containers)
{
    public bool IsBuiltIn => NetworkDrivers.IsBuiltIn(Name);
    public string ContainersText => Containers == 1 ? "1 container" : $"{Containers} containers";
}
