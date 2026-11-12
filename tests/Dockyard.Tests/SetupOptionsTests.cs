using Dockyard.Core;

namespace Dockyard.Tests;

public sealed class SetupOptionsTests
{
    [Fact]
    public void No_arguments_open_the_wizard()
    {
        var o = SetupOptions.Parse([]);
        Assert.False(o.Silent);
        Assert.Null(o.Dashboard);
        Assert.True(o.Start);
    }

    [Theory]
    [InlineData("--silent --dashboard", true)]
    [InlineData("--silent --no-dashboard", false)]
    [InlineData("--silent --engine-only", false)]
    public void Silent_takes_a_dashboard_choice(string args, bool dashboard)
    {
        var o = SetupOptions.Parse(args.Split(' '));
        Assert.True(o.Silent);
        Assert.Equal(SetupAction.Install, o.Action);
        Assert.Equal(dashboard, o.Dashboard);
    }

    [Fact]
    public void Silent_without_a_choice_leaves_it_open()
    {
        Assert.Null(SetupOptions.Parse(["--silent"]).Dashboard);
    }

    // What the one-liners that are already out there call.
    [Theory]
    [InlineData("--install")]
    [InlineData("--install --no-start")]
    [InlineData("--install --engine-only")]
    [InlineData("--install --image image.tar.gz")]
    public void Earlier_install_arguments_still_work(string args)
    {
        var o = SetupOptions.Parse(args.Split(' '));
        Assert.True(o.Silent);
        Assert.Equal(SetupAction.Install, o.Action);
    }

    [Fact]
    public void Uninstall_keeps_data_unless_asked()
    {
        Assert.False(SetupOptions.Parse(["--uninstall"]).RemoveData);
        Assert.True(SetupOptions.Parse(["--silent", "--uninstall", "--remove-data"]).RemoveData);
    }

    [Theory]
    [InlineData("--silent --dashboard --no-dashboard")]
    [InlineData("--silent --typo")]
    [InlineData("--silent --image")]
    [InlineData("--silent --remove-data")]
    public void Wrong_arguments_are_refused_instead_of_ignored(string args)
    {
        Assert.Throws<SetupUsageException>(() => SetupOptions.Parse(args.Split(' ')));
    }

    [Theory]
    [InlineData("--tray")]
    [InlineData("--settings")]
    [InlineData("--start linux")]
    [InlineData("--switch win")]
    [InlineData("--setup --resume")]
    public void Window_arguments_pass(string args)
    {
        Assert.False(SetupOptions.Parse(args.Split(' ')).Silent);
    }

    [Theory]
    [InlineData(null, false, false, false, true)]   // new install: with the dashboard
    [InlineData(false, false, false, false, false)]
    [InlineData(null, true, false, false, false)]   // existing install keeps what it has, also the owner's skipped dashboard
    [InlineData(null, true, true, false, true)]
    [InlineData(true, true, false, false, true)]    // an explicit choice changes it
    [InlineData(false, true, true, false, false)]
    [InlineData(null, false, false, true, false)]   // an earlier script placed the engine-only edition and called --install
    [InlineData(null, false, true, true, true)]
    public void Dashboard_choice(bool? requested, bool installed, bool has, bool inPlace, bool expected)
    {
        Assert.Equal(expected, DashboardChoice.Resolve(requested, installed, has, inPlace));
    }

    [Fact]
    public void With_and_without_name_any_component_and_dashboard_stays_what_the_old_flags_mean()
    {
        var o = SetupOptions.Parse(["--silent", "--with", "compose,buildx", "--without", "linux-engine"]);
        Assert.Equal(["compose", "buildx"], o.With);
        Assert.Equal(["linux-engine"], o.Without);

        Assert.True(SetupOptions.Parse(["--with", "dashboard"]).Dashboard);
        Assert.False(SetupOptions.Parse(["--without", "dashboard"]).Dashboard);
        Assert.True(SetupOptions.Parse(["--dashboard"]).Dashboard);
        Assert.False(SetupOptions.Parse(["--no-dashboard"]).Dashboard);
        Assert.Throws<SetupUsageException>(() => SetupOptions.Parse(["--dashboard", "--without", "dashboard"]));
        Assert.Throws<SetupUsageException>(() => SetupOptions.Parse(["--with", "compose", "--without", "compose"]));
        Assert.Throws<SetupUsageException>(() => SetupOptions.Parse(["--with", "nonsense"]));
        Assert.Throws<SetupUsageException>(() => SetupOptions.Parse(["--with"]));
    }

    [Fact]
    public void Remove_data_needs_something_that_removes()
    {
        Assert.True(SetupOptions.Parse(["--without", "linux-engine", "--remove-data"]).RemoveData);
        Assert.Throws<SetupUsageException>(() => SetupOptions.Parse(["--remove-data"]));
    }

    [Fact]
    public void Import_needs_no_window_and_can_name_its_source()
    {
        var plain = SetupOptions.Parse(["--import"]);
        Assert.True(plain.Silent);
        Assert.Equal(SetupAction.Import, plain.Action);
        Assert.Null(plain.ImportFrom);
        Assert.Equal("unix:///tmp/x.sock", SetupOptions.Parse(["--import-from", "unix:///tmp/x.sock"]).ImportFrom);
        Assert.Throws<SetupUsageException>(() => SetupOptions.Parse(["--import-from"]));
    }

    [Fact]
    public void Local_files_are_only_taken_from_a_folder_that_was_named()
    {
        var dir = Directory.CreateTempSubdirectory("dy-assets").FullName;
        try
        {
            Assert.Null(SetupOptions.Parse(["--silent"]).Assets);
            Assert.Equal(dir, SetupOptions.Parse(["--silent", "--assets", dir]).Assets);
            Assert.Throws<SetupUsageException>(() => SetupOptions.Parse(["--assets", Path.Combine(dir, "missing")]));
            Assert.Throws<SetupUsageException>(() => SetupOptions.Parse(["--assets"]));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }
}
