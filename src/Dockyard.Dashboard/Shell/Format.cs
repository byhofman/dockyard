namespace Dockyard.Dashboard.Shell;

static class Format
{
    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:0} {units[i]}" : $"{bytes:0.#} {units[i]}";
    }

    public static string Age(DateTimeOffset since, DateTimeOffset now)
    {
        var d = now - since;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 60) return $"{(int)d.TotalDays} days ago";
        return $"{(int)(d.TotalDays / 30)} months ago";
    }
}
