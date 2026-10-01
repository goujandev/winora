namespace Winora;

public readonly record struct TaskbarPreference(TaskbarMode Mode, bool StartWithWindows)
{
    public TaskbarPreference Normalize() => this with { StartWithWindows = Mode != TaskbarMode.Default && StartWithWindows };
}

public sealed record TaskbarChangeFailure(TaskbarPreference Attempted, Exception Error, Exception? RestoreError, bool Superseded);

/// <summary>Serializes OS changes, retaining only the newest pending choice.</summary>
public sealed class LatestTaskbarChange
{
    private readonly Func<TaskbarPreference, Task> apply;
    private readonly Action<TaskbarPreference> persist;
    private (TaskbarPreference Preference, bool Force)? pending;
    private TaskCompletionSource? completion;
    public TaskbarPreference Committed { get; private set; }
    public TaskbarPreference Requested { get; private set; }
    public bool IsBusy { get; private set; }
    public event Action<TaskbarPreference>? Applying;
    public event Action<TaskbarPreference>? Applied;
    public event Action<TaskbarChangeFailure>? Failed;
    public event Action? StateChanged;

    public LatestTaskbarChange(TaskbarPreference initial, Func<TaskbarPreference, Task> apply, Action<TaskbarPreference> persist)
    {
        Committed = Requested = initial.Normalize();
        this.apply = apply;
        this.persist = persist;
    }

    // Call from one synchronization context (the WPF dispatcher in the app).
    public Task RequestAsync(TaskbarPreference preference, bool force = false)
    {
        Requested = preference.Normalize();
        pending = (Requested, force);
        if (IsBusy) return completion!.Task;
        IsBusy = true;
        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = completion.Task;
        StateChanged?.Invoke();
        _ = DrainAsync();
        return result;
    }

    private async Task DrainAsync()
    {
        try
        {
            while (pending is { } request)
            {
                pending = null;
                if (!request.Force && request.Preference == Committed) continue;
                var previous = Committed;
                Applying?.Invoke(request.Preference);
                try
                {
                    await apply(request.Preference);
                    persist(request.Preference);
                    Committed = request.Preference;
                    Applied?.Invoke(Committed);
                }
                catch (Exception error)
                {
                    Exception? restoreError = null;
                    try { await apply(previous); }
                    catch (Exception restoration) { restoreError = restoration; }
                    var superseded = pending is not null;
                    if (!superseded) Requested = Committed;
                    Failed?.Invoke(new TaskbarChangeFailure(request.Preference, error, restoreError, superseded));
                }
            }
        }
        finally
        {
            IsBusy = false;
            completion!.TrySetResult();
            StateChanged?.Invoke();
        }
    }
}
