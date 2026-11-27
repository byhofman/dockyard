namespace Dockyard.Kind;

// A new file only the current user can read: the mode is set when it is created on Unix, and the ACL is narrowed before the
// content goes in on Windows.
static class PrivateFile
{
    public static void Write(string path, string text)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        if (OperatingSystem.IsWindows()) OwnerOnly(path);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void OwnerOnly(string path)
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rules = new System.Security.AccessControl.FileSecurity();
        rules.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        rules.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            user, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(rules);
    }
}
