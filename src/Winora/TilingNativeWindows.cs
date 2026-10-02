using System.Runtime.InteropServices;
using System.Text;

namespace Winora;

internal sealed record TilingNativeMonitor(string Device, TilingRect MonitorRect, TilingRect WorkRect,
    bool IsPrimary, int DisplayOrder);

internal readonly record struct TilingFrameInsets(int Left, int Top, int Right, int Bottom);

internal sealed record TilingNativeWindow(long Handle, int ProcessId, long ProcessStart,
    TilingRect Rect, TilingRect FrameRect, TilingFrameInsets FrameInsets, string MonitorDevice,
    bool IsVisible, bool IsMinimized, bool IsCloaked, bool IsMaximized, bool IsMoveSizeActive,
    bool IsApplicationWindow, bool IsForeground, uint Dpi,
    int MinWidth, int MinHeight, int? MaxWidth, int? MaxHeight, bool ConstraintsKnown);

// The engine's model is not proof that an app accepted its requested size.
// Read actual frame bounds and native tracking limits before reconciling tiles.
internal sealed class TilingNativeWindows
{
    private const uint GetMinMaxInfo = 0x0024;
    private const uint MessageTimeoutFlags = 0x0001 | 0x0002 | 0x0020; // BLOCK | ABORTIFHUNG | ERRORONEXIT
    private const uint WindowPositionFlags = 0x0004 | 0x0010 | 0x0200 | 0x4000; // NOZORDER | NOACTIVATE | NOOWNERZORDER | ASYNC
    private readonly Dictionary<ConstraintKey, CachedConstraints> constraints = [];
    private readonly object constraintsLock = new();

    internal static long ForegroundHandle => (long)GetForegroundWindow();
    internal static bool IsDesktopHandle(long handle)
    {
        var shell = GetShellWindow();
        return handle != 0 && handle == (long)(shell != 0 ? shell : GetDesktopWindow());
    }

