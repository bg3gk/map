using System.Globalization;

namespace GateKit.Site;

/// <summary>What a value is, which decides how both renderers show it.</summary>
public enum ValueKind
{
    /// <summary>Ordinary prose or a plain string.</summary>
    Text,

    /// <summary>A number.</summary>
    Number,

    /// <summary>Something that names an object but has no page here.</summary>
    Identifier,

    /// <summary>Something that names an object that does have a page.</summary>
    Link,

    /// <summary>A file path inside the game's packages.</summary>
    Path,

    /// <summary>Nothing at all.</summary>
    Missing,
}

/// <summary>
/// One value on a page. The two renderers read exactly this, so an HTML page and its
/// <c>.json</c> twin cannot say different things.
/// </summary>
public sealed record SiteValue
{
    public required ValueKind Kind { get; init; }

    /// <summary>What is shown: the identifier itself, the number, the sentence.</summary>
    public required string Text { get; init; }

    /// <summary>The kind of the object this names, when it names one.</summary>
    public string? RefKind { get; init; }

    /// <summary>The id of the object this names, in the form its URL uses.</summary>
    public string? RefId { get; init; }

    /// <summary>The route of the page it names, without a leading slash: <c>template/&lt;uuid&gt;</c>.</summary>
    public string? Route { get; init; }

    /// <summary>A few words that say what the identifier is, shown beside it.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// For a line of the game's text: what it says in every language other than English, by
    /// language. English is the value's own text or note; a reader picks which other one to see.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Texts { get; init; }
}

/// <summary>Helpers that make the values of a page.</summary>
public static class V
{
    public static readonly SiteValue None = new() { Kind = ValueKind.Missing, Text = "" };

    public static SiteValue Text(string? text) =>
        string.IsNullOrEmpty(text) ? None : new SiteValue { Kind = ValueKind.Text, Text = text };

    public static SiteValue Num(long? value) =>
        value is null ? None : new SiteValue { Kind = ValueKind.Number, Text = value.Value.ToString(CultureInfo.InvariantCulture) };

    public static SiteValue Num(double? value, int decimals = 2) =>
        value is null ? None : new SiteValue
        {
            Kind = ValueKind.Number,
            Text = value.Value.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture),
        };

    public static SiteValue Id(string? text, string? note = null) =>
        string.IsNullOrEmpty(text) ? None : new SiteValue { Kind = ValueKind.Identifier, Text = text, Note = note };

    public static SiteValue Path(string? text) =>
        string.IsNullOrEmpty(text) ? None : new SiteValue { Kind = ValueKind.Path, Text = text };

    public static SiteValue Link(string kind, string id, string route, string text, string? note = null) =>
        new() { Kind = ValueKind.Link, Text = text, RefKind = kind, RefId = id, Route = route, Note = note };

    public static SiteValue Flag(bool? on) => on is null ? None : Text(on.Value ? "yes" : "no");
}

/// <summary>A named value in the head of a page.</summary>
/// <remarks>
/// <paramref name="Spec"/> is the name a reader sees, and may carry a longer explanation after a
/// <c>|</c>: <c>"status|how far the mapping got"</c>. The name is kept short so that a page reads
/// like a record; the explanation is a tooltip, and travels in the JSON's <c>hints</c>.
/// </remarks>
public sealed record SiteField(string Spec, SiteValue Value)
{
    public string Name => Hints.NameOf(Spec);

    public string? Hint => Hints.HintOf(Spec);
}

/// <summary>Splits a name written as <c>name|explanation</c>.</summary>
public static class Hints
{
    /// <summary>The longest name a column or a field may have; a test holds every kind to it.</summary>
    public const int MaxName = 14;

    public static string NameOf(string spec)
    {
        var cut = spec.IndexOf('|', StringComparison.Ordinal);
        return cut < 0 ? spec : spec[..cut];
    }

    public static string? HintOf(string spec)
    {
        var cut = spec.IndexOf('|', StringComparison.Ordinal);
        return cut < 0 ? null : spec[(cut + 1)..];
    }
}

/// <summary>A table on a page: what it lists, its columns, its rows, and what was left out.</summary>
public sealed record SiteSection(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<SiteValue>> Rows)
{
    /// <summary>The names of the columns, without the explanations some of them carry after a <c>|</c>.</summary>
    public IEnumerable<string> Names => Columns.Select(Hints.NameOf);

    /// <summary>The explanations of the columns that have one, by name.</summary>
    public IEnumerable<KeyValuePair<string, string>> ColumnHints =>
        Columns.Where(c => Hints.HintOf(c) != null).Select(c => new KeyValuePair<string, string>(Hints.NameOf(c), Hints.HintOf(c)!));

    /// <summary>How many rows there were in all, when the table shows only the first of them.</summary>
    public int Total { get; init; }

    /// <summary>A sentence shown under the table.</summary>
    public string? Note { get; init; }
}

/// <summary>A whole page, before it is written as HTML or as JSON.</summary>
public sealed record PageDoc
{
    public required string Kind { get; init; }

    public required string Id { get; init; }

    /// <summary>The site path of this page, without the base prefix: <c>dialog/Foo</c>.</summary>
    public required string Route { get; init; }

    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    public IReadOnlyList<SiteField> Fields { get; init; } = [];

    public IReadOnlyList<SiteSection> Sections { get; init; } = [];

    /// <summary>Every distinct object this page names and can be followed to, in the order it appears.</summary>
    public IReadOnlyList<SiteValue> Links()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<SiteValue>();
        void Take(SiteValue value)
        {
            if (value.Kind == ValueKind.Link && value.Route != null && seen.Add(value.Route))
            {
                links.Add(value);
            }
        }
        foreach (var field in Fields)
        {
            Take(field.Value);
        }
        foreach (var row in Sections.SelectMany(s => s.Rows).SelectMany(r => r))
        {
            Take(row);
        }
        return links;
    }
}
