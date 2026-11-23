using System.Collections.ObjectModel;
using System.Text;
using Dockyard.Dashboard.Shell;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Files;

sealed record Crumb(string Name, string Path, bool IsLast);

// One folder and at most one open file, from a container, a volume or an image.
sealed class FileBrowserViewModel : Observable, IDisposable
{
    public const int MaxEditableBytes = 2 * 1024 * 1024;

    readonly IFileSource source;
    readonly string root;
    readonly Action<string> showError;
    readonly Func<Task>? allowChanges;
    readonly CancellationTokenSource lifetime = new();
    CancellationTokenSource? loading;
    List<FileRow> all = [];
    string directory;
    string filter = "";
    FileRow? selected;
    FileEntry? openEntry;
    string? text;
    string? savedText;
    string? note;
    string? status;
    bool busy;
    bool loaded;
    bool listed;

    // allowChanges is offered when the files are read-only only until asked: a volume opens read-only.
    public FileBrowserViewModel(IFileSource source, Action<string> showError, Func<Task>? allowChanges = null)
    {
        this.source = source;
        root = source.Root;
        this.showError = showError;
        this.allowChanges = source.CanWrite ? null : allowChanges;
        directory = root;
        UpCommand = new Command(() => Go(ContainerFiles.Parent(directory, root)), () => CanGoUp);
        GoCommand = new Command<Crumb>(c => Go(c.Path));
        RefreshCommand = new Command(() =>
        {
            source.Forget();
            return Go(directory);
        });
        SaveCommand = new Command(Save, () => HasChanges);
        CloseFileCommand = new Command(() => { Selected = null; return Task.CompletedTask; });
        AllowChangesCommand = new Command(() => this.allowChanges?.Invoke() ?? Task.CompletedTask, () => CanAllowChanges);
        UpdateCrumbs();
    }

    public ObservableCollection<FileRow> Rows { get; } = [];
    public ObservableCollection<Crumb> Crumbs { get; } = [];
    public Command UpCommand { get; }
    public Command<Crumb> GoCommand { get; }
    public Command RefreshCommand { get; }
    public Command SaveCommand { get; }
    public Command CloseFileCommand { get; }
    public Command AllowChangesCommand { get; }
    public bool CanWrite => source.CanWrite;
    public bool IsReadOnly => !source.CanWrite;
    public bool CanAllowChanges => allowChanges is not null;
    public string? Status { get => status; private set { if (Set(ref status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => status is not null;
    public string Directory { get => directory; private set { if (Set(ref directory, value)) { Raise(nameof(CanGoUp)); UpCommand.Refresh(); UpdateCrumbs(); } } }
    public bool CanGoUp => directory.TrimEnd('/').Length > root.TrimEnd('/').Length;
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) Raise(nameof(IsFirstLoad), nameof(IsEmptyFolder)); } }
    public bool IsFirstLoad => busy && Rows.Count == 0;
    public bool IsEmptyFolder => listed && !busy && Rows.Count == 0;
    public string EmptyText => all.Count == 0 ? "This folder is empty." : $"Nothing here matches \"{filter}\".";
    public string? Note { get => note; private set { if (Set(ref note, value)) Raise(nameof(HasNote)); } }
    public bool HasNote => note is not null;
    public bool HasOpenFile => openEntry is not null;
    public bool HasText => text is not null;
    public bool CanSave => CanWrite && HasText;
    public string OpenName => openEntry?.Name ?? "";
    public string OpenDetail => openEntry is { } e ? $"{Format.Bytes(e.Size)} · {e.Modified.LocalDateTime:yyyy-MM-dd HH:mm}" : "";
    public bool HasChanges => text is not null && text != savedText;
    public FileRow? SelectedFile => selected is { IsDirectory: false } ? selected : null;
    public bool CanDownload => SelectedFile is not null;

    public string Filter
    {
        get => filter;
        set { if (Set(ref filter, value ?? "")) ShowRows(); }
    }

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
            else CloseFile();
        }
    }

    // Only once the tab is looked at: a listing can read a lot through the engine.
    public void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        _ = Go(root);
    }

    void UpdateCrumbs()
    {
        Crumbs.Clear();
        var rootName = root.TrimEnd('/') is { Length: > 0 } r ? r[(r.LastIndexOf('/') + 1)..] : "/";
        var parts = directory.TrimEnd('/')[root.TrimEnd('/').Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        Crumbs.Add(new Crumb(rootName, root, parts.Length == 0));
        var path = root.TrimEnd('/');
        for (var i = 0; i < parts.Length; i++)
        {
            path += "/" + parts[i];
            Crumbs.Add(new Crumb(parts[i], path, i == parts.Length - 1));
        }
    }

    void ShowRows()
    {
        var rows = filter.Length == 0 ? all : all.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        selected = null;
        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
        Raise(nameof(Selected), nameof(SelectedFile), nameof(CanDownload), nameof(IsEmptyFolder), nameof(EmptyText), nameof(IsFirstLoad));
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
            var entries = await source.ListAsync(path, new Progress<string>(s => { if (!token.IsCancellationRequested) Status = s; }), token);
            if (token.IsCancellationRequested) return;
            Status = null;
            Directory = path;
            all = entries.Select(e => new FileRow(e, path)).ToList();
            listed = true;
            filter = "";
            Raise(nameof(Filter));
            ShowRows();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is DockerException or IOException or InvalidDataException or EngineUnavailableException)
        {
            Status = null;
            showError($"{path}: {e.Message}");
        }
        finally { if (!token.IsCancellationRequested) Busy = false; }
    }

    async Task Open(FileRow row)
    {
        CloseFile();
        Busy = true;
        try
        {
            var content = await source.ReadAsync(row.Path, MaxEditableBytes + 1, lifetime.Token);
            openEntry = content.Entry;
            if (content.Truncated) Note = CanWrite ? "Too large to edit here. Download it instead." : "Too large to show here. Download it instead.";
            else if (content.Entry.LinkTarget is { } target) Note = $"A link to {target}.";
            else if (Array.IndexOf(content.Data, (byte)0) >= 0) Note = "A binary file. Download it instead.";
            else
            {
                savedText = Encoding.UTF8.GetString(content.Data);
                text = savedText;
            }
            RaiseFile();
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
        RaiseFile();
    }

    void RaiseFile()
    {
        Raise(nameof(Text), nameof(HasText), nameof(CanSave), nameof(HasOpenFile), nameof(OpenName), nameof(OpenDetail), nameof(HasChanges));
        SaveCommand.Refresh();
    }

    async Task Save()
    {
        if (!CanWrite || openEntry is not { } entry || text is not { } content) return;
        Busy = true;
        try
        {
            using var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
            await source.WriteAsync(directory, entry.Name, data, entry, lifetime.Token);
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
        try { await source.SaveToAsync(row.Path, destination, lifetime.Token); }
        catch (Exception e) when (e is DockerException or IOException or EngineUnavailableException) { showError($"{row.Name}: {e.Message}"); }
        finally { Busy = false; }
    }

    public async Task Upload(string name, Stream content)
    {
        if (!CanWrite) return;
        Busy = true;
        try
        {
            await source.WriteAsync(directory, name, content, null, lifetime.Token);
            await Go(directory);
        }
        catch (Exception e) when (e is DockerException or IOException or EngineUnavailableException) { showError($"{name}: {e.Message}"); }
        finally { Busy = false; }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        _ = source.DisposeAsync().AsTask();
    }
}