    internal IReadOnlyList<TilingNativeMonitor> GetMonitors()
    {
        var monitors = new List<TilingNativeMonitor>();
        MonitorCallback callback = (monitor, context, rectangle, parameter) =>
        {
            var info = NewMonitorInfo();
            if (GetMonitorInfo(monitor, ref info) && IsValid(info.Monitor) && IsValid(info.Work))
                monitors.Add(new(info.Device, ToRect(info.Monitor), ToRect(info.Work), (info.Flags & 1) != 0, 0));
            return true;
        };
        _ = EnumDisplayMonitors(0, 0, callback, 0);
        // Enumeration order is not Windows' display-number order. Keep the
        // primary display first, then DISPLAY2, DISPLAY3, etc. for stable ties.
        return monitors.DistinctBy(monitor => monitor.Device, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(monitor => monitor.IsPrimary)
            .ThenBy(monitor => DisplayNumber(monitor.Device))
            .ThenBy(monitor => monitor.Device, StringComparer.OrdinalIgnoreCase)
            .Select((monitor, order) => monitor with { DisplayOrder = order }).ToArray();
    }

    internal IReadOnlyList<TilingNativeWindow> GetOpenWindows()
    {
        var windows = new List<TilingNativeWindow>();
        WindowCallback callback = (handle, parameter) =>
        {
            if (IsWindowVisible(handle) && TryRead((long)handle, out var window, queryConstraints: false) &&
                window.IsApplicationWindow && !window.IsCloaked) windows.Add(window);
            return true;
        };
        _ = EnumWindows(callback, 0);
        return windows;
    }

    internal bool IsAnyMoveSizeActive()
    {
        var info = NewGuiThreadInfo();
        return GetGUIThreadInfo(0, ref info) && (info.Flags & 0x0002 /* GUI_INMOVESIZE */) != 0;
    }

    internal bool TryRead(long handle, out TilingNativeWindow window, bool queryConstraints = true)
    {
        window = null!;
        var nativeHandle = (nint)handle;
        if (!FullscreenGameDetector.TryGetIdentity(handle, out var processId, out var processStart) ||
            !GetWindowRect(nativeHandle, out var outer) || !IsValid(outer)) return false;
        var frame = outer;
        if (DwmGetWindowAttribute(nativeHandle, 9 /* EXTENDED_FRAME_BOUNDS */, out NativeRect visibleFrame,
            Marshal.SizeOf<NativeRect>()) == 0 && IsValid(visibleFrame)) frame = visibleFrame;
        var insets = new TilingFrameInsets(frame.Left - outer.Left, frame.Top - outer.Top,
            outer.Right - frame.Right, outer.Bottom - frame.Bottom);
        // A disappearing or animating window can briefly have stale DWM bounds.
        // Do not turn those into a large, negative frame-to-window adjustment.
        if (!ValidInset(insets.Left) || !ValidInset(insets.Top) || !ValidInset(insets.Right) || !ValidInset(insets.Bottom))
        { frame = outer; insets = new(0, 0, 0, 0); }

        var monitor = MonitorFromWindow(nativeHandle, 2 /* MONITOR_DEFAULTTONEAREST */);
        var monitorInfo = NewMonitorInfo();
        if (monitor == 0 || !GetMonitorInfo(monitor, ref monitorInfo)) return false;
        var style = GetWindowLongPtr(nativeHandle, -16 /* GWL_STYLE */).ToInt64();
        var extended = GetWindowLongPtr(nativeHandle, -20 /* GWL_EXSTYLE */).ToInt64();
        var threadId = GetWindowThreadProcessId(nativeHandle, out var currentProcessId);
        if (currentProcessId != (uint)processId) return false;
        var dpi = GetDpiForWindow(nativeHandle);
        if (dpi == 0) dpi = 96;
        var limits = ReadConstraints(nativeHandle, processId, processStart, threadId, dpi, style, extended, queryConstraints);
        var gui = NewGuiThreadInfo();
        var dragging = GetGUIThreadInfo(threadId, ref gui) &&
            (gui.Flags & 0x0002 /* GUI_INMOVESIZE */) != 0 && gui.MoveSize == nativeHandle;
        var cloaked = DwmGetWindowAttribute(nativeHandle, 14 /* CLOAKED */, out int cloak, sizeof(int)) == 0 && cloak != 0;
        var horizontalInsets = insets.Left + insets.Right;
        var verticalInsets = insets.Top + insets.Bottom;
        var minimumWidth = Math.Max(1, limits.MinWidth - horizontalInsets);
        var minimumHeight = Math.Max(1, limits.MinHeight - verticalInsets);
        window = new(handle, processId, processStart, ToRect(outer), ToRect(frame), insets, monitorInfo.Device,
            IsWindowVisible(nativeHandle), IsIconic(nativeHandle), cloaked, IsZoomed(nativeHandle), dragging,
            IsApplicationRoot(nativeHandle, style, extended), GetForegroundWindow() == nativeHandle, dpi,
            minimumWidth, minimumHeight,
            limits.MaxWidth is { } maxWidth ? Math.Max(minimumWidth, maxWidth - horizontalInsets) : null,
            limits.MaxHeight is { } maxHeight ? Math.Max(minimumHeight, maxHeight - verticalInsets) : null,
            limits.Known);
        return FullscreenGameDetector.TryGetIdentity(handle, out var currentProcessIdAsInt, out var currentStart) &&
            currentProcessIdAsInt == processId && currentStart == processStart;
    }

    // Success means Windows accepted the request. ASYNCWINDOWPOS avoids waiting
    // on another app's thread; the coordinator must verify its resulting bounds.
    internal bool TrySetFrameBounds(TilingNativeWindow expected, TilingRect desiredFrame)
    {
        if (desiredFrame.Width <= 0 || desiredFrame.Height <= 0 || IsAnyMoveSizeActive() ||
            !TryRead(expected.Handle, out var current) || current.ProcessId != expected.ProcessId ||
            current.ProcessStart != expected.ProcessStart || !current.IsVisible || current.IsMinimized ||
            current.IsCloaked || current.IsMaximized || current.IsMoveSizeActive || !current.IsApplicationWindow ||
            IsHungAppWindow((nint)expected.Handle)) return false;
        // Never send a size below the app's current tracking minimum. Reject an
        // impossible plan rather than silently overlap its neighbouring tiles.
        if (desiredFrame.Width < current.MinWidth || desiredFrame.Height < current.MinHeight ||
            current.MaxWidth is { } maxWidth && desiredFrame.Width > maxWidth ||
            current.MaxHeight is { } maxHeight && desiredFrame.Height > maxHeight) return false;
        var insets = current.FrameInsets;
        var x = (long)desiredFrame.X - insets.Left;
        var y = (long)desiredFrame.Y - insets.Top;
        var width = (long)desiredFrame.Width + insets.Left + insets.Right;
        var height = (long)desiredFrame.Height + insets.Top + insets.Bottom;
        if (x is < int.MinValue or > int.MaxValue || y is < int.MinValue or > int.MaxValue ||
            width is <= 0 or > int.MaxValue || height is <= 0 or > int.MaxValue) return false;
        if (!FullscreenGameDetector.TryGetIdentity(expected.Handle, out var processId, out var started) ||
            processId != expected.ProcessId || started != expected.ProcessStart) return false;
        return SetWindowPos((nint)expected.Handle, 0, (int)x, (int)y, (int)width, (int)height, WindowPositionFlags);
    }

    private SizeLimits ReadConstraints(nint handle, int processId, long started, uint threadId, uint dpi,
        long style, long extended, bool query)
    {
        var key = new ConstraintKey((long)handle, processId, started, dpi, style, extended);
        var now = Environment.TickCount64;
        lock (constraintsLock)
        {
            if (constraints.TryGetValue(key, out var cached) && cached.Expires > now) return cached.Limits;
        }
        var minimumWidth = Math.Max(1, GetSystemMetricsForDpi(34 /* CXMINTRACK */, dpi));
        var minimumHeight = Math.Max(1, GetSystemMetricsForDpi(35 /* CYMINTRACK */, dpi));
        var maximumWidth = GetSystemMetricsForDpi(59 /* CXMAXTRACK */, dpi);
        var maximumHeight = GetSystemMetricsForDpi(60 /* CYMAXTRACK */, dpi);
        var fallback = new SizeLimits(minimumWidth, minimumHeight,
            maximumWidth >= minimumWidth ? maximumWidth : null,
            maximumHeight >= minimumHeight ? maximumHeight : null, false);
        if (!query) return fallback;
        // SendMessageTimeout's timeout is ignored for the caller's own queue.
        // The headless coordinator normally queries another process; this guard
        // also keeps a same-thread diagnostic bounded.
        if (threadId == GetCurrentThreadId() || IsHungAppWindow(handle)) return fallback;
        var info = new MinMaxInfo
        {
            MaxSize = new() { X = GetSystemMetricsForDpi(61 /* CXMAXIMIZED */, dpi), Y = GetSystemMetricsForDpi(62 /* CYMAXIMIZED */, dpi) },
            MinTrackSize = new() { X = minimumWidth, Y = minimumHeight },
            MaxTrackSize = new() { X = maximumWidth, Y = maximumHeight }
        };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<MinMaxInfo>());
        var limits = fallback;
        try
        {
            Marshal.StructureToPtr(info, memory, false);
            // WM_GETMINMAXINFO is a system message: Windows marshals this
            // structure across processes. Its message result is normally zero;
            // only SendMessageTimeout's own return value indicates success.
            if (SendMessageTimeout(handle, GetMinMaxInfo, 0, memory, MessageTimeoutFlags, 25, out _) != 0)
            {
                info = Marshal.PtrToStructure<MinMaxInfo>(memory);
                var minWidth = ValidDimension(info.MinTrackSize.X) ? Math.Max(minimumWidth, info.MinTrackSize.X) : minimumWidth;
                var minHeight = ValidDimension(info.MinTrackSize.Y) ? Math.Max(minimumHeight, info.MinTrackSize.Y) : minimumHeight;
                limits = new(minWidth, minHeight,
                    ValidDimension(info.MaxTrackSize.X) && info.MaxTrackSize.X >= minWidth ? info.MaxTrackSize.X : fallback.MaxWidth,
                    ValidDimension(info.MaxTrackSize.Y) && info.MaxTrackSize.Y >= minHeight ? info.MaxTrackSize.Y : fallback.MaxHeight, true);
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
        lock (constraintsLock)
        {
            if (constraints.Count >= 512)
            {
                foreach (var old in constraints.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) constraints.Remove(old);
                if (constraints.Count >= 512) constraints.Clear();
            }
            constraints[key] = new(limits, now + (limits.Known ? 5000 : 1000));
        }
        return limits;
    }

    private static bool IsApplicationRoot(nint handle, long style, long extended)
    {
        if (handle == GetDesktopWindow() || handle == GetShellWindow() ||
            (style & 0x40000000L /* CHILD */) != 0 ||
            (extended & (0x80L /* TOOLWINDOW */ | 0x08000000L /* NOACTIVATE */)) != 0 ||
            GetWindow(handle, 4 /* GW_OWNER */) != 0 && (extended & 0x00040000L /* APPWINDOW */) == 0) return false;
        var name = new StringBuilder(256);
        _ = GetClassName(handle, name, name.Capacity);
        return name.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    private static int DisplayNumber(string device)
    {
        var start = device.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
        return start >= 0 && int.TryParse(device.AsSpan(start + 7), out var number) ? number : int.MaxValue;
    }
    private static bool ValidDimension(int size) => size is > 0 and <= 1_000_000;
    private static bool ValidInset(int inset) => inset is >= -64 and <= 64;
    private static bool IsValid(NativeRect rectangle) => rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top &&
        (long)rectangle.Right - rectangle.Left <= int.MaxValue && (long)rectangle.Bottom - rectangle.Top <= int.MaxValue;
    private static TilingRect ToRect(NativeRect rectangle) => new(rectangle.Left, rectangle.Top,
        rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
    private static MonitorInfo NewMonitorInfo() => new() { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
    private static GuiThreadInfo NewGuiThreadInfo() => new() { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
    private readonly record struct ConstraintKey(long Handle, int ProcessId, long Started, uint Dpi, long Style, long Extended);
    private readonly record struct SizeLimits(int MinWidth, int MinHeight, int? MaxWidth, int? MaxHeight, bool Known);
    private readonly record struct CachedConstraints(SizeLimits Limits, long Expires);
    private delegate bool MonitorCallback(nint monitor, nint context, nint rectangle, nint parameter);
    private delegate bool WindowCallback(nint handle, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(nint context, nint clip, MonitorCallback callback, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW", SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint handle, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint handle, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint handle);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW", SetLastError = true)] private static extern int GetClassName(nint handle, StringBuilder name, int capacity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint handle);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)] private static extern nint SendMessageTimeout(nint handle, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
