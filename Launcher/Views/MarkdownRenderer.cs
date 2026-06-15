using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using MorrowindRemasteredLauncher.Services;

namespace MorrowindRemasteredLauncher.Views;

/// <summary>
/// A tiny, dependency-free Markdown → WPF renderer for the About page. Emits the
/// same styled <see cref="TextBlock"/>s the other panels use (theme fonts/brushes,
/// no backgrounds), so the rendered doc reads as part of the parchment page.
///
/// Supported subset: <c>#/##/###</c> headings, blank-line-separated paragraphs,
/// <c>-</c>/<c>*</c> bullets, <c>---</c> rules, GitHub-style pipe tables
/// (<c>| a | b |</c> with a <c>| --- | :--: |</c> alignment row), and inline
/// <c>**bold**</c>, <c>*italic*</c>, <c>`code`</c> and <c>[text](url)</c> (links
/// open externally).
/// </summary>
public static class MarkdownRenderer
{
    private static readonly Regex InlineRegex = new(
        @"(?<link>\[(?<ltext>[^\]]+)\]\((?<lurl>[^)]+)\))" +
        @"|(?<code>`(?<ctext>[^`]+)`)" +
        @"|(?<bold>\*\*(?<btext>.+?)\*\*)" +
        @"|(?<italic>\*(?<itext>.+?)\*)",
        RegexOptions.Compiled);

    /// <summary>
    /// Renders <paramref name="markdown"/> into <paramref name="host"/>. When
    /// <paramref name="colorStatusMarks"/> is set, check (U+2713) and cross (U+2717)
    /// glyphs are tinted green/red — used by the Mods page for modlist.md, off elsewhere.
    /// </summary>
    public static void Render(string markdown, Panel host, bool colorStatusMarks = false)
    {
        host.Children.Clear();
        foreach (var element in Render(markdown, colorStatusMarks))
        {
            host.Children.Add(element);
        }
    }

