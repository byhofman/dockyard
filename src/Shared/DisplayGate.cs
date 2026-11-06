using System.Runtime.InteropServices;

namespace Dockyard.Shared;

// Avalonia's native macOS code aborts the process at startup (error -6661) without a usable display, which no managed
// handler can catch, so a window waits here until the screen is unlocked. Elsewhere there is nothing to wait for.
static class DisplayGate
{
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    const uint Utf8 = 0x08000100;

    public static bool WaitUntilUsable(TimeSpan patience)
    {
        if (!OperatingSystem.IsMacOS()) return true;
        var until = patience == Timeout.InfiniteTimeSpan ? DateTime.MaxValue : DateTime.UtcNow + patience;
        while (!IsUsable())
        {
            if (DateTime.UtcNow > until) return false;
            Thread.Sleep(2000);
        }
        return true;
    }

    public static bool IsUsable()
    {
        var session = CGSessionCopyCurrentDictionary();
        if (session == 0) return false;
        try
        {
            return !Flag(session, "CGSSessionScreenIsLocked", whenAbsent: false) && Flag(session, "kCGSSessionOnConsoleKey", whenAbsent: true);
        }
        finally
        {
            CFRelease(session);
        }
    }

    static bool Flag(nint dictionary, string name, bool whenAbsent)
    {
        var key = CFStringCreateWithCString(0, name, Utf8);
        try
        {
            var value = CFDictionaryGetValue(dictionary, key);
            return value == 0 ? whenAbsent : CFBooleanGetValue(value) != 0;
        }
        finally
        {
            CFRelease(key);
        }
    }

    [DllImport(CoreGraphics)] static extern nint CGSessionCopyCurrentDictionary();
    [DllImport(CoreFoundation)] static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)] static extern nint CFDictionaryGetValue(nint dictionary, nint key);
    [DllImport(CoreFoundation)] static extern byte CFBooleanGetValue(nint value);
    [DllImport(CoreFoundation)] static extern void CFRelease(nint value);
}
