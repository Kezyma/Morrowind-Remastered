using System.IO;
using System.Windows.Controls;
using MorrowindRemasteredLauncher.Services;
using MorrowindRemasteredLauncher.ViewModels;

namespace MorrowindRemasteredLauncher.Views;

/// <summary>The optional Mods page; renders the installed list's <c>modlist.md</c> with the app's styling.</summary>
/// <remarks>
/// Path comes from <see cref="ShellViewModel.ModsMarkdownPath"/> (the single shared MO2 folder).
/// Re-read each time the page is shown so it picks up the file once an install creates it; the
/// nav item is disabled when no such file exists.
/// </remarks>
public partial class ModsPanel : UserControl
{
    /// <summary>Initializes the panel and re-renders the mod list whenever it becomes visible.</summary>
    public ModsPanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                LoadMods();
            }
        };
    }

    /// <summary>Reads and renders the current modlist.md, showing a fallback if absent.</summary>
    private void LoadMods()
    {
        try
        {
            ContentHost.Children.Clear();
            var path = (DataContext as ShellViewModel)?.ModsMarkdownPath;
            var markdown = path is not null && File.Exists(path)
                ? File.ReadAllText(path)
                : null;

            if (!string.IsNullOrWhiteSpace(markdown))
            {
                MarkdownRenderer.Render(markdown, ContentHost, colorStatusMarks: true);
            }
            else
            {
                ContentHost.Children.Add(new TextBlock { Text = "No mod list is available for this install." });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't render the Mods page: {ex.Message}");
        }
    }
}
