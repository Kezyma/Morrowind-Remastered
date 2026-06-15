using CommunityToolkit.Mvvm.ComponentModel;

namespace MorrowindRemasteredLauncher.ViewModels;

/// <summary>
/// A named group of setting rows, rendered as one collapsible accordion section.
/// <see cref="IsExpanded"/> drives the header chevron and the rows' visibility; the
/// accordion (only one open at a time) is coordinated by the shell view model.
/// </summary>
public sealed partial class SettingCategoryViewModel : ObservableObject
{
    public SettingCategoryViewModel(string name, IReadOnlyList<SettingRowViewModel> rows)
    {
        Name = name;
        Rows = rows;
    }

    public string Name { get; }
    public IReadOnlyList<SettingRowViewModel> Rows { get; }

    [ObservableProperty]
    private bool _isExpanded;
}
