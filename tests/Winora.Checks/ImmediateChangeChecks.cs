using System.IO;
using Winora;

internal static class ImmediateChangeChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var original = new TaskbarPreference(TaskbarMode.Default);
        var transparent = new TaskbarPreference(TaskbarMode.Transparent);
        var acrylic = new TaskbarPreference(TaskbarMode.Acrylic);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<TaskbarPreference>();
        var persisted = new List<TaskbarPreference>();
        var concurrent = 0;
        var maximumConcurrent = 0;
        var queue = new LatestTaskbarChange(original, async preference =>
        {
            concurrent++;
            maximumConcurrent = Math.Max(maximumConcurrent, concurrent);
            applied.Add(preference);
            if (applied.Count == 1) await gate.Task;
            concurrent--;
        }, persisted.Add);
        var drain = queue.RequestAsync(transparent);
        _ = queue.RequestAsync(original);
        _ = queue.RequestAsync(acrylic);
        var latest = acrylic;
        _ = queue.RequestAsync(latest);
        check(queue.IsBusy && queue.Requested == latest && queue.Committed == original, "Pending finish is distinct from committed finish");
        gate.SetResult();
        await drain;
        check(maximumConcurrent == 1 && applied.SequenceEqual(new[] { transparent, latest }), "Rapid choices apply serially and discard obsolete pending choices");
        check(queue.Committed == latest && persisted.Last() == latest && !queue.IsBusy, "Latest finish commits after the OS change");

        var existing = new TaskbarPreference(TaskbarMode.Transparent);
        var native = existing;
        TaskbarChangeFailure? failure = null;
        var failureQueue = new LatestTaskbarChange(existing, preference =>
        {
            native = preference;
            if (preference.Mode == TaskbarMode.Acrylic) throw new InvalidOperationException("Engine unavailable");
            return Task.CompletedTask;
        }, _ => throw new InvalidOperationException("Unexpected persistence"));
        failureQueue.Failed += error => failure = error;
        await failureQueue.RequestAsync(acrylic);
        check(native == existing && failureQueue.Committed == existing && failureQueue.Requested == existing, "Failed application restores the previous finish and selection");
        check(failure is { RestoreError: null, Superseded: false }, "Application failure is exposed for an explicit retry");

        native = existing;
        failure = null;
        var persistenceQueue = new LatestTaskbarChange(existing, preference => { native = preference; return Task.CompletedTask; },
            _ => throw new IOException("Preferences are read only"));
        persistenceQueue.Failed += error => failure = error;
        await persistenceQueue.RequestAsync(acrylic);
        check(native == existing && persistenceQueue.Committed == existing && failure?.Error is IOException,
            "Persistence failure compensates the OS change and preserves committed preferences");

        gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        failure = null;
        native = existing;
        var recoveryQueue = new LatestTaskbarChange(existing, async preference =>
        {
            native = preference;
            if (preference.Mode == TaskbarMode.Acrylic)
            {
                await gate.Task;
                throw new InvalidOperationException("First selection failed");
            }
        }, _ => { });
        recoveryQueue.Failed += error => failure = error;
        drain = recoveryQueue.RequestAsync(acrylic);
        _ = recoveryQueue.RequestAsync(original);
        gate.SetResult();
        await drain;
        check(failure is { Superseded: true } && recoveryQueue.Committed == original && native == original,
            "An obsolete failure cannot overwrite a newer selection");

        var count = 0;
        var sameQueue = new LatestTaskbarChange(existing, _ => { count++; return Task.CompletedTask; }, _ => { });
        await sameQueue.RequestAsync(existing);
        await sameQueue.RequestAsync(existing, force: true);
        check(count == 1, "Explicit retry and saved-state restoration can reapply an unchanged finish");
        var preferences = new UserSettings(Mode: TaskbarMode.Acrylic, AlwaysShowTrayIcons: true, StartWinoraWithWindows: true);
        var defaultQueue = new LatestTaskbarChange(acrylic, _ => Task.CompletedTask,
            preference => preferences = preferences with { Mode = preference.Mode });
        await defaultQueue.RequestAsync(original);
        check(defaultQueue.Committed == original && preferences.StartWinoraWithWindows && preferences.AlwaysShowTrayIcons,
            "Choosing the Windows taskbar keeps app startup and tray preferences enabled");

        failure = null;
        var restoreFailureQueue = new LatestTaskbarChange(existing, _ => throw new IOException("OS operation failed"), _ => { });
        restoreFailureQueue.Failed += error => failure = error;
        await restoreFailureQueue.RequestAsync(acrylic);
        check(failure?.RestoreError is IOException && !restoreFailureQueue.IsBusy, "Failed restoration is reported without leaving controls busy");

        var reentrantApplied = new LatestTaskbarChange(original, _ => Task.CompletedTask, _ => { });
        reentrantApplied.Applied += preference =>
        {
            if (preference == transparent) _ = reentrantApplied.RequestAsync(acrylic);
        };
        await reentrantApplied.RequestAsync(transparent);
        check(reentrantApplied.Committed == acrylic && !reentrantApplied.IsBusy, "An applied callback can queue a new choice without losing it");

        var finalStateReentry = new LatestTaskbarChange(original, _ => Task.CompletedTask, _ => { });
        finalStateReentry.StateChanged += () =>
        {
            if (!finalStateReentry.IsBusy && finalStateReentry.Committed == transparent)
                _ = finalStateReentry.RequestAsync(acrylic);
        };
        await finalStateReentry.RequestAsync(transparent);
        check(finalStateReentry.Committed == acrylic && !finalStateReentry.IsBusy, "An idle-state callback can start a separate new operation");

        var firstAttempt = true;
        var failureReentry = new LatestTaskbarChange(original, preference =>
        {
            if (preference == acrylic && firstAttempt) { firstAttempt = false; throw new IOException("Retryable failure"); }
            return Task.CompletedTask;
        }, _ => { });
        failureReentry.Failed += error => { _ = failureReentry.RequestAsync(acrylic); };
        await failureReentry.RequestAsync(acrylic);
        check(failureReentry.Committed == acrylic && !failureReentry.IsBusy, "A failure callback can retry after rollback without concurrent OS changes");
    }
}
