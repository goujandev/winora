using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Winora;

// A focus-preserving move must stop if the user interacts with another app.
// GlazeWM tags its own injected focus mouse event with 6379, which must not be
// mistaken for user intent. Retain only an event counter, never input contents.
internal interface ITilingInputMonitor : IDisposable
{
    bool IsReliable { get; }
    long Version { get; }
    Task<bool> RearmAsync();
}

internal sealed class TilingInputMonitor : ITilingInputMonitor
{
    private const uint Quit = 0x0012;
    private const uint RearmMessage = 0x8039; // Private thread message, no window.
    private const long GlazeInputTag = 6379;
    private readonly Thread thread;
    private readonly HookCallback mouseCallback;
    private readonly HookCallback keyboardCallback;
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object rearmLock = new();
    private RearmRequest? pendingRearm;
    private nint mouseHook;
    private nint keyboardHook;
    private uint threadId;
    private long version;
    private long callbackFailures;
    private long nextRearm;
    private int reliable;
    private int disposed;

    internal TilingInputMonitor()
    {
        mouseCallback = OnMouse;
        keyboardCallback = OnKeyboard;
        thread = new Thread(Pump) { IsBackground = true, Name = "Winora tiling input monitor" };
        try
        {
            thread.Start();
            if (!ready.Task.Wait(TimeSpan.FromSeconds(1))) RequestStop();
        }
        catch (Exception)
        {
            // Fail closed: callers can still arrange tiles but must not attempt
            // to preserve foreground focus without reliable input observation.
            RequestStop();
        }
    }

    public bool IsReliable => Volatile.Read(ref reliable) != 0 && Volatile.Read(ref disposed) == 0 && thread.IsAlive;
    public long Version => Interlocked.Read(ref version);

