using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.ViewModels;

/// <summary>One editable setting row holding typed bindable values, with the active control chosen from <see cref="Control"/>.</summary>
/// <remarks>Editing a value invokes the apply callback, except while the initial value is being seeded (gated by <c>_suppress</c>).</remarks>
public sealed partial class SettingRowViewModel : ObservableObject
{
    private readonly Action<SettingRowViewModel> _apply;
    private bool _suppress;

    /// <summary>The static descriptor this row edits.</summary>
    public SettingDescriptor Descriptor { get; }

    /// <summary>Display label for the row.</summary>
    public string Label => Descriptor.Label;
    /// <summary>Help text for the row.</summary>
    public string Description => Descriptor.Description;
    /// <summary>True when the descriptor has help text to show.</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Descriptor.Description);
    /// <summary>Which control kind renders this row.</summary>
    public SettingControl Control => Descriptor.Control;
    /// <summary>Minimum value for slider/number controls.</summary>
    public double Minimum => Descriptor.Min ?? 0;
    /// <summary>Maximum value for slider/number controls.</summary>
    public double Maximum => Descriptor.Max ?? 1;
    /// <summary>Step increment for slider/number controls.</summary>
    public double Step => Descriptor.Step ?? 0.1;
    /// <summary>The choices for a dropdown control.</summary>
    public IReadOnlyList<SettingOption> Options => Descriptor.Options ?? Array.Empty<SettingOption>();

    /// <summary>Bound value for a toggle control.</summary>
    [ObservableProperty]
    private bool _boolValue;

    /// <summary>Bound value for a slider/number control.</summary>
    [ObservableProperty]
    private double _doubleValue;

    /// <summary>Bound value for a dropdown control.</summary>
    [ObservableProperty]
    private SettingOption? _selectedOption;

    /// <summary>Bound value for a text control.</summary>
    [ObservableProperty]
    private string? _textValue;

    /// <summary>Creates a row from a descriptor and seeds its control from the stored token without triggering the apply callback.</summary>
    public SettingRowViewModel(
        SettingDescriptor descriptor, string? currentToken, Action<SettingRowViewModel> apply)
    {
        Descriptor = descriptor;
        _apply = apply;

        _suppress = true;
        Seed(SettingValueCodec.FromStored(descriptor.Target.Format, currentToken));
        _suppress = false;
    }

    /// <summary>Re-seeds the control from a stored token without triggering the apply callback, used to roll the row back to its on-disk value when a write fails.</summary>
    public void Revert(string? storedToken)
    {
        _suppress = true;
        Seed(SettingValueCodec.FromStored(Descriptor.Target.Format, storedToken));
        _suppress = false;
    }

    /// <summary>The logical value the service should apply for the active control.</summary>
    public string CurrentToken => Control switch
    {
        SettingControl.Toggle => BoolValue ? "true" : "false",
        SettingControl.Dropdown => SelectedOption?.StoredToken ?? string.Empty,
        SettingControl.Slider or SettingControl.NumberField
            => DoubleValue.ToString(CultureInfo.InvariantCulture),
        _ => TextValue ?? string.Empty
    };

    /// <summary>Loads the given logical value into whichever bindable property matches the active control.</summary>
    private void Seed(string? logical)
    {
        switch (Control)
        {
            case SettingControl.Toggle:
                BoolValue = SettingValueCodec.ParseBool(logical);
                break;
            case SettingControl.Dropdown:
                SelectedOption = Options.FirstOrDefault(o =>
                    string.Equals(o.StoredToken, logical, StringComparison.OrdinalIgnoreCase));
                break;
            case SettingControl.Slider:
            case SettingControl.NumberField:
                DoubleValue = double.TryParse(
                    logical, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d
                    : Minimum;
                break;
            default:
                TextValue = logical;
                break;
        }
    }

    /// <summary>Applies the change when the toggle value is edited (skipped while seeding).</summary>
    partial void OnBoolValueChanged(bool value)
    {
        if (!_suppress)
        {
            _apply(this);
        }
    }

    /// <summary>Applies the change when the slider/number value is edited (skipped while seeding).</summary>
    partial void OnDoubleValueChanged(double value)
    {
        if (!_suppress)
        {
            _apply(this);
        }
    }

    /// <summary>Applies the change when the dropdown selection is edited (skipped while seeding).</summary>
    partial void OnSelectedOptionChanged(SettingOption? value)
    {
        if (!_suppress)
        {
            _apply(this);
        }
    }

    /// <summary>Applies the change when the text value is edited (skipped while seeding).</summary>
    partial void OnTextValueChanged(string? value)
    {
        if (!_suppress)
        {
            _apply(this);
        }
    }
}
