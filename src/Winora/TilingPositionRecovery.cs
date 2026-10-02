namespace Winora;

internal sealed record TilingWindowPosition(long Handle, int ProcessId, long ProcessStart, uint Flags, uint ShowCommand,
    int MinX, int MinY, int MaxX, int MaxY, int Left, int Top, int Right, int Bottom);

internal static class TilingPositionRecovery
{
    // Keep unsuccessful native requests for retry; closed/replaced and never
    // managed windows are deliberately excluded from restoration.
    internal static IReadOnlyList<TilingWindowPosition> Restore(IEnumerable<TilingWindowPosition> positions,
        IReadOnlySet<long> managed, Func<TilingWindowPosition, bool> sameIdentity,
        Func<TilingWindowPosition, bool> restore) => positions
        .Where(position => managed.Contains(position.Handle) && sameIdentity(position) && !restore(position)).ToArray();
}
