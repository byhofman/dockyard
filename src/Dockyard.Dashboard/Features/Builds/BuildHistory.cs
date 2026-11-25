using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dockyard.Dashboard.Features.Builds;

sealed record BuildRecord(string Ref, string Name, DateTimeOffset Started, DateTimeOffset? Completed, string Status, int TotalSteps, int CompletedSteps, int CachedSteps)
{
    public bool IsRunning => Status.Equals("Running", StringComparison.OrdinalIgnoreCase);
    public bool IsOk => Status.Equals("Completed", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => !IsOk && !IsRunning;
    public TimeSpan? Duration => Completed - Started;
    public string StartedText => Started.ToLocalTime().ToString("d MMM yyyy HH:mm:ss", CultureInfo.CurrentCulture);
    public string DurationText => IsRunning ? "running" : Duration is { } d ? BuildHistory.Format(d) : "";
    public string StepsText => $"{CompletedSteps}/{TotalSteps} ({CachedSteps} cached)";
    public string StepsLine => $"Steps {StepsText}";
    public override string ToString() => Name;
    public string StatusText => IsOk ? "Succeeded" : IsRunning ? "Running" : Status.Equals("Canceled", StringComparison.OrdinalIgnoreCase) ? "Canceled" : "Failed";
}

sealed record BuildDetails(string? Context, string? Dockerfile, string? Error);

enum StepState { Done, Cached, Failed, Canceled, Running }

sealed record BuildStep(int Number, string Name, StepState State, TimeSpan? Duration, IReadOnlyList<string> Lines)
{
    public bool IsInternal => Name.StartsWith("[internal]", StringComparison.Ordinal);
}

// BuildKit's build history as `docker buildx history` gives it: the list as JSON lines, one build's details as JSON, and
// its steps from the plain progress output, where every line names its step: "#6 [2/4] RUN …", "#6 DONE 0.3s", "#6 CACHED".
static partial class BuildHistory
{
    public static string[] ListArgs(string context) => ["--context", context, "buildx", "history", "ls", "--format", "json"];
    public static string[] InspectArgs(string context, string reference) => ["--context", context, "buildx", "history", "inspect", reference, "--format", "json"];
    public static string[] LogsArgs(string context, string reference) => ["--context", context, "buildx", "history", "logs", reference];
    public static string[] RemoveArgs(string context, string reference) => ["--context", context, "buildx", "history", "rm", reference];

    public static List<BuildRecord> ParseList(string output)
    {
        var records = new List<BuildRecord>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                var r = json.RootElement;
                var reference = Text(r, "ref") ?? "";
                records.Add(new BuildRecord(
                    reference[(reference.LastIndexOf('/') + 1)..],
                    Text(r, "name") ?? reference,
                    Time(r, "created_at") ?? DateTimeOffset.MinValue,
                    Time(r, "completed_at"),
                    Text(r, "status") ?? "",
                    Number(r, "total_steps"), Number(r, "completed_steps"), Number(r, "cached_steps")));
            }
            catch (JsonException) { }
        }
        return records.OrderByDescending(b => b.Started).ToList();
    }

    public static BuildDetails ParseDetails(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var error = r.TryGetProperty("Error", out var e) && e.ValueKind == JsonValueKind.Object ? Text(e, "Message") : null;
            return new BuildDetails(Text(r, "Context"), Text(r, "Dockerfile"), error);
        }
        catch (JsonException) { return new BuildDetails(null, null, null); }
    }

    public static List<BuildStep> ParseSteps(string logs)
    {
        var order = new List<int>();
        var names = new Dictionary<int, string>();
        var states = new Dictionary<int, StepState>();
        var durations = new Dictionary<int, TimeSpan>();
        var lines = new Dictionary<int, List<string>>();
        foreach (var raw in logs.Split('\n'))
        {
            var match = StepLine().Match(raw.TrimEnd('\r'));
            if (!match.Success) continue;
            var n = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var rest = match.Groups[2].Value;
            if (!names.ContainsKey(n))
            {
                order.Add(n);
                names[n] = rest;
                lines[n] = [];
                continue;
            }
            if (rest == "CACHED") states[n] = StepState.Cached;
            else if (rest == "CANCELED") states[n] = StepState.Canceled;
            else if (Done().Match(rest) is { Success: true } done)
            {
                states[n] = StepState.Done;
                durations[n] = TimeSpan.FromSeconds(double.Parse(done.Groups[1].Value, CultureInfo.InvariantCulture));
            }
            else
            {
                if (rest.StartsWith("ERROR:", StringComparison.Ordinal)) states[n] = StepState.Failed;
                lines[n].Add(rest);
            }
        }
        return order.Select(n => new BuildStep(n, names[n], states.GetValueOrDefault(n, StepState.Running), durations.TryGetValue(n, out var d) ? d : null, lines[n])).ToList();
    }

    public static string Format(TimeSpan d) =>
        d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : d.TotalSeconds >= 10 ? $"{d.TotalSeconds:0}s" : $"{d.TotalSeconds:0.0}s";

    static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    static DateTimeOffset? Time(JsonElement e, string name) =>
        Text(e, name) is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var v) ? v : null;

    [GeneratedRegex(@"^#(\d+) (.*)$")]
    private static partial Regex StepLine();

    [GeneratedRegex(@"^DONE (\d+(?:\.\d+)?)s$")]
    private static partial Regex Done();
}
