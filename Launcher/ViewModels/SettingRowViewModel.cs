using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MorrowindRemasteredLauncher.Models;

namespace MorrowindRemasteredLauncher.ViewModels;

/// <summary>
/// One editable setting row. Holds typed bindable values (one per control kind);
/// the active control is chosen from <see cref="Control"/>. Editing a value invokes
/// the apply callback — except while the initial value is being seeded, gated by
/// <c>_suppress</c> (the same pattern as the old display settings loader).
/// </summary>
public sealed partial class SettingRowViewModel : ObservableObject
{
    private readonly Action<SettingRowViewModel> _apply;
    private bool _suppress;

    public SettingDescriptor Descriptor { get; }

    public string Label => Descriptor.Label;
    public string Description => Descriptor.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Descriptor.Description);
    public SettingControl Control => Descriptor.Control;
    public double Minimum => Descriptor.Min ?? 0;
    public double Maximum => Descriptor.Max ?? 1;
    public double Step => Descriptor.Step ?? 0.1;
    public IReadOnlyList<SettingOption> Options => Descriptor.Options ?? Array.Empty<SettingOption>();

    [ObservableProperty]
    private bool _boolValue;

    [ObservableProperty]
    private double _doubleValue;

    [ObservableProperty]
    private SettingOption? _selectedOption;

    [ObservableProperty]
    private string? _textValue;

    public SettingRowViewModel(
        SettingDescriptor descriptor, string? currentToken, Action<SettingRowViewModel> apply)
    {
        Descriptor = descriptor;
        _apply = apply;

        _suppress = true;
        Seed(SettingValueCodec.FromStored(descriptor.Target.Format, currentToken));
        _suppress = false;
    }

    /// <summary>
    /// Re-seeds the control from a stored token without triggering the apply
    /// callback (used to roll the row back to its on-disk value when a write fails).
    /// </summary>
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

    partial void OnBoolValueChanged(bool value)
    {
        if (!_suppress)
        {
            _apply(this);
        }
    }

    partial void OnDoubleValueChanged(double value)
    {
        if (!_suppress)
        {
            _apply(this);
        }
    }

    partial void OnSelectedOptionChanged(SettingOption? value)
    {
        if (!_suppress)
        {
            _apply(this);
        }
    }

    partial void OnTextValueChanged(string? value)
    {
        if (!_suppress)
        {
            _apply(this);
        }
    }
}
