using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Winora;

internal sealed record NativeFullscreenWindow(long Handle, int ProcessId, long ProcessStart, string Executable,
    string MonitorDevice, bool IsGame);

// Windows only positively identifies exclusive Direct3D. Explicit executable
// paths cover borderless games without guessing from names or loaded DLLs.
internal sealed class FullscreenGameDetector
{
    private readonly HashSet<(long Handle, int ProcessId, long ProcessStart)> detectedGames = [];

    internal IReadOnlyList<NativeFullscreenWindow> Detect(string[]? gameExecutables)
    {
        var configured = new HashSet<string>(gameExecutables ?? [], StringComparer.OrdinalIgnoreCase);
        var foreground = GetForegroundWindow();
        var result = new List<NativeFullscreenWindow>();
        WindowCallback callback = (window, parameter) =>
        {
            if (!TryGetFullscreenMonitor(window, out var device, out var requiresGameIdentity)) return true;
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId == 0) return true;
            if (TryGetProcessIdentity((int)processId, out var started, out var executable))
            {
                var appHost = Path.Combine(AppContext.BaseDirectory, AppBuild.IsDevelopment ? "Winora.Dev.exe" : "Winora.exe");
                if (string.Equals(executable, appHost, StringComparison.OrdinalIgnoreCase)) return true;
                var identity = ((long)window, (int)processId, started);
                var game = configured.Contains(executable) || detectedGames.Contains(identity);
                if (!game && window == foreground && SHQueryUserNotificationState(out var notificationState) == 0 && notificationState == 3)
                { detectedGames.Add(identity); game = true; }
                if (requiresGameIdentity && !game) return true;
                result.Add(new((long)window, (int)processId, started, executable, device, game));
            }
            else
            {
                // Unknown/elevated fullscreen apps still occupy a screen. They
                // are never guessed to be games or used as free destinations.
                if (!requiresGameIdentity && IsWindow(window)) result.Add(new((long)window, (int)processId, 0, "", device, IsGame: false));
            }
            return true;
        };
        _ = EnumWindows(callback, 0);
        detectedGames.IntersectWith(result.Select(window => (window.Handle, window.ProcessId, window.ProcessStart)));
        return result;
    }

    internal static bool TryGetIdentity(long handle, out int processId, out long processStart)
    {
        processId = 0;
        processStart = 0;
        if (!IsWindow((nint)handle)) return false;
        _ = GetWindowThreadProcessId((nint)handle, out var id);
        var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, id);
        if (process == 0) return false;
        try
        {
            if (!GetProcessTimes(process, out var created, out _, out _, out _)) return false;
            processId = (int)id;
            processStart = DateTime.FromFileTimeUtc(created).Ticks;
            return true;
        }
        finally { _ = CloseHandle(process); }
    }

    private static bool TryGetProcessIdentity(int id, out long started, out string executable)
    {
        started = 0;
        executable = "";
        var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, (uint)id);
        if (process == 0) return false;
        try
        {
            if (!GetProcessTimes(process, out var created, out _, out _, out _)) return false;
            var path = new StringBuilder(1024);
            var length = path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref length)) return false;
            started = DateTime.FromFileTimeUtc(created).Ticks;
            executable = path.ToString();
            return true;
        }
        finally { _ = CloseHandle(process); }
    }

    private static bool TryGetFullscreenMonitor(nint window, out string device, out bool requiresGameIdentity)
    {
        device = "";
        requiresGameIdentity = false;
        if (window == GetDesktopWindow() || window == GetShellWindow()) return false;
        if (!IsWindowVisible(window) || IsIconic(window) || GetWindow(window, 4 /* GW_OWNER */) != 0) return false;
        var className = new StringBuilder(256);
        _ = GetClassName(window, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        var style = GetWindowLongPtr(window, -16 /* GWL_STYLE */).ToInt64();
        var extended = GetWindowLongPtr(window, -20 /* GWL_EXSTYLE */).ToInt64();
        if ((style & 0x40000000L /* WS_CHILD */) != 0 || (extended & 0x80L /* WS_EX_TOOLWINDOW */) != 0) return false;
        var maximizedCaption = IsZoomed(window) && (style & 0x00C00000L /* WS_CAPTION */) != 0;
        if (DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var monitor = MonitorFromWindow(window, 0 /* MONITOR_DEFAULTTONULL */);
        if (monitor == 0) return false;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        if (DwmGetWindowAttribute(window, 9 /* EXTENDED_FRAME_BOUNDS */, out Rect frame, Marshal.SizeOf<Rect>()) != 0 &&
            !GetWindowRect(window, out frame)) return false;
        var visibleFullscreen = SameBounds(frame, info.Monitor);
        if (!visibleFullscreen && (!CouldCoverMonitor(frame, info.Monitor) || !ClientCoversMonitor(window, info.Monitor))) return false;
        // Registered games can retain maximized/caption/border styles while
        // rendering a full-monitor client surface. Ordinary maximized apps
        // still fail the geometry check or require a positive game identity.
        requiresGameIdentity = maximizedCaption || !visibleFullscreen;
        device = info.Device;
        return true;
    }

    private static bool ClientCoversMonitor(nint window, Rect monitor)
    {
        if (!GetClientRect(window, out var client)) return false;
        var topLeft = new Point { X = client.Left, Y = client.Top };
        var bottomRight = new Point { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(window, ref topLeft) || !ClientToScreen(window, ref bottomRight)) return false;
        return SameBounds(new Rect { Left = topLeft.X, Top = topLeft.Y, Right = bottomRight.X, Bottom = bottomRight.Y }, monitor);
    }

    private static bool SameBounds(Rect frame, Rect monitor) => Math.Abs(frame.Left - monitor.Left) <= 2 &&
        Math.Abs(frame.Top - monitor.Top) <= 2 && Math.Abs(frame.Right - monitor.Right) <= 2 && Math.Abs(frame.Bottom - monitor.Bottom) <= 2;

    private static bool CouldCoverMonitor(Rect frame, Rect monitor) => frame.Left <= monitor.Left + 2 &&
        frame.Top <= monitor.Top + 2 && frame.Right >= monitor.Right - 2 && frame.Bottom >= monitor.Bottom - 2;

    private delegate bool WindowCallback(nint window, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out Rect value, int size);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
