using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Winora;

public partial class MainWindow
{
    private readonly TilingService tiling = new();
    private readonly SemaphoreSlim tilingChanges = new(1, 1);
    private readonly DispatcherTimer tilingHealthTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool busyTiling;
    private bool tilingPaused;
    private bool closingTilingWindow;
    private bool tilingWindowReadyToClose;
    private int tilingGapGeneration;
    private Func<Task>? retryTiling;

    private void InitializeTiling(UserSettings settings)
    {
        TilingEnabledCheckBox.IsChecked = settings.TilingEnabled;
        TilingGapSlider.Value = settings.TilingGap;
        InitializeFullscreenRouting(settings);
        InitializeApplicationRules(settings);
        RenderTilingPreview(settings.TilingGap);
        if (AppBuild.IsDevelopment && !AppBuild.AllowTilingEffects)
            TilingEnabledCheckBox.ToolTip = "Preview only. Run dev.cmd -TestTiling to test real window movement.";
        tilingHealthTimer.Tick += async (_, _) =>
        {
            if (!busyTiling && retryTiling is null && !previewOnly && AppBuild.AllowTilingEffects && TilingEnabledCheckBox.IsChecked == true)
            {
                try
                {
                    var status = await tiling.GetStatusAsync();
                    if (!busyTiling && retryTiling is null && TilingEnabledCheckBox.IsChecked == true) UpdateTilingStatus(status);
                }
                catch (Exception error) { ShowTilingStatus($"Couldn’t check tiling: {error.Message}", error: true); }
            }
        };
    }

    private async Task RestoreTilingAsync()
    {
        if (previewOnly || !AppBuild.AllowTilingEffects) return;
        tilingHealthTimer.Start();
        if (!Settings.Load().TilingEnabled) return;
        await ChangeTilingAsync(enabled: true, restoring: true);
    }

    private void RenderTilingPreview(int gap)
    {
        if (TilingPreviewPrimary is null) return;
        var half = gap / 2.0;
        TilingPreviewPrimary.Margin = new Thickness(0, 0, half, 0);
        TilingPreviewSecondary.Margin = new Thickness(half, 0, 0, half);
        TilingPreviewTertiary.Margin = new Thickness(half, half, 0, 0);
        TilingGapValue.Text = $"{gap} px";
    }

    private void RefreshTilingInteractionState()
    {
        if (TilingEnabledCheckBox is null) return;
        var available = !busyTray && !busyTiling && !changes.IsBusy && !restartingUpdate && !closingTilingWindow;
        TilingEnabledCheckBox.IsEnabled = available;
        TilingGapSlider.IsEnabled = !busyTray && !restartingUpdate && !closingTilingWindow;
        TilingPauseButton.IsEnabled = available && TilingEnabledCheckBox.IsChecked == true;
        TilingRetileButton.IsEnabled = TilingPauseButton.IsEnabled && !tilingPaused;
        TilingRetileButton.ToolTip = tilingPaused ? "Resume tiling before rearranging windows." : null;
        RetryTilingButton.IsEnabled = available;
        RefreshFullscreenRoutingInteractionState(available);
        RefreshApplicationRulesInteractionState(available);
        TilingPauseButton.Content = tilingPaused ? "Resume" : "Pause";
        System.Windows.Automation.AutomationProperties.SetName(TilingPauseButton,
            tilingPaused ? "Resume automatic tiling" : "Pause automatic tiling");
    }

    private void ShowTilingStatus(string message, bool error = false)
    {
        TilingStatusLabel.Text = message;
        TilingStatusLabel.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        TilingStatusLabel.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorInk" : "Muted");
        RetryTilingButton.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        if (!error) retryTiling = null;
    }

    private void UpdateTilingStatus(TilingStatus status)
    {
        tilingPaused = status.Paused;
        ShowTilingStatus(status.Message, error: !status.Responsive || !status.Running && TilingEnabledCheckBox.IsChecked == true);
        RefreshTilingInteractionState();
    }

