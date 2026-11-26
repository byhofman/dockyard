using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Tests;

public sealed class FailureTests
{
    [Fact]
    public void A_command_that_throws_is_logged_and_shown_instead_of_ending_the_dashboard()
    {
        var folder = Directory.CreateTempSubdirectory("dockyard-failures-").FullName;
        var (log, show) = (Failures.LogFile, Failures.Show);
        try
        {
            Failures.LogFile = Path.Combine(folder, "dashboard-errors.log");
            var shown = new List<string>();
            Failures.Show = shown.Add;

            new Command(() => throw new InvalidOperationException("engine went away")).Execute(null);
            new Command<string>(_ => Task.FromException(new IOException("pipe broke"))).Execute("x");

            Assert.Equal(2, shown.Count);
            Assert.Contains("engine went away", shown[0]);
            var text = File.ReadAllText(Failures.LogFile);
            Assert.Contains("InvalidOperationException", text);
            Assert.Contains("pipe broke", text);
        }
        finally
        {
            (Failures.LogFile, Failures.Show) = (log, show);
            Directory.Delete(folder, recursive: true);
        }
    }
}
