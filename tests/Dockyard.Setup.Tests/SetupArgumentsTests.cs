using Dockyard.Core;

namespace Dockyard.Setup.Tests;

public sealed class SetupArgumentsTests
{
    static SetupRequest Ok(params string[] args)
    {
        var request = SetupArguments.Parse(args, out var problems);
        Assert.Empty(problems);
        return request;
    }

    static IReadOnlyList<string> Problems(params string[] args)
    {
        SetupArguments.Parse(args, out var problems);
        return problems;
    }

    [Fact]
    public void No_arguments_is_an_interactive_request_with_the_defaults()
    {
        var request = Ok();

        Assert.Empty(request.With);
        Assert.Empty(request.Without);
        Assert.Null(request.Source);
        Assert.False(request.TrustHttps);
        Assert.False(request.KubectlShell);
        Assert.False(request.Silent);
        Assert.False(request.Resume);
        Assert.True(request.Start);
        Assert.Null(request.Image);
        Assert.False(request.RemoveData);
        Assert.False(request.OpenDashboard);
        Assert.Null(request.Assets);
    }

    [Fact]
    public void Silent_trust_https_kubectl_shell_remove_data_and_open_dashboard_are_switches()
    {
        var request = Ok("--silent", "--trust-https", "--kubectl-shell", "--remove-data", "--open-dashboard");

        Assert.True(request.Silent);
        Assert.True(request.TrustHttps);
        Assert.True(request.KubectlShell);
        Assert.True(request.RemoveData);
        Assert.True(request.OpenDashboard);
    }

    [Fact]
    public void No_start_turns_start_off()
    {
        Assert.False(Ok("--no-start").Start);
    }

    [Fact]
    public void Resume_is_silent_too()
    {
        var request = Ok("--resume");

        Assert.True(request.Resume);
        Assert.True(request.Silent);
    }

    [Fact]
    public void With_and_without_take_repeats_and_comma_lists()
    {
        var request = Ok("--with", "kind, helm", "--with", "tray", "--without", "kubectl,", "--without=dashboard");

        Assert.Equal(["kind", "helm", "tray"], request.With);
        Assert.Equal(["kubectl", "dashboard"], request.Without);
    }

    [Theory]
    [InlineData("dockyard", ToolSource.Dockyard)]
    [InlineData("package-manager", ToolSource.PackageManager)]
    public void Source_names_where_the_tools_come_from(string value, ToolSource expected)
    {
        Assert.Equal(expected, Ok("--source", value).Source);
        Assert.Equal(expected, Ok($"--source={value}").Source);
    }

    [Fact]
    public void An_unknown_source_is_a_problem()
    {
        Assert.Equal(["--source is dockyard or package-manager"], Problems("--source", "brew"));
    }

    [Fact]
    public void Image_is_taken_as_given()
    {
        Assert.Equal("engine.tar", Ok("--image", "engine.tar").Image);
    }

    [Fact]
    public void Assets_must_be_a_folder_that_exists_and_comes_back_in_full()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Equal(folder, Ok("--assets", folder).Assets);
            Assert.Equal(["--assets needs a folder that exists"], Problems("--assets", Path.Combine(folder, "missing")));
        }
        finally { Directory.Delete(folder); }
    }

    [Fact]
    public void Parsing_does_not_touch_the_global_assets_folder()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var before = DockyardSetup.LocalAssets;
            Ok("--assets", folder);
            Assert.Equal(before, DockyardSetup.LocalAssets);
        }
        finally { Directory.Delete(folder); }
    }

    [Theory]
    [InlineData("--with")]
    [InlineData("--without")]
    [InlineData("--source")]
    [InlineData("--image")]
    [InlineData("--assets")]
    public void An_option_that_needs_a_value_without_one_is_a_problem(string option)
    {
        Assert.Equal([$"{option} needs a value"], Problems(option));
        Assert.Equal([$"{option} needs a value"], Problems("--silent", option));
    }

    [Fact]
    public void A_switch_with_a_value_is_a_problem()
    {
        Assert.Equal(["--silent takes no value"], Problems("--silent=yes"));
    }

    [Fact]
    public void Unknown_options_and_stray_words_are_problems_and_all_are_reported()
    {
        Assert.Equal(["Unknown option --bogus", "Unexpected argument extra"], Problems("--bogus", "--silent", "extra"));
    }
}
