using CommunityToolkit.Mvvm.ComponentModel;

namespace MorrowindRemasteredLauncher.ViewModels;

/// <summary>A named group of setting rows rendered as one collapsible accordion section.</summary>
/// <remarks><see cref="IsExpanded"/> drives the header chevron and the rows' visibility; the accordion (only one open at a time) is coordinated by the shell view model.</remarks>
public sealed partial class SettingCategoryViewModel : ObservableObject
{
    /// <summary>Creates a category section with its display name and rows.</summary>
    public SettingCategoryViewModel(string name, IReadOnlyList<SettingRowViewModel> rows)
    {
        Name = name;
        Rows = rows;
    }

    /// <summary>The category's display name (its header text).</summary>
    public string Name { get; }
    /// <summary>The setting rows in this category.</summary>
    public IReadOnlyList<SettingRowViewModel> Rows { get; }

    /// <summary>Whether this section is currently open in the accordion.</summary>
    [ObservableProperty]
    private bool _isExpanded;
}
