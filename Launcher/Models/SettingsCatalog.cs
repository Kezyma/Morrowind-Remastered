namespace MorrowindRemasteredLauncher.Models;

/// <summary>
/// The curated, per-edition list of settings the editor exposes. Pure data — no
/// I/O. Resolution and refresh options are filled at runtime by
/// <c>GameSettingsService</c> from the active monitor's modes.
/// </summary>
public static class SettingsCatalog
{
    // Well-known ids the service special-cases (registry display / config mirror).
    public const string ResolutionIdValue = "display.resolution";
    public const string RefreshIdValue = "display.refresh";
    public const string UiScaleIdValue = "ui.scale";

    /// <summary>The resolution setting id (same in both editions; lists are separate).</summary>
    public static string ResolutionId(Edition edition) => ResolutionIdValue;

    /// <summary>Category render order. Categories with no settings are skipped by the UI.</summary>
    public static IReadOnlyList<string> CategoryOrder { get; } = new[]
    {
        "Display", "Graphics", "View Distance", "Shadows",
        "Interface", "Gameplay", "Audio", "Controls"
    };

    public static IReadOnlyList<SettingDescriptor> For(Edition edition) =>
        edition == Edition.OpenMW ? OpenMw : Mwse;

    // ----------------------------------------------------------- build helpers

    private static SettingTarget Cfg(string section, string key, ValueFormat fmt = ValueFormat.Raw) =>
        new(SettingStore.IniFile, fmt, SettingFile.SettingsCfg, section, key);

    private static SettingTarget Mge(string section, string key, ValueFormat fmt = ValueFormat.Raw) =>
        new(SettingStore.IniFile, fmt, SettingFile.MgeIni, section, key);

    private static SettingTarget MwIni(string section, string key, ValueFormat fmt = ValueFormat.Raw) =>
        new(SettingStore.IniFile, fmt, SettingFile.MorrowindIni, section, key);

    private static SettingTarget Reg(ScreenField field) =>
        new(SettingStore.RegistryScreen, ValueFormat.Raw, ScreenField: field);

    private static SettingDescriptor Toggle(
        string id, string cat, string label, string desc, SettingTarget target) =>
        new(id, cat, label, desc, SettingControl.Toggle, target);

    private static SettingDescriptor Slider(
        string id, string cat, string label, string desc, SettingTarget target,
        double min, double max, double step) =>
        new(id, cat, label, desc, SettingControl.Slider, target, min, max, step);

    private static SettingDescriptor Number(
        string id, string cat, string label, string desc, SettingTarget target,
        double min = 0, double max = double.MaxValue) =>
        new(id, cat, label, desc, SettingControl.NumberField, target, min, max);

    private static SettingDescriptor Dropdown(
        string id, string cat, string label, string desc, SettingTarget target,
        params SettingOption[] options) =>
        new(id, cat, label, desc, SettingControl.Dropdown, target, Options: options);

    private static SettingOption Opt(string label, string token) => new(label, token);

    // ----------------------------------------------------------------- OpenMW

