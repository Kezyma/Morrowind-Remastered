namespace MorrowindRemasteredLauncher.Models;

/// <summary>The kind of UI control a setting renders as in the editor.</summary>
public enum SettingControl
{
    Toggle,
    Dropdown,
    Slider,
    NumberField,
    TextField
}

/// <summary>Where a setting's value is persisted.</summary>
public enum SettingStore
{
    /// <summary>The Bethesda Softworks\Morrowind registry screen mode (MWSE display).</summary>
    RegistryScreen,

    /// <summary>A key in one of the game config files (see <see cref="SettingFile"/>).</summary>
    IniFile
}

/// <summary>Which config file an <see cref="SettingStore.IniFile"/> setting lives in.</summary>
public enum SettingFile
{
    /// <summary>MGE XE's MGE.ini (MWSE graphics layer).</summary>
    MgeIni,

    /// <summary>OpenMW's settings.cfg.</summary>
    SettingsCfg,

    /// <summary>Vanilla Morrowind.ini (MWSE gameplay toggles).</summary>
    MorrowindIni
}

/// <summary>Which registry display field a <see cref="SettingStore.RegistryScreen"/> setting maps to.</summary>
public enum ScreenField
{
    Width,
    Height,
    Refresh
}

/// <summary>
/// How a setting's value is encoded in its backing store. The editor works with a
/// normalised "logical" value (bools as "true"/"false", numbers as invariant
/// strings) and <see cref="SettingValueCodec"/> converts to/from the stored token.
/// </summary>
public enum ValueFormat
{
    /// <summary>Stored verbatim (dropdown tokens, free text).</summary>
    Raw,

    /// <summary>Integer (e.g. "1920").</summary>
    Int,

    /// <summary>Float formatted as "0.0###" (UI scale, gamma, volumes, draw distance).</summary>
    Float1,

    /// <summary>Boolean stored as "true"/"false" (most OpenMW + some MGE keys).</summary>
    BoolTrueFalse,

    /// <summary>Boolean stored as "On"/"Off" (some MGE keys).</summary>
    BoolOnOff,

    /// <summary>Boolean stored as "1"/"0" (Morrowind.ini toggles).</summary>
    BoolOneZero
}

/// <summary>One choice in a <see cref="SettingControl.Dropdown"/> (display label + stored token).</summary>
public sealed record SettingOption(string Label, string StoredToken)
{
    // The combo box's selection-box ContentPresenter falls back to ToString() for
    // the collapsed display (DisplayMemberPath only covers the dropdown list), so
    // surface the label here rather than the default record formatting.
    public override string ToString() => Label;
}

/// <summary>Describes where and how a setting's value is read/written.</summary>
public sealed record SettingTarget(
    SettingStore Store,
    ValueFormat Format,
    SettingFile File = SettingFile.SettingsCfg,
    string? Section = null,
    string? Key = null,
    ScreenField ScreenField = ScreenField.Width);

/// <summary>
/// A single curated setting: its category/label/help, the control to render, the
/// backing target, and (for sliders/dropdowns) range or option metadata.
/// </summary>
public sealed record SettingDescriptor(
    string Id,
    string Category,
    string Label,
    string Description,
    SettingControl Control,
    SettingTarget Target,
    double? Min = null,
    double? Max = null,
    double? Step = null,
    IReadOnlyList<SettingOption>? Options = null);
