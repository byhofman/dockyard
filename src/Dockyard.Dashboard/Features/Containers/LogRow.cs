namespace Dockyard.Dashboard.Features.Containers;

sealed record LogRow(AnsiLine Line, bool IsError)
{
    public string Text => Line.Text;
}
