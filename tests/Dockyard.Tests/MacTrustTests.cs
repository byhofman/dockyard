using System.Diagnostics;
using System.Runtime.Versioning;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Tests;

/// <summary>DOK-20: turning HTTPS off and uninstalling take macOS's trust setting for the CA, not only the certificate.</summary>
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

    /// <summary>`security`, as far as trust goes: which domains trust the CA, and what removing does.</summary>
    sealed class Security(bool admin, bool user, bool removable = true, int sudoExit = 0) : ICommandRunner
    {
        public bool Admin = admin, User = user;
        public List<string> Commands { get; } = [];

        public Task<ShellResult> Run(string file, params string[] args)
        {
            var command = string.Join(' ', [file, .. args]);
            Commands.Add(command);
            var listing = (bool trusted) => new ShellResult(trusted ? 0 : 1, trusted ? $"Number of trusted certs = 1\nCert 0: {LocalCa.SubjectName}\n" : "", trusted ? "" : "SecTrustSettingsCopyCertificates: No Trust Settings were found.");
            if (command == "/usr/bin/security dump-trust-settings -d") return Task.FromResult(listing(Admin));
            if (command == "/usr/bin/security dump-trust-settings") return Task.FromResult(listing(User));
            if (command.StartsWith("sudo -n /bin/sh -c ", StringComparison.Ordinal) && command.Contains("trust-settings-import -d"))
            {
                if (sudoExit == 0 && removable) Admin = false;
                return Task.FromResult(new ShellResult(sudoExit, "", ""));
            }
            if (command.StartsWith("/usr/bin/osascript", StringComparison.Ordinal) && command.Contains("trust-settings-import -d") && removable) Admin = false;
            if (command.StartsWith("/usr/bin/security remove-trusted-cert ", StringComparison.Ordinal) && removable) User = false;
            return Task.FromResult(new ShellResult(0, "", ""));
        }

        public Process Spawn(string file, params string[] args) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_trust_setting_from_a_silent_setup_is_removed_as_administrator_before_the_certificate()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS host");
        var security = new Security(admin: true, user: false);

        await new MacHost(security).UntrustCa(_ => { }, certificate);

        var thumbprint = LocalCa.Thumbprint(File.ReadAllText(certificate));
        var remove = security.Commands.FindIndex(c => c.StartsWith("sudo -n /bin/sh -c ", StringComparison.Ordinal) && c.Contains($"Delete :trustList:{thumbprint}"));
        Assert.True(remove >= 0, string.Join('\n', security.Commands));
        Assert.True(remove < security.Commands.FindIndex(c => c.Contains("delete-certificate") || c.Contains("find-certificate")));
        Assert.False(security.Admin);
    }

    [Fact]
    public async Task Without_sudo_the_administrator_is_asked()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS host");
        var security = new Security(admin: true, user: false, sudoExit: 1);

        await new MacHost(security).UntrustCa(_ => { }, certificate);

        Assert.Contains(security.Commands, c => c.StartsWith("/usr/bin/osascript", StringComparison.Ordinal) && c.Contains("trust-settings-import -d"));
        Assert.False(security.Admin);
    }

    [Fact]
    public async Task A_trust_setting_from_the_window_is_removed_in_the_users_own_domain()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS host");
        var security = new Security(admin: false, user: true);

        await new MacHost(security).UntrustCa(_ => { }, certificate);

        Assert.Contains($"/usr/bin/security remove-trusted-cert {certificate}", security.Commands);
        Assert.DoesNotContain(security.Commands, c => c.Contains("trust-settings-import"));
        Assert.False(security.User);
    }

    [Fact]
    public async Task Nothing_is_asked_where_nothing_is_trusted()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS host");
        var security = new Security(admin: false, user: false);

        await new MacHost(security).UntrustCa(_ => { }, certificate);

        Assert.DoesNotContain(security.Commands, c => c.Contains("remove-trusted-cert") || c.Contains("trust-settings-import"));
    }

    [Fact]
    public async Task A_trust_setting_that_stays_stops_it_before_anything_is_removed()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS host");
        var security = new Security(admin: false, user: true, removable: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MacHost(security).UntrustCa(_ => { }, certificate));

        Assert.Contains("still trusts", error.Message);
        Assert.DoesNotContain(security.Commands, c => c.Contains("delete-certificate"));
    }
}
