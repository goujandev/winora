using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Winora;

public partial class MainWindow
{
    private void InitializeFullscreenRouting(UserSettings settings)
    {
        FullscreenRoutingCheckBox.IsChecked = settings.MoveTilesForFullscreenGames;
        RenderFullscreenGames(settings.FullscreenGameExecutables);
        KeyboardNavigation.SetTabNavigation(FullscreenGamesPopup.Child, KeyboardNavigationMode.Cycle);
        FullscreenGamesPopup.Opened += (_, _) =>
            FullscreenGamesPopup.Child?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        FullscreenGamesPopup.Child.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            FullscreenGamesPopup.IsOpen = false;
            FullscreenGamesButton.Focus();
            e.Handled = true;
        };
    }

    private void RefreshFullscreenRoutingInteractionState(bool available)
    {
        FullscreenRoutingCheckBox.IsEnabled = available;
        FullscreenGamesButton.IsEnabled = available && FullscreenRoutingCheckBox.IsChecked == true;
        FullscreenGamesPopup.Child.IsEnabled = available;
    }

    private async void OnFullscreenRoutingChanged(object sender, RoutedEventArgs e)
    {
        if (initializing || synchronizing || busyTiling) return;
        if (previewOnly) { RefreshInteractionState(); return; }
        var enabled = FullscreenRoutingCheckBox.IsChecked == true;
        await ChangeFullscreenRoutingAsync(settings => settings with { MoveTilesForFullscreenGames = enabled });
    }

    private void OnManageFullscreenGames(object sender, RoutedEventArgs e)
    {
        if (busyTiling || !FullscreenGamesButton.IsEnabled) return;
        RenderFullscreenGames(Settings.Load().FullscreenGameExecutables);
        FullscreenGamesPopup.IsOpen = !FullscreenGamesPopup.IsOpen;
    }

    private async void OnAddFullscreenGame(object sender, RoutedEventArgs e)
    {
        if (previewOnly || busyTiling) return;
        FullscreenGamesPopup.IsOpen = false;
        var dialog = new OpenFileDialog
        {
            Title = "Choose game applications", Filter = "Applications (*.exe)|*.exe",
            Multiselect = true, CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var additions = dialog.FileNames;
        await ChangeFullscreenRoutingAsync(settings => settings with
        {
            FullscreenGameExecutables = (settings.FullscreenGameExecutables ?? []).Concat(additions).ToArray()
        });
    }

    private void RenderFullscreenGames(string[]? games)
    {
        FullscreenGamesList.Children.Clear();
        foreach (var game in games ?? [])
        {
            var name = Path.GetFileNameWithoutExtension(game);
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var remove = new Button
            {
                Content = "\u00d7", Padding = new Thickness(7, 0, 7, 1), MinWidth = 26,
                ToolTip = $"Remove {name}", VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(remove, $"Remove {name}");
            DockPanel.SetDock(remove, Dock.Right);
            remove.Click += async (_, _) =>
            {
                if (previewOnly || busyTiling) return;
                await ChangeFullscreenRoutingAsync(settings => settings with
                {
                    FullscreenGameExecutables = settings.FullscreenGameExecutables?
                        .Where(path => !string.Equals(path, game, StringComparison.OrdinalIgnoreCase)).ToArray()
                });
            };
            row.Children.Add(remove);
            var label = new TextBlock
            {
                Text = name, ToolTip = game, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            row.Children.Add(label);
            FullscreenGamesList.Children.Add(row);
        }
    }

    private async Task ChangeFullscreenRoutingAsync(Func<UserSettings, UserSettings> change)
    {
        if (closingTilingWindow) return;
        await tilingChanges.WaitAsync();
        if (closingTilingWindow) { tilingChanges.Release(); return; }
        busyTiling = true;
        RefreshInteractionState();
        var previous = Settings.Load();
        try
        {
            Settings.Save(change(Settings.Load()));
            await tiling.RefreshFullscreenRoutingAsync();
            var current = Settings.Load();
            synchronizing = true;
            try { FullscreenRoutingCheckBox.IsChecked = current.MoveTilesForFullscreenGames; }
            finally { synchronizing = false; }
            RenderFullscreenGames(current.FullscreenGameExecutables);
            if (AppBuild.AllowTilingEffects && current.TilingEnabled)
                UpdateTilingStatus(await tiling.GetStatusAsync());
            else ShowTilingStatus(AppBuild.AllowTilingEffects ? "" : "Preview saved");
        }
        catch (Exception error)
        {
            Trace.WriteLine(error);
            Exception? restoreError = null;
            try
            {
                Settings.Save(Settings.Load() with
                {
                    MoveTilesForFullscreenGames = previous.MoveTilesForFullscreenGames,
                    FullscreenGameExecutables = previous.FullscreenGameExecutables
                });
                await tiling.RefreshFullscreenRoutingAsync();
            }
            catch (Exception failure) { restoreError = failure; Trace.WriteLine(failure); }
            synchronizing = true;
            try { FullscreenRoutingCheckBox.IsChecked = previous.MoveTilesForFullscreenGames; }
            finally { synchronizing = false; }
            RenderFullscreenGames(previous.FullscreenGameExecutables);
            ShowTilingStatus(restoreError is null ? $"Couldn’t change fullscreen routing: {error.Message}"
                : $"Couldn’t change or restore fullscreen routing: {error.Message}", error: true);
            retryTiling = () => ChangeFullscreenRoutingAsync(change);
        }
        finally { busyTiling = false; RefreshInteractionState(); tilingChanges.Release(); }
    }
}
