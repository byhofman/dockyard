using System.Diagnostics;
using System.Runtime.Versioning;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

/// <summary>#176: where a person can approve it, turning HTTPS off and uninstalling take macOS's trust setting for the CA too.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacTrustTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "dockyard-trust-" + Guid.NewGuid().ToString("N"));
    readonly string certificate;

    public MacTrustTests()
    {
        Directory.CreateDirectory(dir);
        certificate = Path.Combine(dir, "ca.pem");
        File.WriteAllText(certificate, LocalCa.Create(DateTimeOffset.UtcNow).Certificate);
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    /// <summary>`security`, as far as trust goes: which domains trust the CA, and whether removing is approved.</summary>
    sealed class Security(bool admin, bool user, bool approved = true) : ICommandRunner
    {
        public bool Admin = admin, User = user;
        public List<string> Commands { get; } = [];
        public List<string> Limited { get; } = [];

        public Task<ShellResult> Run(string file, params string[] args)
        {
            var command = string.Join(' ', [file, .. args]);
            Commands.Add(command);
            ShellResult Listing(bool trusted) => trusted
                ? new(0, $"Number of trusted certs = 1\nCert 0: {LocalCa.SubjectName}\n", "")
                : new(1, "", "SecTrustSettingsCopyCertificates: No Trust Settings were found.");
            if (command == "/usr/bin/security dump-trust-settings -d") return Task.FromResult(Listing(Admin));
            if (command == "/usr/bin/security dump-trust-settings") return Task.FromResult(Listing(User));
            if (command.StartsWith("/usr/bin/security remove-trusted-cert -d ", StringComparison.Ordinal)) { if (approved) Admin = false; return Done(approved); }
            if (command.StartsWith("/usr/bin/security remove-trusted-cert ", StringComparison.Ordinal)) { if (approved) User = false; return Done(approved); }
            return Done(true);
        }

        public Task<ShellResult> RunWithin(TimeSpan limit, string file, params string[] args)
        {
            Limited.Add(string.Join(' ', [file, .. args]));
            return Run(file, args);
        }

        static Task<ShellResult> Done(bool ok) => Task.FromResult(new ShellResult(ok ? 0 : 1, "", ok ? "" : "The authorization was canceled by the user."));

        public Process Spawn(string file, params string[] args) => throw new NotSupportedException();
    }

    [Fact]
    public async Task With_a_person_there_both_trust_settings_go_before_the_certificate_each_asked_within_a_limit()
    {
        var security = new Security(admin: true, user: true);

        await new MacTrust(security).UntrustCa(_ => { }, certificate, silent: false);

        Assert.Equal([$"/usr/bin/security remove-trusted-cert -d {certificate}", $"/usr/bin/security remove-trusted-cert {certificate}"], security.Limited);
        var removed = security.Commands.FindLastIndex(c => c.Contains("remove-trusted-cert"));
        Assert.True(removed < security.Commands.FindIndex(c => c.Contains("find-certificate")));
        Assert.False(security.Admin);
        Assert.False(security.User);
    }

    [Fact]
    public async Task Nothing_is_asked_where_nothing_is_trusted()
    {
        var security = new Security(admin: false, user: false);

        await new MacTrust(security).UntrustCa(_ => { }, certificate, silent: false);

        Assert.Empty(security.Limited);
    }

    [Fact]
    public async Task A_removal_nobody_approved_stops_it_before_the_certificate_goes()
    {
        var security = new Security(admin: false, user: true, approved: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MacTrust(security).UntrustCa(_ => { }, certificate, silent: false));

        Assert.Contains("user's trust settings", error.Message);
        Assert.DoesNotContain(security.Commands, c => c.Contains("delete-certificate") || c.Contains("find-certificate"));
    }

    [Fact]
    public async Task Silently_no_trust_setting_is_touched_and_the_log_says_one_stays()
    {
        var security = new Security(admin: true, user: false);
        var log = new List<string>();

        await new MacTrust(security).UntrustCa(log.Add, certificate, silent: true);

        Assert.DoesNotContain(security.Commands, c => c.Contains("remove-trusted-cert"));
        Assert.Contains(log, l => l.Contains("keeps a trust setting"));
        Assert.True(security.Admin);
    }
}
