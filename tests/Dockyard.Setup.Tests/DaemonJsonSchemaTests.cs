using System.Text.Json.Nodes;
using Dockyard.Core;

namespace Dockyard.Setup.Tests;

public sealed class DaemonJsonSchemaTests
{
    static List<(int Line, DaemonJsonSchema.Severity Severity)> Check(string json, string engine = "linux") =>
        DaemonJsonSchema.Check(engine, json).Select(i => (i.Line, i.Severity)).ToList();

    [Fact]
    public void Common_settings_of_the_right_type_pass()
    {
        Assert.Empty(Check("""
            {
              "registry-mirrors": ["https://mirror.gcr.io"],
              "default-address-pools": [{ "base": "10.20.0.0/16", "size": 24 }],
              "log-opts": { "max-size": "10m" },
              "debug": true,
              "max-concurrent-downloads": 6
            }
            """));
    }

    [Fact]
    public void A_wrong_type_is_an_error_on_its_own_line()
    {
        var issues = DaemonJsonSchema.Check("linux", "{\n  \"debug\": true,\n  \"registry-mirrors\": \"https://mirror.gcr.io\"\n}");

        var issue = Assert.Single(issues);
        Assert.Equal((3, DaemonJsonSchema.Severity.Error), (issue.Line, issue.Severity));
        Assert.Contains("a list of texts", issue.Message);
    }

    [Fact]
    public void Broken_JSON_is_an_error_where_it_breaks()
    {
        var issue = Assert.Single(DaemonJsonSchema.Check("linux", "{\n  \"debug\": true,\n  \"log-level\": warn\n}"));

        Assert.Equal((3, DaemonJsonSchema.Severity.Error), (issue.Line, issue.Severity));
    }

    [Fact]
    public void An_unknown_key_and_one_Dockyard_sets_are_notes_since_dockerd_decides()
    {
        Assert.Equal([(2, DaemonJsonSchema.Severity.Note), (3, DaemonJsonSchema.Severity.Note)], Check("{\n  \"seccomp-profile\": \"x\",\n  \"dns\": [\"1.1.1.1\"]\n}"));
        Assert.Equal([(2, DaemonJsonSchema.Severity.Note)], Check("{\n  \"hosts\": [\"tcp://0.0.0.0:2375\"]\n}", "win"));
    }

    [Fact]
    public void The_Windows_engine_offers_only_what_it_has()
    {
        var keys = DaemonJsonSchema.For("win").Select(k => k.Name).ToList();

        Assert.Contains("registry-mirrors", keys);
        Assert.DoesNotContain("default-address-pools", keys);
        Assert.DoesNotContain("dns", keys);
        Assert.DoesNotContain("hosts", DaemonJsonSchema.For("linux").Select(k => k.Name));
    }

    [Fact]
    public void Adding_a_section_merges_lists_and_objects_into_what_is_there()
    {
        var mirror = DaemonJsonSchema.Snippets("linux").Single(s => s.Title == "Registry mirror");
        var rotation = DaemonJsonSchema.Snippets("linux").Single(s => s.Title == "Log rotation");
        const string json = """{ "registry-mirrors": ["https://my.cache"], "log-opts": { "labels": "app" } }""";

        var merged = JsonNode.Parse(DaemonJsonSchema.Add(DaemonJsonSchema.Add(json, mirror)!, rotation)!)!;

        Assert.Equal(["https://my.cache", "https://mirror.gcr.io"], merged["registry-mirrors"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Equal("app", merged["log-opts"]!["labels"]!.GetValue<string>());
        Assert.Equal("10m", merged["log-opts"]!["max-size"]!.GetValue<string>());
        Assert.Equal(merged.ToJsonString(), JsonNode.Parse(DaemonJsonSchema.Add(merged.ToJsonString(), mirror)!)!.ToJsonString());
        Assert.Null(DaemonJsonSchema.Add("{ broken", mirror));
    }

    [Fact]
    public void Every_example_has_the_type_its_key_takes()
    {
        foreach (var key in DaemonJsonSchema.Keys)
            Assert.Empty(DaemonJsonSchema.Check("linux", $"{{ \"{key.Name}\": {key.Example} }}").Where(i => i.Severity == DaemonJsonSchema.Severity.Error));
    }
}
