using System.IO;

namespace Winora;

// File ownership is released by Windows if either process exits. Unlike an
// async named Mutex, it is not tied to the thread that acquired it.
internal sealed class TilingMutationLock(FileStream file) : IDisposable
{
    internal static async Task<TilingMutationLock> AcquireAsync(Func<bool>? stopping = null, string? path = null)
    {
        path ??= Path.Combine(TilingService.DirectoryPath, "mutation.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        while (true)
        {
            if (stopping?.Invoke() == true) throw new OperationCanceledException();
            try { return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(25); }
        }
    }

    public void Dispose() => file.Dispose();
}
