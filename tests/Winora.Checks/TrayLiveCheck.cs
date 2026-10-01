using System.Runtime.InteropServices;
using Microsoft.Win32;
using Winora;

// Opt-in real-shell check. Restore the user's exact prior promotion values and
// remove only our own synthetic icon's cache entry after stopping enforcement.
internal static class TrayLiveCheck
{
    public static void Run()
    {
        if (Settings.Load().AlwaysShowTrayIcons || ProcessRunning())
            throw new InvalidOperationException("Disable Winora tray automation before running this isolated live check.");
        using var root = Registry.CurrentUser.OpenSubKey(TrayIconService.TrayKey, true)
            ?? throw new InvalidOperationException("This Windows shell does not expose tray preferences.");
        var before = root.GetSubKeyNames().ToDictionary(name => name, name =>
        {
            using var key = root.OpenSubKey(name)!;
            return key.GetValue("IsPromoted");
        });
        var startupBefore = StartupEntries();
        var guid = Guid.NewGuid();
        var window = CreateWindowExW(0, "STATIC", "Winora tray verification", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
        if (window == 0) throw new InvalidOperationException("Could not create a test notification window.");
        var icon = new NotifyIconData { Size = Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1,
            Flags = 0x02 | 0x04 | 0x20 /* ICON, TIP, GUID */, Icon = LoadIconW(0, new nint(32512)),
            Tip = "Winora tray verification", Info = "", Title = "", Guid = guid };
        string? ownEntry = null;
        try
        {
            new TrayIconService().EnableAsync().GetAwaiter().GetResult();
            if (!Shell_NotifyIconW(0 /* NIM_ADD */, ref icon)) throw new InvalidOperationException("Windows rejected the test tray icon.");
            for (var attempt = 0; attempt < 100; attempt++)
            {
                ownEntry = root.GetSubKeyNames().FirstOrDefault(name =>
                {
                    using var key = root.OpenSubKey(name);
                    return Guid.TryParse(key?.GetValue("IconGuid") as string, out var candidate) && candidate == guid;
                });
                if (ownEntry is not null) break;
                Thread.Sleep(100);
            }
            if (ownEntry is null) throw new InvalidOperationException("Windows did not register a visibility preference for the test icon.");
            ExpectPromoted(root, ownEntry, "Actual Shell_NotifyIcon registration promoted");
            var identifier = new IconIdentifier { Size = Marshal.SizeOf<IconIdentifier>(), Window = window, Id = 1, Guid = guid };
            var visible = false;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                if (Shell_NotifyIconGetRect(ref identifier, out var rect) == 0 && GetWindowRect(FindWindowW("Shell_TrayWnd", null), out var taskbar)
                    && rect.Right > rect.Left && rect.Bottom > rect.Top
                    && rect.Left >= taskbar.Left && rect.Right <= taskbar.Right && rect.Top >= taskbar.Top && rect.Bottom <= taskbar.Bottom)
                { visible = true; break; }
                Thread.Sleep(100);
            }
            if (!visible) throw new InvalidOperationException("The promoted test icon did not appear inside the actual taskbar bounds.");
            Console.WriteLine("PASS Promoted test icon is located directly inside the taskbar");
            using (var entry = root.OpenSubKey(ownEntry, true)!) entry.SetValue("IsPromoted", 0, RegistryValueKind.DWord);
            ExpectPromoted(root, ownEntry, "Actual Windows visibility reset restored");
            if (startupBefore != StartupEntries())
                throw new InvalidOperationException("Enabling tray automation changed the app-wide startup preference.");
            Console.WriteLine("PASS Native helper activation leaves app startup registration unchanged");
        }
        finally
        {
            TrayIconService.DisableAsync().GetAwaiter().GetResult();
            Shell_NotifyIconW(2 /* NIM_DELETE */, ref icon);
            DestroyWindow(window);
            foreach (var pair in before)
            {
                using var entry = root.OpenSubKey(pair.Key, true);
                if (entry is null) continue;
                if (pair.Value is null) entry.DeleteValue("IsPromoted", false);
                else entry.SetValue("IsPromoted", pair.Value);
            }
            if (ownEntry is not null) root.DeleteSubKeyTree(ownEntry, false);
            Console.WriteLine("Restored original tray visibility and removed test icon.");
            if (startupBefore != StartupEntries())
                throw new InvalidOperationException("Disabling tray automation changed the app-wide startup preference.");
            Console.WriteLine("PASS Stopping tray automation leaves app startup registration unchanged");
        }
    }
    private static string StartupEntries()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupService.RunKey);
        return System.Text.Json.JsonSerializer.Serialize(new[] { StartupService.StartupName, "Winora.Taskbar", "Winora.TrayIcons" }
            .ToDictionary(name => name, name => key?.GetValue(name)));
    }
    private static bool ProcessRunning() => System.Diagnostics.Process.GetProcessesByName("Winora.TrayAgent").Length != 0;
    private static void ExpectPromoted(RegistryKey root, string name, string message)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var entry = root.OpenSubKey(name);
            if (entry?.GetValue("IsPromoted") is int value && value == 1) { Console.WriteLine($"PASS {message}"); return; }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException(message + " failed.");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public nint Balloon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint LoadIconW(nint instance, nint name);
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier { public int Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string name, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
}
