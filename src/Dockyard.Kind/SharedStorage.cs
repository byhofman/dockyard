using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Dockyard.Kind;

// One folder on the engine that every node of a cluster made with it mounts. A second instance of the local-path
// provisioner kind ships, with the images kind already loaded on the nodes, offers it as the storage class "shared":
// ReadWriteMany, and a claim's folder is its namespace and name, so clusters made with it share the same files.
static partial class SharedStorage
{
    public const string HostPath = "/var/lib/dockyard/shared";
    public const string NodePath = "/var/dockyard-shared";
    public const string ClassName = "shared";
    const string WslDistro = "dockyard";
    internal const string AdminConfig = "/etc/kubernetes/admin.conf";

    public static string NodeMounts => $"  extraMounts:\n  - hostPath: {HostPath}\n    containerPath: {NodePath}\n";

    // A cluster has it when every node mounts the folder; a mount can only be added when the cluster is made.
    public static bool On(IEnumerable<(string Role, IEnumerable<string> Mounts)> nodes)
    {
        var real = nodes.Where(n => n.Role != "external-load-balancer").ToList();
        return real.Count > 0 && real.All(n => n.Mounts.Contains(NodePath));
    }

    public static string? WindowsFolder(bool linuxEngineOnWsl) =>
        linuxEngineOnWsl ? $@"\\wsl.localhost\{WslDistro}" + HostPath.Replace('/', '\\') : null;

    public static string[] ImagesArgs(string context, string node) =>
    [
        "--context", context, "exec", node, "kubectl", "--kubeconfig", AdminConfig, "-n", "local-path-storage",
        "get", "deployment", "local-path-provisioner", "-o", "jsonpath={.spec.template.spec.containers[0].image} {.spec.template.spec.containers[0].command}",
    ];

    // kind's provisioner and the helper image it names, from the output of ImagesArgs.
    public static (string Provisioner, string Helper)? Images(string output)
    {
        var provisioner = output.Trim().Split(' ', 2)[0];
        var helper = HelperImage().Match(output);
        return provisioner.Length > 0 && helper.Success ? (provisioner, helper.Groups["image"].Value) : null;
    }

    // The manifest goes to the script as an argument, never inside it.
    public static string[] ApplyArgs(string context, string node, string manifest) =>
        ["--context", context, "exec", node, "sh", "-c", $"printf '%s' \"$1\" | kubectl --kubeconfig {AdminConfig} apply -f -", "sh", manifest];

    public static async Task Install(string context, string cluster, Action<string>? onLine = null)
    {
        var node = cluster + "-control-plane";
        onLine?.Invoke("Adding the shared storage class");
        var (exit, output, error) = await Docker(ImagesArgs(context, node));
        if (exit != 0 || Images(output) is not { } images)
            throw new InvalidOperationException($"Could not find kind's storage provisioner in {cluster}: {error.Trim()}");
        (exit, output, error) = await Docker(ApplyArgs(context, node, Manifest(images.Provisioner, images.Helper)));
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) onLine?.Invoke(line);
        if (exit != 0) throw new InvalidOperationException($"Could not add the shared storage class to {cluster}: {error.Trim()}");
    }

    internal static async Task<(int ExitCode, string Output, string Error)> Docker(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(DockerCli.Path())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start docker");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    // Retain: a claim deleted in one cluster leaves the files the others may still use. The provisioner wants a folder
    // below namespace/name.
    public static string Manifest(string provisionerImage, string helperImage) => $$$"""
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: dockyard-shared-config
          namespace: local-path-storage
        data:
          config.json: |-
            {"sharedFileSystemPath": "{{{NodePath}}}"}
          helperPod.yaml: |-
            apiVersion: v1
            kind: Pod
            metadata:
              name: helper-pod
            spec:
              priorityClassName: system-node-critical
              tolerations:
                - operator: Exists
                  effect: NoSchedule
                - operator: Exists
                  effect: NoExecute
              containers:
              - name: helper-pod
                image: {{{helperImage}}}
                imagePullPolicy: IfNotPresent
          setup: |-
            #!/bin/sh
            set -eu
            mkdir -m 0777 -p "$VOL_DIR"
          teardown: |-
            #!/bin/sh
            set -eu
            rm -rf "$VOL_DIR"
        ---
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: dockyard-shared-provisioner
          namespace: local-path-storage
        spec:
          replicas: 1
          selector:
            matchLabels:
              app: dockyard-shared-provisioner
          template:
            metadata:
              labels:
                app: dockyard-shared-provisioner
            spec:
              serviceAccountName: local-path-provisioner-service-account
              nodeSelector:
                kubernetes.io/os: linux
              tolerations:
              - effect: NoSchedule
                key: node-role.kubernetes.io/control-plane
                operator: Equal
              containers:
              - name: provisioner
                image: {{{provisionerImage}}}
                imagePullPolicy: IfNotPresent
                command: [local-path-provisioner, start, --provisioner-name, dockyard.io/shared, --configmap-name, dockyard-shared-config, --helper-image, "{{{helperImage}}}", --config, /etc/config/config.json]
                env:
                - name: POD_NAMESPACE
                  valueFrom:
                    fieldRef:
                      fieldPath: metadata.namespace
                - name: CONFIG_MOUNT_PATH
                  value: /etc/config/
                volumeMounts:
                - mountPath: /etc/config/
                  name: config-volume
              volumes:
              - name: config-volume
                configMap:
                  name: dockyard-shared-config
        ---
        apiVersion: storage.k8s.io/v1
        kind: StorageClass
        metadata:
          name: {{{ClassName}}}
        provisioner: dockyard.io/shared
        reclaimPolicy: Retain
        volumeBindingMode: Immediate
        parameters:
          pathPattern: "{{ .PVC.Namespace }}/{{ .PVC.Name }}/files"

        """.ReplaceLineEndings("\n");

    [GeneratedRegex("\"--helper-image\",\"(?<image>[^\"]+)\"")]
    private static partial Regex HelperImage();
}
