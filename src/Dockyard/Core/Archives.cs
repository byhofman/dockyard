using System.Formats.Tar;
using System.IO.Compression;

namespace Dockyard.Core;

static class Archives
{
    // One file out of a .zip or a .tar.gz, told apart by their first bytes, executable where that means anything.
    public static void Extract(string archive, string entry, string target)
    {
        var incoming = target + ".new";
        try
        {
            using (var file = File.OpenRead(archive))
            {
                var zip = file.ReadByte() == 'P' && file.ReadByte() == 'K';
                file.Position = 0;
                if (zip) ExtractZip(file, entry, incoming);
                else ExtractTarGz(file, entry, incoming);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(incoming, (UnixFileMode)0b111_101_101);
            File.Move(incoming, target, overwrite: true);
        }
        finally
        {
            File.Delete(incoming);
        }
    }

    static void ExtractZip(Stream file, string entry, string target)
    {
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var found = archive.Entries.FirstOrDefault(e => Normalize(e.FullName) == entry) ?? throw Missing(entry);
        found.ExtractToFile(target, overwrite: true);
    }

    static void ExtractTarGz(Stream file, string entry, string target)
    {
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } e)
        {
            if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || Normalize(e.Name) != entry) continue;
            e.ExtractToFile(target, overwrite: true);
            return;
        }
        throw Missing(entry);
    }

    static string Normalize(string name) => name.Replace('\\', '/').TrimStart('.', '/');

    static InvalidOperationException Missing(string entry) => new($"The archive has no {entry}");
}
