using Dockyard.Dashboard.Features.Builds;

namespace Dockyard.Dashboard.Tests;

public sealed class BuildHistoryTests
{
    [Fact]
    public void The_list_is_newest_first_with_its_counts_and_the_ref_without_builder_and_node()
    {
        const string output = """
            {"cached_steps":1,"completed_at":"2026-11-23T21:35:46.96Z","completed_steps":12,"created_at":"2026-11-23T21:35:43.40Z","name":"notes/Dockerfile","ref":"linux/linux/y77jjq","status":"Completed","total_steps":12}
            {"cached_steps":0,"completed_at":"2026-11-11T18:26:14.65Z","completed_steps":8,"created_at":"2026-11-11T18:26:05.88Z","name":"azdo/.artifact-test","ref":"linux/linux/c0kazd","status":"Error","total_steps":10}
            {"created_at":"2026-11-24T08:00:00Z","name":"api","ref":"linux/linux/run1","status":"Running","total_steps":5,"completed_steps":2,"cached_steps":2}
            """;

        var builds = BuildHistory.ParseList(output);

        Assert.Equal(["run1", "y77jjq", "c0kazd"], builds.Select(b => b.Ref));
        Assert.True(builds[0].IsRunning && builds[1].IsOk && builds[2].IsFailed);
        Assert.Equal("Steps 12/12 (1 cached)", builds[1].StepsLine);
        Assert.Equal($"{3.6:0.0}s", builds[1].DurationText);
        Assert.Equal("Failed", builds[2].StatusText);
    }

    [Fact]
    public void Steps_come_from_the_plain_progress_with_their_state_duration_and_own_lines()
    {
        const string logs = """
            #1 [internal] load build definition from Dockerfile
            #1 transferring dockerfile: 413B 0.1s done
            #1 DONE 0.2s

            #5 [1/4] FROM docker.io/library/node@sha256:968d
            #5 CACHED

            #8 [internal] load build context
            #8 transferring context: 82.92MB 5.0s done
            #8 ERROR: invalid file request gateway/node_modules/.bin/node-which
            ------
             > [internal] load build context:
            ------

            #9 [3/4] RUN npm ci
            #9 12.31 added 400 packages
            #9 DONE 63.2s
            """;

        var steps = BuildHistory.ParseSteps(logs);

        Assert.Equal([1, 5, 8, 9], steps.Select(s => s.Number));
        Assert.Equal([StepState.Done, StepState.Cached, StepState.Failed, StepState.Done], steps.Select(s => s.State));
        Assert.Equal(TimeSpan.FromSeconds(63.2), steps[3].Duration);
        Assert.Equal(["12.31 added 400 packages"], steps[3].Lines);
        Assert.Equal("ERROR: invalid file request gateway/node_modules/.bin/node-which", steps[2].Lines[^1]);
        Assert.True(steps[0].IsInternal && !steps[3].IsInternal);
        Assert.Equal("1m 3s", BuildHistory.Format(TimeSpan.FromSeconds(63.2)));
    }

    [Fact]
    public void A_failed_builds_details_carry_its_error()
    {
        var details = BuildHistory.ParseDetails("""{ "Context": "C:\\src\\azdo", "Dockerfile": "Dockerfile", "Status": "failed", "Error": { "Code": 2, "Message": "invalid file request" } }""");

        Assert.Equal(new BuildDetails(@"C:\src\azdo", "Dockerfile", "invalid file request"), details);
    }
}
