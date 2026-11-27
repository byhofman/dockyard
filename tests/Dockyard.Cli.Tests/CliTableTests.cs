namespace Dockyard.Cli.Tests;

public class CliTableTests
{
    static CliTable Components() => new CliTable("FLAG", "COMPONENT", "INSTALLED", "NOTE")
        .Add("cli", "Dockyard", "0.11.0", "The dockyard command. It or the dashboard has to stay: either one updates and uninstalls Dockyard.")
        .Add(null, "Docker Engine (Linux VM)", "29.9.2", "Required: it runs your containers.");

    [Fact]
    public void Columns_line_up_under_their_headers_and_an_empty_cell_shows_a_dash()
    {
        var lines = Components().Lines(width: null);

        Assert.Equal("FLAG  COMPONENT                 INSTALLED  NOTE", lines[0]);
        Assert.Equal("cli   Dockyard                  0.11.0     The dockyard command. It or the dashboard has to stay: either one updates and uninstalls Dockyard.", lines[1]);
        Assert.Equal("-     Docker Engine (Linux VM)  29.9.2     Required: it runs your containers.", lines[2]);
    }

    [Fact]
    public void Without_a_width_as_when_piped_every_row_stays_on_one_line()
    {
        Assert.Equal(3, Components().Lines(width: null).Count);
    }

    [Fact]
    public void In_a_narrow_terminal_the_last_column_wraps_within_itself()
    {
        var lines = Components().Lines(width: 80);

        Assert.All(lines, l => Assert.True(l.Length <= 80, l));
        Assert.Equal("cli   Dockyard                  0.11.0     The dockyard command. It or the", lines[1]);
        Assert.StartsWith(new string(' ', 43) + "dashboard", lines[2]);
        Assert.StartsWith("-     Docker Engine (Linux VM)", lines.Single(l => l.Contains("Docker Engine")));
    }

    [Fact]
    public void Where_the_last_column_would_be_too_narrow_it_goes_under_its_row()
    {
        var lines = Components().Lines(width: 56);

        Assert.Equal("FLAG  COMPONENT                 INSTALLED", lines[0]);
        Assert.Equal("cli   Dockyard                  0.11.0", lines[1]);
        Assert.Equal("      The dockyard command. It or the dashboard has to", lines[2]);
        Assert.Equal("      stay: either one updates and uninstalls Dockyard.", lines[3]);
        Assert.Equal("-     Docker Engine (Linux VM)  29.9.2", lines[4]);
        Assert.All(lines, l => Assert.True(l.Length <= 56, l));
    }

    [Fact]
    public void A_short_last_column_stays_on_its_row_however_little_room_is_left()
    {
        var lines = new CliTable("COMPONENT", "INSTALLED", "STATUS").Add("kind (Kubernetes in Docker)", "0.33.0", "update available").Lines(width: 60);

        Assert.Equal("kind (Kubernetes in Docker)  0.33.0     update available", lines[1]);
    }

    [Fact]
    public void A_word_longer_than_the_column_keeps_its_own_line()
    {
        var lines = new CliTable("ENGINE", "ENDPOINT").Add("linux", "unix:///Users/someone/with/a/long/home/.dockyard/docker.sock now").Lines(width: 40);

        Assert.Equal("linux   unix:///Users/someone/with/a/long/home/.dockyard/docker.sock", lines[1]);
        Assert.Equal("        now", lines[2]);
    }
}
