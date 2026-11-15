using Dockyard.Dashboard.Features.Clusters;

namespace Dockyard.Dashboard.Tests;

// Every test works on files in its own temporary folder, never on the kubeconfig of whoever runs it.
public sealed class KubeconfigTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("dockyard-kubeconfig-test-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    const string Users = """
        apiVersion: v1
        kind: Config
        preferences:
          colors: true
        clusters:
        - name: contoso-prod
          cluster:
            server: https://k8s.contoso.example.com
            certificate-authority-data: Q09OVE9TTw==
            extensions:
            - name: client.authentication.k8s.io/exec
              extension:
                audience: contoso
        users:
        - name: contoso-admin
          user:
            exec:
              apiVersion: client.authentication.k8s.io/v1beta1
              command: contoso-login
              args: ["--tenant", "northwind"]
        contexts:
        - name: contoso
          context:
            cluster: contoso-prod
            user: contoso-admin
            namespace: "007"
        current-context: contoso
        """;

    static string Kind(string cluster, int port = 52731) => $"""
        apiVersion: v1
        clusters:
        - cluster:
            certificate-authority-data: S0lORA==
            server: https://127.0.0.1:{port}
          name: kind-{cluster}
        contexts:
        - context:
            cluster: kind-{cluster}
            user: kind-{cluster}
          name: kind-{cluster}
        current-context: kind-{cluster}
        kind: Config
        preferences: {"{}"}
        users:
        - name: kind-{cluster}
          user:
            client-certificate-data: Q0VSVA==
            client-key-data: S0VZ
        """;

    string File(string name = "config") => Path.Combine(dir, name);

    static string Canonical(string yaml) => KubeconfigFile.Serialize(KubeconfigFile.Parse(yaml, "test"));

    static List<string> Names(string path, string list) =>
        ((YamlDotNet.RepresentationModel.YamlSequenceNode)KubeconfigFile.Read(path).Children[new YamlDotNet.RepresentationModel.YamlScalarNode(list)])
        .Children.Select(e => ((YamlDotNet.RepresentationModel.YamlScalarNode)((YamlDotNet.RepresentationModel.YamlMappingNode)e).Children[new YamlDotNet.RepresentationModel.YamlScalarNode("name")]).Value!).ToList();

    [Fact]
    public void A_new_cluster_joins_the_user_s_entries_and_becomes_the_current_context()
    {
        System.IO.File.WriteAllText(File(), Users);
        new HostKubeconfig([File()]).Add("dev", Kind("dev"));

        Assert.Equal(["contoso-prod", "kind-dev"], Names(File(), "clusters"));
        Assert.Equal(["contoso-admin", "kind-dev"], Names(File(), "users"));
        Assert.Equal(["contoso", "kind-dev"], Names(File(), "contexts"));
        var config = KubeconfigFile.Read(File());
        Assert.Equal("kind-dev", KindKubeconfig.Current(config));
        var text = System.IO.File.ReadAllText(File());
        Assert.Contains("server: https://127.0.0.1:52731", text);
        Assert.Contains("command: contoso-login", text);
        Assert.Contains("audience: contoso", text);
        Assert.Contains("colors: true", text);
    }

    [Fact]
    public void Deleting_the_cluster_gives_back_exactly_the_file_it_found()
    {
        System.IO.File.WriteAllText(File(), Users);
        var host = new HostKubeconfig([File()]);
        host.Add("dev", Kind("dev"));
        host.Remove("dev");

        Assert.Equal(Canonical(Users), System.IO.File.ReadAllText(File()));
        // A string that only looks like a number keeps its quotes, or kubectl would read the namespace as 7.
        Assert.Contains("namespace: \"007\"", System.IO.File.ReadAllText(File()));
    }

    [Fact]
    public void Deleting_one_cluster_leaves_the_other_and_goes_back_to_the_context_before_it()
    {
        System.IO.File.WriteAllText(File(), Users);
        var host = new HostKubeconfig([File()]);
        host.Add("dev", Kind("dev"));
        host.Add("ci", Kind("ci", 52800));
        host.Remove("ci");

        Assert.Equal(["contoso", "kind-dev"], Names(File(), "contexts"));
        Assert.Equal("kind-dev", KindKubeconfig.Current(KubeconfigFile.Read(File())));
        host.Remove("dev");
        Assert.Equal("contoso", KindKubeconfig.Current(KubeconfigFile.Read(File())));
    }

    [Fact]
    public void When_the_context_before_is_gone_none_is_current_rather_than_a_dead_one()
    {
        System.IO.File.WriteAllText(File(), Users);
        var host = new HostKubeconfig([File()]);
        host.Add("dev", Kind("dev"));
        host.Add("ci", Kind("ci", 52800));
        host.Remove("dev");
        host.Remove("ci");

        Assert.Null(KindKubeconfig.Current(KubeconfigFile.Read(File())));
        Assert.Equal(["contoso"], Names(File(), "contexts"));
    }

    [Fact]
    public void Deleting_a_cluster_that_is_not_current_leaves_the_current_context_alone()
    {
        System.IO.File.WriteAllText(File(), Users);
        var host = new HostKubeconfig([File()]);
        host.Add("dev", Kind("dev"));
        KubeconfigFile.Update(File(), c =>
        {
            c.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("current-context")] = new YamlDotNet.RepresentationModel.YamlScalarNode("contoso");
            return true;
        });
        host.Remove("dev");
        Assert.Equal("contoso", KindKubeconfig.Current(KubeconfigFile.Read(File())));
    }

    [Fact]
    public void Creating_a_cluster_again_replaces_its_entries_instead_of_adding_a_second_set()
    {
        System.IO.File.WriteAllText(File(), Users);
        var host = new HostKubeconfig([File()]);
        host.Add("dev", Kind("dev"));
        host.Add("dev", Kind("dev", 60000));

        Assert.Equal(["contoso", "kind-dev"], Names(File(), "contexts"));
        Assert.Contains("https://127.0.0.1:60000", System.IO.File.ReadAllText(File()));
        // Used again while current, it still remembers the context from before the first time.
        host.Remove("dev");
        Assert.Equal("contoso", KindKubeconfig.Current(KubeconfigFile.Read(File())));
    }

    [Fact]
    public void Without_a_kubeconfig_one_is_made_that_only_its_owner_can_read()
    {
        var path = Path.Combine(dir, ".kube", "config");
        new HostKubeconfig([path]).Add("dev", Kind("dev"));

        Assert.Equal("kind-dev", KindKubeconfig.Current(KubeconfigFile.Read(path)));
        Assert.False(System.IO.File.Exists(path + KubeconfigFile.BackupSuffix));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(path));
    }

    [Fact]
    public void The_first_write_keeps_a_backup_of_the_user_s_file_which_later_writes_never_overwrite()
    {
        System.IO.File.WriteAllText(File(), Users);
        var host = new HostKubeconfig([File()]);
        host.Add("dev", Kind("dev"));
        host.Add("ci", Kind("ci"));
        host.Remove("ci");

        Assert.Equal(Users, System.IO.File.ReadAllText(File() + KubeconfigFile.BackupSuffix));
    }

    [Fact]
    public void The_file_keeps_its_permissions_and_the_backup_gets_the_same()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps its ACL in the test below");
        if (OperatingSystem.IsWindows()) return;
        System.IO.File.WriteAllText(File(), Users);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        System.IO.File.SetUnixFileMode(File(), mode);
        new HostKubeconfig([File()]).Add("dev", Kind("dev"));

        Assert.Equal(mode, System.IO.File.GetUnixFileMode(File()));
        Assert.Equal(mode, System.IO.File.GetUnixFileMode(File() + KubeconfigFile.BackupSuffix));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void On_Windows_the_file_keeps_its_access_rules_and_the_backup_gets_the_same()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        System.IO.File.WriteAllText(File(), Users);
        var info = new FileInfo(File());
        var acl = info.GetAccessControl();
        var guests = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinGuestsSid, null);
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(guests, System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Deny));
        info.SetAccessControl(acl);
        new HostKubeconfig([File()]).Add("dev", Kind("dev"));

        bool DeniesGuests(string path) => new FileInfo(path).GetAccessControl()
            .GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .OfType<System.Security.AccessControl.FileSystemAccessRule>()
            .Any(r => r.IdentityReference.Equals(guests) && r.AccessControlType == System.Security.AccessControl.AccessControlType.Deny);
        Assert.True(DeniesGuests(File()));
        Assert.True(DeniesGuests(File() + KubeconfigFile.BackupSuffix));
    }

    [Fact]
    public void A_kubeconfig_that_is_a_link_stays_one_and_the_file_it_points_at_changes()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a link needs Developer Mode or an administrator on Windows");
        var real = File("dotfiles-kubeconfig");
        System.IO.File.WriteAllText(real, Users);
        System.IO.File.CreateSymbolicLink(File(), real);
        new HostKubeconfig([File()]).Add("dev", Kind("dev"));

        Assert.NotNull(new FileInfo(File()).LinkTarget);
        Assert.Contains("kind-dev", System.IO.File.ReadAllText(real));
    }

    [Fact]
    public void A_file_that_does_not_read_as_a_kubeconfig_is_left_byte_for_byte_with_nothing_beside_it()
    {
        const string broken = "clusters:\n- name: contoso\n  cluster: [unclosed\n";
        System.IO.File.WriteAllText(File(), broken);
        Assert.Throws<InvalidDataException>(() => new HostKubeconfig([File()]).Add("dev", Kind("dev")));
        Assert.Equal(broken, System.IO.File.ReadAllText(File()));

        System.IO.File.WriteAllText(File(), "clusters: contoso\n");
        Assert.Throws<InvalidDataException>(() => new HostKubeconfig([File()]).Add("dev", Kind("dev")));
        Assert.Equal("clusters: contoso\n", System.IO.File.ReadAllText(File()));
        Assert.Equal(["config"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void A_write_leaves_no_temporary_or_lock_file_behind()
    {
        System.IO.File.WriteAllText(File(), Users);
        var host = new HostKubeconfig([File()]);
        host.Add("dev", Kind("dev"));
        host.Remove("dev");
        Assert.Equal(["config", "config" + KubeconfigFile.BackupSuffix], Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void While_kubectl_or_kind_holds_the_lock_the_file_is_not_touched()
    {
        System.IO.File.WriteAllText(File(), Users);
        System.IO.File.WriteAllText(File() + ".lock", "");
        var error = Assert.Throws<IOException>(() => new HostKubeconfig([File()]).Add("dev", Kind("dev")));
        Assert.Contains(".lock", error.Message);
        Assert.Equal(Users, System.IO.File.ReadAllText(File()));
        Assert.True(System.IO.File.Exists(File() + ".lock"), "someone else's lock stays");
    }

    [Fact]
    public void A_kubeconfig_written_as_JSON_is_read_too()
    {
        System.IO.File.WriteAllText(File(), """{"apiVersion":"v1","kind":"Config","clusters":[{"name":"contoso-prod","cluster":{"server":"https://k8s.contoso.example.com"}}],"contexts":[],"users":[],"current-context":""}""");
        new HostKubeconfig([File()]).Add("dev", Kind("dev"));
        Assert.Equal(["contoso-prod", "kind-dev"], Names(File(), "clusters"));
    }

    [Fact]
    public void With_a_KUBECONFIG_list_entries_go_into_the_first_file_that_exists_and_come_out_of_every_file()
    {
        var missing = File("missing");
        var team = File("team");
        var own = File("own");
        System.IO.File.WriteAllText(team, Users);
        System.IO.File.WriteAllText(own, Kind("dev"));
        var paths = KubeconfigPaths.All(string.Join(Path.PathSeparator, missing, team, own, team), dir);
        Assert.Equal([missing, team, own], paths);

        var host = new HostKubeconfig(paths);
        Assert.Equal(team, host.Add("dev", Kind("dev")));
        host.Remove("dev");

        Assert.False(System.IO.File.Exists(missing));
        Assert.Equal(["contoso"], Names(team, "contexts"));
        Assert.Equal("contoso", KindKubeconfig.Current(KubeconfigFile.Read(team)));
        Assert.Empty(Names(own, "contexts"));
    }

    [Fact]
    public void Without_KUBECONFIG_it_is_the_config_in_the_home_folder() =>
        Assert.Equal([Path.Combine(dir, ".kube", "config")], KubeconfigPaths.All(null, dir));

    [Fact]
    public void A_kubeconfig_from_kind_without_the_cluster_s_entries_changes_nothing()
    {
        System.IO.File.WriteAllText(File(), Users);
        Assert.Throws<InvalidDataException>(() => new HostKubeconfig([File()]).Add("dev", Kind("other")));
        Assert.Equal(Users, System.IO.File.ReadAllText(File()));
    }
}
