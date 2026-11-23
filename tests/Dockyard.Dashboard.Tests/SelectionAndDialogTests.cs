using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;
using Dockyard.Engine.Transport;

namespace Dockyard.Dashboard.Tests;

public sealed class SelectionAndDialogTests
{
    static Task Ui(Action test) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SelectionAndDialogTests).Assembly).Dispatch(test, TestContext.Current.CancellationToken);

    static Task Ui(Func<Task> test) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SelectionAndDialogTests).Assembly).Dispatch(test, TestContext.Current.CancellationToken);

    static void Set(object target, string member, object? value)
    {
        var type = target.GetType();
        if (type.GetField(member, BindingFlags.Instance | BindingFlags.NonPublic) is { } field) field.SetValue(target, value);
        else type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    static void Call(object target, string method) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, []);

    static EngineSession Session()
    {
        var session = new EngineSession();
        Set(session, "client", new DockerClient(DockerEndpoint.Parse("unix:///nonexistent.sock")));
        Set(session, "ContextName", "dockyard");
        return session;
    }

    static ImagesViewModel Images(ShellViewModel shell, params string[] tags)
    {
        var vm = new ImagesViewModel(shell, Session());
        Set(vm, "images", tags.Select((t, i) => new ImageSummary { Id = "sha256:" + new string((char)('a' + i), 64), RepoTags = [t] }).ToList());
        Call(vm, "Recompute");
        return vm;
    }

    [Fact]
    public Task A_bulk_action_goes_on_past_failures_and_names_them_together() => Ui(async () =>
    {
        var done = new List<string>();
        Exception? error = null;
        var dialog = new Dialog("Remove 3 images?", "", "Remove", d => d.ForEach(["a", "b", "c"], "Removing", s => s, s =>
        {
            if (s == "b") throw new InvalidOperationException("conflict: in use");
            done.Add(s);
            return Task.CompletedTask;
        }));
        var closed = false;
        dialog.Closed += () => closed = true;

        try { await dialog.Confirm(); }
        catch (Exception e) { error = e; }

        Assert.Null(error);
        Assert.Equal(["a", "c"], done);
        Assert.False(closed);
        Assert.Equal("2 of 3 done. b: conflict: in use", dialog.Error);
        Assert.False(dialog.IsWorking);
    });

    [Fact]
    public Task A_dialog_closes_when_its_action_is_done_and_a_hidden_one_reports_its_failure_as_a_toast() => Ui(async () =>
    {
        var ok = new Dialog("Pull an image", "", "Pull", _ => Task.CompletedTask, fields: [new DialogField("Image")]);
        Assert.False(ok.CanConfirm);
        ok.Field("Image").Text = "  nginx:alpine ";
        Assert.True(ok.CanConfirm);
        Assert.Equal("nginx:alpine", ok.Field("Image").Value);
        await ok.Confirm();
        Assert.True(ok.IsClosed);

        var gate = new TaskCompletionSource();
        string? toast = null;
        var slow = new Dialog("Switch to Windows containers?", "", "Switch", async _ =>
        {
            await gate.Task;
            throw new InvalidOperationException("the engine did not start");
        }) { ShowError = m => toast = m };
        var running = slow.Confirm();
        Assert.True(slow.IsWorking);
        slow.HideCommand.Execute(null);
        Assert.True(slow.IsClosed);
        gate.SetResult();
        await running;
        Assert.Equal("Switch to Windows containers: the engine did not start", toast);
    });

    [Fact]
    public Task Picking_rows_keeps_them_across_a_reload_and_only_one_shows_its_detail() => Ui(() =>
    {
        var vm = Images(new ShellViewModel(), "contoso/web:2.4", "fabrikam/worker:1.0", "nginx:alpine");
        vm.SelectedItems.Add(vm.Rows[0]);
        Assert.Same(vm.Rows[0], vm.Selected);
        Assert.False(vm.HasMany);

        vm.SelectedItems.Add(vm.Rows[2]);
        Assert.Null(vm.Selected);
        Assert.True(vm.HasMany);
        Assert.Equal("2 selected", vm.SelectionText);
        Assert.Null(vm.AllSelected);

        Call(vm, "Recompute");
        Assert.Equal(["contoso/web:2.4", "nginx:alpine"], vm.Picked.Select(r => r.Reference));
        Assert.All(vm.Picked, r => Assert.Contains(r, vm.Rows));

        vm.AllSelected = true;
        Assert.Equal(3, vm.SelectionCount);
        Assert.True(vm.AllSelected);
        vm.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, vm.SelectionCount);
        Assert.False(vm.AllSelected);
    });

    [Fact]
    public Task Removing_from_a_row_or_the_selection_asks_in_a_dialog_without_opening_the_detail() => Ui(() =>
    {
        var shell = new ShellViewModel();
        var vm = Images(shell, "contoso/web:2.4", "fabrikam/worker:1.0");

        vm.RemoveRowCommand.Execute(vm.Rows[1]);
        Assert.Equal("Remove fabrikam/worker:1.0?", shell.Dialog?.Title);
        Assert.True(shell.Dialog!.IsDanger);
        Assert.True(shell.Dialog.HasOption);
        Assert.Equal(0, vm.SelectionCount);
        shell.Dialog.CancelCommand.Execute(null);
        Assert.Null(shell.Dialog);

        vm.AllSelected = true;
        vm.RemoveCommand.Execute(null);
        Assert.Equal("Remove 2 images?", shell.Dialog?.Title);
    });

    [Fact]
    public Task The_selection_bar_shows_for_more_than_one_row_picked_in_the_list() => Ui(() =>
    {
        var shell = new ShellViewModel();
        var vm = Images(shell, "contoso/web:2.4", "fabrikam/worker:1.0", "nginx:alpine");
        var window = new Window { Width = 1000, Height = 600, Content = new ImagesView { DataContext = vm } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = window.GetVisualDescendants().OfType<ListBox>().Single();
            var bar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("selectionbar"));
            Assert.False(bar.IsVisible);

            list.SelectedItems!.Add(vm.Rows[0]);
            list.SelectedItems.Add(vm.Rows[1]);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, vm.SelectionCount);
            Assert.True(bar.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task The_dialog_is_drawn_over_the_window_and_escape_cancels_it() => Ui(() =>
    {
        var vm = new ShellViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            vm.Open(new Dialog("Create a network", "", "Create", _ => Task.CompletedTask, fields: [new DialogField("Name")]));
            Dispatcher.UIThread.RunJobs();
            var layer = window.FindControl<Panel>("DialogLayer")!;
            Assert.True(layer.IsVisible);
            Assert.Contains(layer.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Create a network");

            window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.HasDialog);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Containers_picked_through_a_project_count_its_containers_once()
    {
        static ContainerItem Item(string id, string name, string? project = null, string state = "running")
        {
            var labels = new Dictionary<string, string>();
            if (project is not null)
            {
                labels[ContainerItem.ProjectLabel] = project;
                labels[ContainerItem.ServiceLabel] = name;
            }
            var item = new ContainerItem(id);
            item.Update(new ContainerSummary { Id = id, Names = ["/" + name], State = state, Labels = labels });
            return item;
        }
        var api = Item("1", "api", "shop");
        var db = Item("2", "db", "shop", "exited");
        var redis = Item("3", "redis");
        var project = new ProjectRow(ComposeCatalog.Build([api, db, redis], []).Single(), true);

        var vm = new ContainersViewModel(new ShellViewModel(), new EngineSession());
        vm.SelectedItems.Add(project);
        vm.SelectedItems.Add(api);
        vm.SelectedItems.Add(redis);

        Assert.Equal(["1", "2", "3"], vm.PickedContainers.Select(c => c.Id).Order());
        Assert.Equal("3 selected (3 containers)", vm.SelectionText);
        Assert.True(vm.StartPickedCommand.CanExecute(null));
        Assert.Null(vm.Detail);
    }

    [Theory]
    [InlineData("switched win", "win", null)]
    [InlineData("switch-failed linux The Linux engine did not start", "linux", "The Linux engine did not start")]
    [InlineData("switch-failed win", "win", "the tray gave no reason")]
    public void The_trays_answer_to_a_switch_is_read_from_the_pipe(string line, string engine, string? error) =>
        Assert.Equal((engine, error), EngineSwitch.ParseResult(line));

    [Theory]
    [InlineData("show")]
    [InlineData("")]
    [InlineData("switched")]
    public void Other_lines_on_the_pipe_are_not_a_switch_result(string line) => Assert.Null(EngineSwitch.ParseResult(line));

    [Fact]
    public void Whether_the_other_engine_stops_comes_from_the_trays_setting_and_defaults_to_yes()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("dy-settings").FullName, "settings.json");
        Assert.True(EngineSwitch.StopsOther(file));
        File.WriteAllText(file, """{"Distro":"dockyard","StopOtherOnSwitch":false}""");
        Assert.False(EngineSwitch.StopsOther(file));
        Assert.Contains("The Linux engine keeps running", EngineSwitch.Message("win", stopsOther: false));
        File.WriteAllText(file, "not json");
        Assert.True(EngineSwitch.StopsOther(file));
        Assert.Contains("The Windows engine stops, and its running containers with it", EngineSwitch.Message("linux", stopsOther: true));
    }

    [Fact]
    public void Worker_nodes_are_asked_for_through_a_kind_config()
    {
        Assert.Equal(0, ClustersViewModel.Workers("None"));
        Assert.Equal(2, ClustersViewModel.Workers("2"));
        Assert.Equal("kind: Cluster\napiVersion: kind.x-k8s.io/v1alpha4\nnodes:\n- role: control-plane\n- role: worker\n- role: worker\n", KindCli.Config(2));
        Assert.Equal(["create", "cluster", "--name", "dev", "--kubeconfig", "own", "--config", "c.yaml"], KindCli.Create("dev", "own", "c.yaml"));
    }
}
