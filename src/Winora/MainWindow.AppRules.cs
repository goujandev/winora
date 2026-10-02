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
    private void InitializeApplicationRules(UserSettings settings)
    {
        RenderTiledApps(settings.TiledAppExecutables);
        KeyboardNavigation.SetTabNavigation(TiledAppsPopup.Child, KeyboardNavigationMode.Cycle);
        TiledAppsPopup.Opened += (_, _) =>
            TiledAppsPopup.Child?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        TiledAppsPopup.Child.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            TiledAppsPopup.IsOpen = false;
            TiledAppsButton.Focus();
            e.Handled = true;
        };
    }

    private void RefreshApplicationRulesInteractionState(bool available)
    {
        TiledAppsButton.IsEnabled = available;
        TiledAppsPopup.Child.IsEnabled = available;
    }

    private void OnManageTiledApps(object sender, RoutedEventArgs e)
    {
        if (busyTiling || !TiledAppsButton.IsEnabled) return;
        RenderTiledApps(Settings.Load().TiledAppExecutables);
        FullscreenGamesPopup.IsOpen = false;
        TiledAppsPopup.IsOpen = !TiledAppsPopup.IsOpen;
    }

    private async void OnAddTiledApp(object sender, RoutedEventArgs e)
    {
        if (previewOnly || busyTiling) return;
        TiledAppsPopup.IsOpen = false;
        var dialog = new OpenFileDialog
        {
            Title = "Choose applications to keep tiled", Filter = "Applications (*.exe)|*.exe",
            Multiselect = true, CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var additions = dialog.FileNames;
        await ChangeTiledAppsAsync(apps => (apps ?? []).Concat(additions).ToArray());
    }

    private void RenderTiledApps(string[]? apps)
    {
        TiledAppsList.Children.Clear();
        foreach (var app in apps ?? [])
        {
            var name = Path.GetFileNameWithoutExtension(app);
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
                await ChangeTiledAppsAsync(current => current?
                    .Where(path => !string.Equals(path, app, StringComparison.OrdinalIgnoreCase)).ToArray());
            };
            row.Children.Add(remove);
            var label = new TextBlock
            {
                Text = name, ToolTip = app, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            row.Children.Add(label);
            TiledAppsList.Children.Add(row);
        }
    }

    private async Task ChangeTiledAppsAsync(Func<string[]?, string[]?> change)
    {
        if (closingTilingWindow) return;
        await tilingChanges.WaitAsync();
        if (closingTilingWindow) { tilingChanges.Release(); return; }
        busyTiling = true;
        RefreshInteractionState();
        var previous = Settings.Load().TiledAppExecutables;
        try
        {
            var settings = Settings.Load();
            Settings.Save(settings with { TiledAppExecutables = change(settings.TiledAppExecutables) });
            await tiling.RefreshApplicationRulesAsync();
            var current = Settings.Load();
            RenderTiledApps(current.TiledAppExecutables);
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
                Settings.Save(Settings.Load() with { TiledAppExecutables = previous });
                await tiling.RefreshApplicationRulesAsync();
            }
            catch (Exception failure) { restoreError = failure; Trace.WriteLine(failure); }
            RenderTiledApps(previous);
            ShowTilingStatus(restoreError is null ? $"Couldn’t change tiled apps: {error.Message}"
                : $"Couldn’t change or restore tiled apps: {error.Message}", error: true);
            retryTiling = () => ChangeTiledAppsAsync(change);
        }
        finally { busyTiling = false; RefreshInteractionState(); tilingChanges.Release(); }
    }
}