    public static IEnumerable<UIElement> Render(string markdown, bool colorStatusMarks = false)
    {
        var elements = new List<UIElement>();
        var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                elements.Add(Paragraph(string.Join(" ", paragraph), colorStatusMarks));
                paragraph.Clear();
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            // Table: a header row immediately followed by a |---|:--:| alignment row.
            // The whole block (header + alignment + data rows) is consumed here.
            if (IsTableRow(line) && i + 1 < lines.Length && IsTableDelimiter(lines[i + 1].Trim()))
            {
                FlushParagraph();
                var alignments = ParseAlignments(lines[i + 1].Trim());
                var rows = new List<string> { line };
                var j = i + 2;
                while (j < lines.Length && IsTableRow(lines[j].Trim()))
                {
                    rows.Add(lines[j].Trim());
                    j++;
                }
                elements.Add(Table(rows, alignments, colorStatusMarks));
                i = j - 1;
                continue;
            }

            if (line.Length == 0)
            {
                FlushParagraph();
            }
            else if (line is "---" or "***" or "___")
            {
                FlushParagraph();
                elements.Add(HorizontalRule());
            }
            else if (line.StartsWith("### "))
            {
                FlushParagraph();
                elements.Add(Heading(line[4..].Trim(), 3, colorStatusMarks));
            }
            else if (line.StartsWith("## "))
            {
                FlushParagraph();
                elements.Add(Heading(line[3..].Trim(), 2, colorStatusMarks));
            }
            else if (line.StartsWith("# "))
            {
                FlushParagraph();
                elements.Add(Heading(line[2..].Trim(), 1, colorStatusMarks));
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                FlushParagraph();
                elements.Add(Bullet(line[2..].Trim(), colorStatusMarks));
            }
            else
            {
                paragraph.Add(line);
            }
        }
        FlushParagraph();
        return elements;
    }

    // ------------------------------------------------------------- block builders

    private static TextBlock Heading(string text, int level, bool colorStatusMarks)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        switch (level)
        {
            case 1:
                tb.Style = Res<Style>("HeaderText");
                tb.Margin = new Thickness(0, 0, 0, 8);
                break;
            case 2:
                tb.FontFamily = Res<FontFamily>("DisplayFont");
                tb.FontSize = 17;
                tb.FontWeight = FontWeights.SemiBold;
                tb.Foreground = Res<Brush>("AccentInkBrush");
                tb.Margin = new Thickness(0, 16, 0, 4);
                break;
            default:
                tb.FontFamily = Res<FontFamily>("BodyFont");
                tb.FontSize = 14;
                tb.FontWeight = FontWeights.SemiBold;
                tb.Foreground = Res<Brush>("TextBrush");
                tb.Margin = new Thickness(0, 12, 0, 2);
                break;
        }
        AddInlines(tb, text, colorStatusMarks);
        return tb;
    }

    private static TextBlock Paragraph(string text, bool colorStatusMarks)
    {
        var tb = new TextBlock
        {
            FontFamily = Res<FontFamily>("BodyFont"),
            FontSize = 14,
            Foreground = Res<Brush>("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        AddInlines(tb, text, colorStatusMarks);
        return tb;
    }

    private static UIElement Bullet(string text, bool colorStatusMarks)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new TextBlock
        {
            Text = "•",
            FontFamily = Res<FontFamily>("BodyFont"),
            Foreground = Res<Brush>("AccentInkBrush"),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var body = new TextBlock
        {
            FontFamily = Res<FontFamily>("BodyFont"),
            FontSize = 14,
            Foreground = Res<Brush>("TextBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        AddInlines(body, text, colorStatusMarks);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private static UIElement HorizontalRule() => new Border
    {
        Height = 1,
        Background = Res<Brush>("BorderBrush"),
        Margin = new Thickness(0, 12, 0, 12),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    // -------------------------------------------------------------- table blocks

    /// <summary>A row that is part of a pipe table (contains at least one <c>|</c>).</summary>
    private static bool IsTableRow(string line) => line.Contains('|');

    /// <summary>A GFM alignment row: cells of only dashes/colons, e.g. <c>| --- | :--: |</c>.</summary>
    private static bool IsTableDelimiter(string line)
    {
        if (!line.Contains('|') || !line.Contains('-'))
        {
            return false;
        }
        var cells = SplitRow(line);
        return cells.Count > 0 &&
               cells.All(c => c.Length > 0 && c.Contains('-') && c.All(ch => ch is '-' or ':'));
    }

    /// <summary>Per-column text alignment read from the <c>:---:</c> markers.</summary>
    private static List<TextAlignment> ParseAlignments(string delimiterLine) =>
        SplitRow(delimiterLine).Select(c =>
        {
            var left = c.StartsWith(':');
            var right = c.EndsWith(':');
            return left && right ? TextAlignment.Center
                : right ? TextAlignment.Right
                : TextAlignment.Left;
        }).ToList();

    /// <summary>Splits a <c>| a | b | c |</c> row into trimmed cell strings.</summary>
    private static List<string> SplitRow(string line)
    {
        var s = line.Trim();
        if (s.StartsWith('|'))
        {
            s = s[1..];
        }
        if (s.EndsWith('|'))
        {
            s = s[..^1];
        }
        return s.Split('|').Select(c => c.Trim()).ToList();
    }

    /// <summary>
    /// Renders a pipe table as a <see cref="Grid"/>: the first (label) column flexes
    /// and wraps, the rest size to content. Header row is emphasised; a faint rule
    /// separates each row. Cells render inline markdown (links, bold, etc.).
    /// </summary>
    private static UIElement Table(IReadOnlyList<string> rows, IReadOnlyList<TextAlignment> alignments,
        bool colorStatusMarks)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 12) };
        var colCount = SplitRow(rows[0]).Count;
        for (var c = 0; c < colCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = c == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
            });
        }

        var borderBrush = Res<Brush>("BorderBrush");
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var isHeader = r == 0;
            var cells = SplitRow(rows[r]);

            // Full-width underline: solid under the header, faint between data rows.
            var rule = new Border
            {
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(0, 0, 0, isHeader ? 1.2 : 0.6),
                Opacity = isHeader ? 1.0 : 0.45
            };
            Grid.SetRow(rule, r);
            Grid.SetColumn(rule, 0);
            Grid.SetColumnSpan(rule, colCount);
            grid.Children.Add(rule);

            for (var c = 0; c < colCount; c++)
            {
                var tb = new TextBlock
                {
                    FontFamily = Res<FontFamily>("BodyFont"),
                    FontSize = 13,
                    Foreground = Res<Brush>(isHeader ? "AccentInkBrush" : "TextBrush"),
                    FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = c < alignments.Count ? alignments[c] : TextAlignment.Left,
                    Margin = new Thickness(c == 0 ? 0 : 14, 4, 8, 4)
                };
                AddInlines(tb, c < cells.Count ? cells[c] : "", colorStatusMarks);
                Grid.SetRow(tb, r);
                Grid.SetColumn(tb, c);
                grid.Children.Add(tb);
            }
        }
        return grid;
    }

    // ------------------------------------------------------------ inline builders

    private static void AddInlines(TextBlock tb, string text, bool colorStatusMarks)
    {
        var pos = 0;
        foreach (Match m in InlineRegex.Matches(text))
        {
            if (m.Index > pos)
            {
                AddText(tb, text[pos..m.Index], colorStatusMarks);
            }
            if (m.Groups["link"].Success)
            {
                tb.Inlines.Add(Link(m.Groups["ltext"].Value, m.Groups["lurl"].Value));
            }
            else if (m.Groups["code"].Success)
            {
                tb.Inlines.Add(new Run(m.Groups["ctext"].Value)
                {
                    FontFamily = new FontFamily("Consolas, Courier New"),
                    Background = Res<Brush>("PanelAltBrush")
                });
            }
            else if (m.Groups["bold"].Success)
            {
                tb.Inlines.Add(new Bold(new Run(m.Groups["btext"].Value)));
            }
            else if (m.Groups["italic"].Success)
            {
                tb.Inlines.Add(new Italic(new Run(m.Groups["itext"].Value)));
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
        {
            AddText(tb, text[pos..], colorStatusMarks);
        }
    }

    /// <summary>
    /// Appends plain (non-inline-markdown) text. With <paramref name="colorStatusMarks"/>
    /// on, each check (U+2713) / cross (U+2717) glyph is split into its own coloured run
    /// (green/red); otherwise the text is added as a single run.
    /// </summary>
    private static void AddText(TextBlock tb, string text, bool colorStatusMarks)
    {
        if (!colorStatusMarks || (!text.Contains('✓') && !text.Contains('✗')))
        {
            tb.Inlines.Add(new Run(text));
            return;
        }

        var start = 0;
        for (var k = 0; k < text.Length; k++)
        {
            var brushKey = text[k] switch
            {
                '✓' => "OkBrush",      // check mark → dark green
                '✗' => "DangerBrush",  // ballot X  → dark red
                _ => null
            };
            if (brushKey is null)
            {
                continue;
            }
            if (k > start)
            {
                tb.Inlines.Add(new Run(text[start..k]));
            }
            tb.Inlines.Add(new Run(text[k].ToString()) { Foreground = Res<Brush>(brushKey) });
            start = k + 1;
        }
        if (start < text.Length)
        {
            tb.Inlines.Add(new Run(text[start..]));
        }
    }

    private static Inline Link(string text, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new Run(text);
        }
        var link = new Hyperlink(new Run(text))
        {
            NavigateUri = uri,
            Foreground = Res<Brush>("AccentInkBrush"),
            // Hyperlink underlines by default; null (not omission) clears it. The accent
            // colour alone marks the link.
            TextDecorations = null,
            ToolTip = url
        };
        link.RequestNavigate += OnRequestNavigate;
        return link;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Couldn't open link {e.Uri}: {ex.Message}");
        }
        e.Handled = true;
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);
}
