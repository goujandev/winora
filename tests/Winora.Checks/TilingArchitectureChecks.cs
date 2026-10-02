using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using Winora;

internal static class TilingArchitectureChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "winora-architecture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var refresh = Path.Combine(root, "refresh");
            var engine = new Engine();
            using (var controller = new TilingLayoutController(() => true, engine.Desktop, new Input(), refreshPath: refresh))
            {
                await controller.ReconcileAsync(engine.SendAsync, [], new());
                engine.Direction = "vertical";
                engine.Weight = .7;
                await controller.ReconcileAsync(engine.SendAsync, [], new());
                engine.Gap = 16;
                engine.Commands.Clear();
                await controller.ReconcileAsync(engine.SendAsync, [], new(TilingGap: 16));
                check(engine.Direction == "vertical" && Math.Abs(engine.Weight - .7) < .005
                    && !engine.Commands.Any(command => command.Contains("set-tiling-direction")),
                    "Changing gaps reflows a manual 70/30 vertical layout without resetting its orientation");
                File.WriteAllText(refresh, Guid.NewGuid().ToString("D"));
                await controller.ReconcileAsync(engine.SendAsync, [], new(TilingGap: 16));
                check(engine.Direction == "horizontal" && Math.Abs(engine.Weight - .5) < .005,
                    "Explicit Rearrange still clears manual split choices");
            }

            engine = new Engine { Scale = 1.5 };
            using (var controller = new TilingLayoutController(() => true, engine.Desktop, new Input(), refreshPath: refresh))
            {
                await controller.ReconcileAsync(engine.SendAsync, [], new());
                check(engine.Desktop.Windows[1].Dpi == 96 && engine.Desktop.Windows[1].FrameRect.X == 12
                    && engine.Desktop.Windows[1].FrameRect.Y == 12,
                    "DPI-unaware app frames use the engine monitor's 150 percent gap scale");
            }

            engine = new Engine();
            engine.Desktop.Windows.Remove(2);
            using (var controller = new TilingLayoutController(() => true, engine.Desktop, new Input(), refreshPath: refresh))
            {
                await controller.ReconcileAsync(engine.SendAsync, [], new());
                check(engine.Desktop.Windows[1].FrameRect == engine.Bounds(1) && engine.Commands.Count == 0,
                    "An unreadable HWND keeps its allocation while its healthy neighbour's native frame is repaired");
                engine.Paused = true;
                engine.Desktop.Windows[1] = engine.Desktop.Windows[1] with { FrameRect = new(100, 100, 200, 200) };
                await controller.ReconcileAsync(engine.SendAsync, [], new());
                check(engine.Desktop.Windows[1].FrameRect == new TilingRect(100, 100, 200, 200),
                    "Partial-workspace native repairs stop while tiling is paused");
            }

            engine = new Engine();
            engine.Desktop.Windows[2] = engine.Desktop.Windows[2] with { MinWidth = 1100 };
            var changes = new List<TilingAutomaticChange>();
            using (var controller = new TilingLayoutController(() => true, engine.Desktop, new Input(),
                change => { changes.Add(change); return Task.CompletedTask; }, refresh))
            {
                await controller.ReconcileAsync(engine.SendAsync, [], new());
                check(changes.Single() is { Handle: 2, Floating: true, MonitorId: "physical-A", Workspace: "monitor-1" },
                    "A verified native-size fallback reports its physical destination and floating state to routing");
                engine.Desktop.ForegroundHandle = 2;
                engine.Desktop.Dragging = true;
                await controller.ReconcileAsync(engine.SendAsync, [], new());
                check(changes.Count == 2 && changes[^1].UserOverride,
                    "Manual manipulation of an automatic fallback explicitly releases its routing restoration claim");
            }

            var iterations = 0;
            var recoveries = 0;
            var delays = new List<TimeSpan>();
            await TilingWorkerLoop.RunAsync(() => iterations < 5, () =>
            {
                ++iterations;
                return iterations switch
                {
                    1 => Task.FromException(new OperationCanceledException()),
                    2 => Task.FromException(new TilingCommandException("window closed")),
                    4 => Task.FromException(new WebSocketException()),
                    _ => Task.CompletedTask
                };
            }, _ => ++recoveries, delay => { delays.Add(delay); return Task.CompletedTask; });
            check(iterations == 5 && recoveries == 3
                && delays.Select(delay => delay.TotalMilliseconds).SequenceEqual([500d, 1000d, 250d, 500d]),
                "Transient timeouts, disappearing windows and IPC disconnects recover, with backoff reset after success");
            var fatal = false;
            try
            {
                await TilingWorkerLoop.RunAsync(() => true,
                    () => Task.FromException(new InvalidOperationException("foreign engine")), _ => { });
            }
            catch (InvalidOperationException) { fatal = true; }
            check(fatal, "An engine identity or version mismatch cannot be masked as a transient reconnect");

            var listenerMissing = false;
            try { TilingService.VerifyListenerOwner(10, null); }
            catch (WebSocketException) { listenerMissing = true; }
            check(listenerMissing, "A briefly unavailable IPC listener is classified as a recoverable disconnect");
            var foreignListener = false;
            try { TilingService.VerifyListenerOwner(10, 11); }
            catch (InvalidOperationException) { foreignListener = true; }
            check(foreignListener, "A foreign process on the IPC port remains a fatal ownership failure");
            var orderlyClose = false;
            using (var socket = new ClosingSocket())
            {
                try { using var response = await TilingService.SendReceiveAsync(socket, "query monitors", new byte[32], default); }
                catch (WebSocketException) { orderlyClose = true; }
            }
            check(orderlyClose, "An orderly WebSocket close is recoverable instead of terminating layout checks");

            var lockPath = Path.Combine(root, "mutation.lock");
            using (var first = await TilingMutationLock.AcquireAsync(path: lockPath))
            {
                var stopping = false;
                var waiter = TilingMutationLock.AcquireAsync(() => stopping, lockPath);
                check(!waiter.IsCompleted, "The settings/helper mutation lock excludes overlapping owners");
                stopping = true;
                var canceled = false;
                try { using var ignored = await waiter.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (OperationCanceledException) { canceled = true; }
                check(canceled, "A helper waiting for a settings mutation can stop without deadlocking shutdown");
            }
            using (var next = await TilingMutationLock.AcquireAsync(path: lockPath))
                check(true, "Mutation ownership is released when the previous owner's file handle closes");

            using (var settingsMutation = await TilingMutationLock.AcquireAsync(path: lockPath))
            {
                var ready = false;
                var stopping = false;
                var repairsStarted = false;
                var handshakeAttempts = 0;
                var worker = TilingWorkerLoop.RunAsync(() => !stopping, async () =>
                {
                    using var mutation = await TilingMutationLock.AcquireAsync(() => stopping, lockPath);
                    repairsStarted = true;
                }, _ => { }, _ => Task.CompletedTask, initialize: () =>
                {
                    if (++handshakeAttempts == 1) throw new OperationCanceledException();
                    ready = true;
                    return Task.CompletedTask;
                });
                check(ready && handshakeAttempts == 2 && !repairsStarted && !worker.IsCompleted,
                    "Helper startup retries and reports readiness while settings still owns the mutation lock");
                stopping = true;
                await worker.WaitAsync(TimeSpan.FromSeconds(2));
            }

            TilingWindowPosition Position(long handle) => new(handle, 10, 20, 0, 1, 0, 0, 0, 0, 100, 100, 400, 400);
            var attempts = new List<long>();
            var remaining = TilingPositionRecovery.Restore([Position(1), Position(2), Position(3), Position(4)],
                new HashSet<long> { 1, 2, 3 }, position => position.Handle != 3,
                position => { attempts.Add(position.Handle); return position.Handle == 1; });
            check(attempts.SequenceEqual([1L, 2L]) && remaining.Single().Handle == 2,
                "Position recovery works from saved ownership after engine exit and retains failed native requests for retry");
            check(TilingPositionRecovery.Restore(remaining, new HashSet<long> { 2 }, _ => true, _ => true).Count == 0,
                "Retry can finish the remaining position recovery without replaying completed or unrelated windows");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Input : ITilingInputMonitor
    {
        public bool IsReliable => false;
        public long Version => 0;
        public Task<bool> RearmAsync() => Task.FromResult(false);
        public void Dispose() { }
    }

    private sealed class ClosingSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Desktop : ITilingNativeWindows
    {
        internal readonly Dictionary<long, TilingNativeWindow> Windows = [];
        public long ForegroundHandle { get; set; }
        internal bool Dragging;
        public bool IsDesktop(long handle) => false;
        public bool IsAnyMoveSizeActive() => Dragging;
        public IReadOnlyList<TilingNativeMonitor> GetMonitors() => [new("display-A", new(0, 0, 1000, 600), new(0, 0, 1000, 600), true, 0)];
        public IReadOnlyList<TilingNativeWindow> GetOpenWindows() => Windows.Values.ToArray();
        public bool TryRead(long handle, out TilingNativeWindow window, bool queryConstraints = true) => Windows.TryGetValue(handle, out window!);
        public bool TrySetFrameBounds(TilingNativeWindow expected, TilingRect bounds)
        {
            Windows[expected.Handle] = expected with { Rect = bounds, FrameRect = bounds };
            return true;
        }
        public bool TryGetIdentity(long handle, out int process, out long start)
        {
            var found = Windows.TryGetValue(handle, out var window);
            process = window?.ProcessId ?? 0; start = window?.ProcessStart ?? 0;
            return found;
        }
    }

    private sealed class Engine
    {
        private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
        private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        private static readonly Guid Workspace = Guid.Parse("00000000-0000-0000-0000-000000000010");
        internal readonly Desktop Desktop = new();
        internal readonly List<string> Commands = [];
        private readonly HashSet<long> floating = [];
        internal string Direction = "horizontal";
        internal double Weight = .5;
        internal double Scale = 1;
        internal int Gap = 8;
        internal bool Paused;
        internal Engine()
        {
            for (var handle = 1L; handle <= 2; handle++)
                Desktop.Windows[handle] = new(handle, 10, 20, new(100, 100, 200, 200), new(100, 100, 200, 200), default,
                    "display-A", true, false, false, false, false, true, false, 96, 160, 120, null, null, true);
        }

        internal TilingRect Bounds(long handle)
        {
            var gap = (int)Math.Round(Gap * Scale);
            var area = new TilingRect(gap, gap, 1000 - 2 * gap, 600 - 2 * gap);
            if (floating.Contains(handle)) return new(100, 100, 200, 200);
            if (floating.Count > 0) return area;
            var horizontal = Direction == "horizontal";
            var available = (horizontal ? area.Width : area.Height) - gap;
            var first = (int)Math.Round(available * Weight);
            return horizontal
                ? handle == 1 ? area with { Width = first } : area with { X = gap + first + gap, Width = available - first }
                : handle == 1 ? area with { Height = first } : area with { Y = gap + first + gap, Height = available - first };
        }

        internal Task<JsonDocument> SendAsync(string command)
        {
            if (command == "query paused") return Reply(Paused);
            if (command == "query monitors")
            {
                object Window(Guid id, long handle, double weight)
                {
                    var bounds = Bounds(handle);
                    return new { type = "window", id, handle, state = new { type = floating.Contains(handle) ? "floating" : "tiling" },
                        displayState = "shown", hasFocus = false, activeDrag = (object?)null, tilingSize = weight,
                        x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height };
                }
                return Reply(new { monitors = new[] { new
                {
                    id = Guid.Parse("00000000-0000-0000-0000-000000000011"), deviceName = "display-A", devicePath = "physical-A", scaleFactor = Scale,
                    children = new[] { new { type = "workspace", id = Workspace, name = "monitor-1", isDisplayed = true,
                        tilingDirection = Direction, children = new[] { Window(First, 1, Weight), Window(Second, 2, 1 - Weight) } } }
                } } });
            }
            Commands.Add(command);
            if (command.Contains("set-tiling-direction")) Direction = command.Split(' ')[^1];
            if (command.Contains(" size "))
            {
                var weight = double.Parse(command.Split(' ')[^1].TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture) / 100;
                Weight = command.Contains(First.ToString()) ? weight : 1 - weight;
            }
            if (command.Contains("set-floating")) floating.Add(command.Contains(First.ToString()) ? 1 : 2);
            if (command.EndsWith("set-tiling", StringComparison.Ordinal)) floating.Remove(command.Contains(First.ToString()) ? 1 : 2);
            return Reply((object?)null);
        }

        private static Task<JsonDocument> Reply<T>(T data) => Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(new { success = true, data })));
    }
}
