using Dockyard.Setup;
using Dockyard.Setup.Tests;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Dockyard.Cli.Tests;

sealed class RecordingLauncher : IDashboardLauncher
{
    public List<string> Sections { get; } = [];
    public void Open(string? dashboardExe, string section) => Sections.Add(section);
}

// The keys a person would press, one after the other.
sealed class ScriptedInput : IAnsiConsoleInput
{
    readonly Queue<ConsoleKeyInfo> keys = new();

    public void Press(ConsoleKey key, char c = '\0') => keys.Enqueue(new ConsoleKeyInfo(c, key, false, false, false));
    public bool IsKeyAvailable() => keys.Count > 0;
    public ConsoleKeyInfo? ReadKey(bool intercept) => keys.Count > 0 ? keys.Dequeue() : throw new InvalidOperationException("The prompt asked for more keys than the test pressed");
    public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken) => Task.FromResult(ReadKey(intercept));
}

sealed class ScriptedConsole(IAnsiConsole inner, IAnsiConsoleInput input) : IAnsiConsole
{
    public Profile Profile => inner.Profile;
    public IAnsiConsoleCursor Cursor => inner.Cursor;
    public IAnsiConsoleInput Input => input;
    public IExclusivityMode ExclusivityMode => inner.ExclusivityMode;
    public RenderPipeline Pipeline => inner.Pipeline;
    public void Clear(bool home) => inner.Clear(home);
    public void Write(IRenderable renderable) => inner.Write(renderable);
    public void WriteAnsi(Action<AnsiWriter> action) => inner.WriteAnsi(action);
}

sealed class CliHarness : IDisposable
{
    readonly TempSettings settings = new();
    readonly StringWriter output = new();
    readonly StringWriter error = new();

    public CliHarness(IReadOnlyList<Dockyard.Hosts.ComponentInfo>? catalog = null)
    {
        Host = catalog is null ? new FakeHost() : new FakeHost { Components = catalog };
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(output),
            // Spectre's CI enrichers turn interactivity off on build servers, and these tests drive the prompts.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        console.Profile.Width = 200;
        Console = new ScriptedConsole(console, Keys);
        Setup = Facade.For(Host, settings);
    }

    public FakeHost Host { get; }
    public DockyardSetup Setup { get; }
    public ScriptedInput Keys { get; } = new();
    public RecordingLauncher Launcher { get; } = new();
    public IAnsiConsole Console { get; }
    public bool HasTerminal { get; set; } = true;
    public bool HasDesktop { get; set; }
    public string Output => output.ToString();
    public string Error => error.ToString();

    public CliEnvironment Environment => new(() => Setup, Console, error, HasTerminal, HasDesktop, Launcher);

    public int Run(params string[] args) => Commands.Execute(args, Environment);

    public void Dispose()
    {
        Setup.Dispose();
        settings.Dispose();
    }
}
