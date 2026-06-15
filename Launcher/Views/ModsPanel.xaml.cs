using System.IO;
using System.Windows.Controls;
using MorrowindRemasteredLauncher.Services;
using MorrowindRemasteredLauncher.ViewModels;

namespace MorrowindRemasteredLauncher.Views;

/// <summary>
/// The optional Mods page: renders the installed list's <c>modlist.md</c> from the
/// single shared MO2 folder (path supplied by <see cref="ShellViewModel.ModsMarkdownPath"/>)
/// with the app's fonts/styling on a transparent background (see <see cref="MarkdownRenderer"/>).
/// Re-read each time the page is shown so it picks up the file once an install creates
/// it; the nav item is disabled when no such file exists.
/// </summary>
public partial class ModsPanel : UserControl
{
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
