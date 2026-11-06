using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dockyard.Core;

sealed record PlainLogin(string Server, string Username, string Secret);

// ~/.docker/config.json is shared with every other Docker app on the machine, so a store someone already chose stays,
// and only the keys below change. Without a store, docker login writes the password there as base64.
static class CredentialStore
{
    public static string ConfigFile => Path.Combine(
        Environment.GetEnvironmentVariable("DOCKER_CONFIG") is { Length: > 0 } dir ? dir : Path.Combine(Paths.UserProfile, ".docker"), "config.json");

    static JsonObject Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonNode.Parse(json) as JsonObject ?? []; }
        catch (JsonException) { throw new InvalidOperationException($"{ConfigFile} is not valid JSON; it was left as it is"); }
    }

    public static bool HasStore(string? json) => Parse(json)["credsStore"]?.GetValue<string>() is { Length: > 0 };

    public static List<PlainLogin> PlainLogins(string? json)
    {
        List<PlainLogin> logins = [];
        if (Parse(json)["auths"] is not JsonObject auths) return logins;
        foreach (var (server, node) in auths)
        {
            if (node?["identitytoken"]?.GetValue<string>() is { Length: > 0 } token) logins.Add(new(server, "<token>", token));
            else if (node?["auth"]?.GetValue<string>() is { Length: > 0 } auth)
            {
                string decoded;
                try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth)); }
                catch (FormatException) { continue; }
                var colon = decoded.IndexOf(':');
                if (colon > 0) logins.Add(new(server, decoded[..colon], decoded[(colon + 1)..]));
            }
        }
        return logins;
    }

    public static string Adopted(string? json, string store)
    {
        var config = Parse(json);
        config["credsStore"] = store;
        // Only what went to the store leaves the file: an entry that could not be read as a login is still someone's secret.
        var moved = PlainLogins(json).Select(l => l.Server).ToHashSet();
        if (config["auths"] is JsonObject auths)
            foreach (var (server, node) in auths)
                if (node is JsonObject entry && moved.Contains(server))
                {
                    entry.Remove("auth");
                    entry.Remove("identitytoken");
                }
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string? WithoutStore(string? json, string store)
    {
        var config = Parse(json);
        if (config["credsStore"]?.GetValue<string>() != store) return null;
        config.Remove("credsStore");
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // What the helper is given on its stdin to store one login.
    public static string HelperInput(PlainLogin login)
    {
        var input = new JsonObject { ["ServerURL"] = login.Server, ["Username"] = login.Username, ["Secret"] = login.Secret };
        return input.ToJsonString();
    }

    // Logins are in the store before they leave the file, so a failure halfway loses none.
    public static async Task Adopt(Action<string> log, string store, string helper, string marker)
    {
        var existing = File.Exists(ConfigFile) ? await File.ReadAllTextAsync(ConfigFile) : null;
        try
        {
            if (HasStore(existing)) return;
        }
        catch (InvalidOperationException e)
        {
            log(e.Message);
            return;
        }
        foreach (var login in PlainLogins(existing))
        {
            var r = await Shell.RunWithInput(helper, HelperInput(login), "store");
            if (r.ExitCode != 0)
            {
                log($"The login for {login.Server} could not be moved to the credential store, so {ConfigFile} was left as it is: {(r.Error + r.Output).Trim()}");
                return;
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        var temp = ConfigFile + ".dockyard";
        await File.WriteAllTextAsync(temp, Adopted(existing, store));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, ConfigFile, overwrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, store);
        log($"docker login keeps credentials in the {store} credential store");
    }

    // Without the helper a store in the config breaks every docker login and pull, so uninstall takes back what it set.
    public static async Task Forget(string marker)
    {
        if (!File.Exists(marker)) return;
        var store = (await File.ReadAllTextAsync(marker)).Trim();
        try
        {
            if (File.Exists(ConfigFile) && WithoutStore(await File.ReadAllTextAsync(ConfigFile), store) is { } updated)
                await File.WriteAllTextAsync(ConfigFile, updated);
        }
        // It is unreadable for docker too, so there is no store in it to take back, and uninstalling goes on.
        catch (InvalidOperationException) { }
        File.Delete(marker);
    }
}
