using System.ComponentModel;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;

namespace Winora;

internal sealed class TilingCommandException(string message) : InvalidOperationException(message);

internal static class TilingWorkerLoop
{
    internal static async Task RunAsync(Func<bool> running, Func<Task> iteration,
        Action<Exception> recovering, Func<TimeSpan, Task>? delay = null, Func<Task>? initialize = null)
    {
        delay ??= Task.Delay;
        var failures = 0;
        var initialized = false;
        while (running())
        {
            try
            {
                // A startup handshake must not wait for the mutation owner,
                // which can be awaiting that handshake before releasing it.
                if (!initialized && initialize is not null) { await initialize(); initialized = true; }
                await iteration();
                failures = 0;
            }
            catch (Exception error) when (IsTransient(error))
            {
                if (!running()) break;
                failures = Math.Min(failures + 1, 4);
                recovering(error);
            }
            if (running()) await delay(TimeSpan.FromMilliseconds(failures == 0 ? 250 : 250 * (1 << failures)));
        }
    }

    private static bool IsTransient(Exception error) => error is TilingCommandException or WebSocketException
        or OperationCanceledException or IOException or Win32Exception or JsonException;
}
