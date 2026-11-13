using System.Text;

namespace GateKit.Site;

/// <summary>
/// The little HTML the builder still writes itself: the shell. An object's page is not written as
/// HTML at all. The server answers every page's address with the shell, whose script fetches the
/// page's <c>.json</c> and draws it - so the page a person reads and the JSON an agent fetches are
/// not two renderings that might drift, they are one file.
/// </summary>
public static class HtmlWriter
{
    /// <summary>What the site calls itself, in the title bar and in the corner of every page.</summary>
    public const string SiteName = "GateKit Map";

    public static string Escape(string text)
    {
        var needed = false;
        foreach (var c in text)
        {
            if (c is '&' or '<' or '>' or '"')
            {
                needed = true;
                break;
            }
        }
        if (!needed)
        {
            return text;
        }
        var output = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            _ = c switch
            {
                '&' => output.Append("&amp;"),
                '<' => output.Append("&lt;"),
                '>' => output.Append("&gt;"),
                '"' => output.Append("&quot;"),
                _ => output.Append(c),
            };
        }
        return output.ToString();
    }
}

/// <summary>
/// The two sentences on every page of one kind: where the rest of that data is, and under what
/// promise it is shown. They belong to the kind, not to the site, because the site carries maps
/// that are not the game's own text — the engine's symbols and our own component map — and those
/// are ours, published under a different sentence and fetched with another command. The shell
/// carries both, and the client picks one by the <c>origin</c> the page's JSON names.
/// </summary>
public sealed record SiteNotice
{
    /// <summary>The repository the maps are published from, and where the promise lives.</summary>
    public const string Repository = "https://github.com/bg3gk/map";

    /// <summary>
    /// Whose material a page is: <c>game</c> for Larian's, under the withdrawal promise, and
    /// <c>ours</c> for what we measured. The JSON of every page says it.
    /// </summary>
    public required string Origin { get; init; }

    /// <summary>What a reader runs to get everything this page leaves out.</summary>
    public required string Download { get; init; }

    /// <summary>What is not on the page, as HTML.</summary>
    public required string MissingHtml { get; init; }

    /// <summary>
    /// The one line of the footer that changes from page to page: whose material this page is, as
    /// HTML. The rest of the footer - what the site is, the promise, where it came from - is the
    /// same everywhere and lives in the shell.
    /// </summary>
    public required string WhoseHtml { get; init; }

    /// <summary>Every notice there is, for the shell.</summary>
    public static IReadOnlyList<SiteNotice> All => [GameContent, OurFindings];

    /// <summary>For a page made of the game's own content, which is Larian's and goes when asked.</summary>
    public static readonly SiteNotice GameContent = new()
    {
        Origin = "game",
        Download = "map fetch",
        MissingHtml = "Every database this site is built from is one download: <code class=pt>map fetch</code>.",
        WhoseHtml = "Game content © Larian Studios.",
    };

    /// <summary>
    /// For a page made of what we measured about the engine rather than of anything Larian ships:
    /// the component map and the facts about the binaries.
    /// </summary>
    public static readonly SiteNotice OurFindings = new()
    {
        Origin = "ours",
        Download = "map fetch",
        MissingHtml = "Every database this site is built from is one download: <code class=pt>map fetch</code>.",
        WhoseHtml = "This page is our own research, not game content.",
    };
}
