using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Dockyard.exe is a windowed program: cmd and PowerShell neither wait for one nor give it their output. This starts it with
// this console's own handles, waits, and passes on its exit code. .NET Framework, which every Windows 10 and 11 has, keeps
// it a few kilobytes.
static class Program
{
    static int Main(string[] args)
    {
        var exe = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Dockyard.exe"));
        var line = new StringBuilder(Quote(exe));
        foreach (var arg in args) line.Append(' ').Append(Quote(arg));

        var startup = new StartupInfo
        {
            cb = Marshal.SizeOf<StartupInfo>(),
            dwFlags = UseStdHandles,
            hStdInput = GetStdHandle(-10),
            hStdOutput = GetStdHandle(-11),
            hStdError = GetStdHandle(-12),
        };
        if (!CreateProcess(exe, line, IntPtr.Zero, IntPtr.Zero, true, 0, IntPtr.Zero, null, ref startup, out var process))
        {
            Console.Error.WriteLine($"Could not start {exe}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return 1;
        }
        WaitForSingleObject(process.hProcess, uint.MaxValue);
        GetExitCodeProcess(process.hProcess, out var code);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return (int)code;
    }

    // As the C runtime reads a command line back into arguments.
    static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    const int UseStdHandles = 0x100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, int creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
