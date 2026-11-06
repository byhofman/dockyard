using System.Text.Json.Nodes;
using Dockyard.Core;

namespace Dockyard.Tests;

public sealed class CredentialStoreTests
{
    static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Plain_logins_are_read_from_auth_and_identity_tokens()
    {
        var json = """{"auths":{"a.example":{"auth":"AUTH"},"b.example":{"identitytoken":"tok"},"c.example":{}}}""".Replace("AUTH", B64("me:pa:ss"));
        Assert.Equal([new PlainLogin("a.example", "me", "pa:ss"), new PlainLogin("b.example", "<token>", "tok")], CredentialStore.PlainLogins(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"auths":{}}""")]
    public void Nothing_to_move_means_no_logins(string? json) => Assert.Empty(CredentialStore.PlainLogins(json));

    [Fact]
    public void Adopting_a_store_removes_the_secrets_and_keeps_everything_else()
    {
        var json = """{"auths":{"a.example":{"auth":"AUTH"}},"currentContext":"orbstack","proxies":{"default":{"httpProxy":"x"}}}""".Replace("AUTH", B64("me:secret"));
        var adopted = CredentialStore.Adopted(json, "osxkeychain");
        var config = JsonNode.Parse(adopted)!;
        Assert.Equal("osxkeychain", config["credsStore"]!.GetValue<string>());
        Assert.Equal("orbstack", config["currentContext"]!.GetValue<string>());
        Assert.Equal("x", config["proxies"]!["default"]!["httpProxy"]!.GetValue<string>());
        Assert.NotNull(config["auths"]!["a.example"]);
        Assert.DoesNotContain(B64("me:secret"), adopted);
    }

    [Fact]
    public void Uninstall_only_takes_back_the_store_dockyard_set()
    {
        Assert.Null(CredentialStore.WithoutStore("""{"credsStore":"desktop"}""", "osxkeychain"));
        Assert.DoesNotContain("credsStore", CredentialStore.WithoutStore("""{"credsStore":"osxkeychain","currentContext":"x"}""", "osxkeychain")!);
    }

    [Fact]
    public void A_broken_config_is_refused_rather_than_overwritten() =>
        Assert.Throws<InvalidOperationException>(() => CredentialStore.HasStore("{not json"));

    [Fact]
    public void The_helper_is_told_what_docker_itself_would_tell_it()
    {
        var input = JsonNode.Parse(CredentialStore.HelperInput(new PlainLogin("a.example", "me", "s\"x")))!;
        Assert.Equal("a.example", input["ServerURL"]!.GetValue<string>());
        Assert.Equal("me", input["Username"]!.GetValue<string>());
        Assert.Equal("s\"x", input["Secret"]!.GetValue<string>());
    }
}