    private async void OnTilingEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (initializing || synchronizing || busyTiling) return;
        if (previewOnly) { RefreshInteractionState(); return; }
        await ChangeTilingAsync(TilingEnabledCheckBox.IsChecked == true);
    }

    private async Task ChangeTilingAsync(bool enabled, bool restoring = false)
    {
        if (closingTilingWindow) return;
        await tilingChanges.WaitAsync();
        if (closingTilingWindow) { tilingChanges.Release(); return; }
        busyTiling = true;
        RefreshInteractionState();
        var previous = Settings.Load();
        ShowTilingStatus(enabled ? "Starting tiling…" : "Stopping tiling…");
        try
        {
            var gap = (int)Math.Round(TilingGapSlider.Value);
            if (enabled)
                await tiling.EnableAsync(gap, new Progress<string>(message => ShowTilingStatus(message)));
            else await tiling.DisableAsync();
            tilingPaused = false;
            if (enabled && AppBuild.AllowTilingEffects)
            {
                var status = await tiling.GetStatusAsync();
                if (!status.Running || !status.Responsive) throw new InvalidOperationException(status.Message);
                UpdateTilingStatus(status);
            }
            else ShowTilingStatus(AppBuild.AllowTilingEffects ? "" : "Preview saved");
            if (!restoring) Settings.Save(Settings.Load() with { TilingEnabled = enabled, TilingGap = gap });
            synchronizing = true;
            try { TilingEnabledCheckBox.IsChecked = enabled; }
            finally { synchronizing = false; }
        }
        catch (Exception error)
        {
            Trace.WriteLine(error);
            Exception? restoreError = null;
            if (!restoring)
            {
                try
                {
                    if (previous.TilingEnabled) await tiling.EnableAsync(previous.TilingGap);
                    else await tiling.DisableAsync();
                }
                catch (Exception failure) { restoreError = failure; Trace.WriteLine(failure); }
                synchronizing = true;
                try { TilingEnabledCheckBox.IsChecked = previous.TilingEnabled; }
                finally { synchronizing = false; }
            }
            ShowTilingStatus(restoreError is null ? $"Couldn’t change tiling: {error.Message}"
                : $"Couldn’t change or restore tiling: {error.Message}", error: true);
            retryTiling = () => ChangeTilingAsync(enabled, restoring);
        }
        finally
        {
            busyTiling = false;
            RefreshInteractionState();
            tilingChanges.Release();
        }
    }

    private async void OnTilingGapChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var gap = (int)Math.Round(e.NewValue);
        RenderTilingPreview(gap);
        if (initializing || synchronizing || previewOnly) return;
        var generation = ++tilingGapGeneration;
        await Task.Delay(250);
        if (generation != tilingGapGeneration || closingTilingWindow) return;
        await tilingChanges.WaitAsync();
        busyTiling = true;
        RefreshInteractionState();
        var previous = Settings.Load().TilingGap;
        try
        {
            if (generation != tilingGapGeneration) return;
            if (Settings.Load().TilingEnabled) await tiling.SetGapAsync(gap);
            Settings.Save(Settings.Load() with { TilingGap = gap });
            ShowTilingStatus(AppBuild.AllowTilingEffects ? tilingPaused ? "Paused" : Settings.Load().TilingEnabled ? "Tiling active" : "" : "Preview saved");
        }
        catch (Exception error)
        {
            Trace.WriteLine(error);
            try { if (Settings.Load().TilingEnabled) await tiling.SetGapAsync(previous); }
            catch (Exception restoreError) { Trace.WriteLine(restoreError); }
            if (generation == tilingGapGeneration)
            {
                synchronizing = true;
                try { TilingGapSlider.Value = previous; RenderTilingPreview(previous); }
                finally { synchronizing = false; }
            }
            ShowTilingStatus($"Couldn’t change window gaps: {error.Message}", error: true);
            retryTiling = () =>
            {
                TilingGapSlider.Value = gap;
                return Task.CompletedTask;
            };
        }
        finally { busyTiling = false; RefreshInteractionState(); tilingChanges.Release(); }
    }

    private async void OnTilingPause(object sender, RoutedEventArgs e)
    {
        if (previewOnly || busyTiling || TilingEnabledCheckBox.IsChecked != true) return;
        await RunTilingActionAsync(async () =>
        {
            if (AppBuild.AllowTilingEffects) { await tiling.TogglePauseAsync(); UpdateTilingStatus(await tiling.GetStatusAsync()); }
            else { tilingPaused = !tilingPaused; ShowTilingStatus(tilingPaused ? "Preview paused" : "Preview saved"); }
        });
    }

    private async void OnTilingRetile(object sender, RoutedEventArgs e)
    {
        if (previewOnly || busyTiling || TilingEnabledCheckBox.IsChecked != true) return;
        await RunTilingActionAsync(async () =>
        {
            await tiling.RetileAsync();
            if (AppBuild.AllowTilingEffects) UpdateTilingStatus(await tiling.GetStatusAsync());
            else ShowTilingStatus("Preview saved");
        });
    }

    private async Task RunTilingActionAsync(Func<Task> action)
    {
        if (closingTilingWindow) return;
        await tilingChanges.WaitAsync();
        if (closingTilingWindow) { tilingChanges.Release(); return; }
        busyTiling = true;
        RefreshInteractionState();
        try { await action(); }
        catch (Exception error)
        {
            Trace.WriteLine(error);
            ShowTilingStatus($"Couldn’t control tiling: {error.Message}", error: true);
            retryTiling = () => RunTilingActionAsync(action);
        }
        finally { busyTiling = false; RefreshInteractionState(); tilingChanges.Release(); }
    }

    private async void OnTilingRetry(object sender, RoutedEventArgs e)
    {
        if (previewOnly || busyTiling) return;
        if (retryTiling is { } retry) await retry();
        else await ChangeTilingAsync(Settings.Load().TilingEnabled);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || tilingWindowReadyToClose) return;
        // Close only after the running transaction has committed or rolled
        // back. Production keeps its engine; live test mode also stops it.
        e.Cancel = true;
        if (closingTilingWindow) return;
        closingTilingWindow = true;
        ++tilingGapGeneration;
        RefreshInteractionState();
        _ = CloseAfterTilingAsync();
    }

    private async Task CloseAfterTilingAsync()
    {
        // Let the first Closing event finish before issuing the final Close,
        // including when no transaction currently owns the semaphore.
        await Dispatcher.Yield(DispatcherPriority.Background);
        await tilingChanges.WaitAsync();
        try
        {
            if (AppBuild.IsTilingTest) await tiling.DisableAsync();
            tilingWindowReadyToClose = true;
            Close();
        }
        catch (Exception error)
        {
            Trace.WriteLine(error);
            closingTilingWindow = false;
            ShowTilingStatus($"Couldn’t close settings: {error.Message}", error: true);
        }
        finally { tilingChanges.Release(); RefreshInteractionState(); }
    }
}
