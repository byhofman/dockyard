using System.Collections.ObjectModel;
using System.Text;
using Dockyard.Dashboard.Shell;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Files;

// One folder and at most one open file, in a container or in a volume held by a container that never runs.
sealed class FileBrowserViewModel : Observable, IDisposable
{
    public const int MaxEditableBytes = 2 * 1024 * 1024;

    readonly DockerClient client;
    readonly string container;
    readonly string root;
    readonly Action<string> showError;
    readonly Func<Task>? release;
    readonly CancellationTokenSource lifetime = new();
    CancellationTokenSource? loading;
    string directory;
    FileRow? selected;
    FileEntry? openEntry;
    string? text;
    string? savedText;
    string? note;
    bool busy;
    bool loaded;

    public FileBrowserViewModel(DockerClient client, string container, string root, Action<string> showError, Func<Task>? release = null)
    {
        this.client = client;
        this.container = container;
        this.root = root;
        this.showError = showError;
        this.release = release;
        directory = root;
        UpCommand = new Command(() => Go(ContainerFiles.Parent(directory, root)), () => CanGoUp);
        RefreshCommand = new Command(() => Go(directory));
        SaveCommand = new Command(Save, () => HasChanges);
    }

    public ObservableCollection<FileRow> Rows { get; } = [];
    public Command UpCommand { get; }
    public Command RefreshCommand { get; }
    public Command SaveCommand { get; }
    public string Directory { get => directory; private set { if (Set(ref directory, value)) { Raise(nameof(CanGoUp)); UpCommand.Refresh(); } } }
    public bool CanGoUp => directory.TrimEnd('/').Length > root.TrimEnd('/').Length;
    public bool Busy { get => busy; private set => Set(ref busy, value); }
    public string? Note { get => note; private set { if (Set(ref note, value)) Raise(nameof(HasNote)); } }
    public bool HasNote => note is not null;
    public bool HasOpenFile => openEntry is not null;
    public bool HasText => text is not null;
    public string OpenName => openEntry?.Name ?? "";
    public bool HasChanges => text is not null && text != savedText;
    public FileRow? SelectedFile => selected is { IsDirectory: false } ? selected : null;
    public bool CanDownload => SelectedFile is not null;

    public string? Text
    {
        get => text;
        set { if (Set(ref text, value)) { Raise(nameof(HasChanges)); SaveCommand.Refresh(); } }
    }

    public FileRow? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            Raise(nameof(SelectedFile), nameof(CanDownload));
            if (value is { IsDirectory: true }) _ = Go(value.Path);
            else if (value is not null) _ = Open(value);
        }
    }

    // Only once the tab is looked at: listing reads the whole folder through the engine.
    public void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        _ = Go(root);
    }

    async Task Go(string path)
    {
        loading?.Cancel();
        loading = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = loading.Token;
        Busy = true;
        CloseFile();
        try
        {
            var entries = await ContainerFiles.ListAsync(client, container, path, token);
            if (token.IsCancellationRequested) return;
            Directory = path;
            selected = null;
            Rows.Clear();
            foreach (var entry in entries) Rows.Add(new FileRow(entry, path));
            Raise(nameof(Selected), nameof(SelectedFile), nameof(CanDownload));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is DockerException or IOException or InvalidDataException or EngineUnavailableException) { showError($"{path}: {e.Message}"); }
        finally { if (!token.IsCancellationRequested) Busy = false; }
    }

    async Task Open(FileRow row)
    {
        CloseFile();
        Busy = true;
        try
        {
            var content = await ContainerFiles.ReadAsync(client, container, row.Path, MaxEditableBytes + 1, lifetime.Token);
            openEntry = content.Entry;
            if (content.Truncated) Note = "Too large to edit here. Download it instead.";
            else if (content.Entry.LinkTarget is { } target) Note = $"A link to {target}.";
            else if (Array.IndexOf(content.Data, (byte)0) >= 0) Note = "A binary file. Download it instead.";
            else
            {
                savedText = Encoding.UTF8.GetString(content.Data);
                text = savedText;
            }
            Raise(nameof(Text), nameof(HasText), nameof(HasOpenFile), nameof(OpenName), nameof(HasChanges));
            SaveCommand.Refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is DockerException or IOException or InvalidDataException or EngineUnavailableException) { showError($"{row.Path}: {e.Message}"); }
        finally { Busy = false; }
    }

    void CloseFile()
    {
        openEntry = null;
        text = savedText = null;
        Note = null;
        Raise(nameof(Text), nameof(HasText), nameof(HasOpenFile), nameof(OpenName), nameof(HasChanges));
        SaveCommand.Refresh();
    }

    async Task Save()
    {
        if (openEntry is not { } entry || text is not { } content) return;
        Busy = true;
        try
        {
            using var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
            await ContainerFiles.WriteAsync(client, container, directory, entry.Name, data, entry, lifetime.Token);
            savedText = content;
            Raise(nameof(HasChanges));
            SaveCommand.Refresh();
        }
        catch (Exception e) when (e is DockerException or IOException or EngineUnavailableException) { showError($"{entry.Name}: {e.Message}"); }
        finally { Busy = false; }
    }

    public async Task Download(Stream destination)
    {
        if (SelectedFile is not { } row) return;
        Busy = true;
        try { await ContainerFiles.SaveToAsync(client, container, row.Path, destination, lifetime.Token); }
        catch (Exception e) when (e is DockerException or IOException or EngineUnavailableException) { showError($"{row.Name}: {e.Message}"); }
        finally { Busy = false; }
    }

    public async Task Upload(string name, Stream content)
    {
        Busy = true;
        try
        {
            await ContainerFiles.WriteAsync(client, container, directory, name, content, null, lifetime.Token);
            await Go(directory);
        }
        catch (Exception e) when (e is DockerException or IOException or EngineUnavailableException) { showError($"{name}: {e.Message}"); }
        finally { Busy = false; }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        if (release is not null) _ = release();
    }
}
