using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Tests;

public sealed class ToolCheckTests
{
    [Fact]
    public void The_hint_names_what_is_missing_and_where_to_add_it()
    {
        Assert.Equal("", ToolCheck.Missing(docker: true, compose: true, buildx: true));
        Assert.StartsWith("The docker command is not installed", ToolCheck.Missing(docker: false, compose: false, buildx: false), StringComparison.Ordinal);
        Assert.StartsWith("Docker Compose and Docker Buildx", ToolCheck.Missing(docker: true, compose: false, buildx: false), StringComparison.Ordinal);
        Assert.StartsWith("Docker Compose is not installed", ToolCheck.Missing(docker: true, compose: false, buildx: true), StringComparison.Ordinal);
        Assert.StartsWith("Docker Buildx is not installed", ToolCheck.Missing(docker: true, compose: true, buildx: false), StringComparison.Ordinal);
        Assert.All(new[] { ToolCheck.Missing(false, true, true), ToolCheck.Missing(true, false, true) }, h => Assert.EndsWith("in Tools.", h, StringComparison.Ordinal));
    }
}
