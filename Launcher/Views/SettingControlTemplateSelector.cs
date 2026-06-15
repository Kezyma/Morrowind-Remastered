using System.Windows;
using System.Windows.Controls;
using MorrowindRemasteredLauncher.Models;
using MorrowindRemasteredLauncher.ViewModels;

namespace MorrowindRemasteredLauncher.Views;

/// <summary>
/// Picks the editor control template for a <see cref="SettingRowViewModel"/> from
/// its <see cref="SettingControl"/>. Used as the <c>ContentTemplateSelector</c> on
/// each row's control cell in <c>SettingsPanel.xaml</c>.
/// </summary>
public sealed class SettingControlTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ToggleTemplate { get; set; }
    public DataTemplate? DropdownTemplate { get; set; }
    public DataTemplate? SliderTemplate { get; set; }
    public DataTemplate? NumberTemplate { get; set; }
    public DataTemplate? TextTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        (item as SettingRowViewModel)?.Control switch
        {
            SettingControl.Toggle => ToggleTemplate,
            SettingControl.Dropdown => DropdownTemplate,
            SettingControl.Slider => SliderTemplate,
            SettingControl.NumberField => NumberTemplate,
            SettingControl.TextField => TextTemplate,
            _ => base.SelectTemplate(item, container)
        };
}
