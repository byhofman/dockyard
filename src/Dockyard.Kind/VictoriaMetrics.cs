namespace Dockyard.Kind;

// Metrics history for Headlamp's charts: VictoriaMetrics single-node, which scrapes the nodes and the kubelets' container
// metrics itself and answers the Prometheus query API, at a fraction of Prometheus's memory. Headlamp's Prometheus plugin
// looks for a service named as Prometheus's own chart names it, so one by that name points at VictoriaMetrics.
static class VictoriaMetrics
{
    public const string Namespace = "monitoring";

    public static readonly ClusterAddon Addon =
        new("VictoriaMetrics", "victoria-metrics", "victoria-metrics-single", "https://victoriametrics.github.io/helm-charts/", "0.52.0", Namespace,
            ["server.scrape.enabled=true", "fullnameOverride=victoria-metrics"]);

    // Plugin settings, when it does not find it by itself: monitoring/prometheus-server:80.
    public const string Manifest = """
        apiVersion: v1
        kind: Service
        metadata:
          name: prometheus-server
          namespace: monitoring
          labels:
            app: prometheus
            app.kubernetes.io/name: prometheus
            component: server
        spec:
          selector:
            app.kubernetes.io/instance: victoria-metrics
            app.kubernetes.io/name: victoria-metrics-single
          ports:
          - name: http
            port: 80
            targetPort: 8428
        """;

    public static async Task Alias(string context, string cluster, Action<string>? onLine)
    {
        var (exit, output, error) = await SharedStorage.Docker(SharedStorage.ApplyArgs(context, cluster + "-control-plane", Manifest));
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) onLine?.Invoke(line);
        if (exit != 0) onLine?.Invoke($"VictoriaMetrics runs, but Headlamp may not find it by itself: set monitoring/prometheus-server:80 in its Prometheus plugin ({error.Trim()})");
    }
}
