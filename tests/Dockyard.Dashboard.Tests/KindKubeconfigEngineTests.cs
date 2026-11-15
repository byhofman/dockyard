using System.Security.Cryptography.X509Certificates;
using Dockyard.Dashboard.Features.Clusters;
using YamlDotNet.RepresentationModel;

namespace Dockyard.Dashboard.Tests;

// A real cluster on a throwaway engine named by the environment: CI's Docker on Linux, Dockyard's WSL engine in the
// Windows end-to-end run. The kubeconfig is a temporary file, never the runner's or anyone's own.
public sealed class KindKubeconfigEngineTests
{
    [Fact]
    public async Task A_cluster_made_in_the_dashboard_is_reachable_from_the_host_with_its_kubeconfig_entries_and_deleting_it_takes_them_out()
    {
        if (Environment.GetEnvironmentVariable("DOCKYARD_TEST_KIND") is not { Length: > 0 } kind) { Assert.Skip("No kind named"); return; }
        var dockerContext = Environment.GetEnvironmentVariable("DOCKYARD_TEST_DOCKER_CONTEXT") is { Length: > 0 } c ? c : "default";
        var dir = Directory.CreateTempSubdirectory("dockyard-kind-engine-test-").FullName;
        var path = Path.Combine(dir, "config");
        const string before = """
            apiVersion: v1
            kind: Config
            clusters:
            - name: contoso-prod
              cluster:
                server: https://k8s.contoso.example.com
            users:
            - name: contoso-admin
              user:
                token: fabrikam
            contexts:
            - name: contoso
              context:
                cluster: contoso-prod
                user: contoso-admin
            current-context: contoso
            """;
        File.WriteAllText(path, before);
        var actions = new KindActions(kind, dockerContext, new HostKubeconfig([path]));
        const string name = "dockyard-e2e";
        try
        {
            Assert.Equal(path, await actions.Create(name));

            var config = KubeconfigFile.Read(path);
            Assert.Equal("kind-" + name, KindKubeconfig.Current(config));
            var cluster = Entry(config, "clusters", "kind-" + name, "cluster");
            var user = Entry(config, "users", "kind-" + name, "user");
            var server = cluster["server"];
            Assert.StartsWith("https://127.0.0.1:", server);

            // The API server answers on the host's loopback, verified against the cluster's own CA, to the client
            // certificate in the entry: what kubectl on this host does with the context.
            using var ca = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(cluster["certificate-authority-data"]));
            using var pem = X509Certificate2.CreateFromPem(
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(user["client-certificate-data"])),
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(user["client-key-data"])));
            // SChannel only presents a client certificate whose key it can find in a store.
            using var client = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
            using var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(ca);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    return chain.Build(cert);
                },
            };
            handler.ClientCertificates.Add(client);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var response = await http.GetAsync(server + "/api/v1/namespaces/default", TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

            await actions.Delete(name);
            Assert.Equal(KubeconfigFile.Serialize(KubeconfigFile.Parse(before, "test")), File.ReadAllText(path));
            Assert.Equal(before, File.ReadAllText(path + KubeconfigFile.BackupSuffix));
        }
        finally
        {
            await KindCli.Run(kind, dockerContext, KindCli.Delete(name, Path.Combine(dir, "leftover")));
            Directory.Delete(dir, recursive: true);
        }
    }

    static Dictionary<string, string> Entry(YamlMappingNode config, string list, string name, string body)
    {
        var entry = ((YamlSequenceNode)config.Children[new YamlScalarNode(list)]).Children.OfType<YamlMappingNode>()
            .Single(e => ((YamlScalarNode)e.Children[new YamlScalarNode("name")]).Value == name);
        return ((YamlMappingNode)entry.Children[new YamlScalarNode(body)]).Children
            .Where(p => p.Value is YamlScalarNode)
            .ToDictionary(p => ((YamlScalarNode)p.Key).Value!, p => ((YamlScalarNode)p.Value).Value!);
    }
}