    private static readonly IReadOnlyList<SettingDescriptor> OpenMw = new[]
    {
        // Display
        Dropdown(ResolutionIdValue, "Display", "Resolution",
            "Screen resolution.",
            Cfg("Video", "resolution x", ValueFormat.Int)),
        Dropdown("video.windowMode", "Display", "Window mode",
            "How the game window is presented.",
            Cfg("Video", "window mode"),
            Opt("Fullscreen", "0"), Opt("Windowed Fullscreen", "1"), Opt("Windowed", "2")),
        Dropdown("video.vsync", "Display", "V-Sync",
            "Vertical sync. Reduces screen tearing.",
            Cfg("Video", "vsync mode"),
            Opt("Off", "0"), Opt("On", "1"), Opt("Adaptive", "2")),
        Number("video.fpsLimit", "Display", "FPS limit",
            "Maximum frames per second. 0 = unlimited.",
            Cfg("Video", "framerate limit", ValueFormat.Int), 0, 1000),
        Slider("video.gamma", "Display", "Gamma",
            "Screen brightness.",
            Cfg("Video", "gamma", ValueFormat.Float1), 0.5, 2.0, 0.05),

        // Graphics
        Dropdown("video.antialiasing", "Graphics", "Anti-aliasing",
            "Smooths jagged edges (MSAA).",
            Cfg("Video", "antialiasing"),
            Opt("Off", "0"), Opt("2x", "2"), Opt("4x", "4"), Opt("8x", "8"), Opt("16x", "16")),
        Dropdown("video.anisotropy", "Graphics", "Anisotropic filtering",
            "Sharpens textures viewed at steep angles.",
            Cfg("General", "anisotropy"),
            Opt("Off", "0"), Opt("2x", "2"), Opt("4x", "4"), Opt("8x", "8"), Opt("16x", "16")),
        Toggle("shaders.alphaAa", "Graphics", "Smooth alpha edges",
            "Anti-alias cutout edges on foliage and fences. Requires anti-aliasing.",
            Cfg("Shaders", "antialias alpha test", ValueFormat.BoolTrueFalse)),
        Toggle("water.shader", "Graphics", "Water reflections",
            "Reflective, refractive water surface.",
            Cfg("Water", "shader", ValueFormat.BoolTrueFalse)),
        Slider("camera.fov", "Graphics", "Field of view",
            "Field of view, in degrees.",
            Cfg("Camera", "field of view", ValueFormat.Int), 30, 110, 1),
        Slider("camera.fpFov", "Graphics", "First-person FOV",
            "Field of view for the player's hands in first person.",
            Cfg("Camera", "first person field of view", ValueFormat.Int), 30, 110, 1),

        // View Distance
        Slider("camera.viewDistance", "View Distance", "View distance",
            "How far the world is drawn; higher costs performance.",
            Cfg("Camera", "viewing distance", ValueFormat.Int), 6144, 81920, 2048),
        Toggle("terrain.distant", "View Distance", "Distant terrain",
            "Draw the whole landscape, not just nearby cells.",
            Cfg("Terrain", "distant terrain", ValueFormat.BoolTrueFalse)),
        Toggle("fog.distant", "View Distance", "Distant fog",
            "Fade the far landscape with distance fog.",
            Cfg("Fog", "use distant fog", ValueFormat.BoolTrueFalse)),
        Toggle("groundcover.enabled", "View Distance", "Groundcover",
            "Show grass and ground plants.",
            Cfg("Groundcover", "enabled", ValueFormat.BoolTrueFalse)),
        Slider("groundcover.density", "View Distance", "Groundcover density",
            "How much grass is shown (1.0 = full).",
            Cfg("Groundcover", "density", ValueFormat.Float1), 0, 1, 0.05),
        Slider("groundcover.distance", "View Distance", "Groundcover distance",
            "How far away grass is drawn.",
            Cfg("Groundcover", "rendering distance", ValueFormat.Int), 1024, 12288, 512),

        // Shadows
        Toggle("shadows.enable", "Shadows", "Enable shadows",
            "Master toggle for dynamic shadows.",
            Cfg("Shadows", "enable shadows", ValueFormat.BoolTrueFalse)),
        Dropdown("shadows.resolution", "Shadows", "Shadow resolution",
            "Shadow map size. Higher is sharper but costs GPU.",
            Cfg("Shadows", "shadow map resolution"),
            Opt("1024", "1024"), Opt("2048", "2048"), Opt("4096", "4096")),
        Slider("shadows.distance", "Shadows", "Shadow distance",
            "Distance at which shadows fade out. 0 = infinite.",
            Cfg("Shadows", "maximum shadow map distance", ValueFormat.Int), 0, 16384, 512),
        Toggle("shadows.actor", "Shadows", "Actor shadows",
            "Allow NPCs and creatures to cast shadows.",
            Cfg("Shadows", "actor shadows", ValueFormat.BoolTrueFalse)),
        Toggle("shadows.player", "Shadows", "Player shadows",
            "Allow the player to cast a shadow.",
            Cfg("Shadows", "player shadows", ValueFormat.BoolTrueFalse)),
        Toggle("shadows.object", "Shadows", "Object shadows",
            "Allow world objects to cast shadows.",
            Cfg("Shadows", "object shadows", ValueFormat.BoolTrueFalse)),
        Toggle("shadows.terrain", "Shadows", "Terrain shadows",
            "Allow terrain to cast shadows.",
            Cfg("Shadows", "terrain shadows", ValueFormat.BoolTrueFalse)),

        // Interface
        Slider(UiScaleIdValue, "Interface", "UI scale",
            "Scales the interface and HUD.",
            Cfg("GUI", "scaling factor", ValueFormat.Float1), 0.5, 2.0, 0.05),
        Slider("gui.fontSize", "Interface", "Font size",
            "Size of in-game text.",
            Cfg("GUI", "font size", ValueFormat.Int), 12, 26, 1),
        Slider("gui.menuTransparency", "Interface", "Menu transparency",
            "Menu window opacity (0 = clear, 1 = solid).",
            Cfg("GUI", "menu transparency", ValueFormat.Float1), 0, 1, 0.05),
        Toggle("hud.crosshair", "Interface", "Crosshair",
            "Show the aiming crosshair.",
            Cfg("HUD", "crosshair", ValueFormat.BoolTrueFalse)),
        Toggle("gui.subtitles", "Interface", "Subtitles",
            "Show subtitles for spoken dialogue.",
            Cfg("GUI", "subtitles", ValueFormat.BoolTrueFalse)),

        // Gameplay
        Slider("game.difficulty", "Gameplay", "Difficulty",
            "Damage dealt vs. received. Negative is easier.",
            Cfg("Game", "difficulty", ValueFormat.Int), -100, 100, 5),
        Dropdown("game.showOwned", "Gameplay", "Highlight owned items",
            "Colour the crosshair/tooltip when an item is owned by an NPC.",
            Cfg("Game", "show owned"),
            Opt("Off", "0"), Opt("Tooltip", "1"), Opt("Crosshair", "2"), Opt("Both", "3")),
        Toggle("game.bestAttack", "Gameplay", "Always best attack",
            "Always use the strongest attack type for the weapon.",
            Cfg("Game", "best attack", ValueFormat.BoolTrueFalse)),

        // Audio
        Slider("sound.master", "Audio", "Master volume",
            "Overall volume.",
            Cfg("Sound", "master volume", ValueFormat.Float1), 0, 1, 0.05),
        Slider("sound.music", "Audio", "Music volume",
            "Music track volume.",
            Cfg("Sound", "music volume", ValueFormat.Float1), 0, 1, 0.05),
        Slider("sound.sfx", "Audio", "Effects volume",
            "Sound-effects volume.",
            Cfg("Sound", "sfx volume", ValueFormat.Float1), 0, 1, 0.05),
        Slider("sound.voice", "Audio", "Voice volume",
            "Spoken dialog volume.",
            Cfg("Sound", "voice volume", ValueFormat.Float1), 0, 1, 0.05),
        Slider("sound.footsteps", "Audio", "Footsteps volume",
            "Footstep sound volume.",
            Cfg("Sound", "footsteps volume", ValueFormat.Float1), 0, 1, 0.05),

        // Controls
        Slider("input.sensitivity", "Controls", "Mouse sensitivity",
            "Camera look sensitivity.",
            Cfg("Input", "camera sensitivity", ValueFormat.Float1), 0.1, 5.0, 0.05),
        Toggle("input.invertY", "Controls", "Invert Y axis",
            "Invert the vertical look axis.",
            Cfg("Input", "invert y axis", ValueFormat.BoolTrueFalse)),
        Toggle("input.invertX", "Controls", "Invert X axis",
            "Invert the horizontal look axis.",
            Cfg("Input", "invert x axis", ValueFormat.BoolTrueFalse)),
        Toggle("input.controller", "Controls", "Controller support",
            "Enable gamepad/controller input.",
            Cfg("Input", "enable controller", ValueFormat.BoolTrueFalse)),
    };

