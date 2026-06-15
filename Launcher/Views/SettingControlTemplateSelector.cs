using System.Windows;
using System.Windows.Controls;
using MorrowindRemasteredLauncher.Models;
using MorrowindRemasteredLauncher.ViewModels;

namespace MorrowindRemasteredLauncher.Views;

/// <summary>Picks the editor control template for a <see cref="SettingRowViewModel"/> from its <see cref="SettingControl"/> (the <c>ContentTemplateSelector</c> on each row in <c>SettingsPanel.xaml</c>).</summary>
public sealed class SettingControlTemplateSelector : DataTemplateSelector
{
    /// <summary>Template for toggle (boolean) settings.</summary>
    public DataTemplate? ToggleTemplate { get; set; }

    /// <summary>Template for dropdown (choice) settings.</summary>
    public DataTemplate? DropdownTemplate { get; set; }

    /// <summary>Template for slider settings.</summary>
    public DataTemplate? SliderTemplate { get; set; }

    /// <summary>Template for numeric-field settings.</summary>
    public DataTemplate? NumberTemplate { get; set; }

    /// <summary>Template for free-text settings.</summary>
    public DataTemplate? TextTemplate { get; set; }

    /// <summary>Returns the template matching the row's control kind.</summary>
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
