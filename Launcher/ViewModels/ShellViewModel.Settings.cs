using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MorrowindRemasteredLauncher.Models;
using MorrowindRemasteredLauncher.Services;

namespace MorrowindRemasteredLauncher.ViewModels;

/// <summary>Settings panel: a category-grouped, data-driven editor whose setting set switches with the selected edition.</summary>
/// <remarks>Rows are built from <see cref="SettingsCatalog"/>; values are read from / written to the edition's game configs by <see cref="GameSettingsService"/>. Rebuilt on install-state and edition changes (both routed through <see cref="RefreshState"/>).</remarks>
public partial class ShellViewModel
{
    /// <summary>The category sections rendered on the Settings page.</summary>
    public ObservableCollection<SettingCategoryViewModel> SettingCategories { get; } = new();

    /// <summary>True when there are settings to show (edition installed + non-empty).</summary>
    [ObservableProperty]
    private bool _hasSettings;

    /// <summary>Rebuilds the editor rows for the current edition from disk (opening the first section so the page never starts fully collapsed), emptying the list when nothing is installed.</summary>
    public void RebuildGameSettings()
    {
        SettingCategories.Clear();

        if (CurrentState?.IsInstalled != true)
        {
            HasSettings = false;
            return;
        }

        var descriptors = _gameSettings.GetDescriptors(SelectedEdition);
        var current = _gameSettings.LoadCurrent(SelectedEdition);

        foreach (var category in SettingsCatalog.CategoryOrder)
        {
            var rows = descriptors
                .Where(d => d.Category == category)
                .Select(d => new SettingRowViewModel(
                    d,
                    current.TryGetValue(d.Id, out var value) ? value : null,
                    ApplySettingRow))
                .ToList();

            if (rows.Count > 0)
            {
                SettingCategories.Add(new SettingCategoryViewModel(category, rows));
            }
        }

        if (SettingCategories.Count > 0)
        {
            SettingCategories[0].IsExpanded = true;
        }

        HasSettings = SettingCategories.Count > 0;
    }

    /// <summary>Expands the clicked category (collapsing the rest), or collapses it if it was already open.</summary>
    [RelayCommand]
    private void ToggleCategory(SettingCategoryViewModel? category)
    {
        if (category is null)
        {
            return;
        }
        var willOpen = !category.IsExpanded;
        foreach (var c in SettingCategories)
        {
            c.IsExpanded = false;
        }
        category.IsExpanded = willOpen;
    }

    /// <summary>Writes one edited setting row to the game config; on a skipped/failed write, reverts the control to its on-disk value and tells the user instead of failing silently.</summary>
    private void ApplySettingRow(SettingRowViewModel row)
    {
        try
        {
            if (_gameSettings.Apply(SelectedEdition, row.Descriptor, row.CurrentToken))
            {
                return;
            }

            var current = _gameSettings.LoadCurrent(SelectedEdition);
            row.Revert(current.TryGetValue(row.Descriptor.Id, out var v) ? v : null);
            ReportError($"Couldn't save \"{row.Label}\". See the log for details.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't apply setting {row.Descriptor.Id}: {ex.Message}");
            ReportError($"Couldn't save \"{row.Label}\": {ex.Message}");
        }
    }
}