    // Low-level hooks can be silently removed. Before a rare focus-preserving
    // move, positively establish fresh hooks rather than trusting old handles.
    public async Task<bool> RearmAsync()
    {
        RearmRequest request;
        lock (rearmLock)
        {
            if (Volatile.Read(ref disposed) != 0 || !thread.IsAlive || pendingRearm is not null) return false;
            Volatile.Write(ref reliable, 0);
            request = new(++nextRearm, Environment.TickCount64 + 200);
            pendingRearm = request;
            var owner = Volatile.Read(ref threadId);
            if (owner == 0 || !PostThreadMessage(owner, RearmMessage, (nuint)request.Id, 0))
            { pendingRearm = null; request.Completion.TrySetResult(false); return false; }
        }
        try { return await request.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(200)); }
        catch (TimeoutException)
        {
            lock (rearmLock)
            {
                if (pendingRearm == request) { pendingRearm = null; Volatile.Write(ref reliable, 0); }
                request.Completion.TrySetResult(false);
            }
            return false;
        }
    }

    public void Dispose()
    {
        RequestStop();
        if (thread.IsAlive && Thread.CurrentThread != thread) _ = thread.Join(TimeSpan.FromSeconds(1));
    }

    private void RequestStop()
    {
        Interlocked.Exchange(ref disposed, 1);
        Volatile.Write(ref reliable, 0);
        lock (rearmLock)
        {
            pendingRearm?.Completion.TrySetResult(false);
            pendingRearm = null;
        }
        var owner = Volatile.Read(ref threadId);
        if (owner != 0) _ = PostThreadMessage(owner, Quit, 0, 0);
    }

    private void Pump()
    {
        try
        {
            // Creating the queue before publishing its thread ID makes a stop
            // request safe even during hook installation.
            _ = PeekMessage(out _, 0, 0, 0, 0 /* PM_NOREMOVE */);
            Volatile.Write(ref threadId, GetCurrentThreadId());
            if (Volatile.Read(ref disposed) != 0) return;
            var failuresBeforeInstall = Interlocked.Read(ref callbackFailures);
            var module = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, mouseCallback, module, 0);
            keyboardHook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, keyboardCallback, module, 0);
            if (mouseHook == 0 || keyboardHook == 0 || Volatile.Read(ref disposed) != 0 ||
                Interlocked.Read(ref callbackFailures) != failuresBeforeInstall) return;
            Volatile.Write(ref reliable, 1);
            ready.TrySetResult(true);
            while (Volatile.Read(ref disposed) == 0 && GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Message == RearmMessage) { RearmOnOwnerThread((long)message.WParam); continue; }
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        catch (Exception) { Volatile.Write(ref reliable, 0); }
        finally
        {
            Volatile.Write(ref reliable, 0);
            lock (rearmLock)
            {
                pendingRearm?.Completion.TrySetResult(false);
                pendingRearm = null;
            }
            if (mouseHook != 0) { _ = UnhookWindowsHookEx(mouseHook); mouseHook = 0; }
            if (keyboardHook != 0) { _ = UnhookWindowsHookEx(keyboardHook); keyboardHook = 0; }
            Volatile.Write(ref threadId, 0);
            ready.TrySetResult(false);
            GC.KeepAlive(mouseCallback);
            GC.KeepAlive(keyboardCallback);
        }
    }

    private void RearmOnOwnerThread(long id)
    {
        RearmRequest request;
        lock (rearmLock)
        {
            if (pendingRearm is not { } pending || pending.Id != id || Volatile.Read(ref disposed) != 0) return;
            request = pending;
        }
        var failuresBeforeInstall = Interlocked.Read(ref callbackFailures);
        var module = GetModuleHandle(null);
        var freshMouse = SetWindowsHookEx(14 /* WH_MOUSE_LL */, mouseCallback, module, 0);
        var freshKeyboard = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, keyboardCallback, module, 0);
        var installed = freshMouse != 0 && freshKeyboard != 0;
        if (installed)
        {
            var oldMouse = mouseHook; var oldKeyboard = keyboardHook;
            mouseHook = freshMouse; keyboardHook = freshKeyboard;
            if (oldMouse != 0) _ = UnhookWindowsHookEx(oldMouse);
            if (oldKeyboard != 0) _ = UnhookWindowsHookEx(oldKeyboard);
        }
        else
        {
            if (freshMouse != 0) _ = UnhookWindowsHookEx(freshMouse);
            if (freshKeyboard != 0) _ = UnhookWindowsHookEx(freshKeyboard);
        }
        lock (rearmLock)
        {
            if (pendingRearm != request) return;
            var accepted = installed && Volatile.Read(ref disposed) == 0 && Environment.TickCount64 < request.Deadline
                && Interlocked.Read(ref callbackFailures) == failuresBeforeInstall;
            Volatile.Write(ref reliable, accepted ? 1 : 0);
            pendingRearm = null;
            request.Completion.TrySetResult(accepted);
        }
    }

    private nint OnMouse(int code, nuint message, nint data)
    {
        var started = Stopwatch.GetTimestamp();
        if (code >= 0 && data != 0 && Volatile.Read(ref disposed) == 0)
        {
            try
            {
                // Read only flags and extra-info from MSLLHOOKSTRUCT. Do not
                // read or retain cursor coordinates, wheel delta or input time.
                var flags = (uint)Marshal.ReadInt32(data, 12);
                var injected = (flags & 0x0001 /* LLMHF_INJECTED */) != 0;
                var tag = Marshal.ReadIntPtr(data, IntPtr.Size == 8 ? 24 : 20).ToInt64();
                if (!(injected && tag == GlazeInputTag) &&
                    (message is >= 0x0201 and <= 0x020E || message == 0x0200 /* MOVE */ && injected))
                    Interlocked.Increment(ref version);
            }
            catch (Exception) { MarkCallbackFailure(); }
        }
        return Forward(code, message, data, started);
    }

    private nint OnKeyboard(int code, nuint message, nint data)
    {
        var started = Stopwatch.GetTimestamp();
        if (code >= 0 && data != 0 && Volatile.Read(ref disposed) == 0)
        {
            try
            {
                // KBDLLHOOKSTRUCT flags/extra-info only; never read key codes.
                var injected = ((uint)Marshal.ReadInt32(data, 8) & 0x0010 /* LLKHF_INJECTED */) != 0;
                var tag = Marshal.ReadIntPtr(data, 16).ToInt64();
                if (!(injected && tag == GlazeInputTag) &&
                    message is 0x0100 or 0x0101 or 0x0104 or 0x0105 /* DOWN/UP, SYS DOWN/UP */)
                    Interlocked.Increment(ref version);
            }
            catch (Exception) { MarkCallbackFailure(); }
        }
        return Forward(code, message, data, started);
    }

    private nint Forward(int code, nuint message, nint data, long started)
    {
        try { return CallNextHookEx(0, code, message, data); }
        catch (Exception) { MarkCallbackFailure(); return 0; }
        finally
        {
            // Windows can silently remove a slow hook. Fail closed well before
            // its one-second timeout, including time spent in the hook chain.
            if (Stopwatch.GetTimestamp() - started >= Stopwatch.Frequency / 10)
                MarkCallbackFailure();
        }
    }

    private void MarkCallbackFailure()
    {
        Interlocked.Increment(ref callbackFailures);
        Volatile.Write(ref reliable, 0);
    }

    private delegate nint HookCallback(int code, nuint message, nint data);
    private sealed record RearmRequest(long Id, long Deadline)
    {
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, HookCallback callback, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMessageW", SetLastError = true)] private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostThreadMessageW", SetLastError = true)] private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW", SetLastError = true)] private static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
