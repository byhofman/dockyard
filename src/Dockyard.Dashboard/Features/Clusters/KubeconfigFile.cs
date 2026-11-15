using YamlDotNet.RepresentationModel;

namespace Dockyard.Dashboard.Features.Clusters;

// The user's kubeconfig is someone else's file: it is backed up before Dockyard first changes it, changed under the
// lock kubectl and kind take, and replaced in one step with its permissions kept, so a crash leaves the old or the new
// file and never half of one.
static class KubeconfigFile
{
    public const string BackupSuffix = ".dockyard-backup";
    static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    public static YamlMappingNode Read(string path)
    {
        var target = Resolve(path);
        return File.Exists(target) ? Parse(File.ReadAllText(target), path) : New();
    }

    public static YamlMappingNode Parse(string text, string from)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(text)); }
        catch (YamlDotNet.Core.YamlException ex) { throw new InvalidDataException($"{from} is not valid YAML ({ex.Start.Line}:{ex.Start.Column}), so it was left as it is"); }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" }) return New();
        if (stream.Documents.Count > 1) throw new InvalidDataException($"{from} holds more than one YAML document, so it was left as it is");
        return stream.Documents[0].RootNode as YamlMappingNode ?? throw new InvalidDataException($"{from} is not a kubeconfig, so it was left as it is");
    }

    // Runs change on the file's contents and writes them back if it says it changed something.
    public static void Update(string path, Func<YamlMappingNode, bool> change)
    {
        var target = Resolve(path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        Directory.CreateDirectory(dir);
        using var held = Lock(path);
        var config = File.Exists(target) ? Parse(File.ReadAllText(target), path) : New();
        if (!change(config)) return;
        var text = Serialize(config);
        // A file that reads back differently than it was meant must not replace the user's.
        Parse(text, path);
        if (File.Exists(target) && !File.Exists(target + BackupSuffix)) Copy(target, target + BackupSuffix);
        Replace(target, text);
    }

    public static string Serialize(YamlMappingNode config)
    {
        var writer = new StringWriter { NewLine = "\n" };
        new YamlStream(new YamlDocument(config)).Save(writer, assignAnchors: false);
        var text = writer.ToString();
        // YamlDotNet ends every document with "...", which kubectl reads but does not write.
        return text.EndsWith("...\n", StringComparison.Ordinal) ? text[..^4] : text;
    }

    static YamlMappingNode New() => new(
        new YamlScalarNode("apiVersion"), new YamlScalarNode("v1"),
        new YamlScalarNode("kind"), new YamlScalarNode("Config"),
        new YamlScalarNode("preferences"), new YamlMappingNode());

    // A kubeconfig that is a link (to a dotfiles repository, say) stays a link: the file it points at is what changes.
    static string Resolve(string path)
    {
        var info = new FileInfo(path);
        return info.LinkTarget is not null ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path : path;
    }

    sealed class Held(string lockPath) : IDisposable
    {
        public void Dispose()
        {
            try { File.Delete(lockPath); } catch (IOException) { }
        }
    }

    // The same lock file client-go and kind create, so none of the three writes over another.
    static Held Lock(string path)
    {
        var lockPath = path + ".lock";
        var until = DateTime.UtcNow + LockWait;
        while (true)
        {
            try
            {
                new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
                return new Held(lockPath);
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                if (DateTime.UtcNow > until)
                    throw new IOException($"{lockPath} exists: another program is changing the kubeconfig. If none is, remove that file and try again.");
                Thread.Sleep(100);
            }
        }
    }

    static void Replace(string target, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(target)}.dockyard-{Guid.NewGuid():N}.tmp");
        var exists = File.Exists(target);
        try
        {
            using (var stream = new FileStream(temp, Options(exists ? Mode(target) : UnixFileMode.UserRead | UnixFileMode.UserWrite)))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            if (OperatingSystem.IsWindows())
            {
                // ReplaceFile carries the old file's ACL and attributes over to the new one.
                if (exists) File.Replace(temp, target, null, ignoreMetadataErrors: false);
                else File.Move(temp, target);
            }
            else
            {
                // The umask may have narrowed what the file was created with.
                File.SetUnixFileMode(temp, exists ? Mode(target) : UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, target, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    static void Copy(string from, string to)
    {
        File.Copy(from, to);
        if (OperatingSystem.IsWindows()) new FileInfo(to).SetAccessControl(new FileInfo(from).GetAccessControl());
        else File.SetUnixFileMode(to, Mode(from));
    }

    static UnixFileMode Mode(string path) => OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(path);

    static FileStreamOptions Options(UnixFileMode mode)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = mode;
        return options;
    }
}
