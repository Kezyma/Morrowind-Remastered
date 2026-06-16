using System.IO;
using System.Windows.Controls;
using MorrowindRemasteredLauncher.Services;

namespace MorrowindRemasteredLauncher.Views;

/// <summary>The About page; renders the embedded <c>about.md</c> with the app's fonts/styling.</summary>
public partial class AboutPanel : UserControl
{
    /// <summary>Initializes the panel and renders the About content.</summary>
    public AboutPanel()
    {
        InitializeComponent();
        LoadAbout();
    }

    /// <summary>Reads and renders the embedded about.md, showing a fallback if missing.</summary>
    private void LoadAbout()
    {
        try
        {
            var markdown = ReadEmbedded("about.md");
            if (!string.IsNullOrWhiteSpace(markdown))
            {
                MarkdownRenderer.Render(markdown, ContentHost);
            }
            else
            {
                ContentHost.Children.Add(new TextBlock { Text = "About content is unavailable." });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't render the About page: {ex.Message}");
        }
    }

    /// <summary>Reads an embedded resource as text, or null if it isn't present.</summary>
    private static string? ReadEmbedded(string name)
    {
        using var stream = typeof(AboutPanel).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