    // ------------------------------------------------------------------- MWSE
    // Safe subset only. Never touches Distant Land generation params, the
    // Distant Land on/off toggle, [Shader Chain], or [DLWizard Settings] — those
    // are owned by the MGE XE GUI tool. Bool formats match each key's existing
    // convention in MGE.ini (some keys use On/Off, others True/False).

    private static readonly IReadOnlyList<SettingDescriptor> Mwse = new[]
    {
        // Display
        Dropdown(ResolutionIdValue, "Display", "Resolution",
            "Screen resolution.",
            Reg(ScreenField.Width)),
        Dropdown(RefreshIdValue, "Display", "Refresh rate",
            "Monitor refresh rate, in Hz.",
            Reg(ScreenField.Refresh)),
        Dropdown("mge.vwait", "Display", "V-Sync",
            "Vertical sync. Reduces screen tearing.",
            Mge("Global Graphics", "VWait"),
            Opt("Off", "Immediate"), Opt("On", "1"),
            Opt("On (½ rate)", "2"), Opt("On (⅓ rate)", "3"), Opt("On (¼ rate)", "4")),
        Toggle("mge.borderless", "Display", "Borderless window",
            "Run in a borderless window instead of exclusive fullscreen.",
            Mge("Global Graphics", "Borderless Window", ValueFormat.BoolTrueFalse)),
        Number("mw.maxFps", "Display", "FPS limit",
            "Maximum frames per second. 0 = unlimited.",
            MwIni("General", "Max FPS", ValueFormat.Int), 0, 1000),

        // Graphics
        Dropdown("mge.antialiasing", "Graphics", "Anti-aliasing",
            "Smooths jagged edges (MSAA).",
            Mge("Global Graphics", "Antialiasing Level"),
            Opt("Off", "None"), Opt("2x", "2x"), Opt("4x", "4x"), Opt("8x", "8x")),
        Dropdown("mge.anisotropic", "Graphics", "Anisotropic filtering",
            "Sharpens textures viewed at steep angles.",
            Mge("Render State", "Anisotropic Filtering Level"),
            Opt("Off", "Off"), Opt("2x", "2x"), Opt("4x", "4x"), Opt("8x", "8x"), Opt("16x", "16x")),
        Toggle("mge.alphaAa", "Graphics", "Smooth alpha edges",
            "Anti-alias cutout edges on foliage and fences. Requires anti-aliasing.",
            Mge("Render State", "Transparency Antialiasing", ValueFormat.BoolTrueFalse)),
        Dropdown("mge.fogMode", "Graphics", "Fog mode",
            "How distance fog is calculated. 'Range (vertex)' is the default.",
            Mge("Render State", "Fog Mode"),
            Opt("Range (vertex)", "Range vertex"), Opt("Depth (vertex)", "Depth vertex"),
            Opt("Depth (pixel)", "Depth pixel")),
        Slider("mge.fov", "Graphics", "Field of view",
            "Field of view, in degrees.",
            Mge("Render State", "Horizontal Screen FOV", ValueFormat.Float1), 70, 120, 1),
        Toggle("mge.fpsCounter", "Graphics", "FPS counter",
            "Show an on-screen FPS counter.",
            Mge("Render State", "MGE FPS Counter", ValueFormat.BoolTrueFalse)),

        // View Distance
        Slider("mge.drawDistance", "View Distance", "View distance",
            "How far distant land is drawn, in cells (5 = recommended).",
            Mge("Distant Land", "Draw Distance", ValueFormat.Float1), 1, 10, 0.5),

        // Interface
        Slider(UiScaleIdValue, "Interface", "UI scale",
            "Scales the interface and HUD.",
            Mge("Render State", "UI Scaling", ValueFormat.Float1), 0.5, 2.0, 0.05),
        Toggle("mge.crosshairAutohide", "Interface", "Auto-hide crosshair",
            "Hide the crosshair unless it's over something interactive.",
            Mge("Misc", "Crosshair Autohide", ValueFormat.BoolTrueFalse)),
        Toggle("mw.subtitles", "Interface", "Subtitles",
            "Show subtitles for spoken dialogue.",
            MwIni("General", "Subtitles", ValueFormat.BoolOneZero)),

        // Gameplay (MGE [Misc])
        Toggle("mge.skipIntro", "Gameplay", "Skip intro movies",
            "Skip the studio logos and intro video on startup.",
            Mge("Misc", "Skip Intro Movies", ValueFormat.BoolTrueFalse)),

        // NOTE: MWSE intentionally omits Audio volumes, Difficulty, Shadows and Gamma:
        //  - vanilla Morrowind stores master/music/effect/voice volume and difficulty in an
        //    undocumented registry binary blob, not a safe text key;
        //  - MGE's shadow params live in the generation-managed [Distant Land] section (which
        //    the MGE XE tool owns, so we don't touch it), and the MGE core has no gamma key.
        // These appear only under OpenMW by design.
    };
}
