using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Site;

/// <summary>A page the search found.</summary>
public sealed record PageHit(string Kind, string Route, string Name, string? Note);

/// <summary>A line of the game's text the search found; <see cref="Text"/> marks what matched with <c>&lt;em&gt;</c>.</summary>
public sealed record LineHit(string Handle, string Lang, string Text);

/// <summary>An object of the game the search found by its name; <see cref="Route"/> is set when it has a page.</summary>
public sealed record NameHit(string Name, string? Node, string? Key, string? Route);

/// <summary>
/// What one search found. <see cref="Exact"/> is set when what was typed is the id of exactly one
/// page, and the client then goes straight to it.
/// </summary>
public sealed record SearchAnswer(string? Exact, IReadOnlyList<PageHit> Pages, IReadOnlyList<LineHit> Lines, IReadOnlyList<NameHit> Names);

/// <summary>Answers searches from <see cref="SearchDatabase"/>; safe to use from many requests at once.</summary>
public sealed partial class SiteSearch
{
    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotAWord();

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}|\p{IsHiragana}|\p{IsKatakana}|\p{IsHangulSyllables}")]
    private static partial Regex Unspaced();

    [GeneratedRegex(@"^[A-Za-z0-9_.:-]+$")]
    private static partial Regex IdShape();

    /// <summary><c>bg3.exe+0x77F580</c>: an address relative to a binary.</summary>
    [GeneratedRegex(@"^(?<binary>[A-Za-z0-9_.-]+\.(?:exe|dll))\s*\+\s*(?:0x)?(?<hex>[0-9A-Fa-f]+)$")]
    private static partial Regex RelativeShape();

    /// <summary><c>0x14077F580</c> or <c>FUN_14077f580</c>: an absolute address, as a decompiler shows it.</summary>
    [GeneratedRegex(@"^(?:0x|FUN_|LAB_|DAT_|sub_)?(?<hex>1[48][0-9A-Fa-f]{7,8})$")]
    private static partial Regex AbsoluteShape();

    private readonly string _connection;

    public SiteSearch(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{path} is not a search database; render the site with `map build`", path);
        }
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = true }.ToString();
    }

    /// <summary>
    /// One full-text query: every word a prefix, every word quoted so punctuation cannot break it,
    /// and the words joined by AND rather than by a space, which would make them one phrase.
    /// </summary>
    internal static string? Match(string text)
    {
        var words = NotAWord().Split(text).Where(w => w.Length > 0).ToArray();
        return words.Length == 0 ? null : string.Join(" AND ", words.Select(w => "\"" + w.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*"));
    }

    /// <summary>
    /// Searches everything. <paramref name="lang"/> narrows the lines to one language; without it
    /// lines of every language are found.
    /// </summary>
    public SearchAnswer Find(string text, string? lang = null)
    {
        text = text.Trim();
        using var db = new SqliteConnection(_connection);
        db.Open();

        if (SiteIndex.IsHandle(text) && Rows(db, "SELECT 1 FROM loca WHERE handle = $1 UNION ALL SELECT 1 FROM loca_cjk WHERE handle = $1 LIMIT 1", text).Count > 0)
        {
            return new SearchAnswer("loca/" + text.ToLowerInvariant(), [], [], []);
        }
        if (At(db, text) is { } places)
        {
            return places.Count == 1 && places[0].Note == null
                ? new SearchAnswer(places[0].Route, [], [], [])
                : new SearchAnswer(null, places, [], []);
        }
        // Straight to a page only for what reads as an id - a uuid, or a name with `_`, `.`, `::`
        // or a digit in it. A plain word such as "Astarion" is also the id of some page, and a
        // reader who types it wants everything it names, not that one page.
        if (SiteIndex.IsUuid(text) || IdShape().IsMatch(text) && text.IndexOfAny(['_', '.', ':', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9']) >= 0)
        {
            // An object of the game has its raw page as well as its page among the content; the
            // id leads to the second, which links to the first.
            var exact = Rows(db, "SELECT route, kind FROM obj WHERE ident IN ($1, $2) LIMIT 4", text, text.Replace("::", ".", StringComparison.Ordinal));
            if (exact.Count > 1)
            {
                exact = [.. exact.Where(r => (string?)r[1] != "object")];
            }
            if (exact.Count == 1)
            {
                return new SearchAnswer((string)exact[0][0]!, [], [], []);
            }
        }

        var match = Match(text);
        if (match == null)
        {
            return new SearchAnswer(null, [], [], []);
        }
        var pages = Rows(db, "SELECT o.kind, o.route, o.name, o.note FROM obj_fts f JOIN obj o ON o.id = f.rowid WHERE obj_fts MATCH $1 ORDER BY rank LIMIT 40", match)
            .Select(r => new PageHit((string)r[0]!, (string)r[1]!, (string)r[2]!, r[3] as string))
            .ToList();

        var lines = new List<LineHit>();
        var unspacedLanguage = lang != null && SearchDatabase.Unspaced.Contains(lang, StringComparer.Ordinal);
        if (lang == null || !unspacedLanguage)
        {
            lines.AddRange(Rows(db,
                    "SELECT l.handle, l.lang, snippet(loca_fts, 0, '<em>', '</em>', '…', 14) FROM loca_fts f JOIN loca l ON l.id = f.rowid "
                    + "WHERE loca_fts MATCH $1 AND ($2 IS NULL OR l.lang = $2) ORDER BY rank LIMIT 30", match, lang)
                .Select(r => new LineHit((string)r[0]!, (string)r[1]!, (string)r[2]!)));
        }
        if (unspacedLanguage || lang == null && Unspaced().IsMatch(text))
        {
            lines.InsertRange(0, UnspacedLines(db, text, lang));
        }

        var names = Rows(db, "SELECT g.name, n.name, g.key, o.route FROM gname_fts f JOIN gname g ON g.id = f.rowid "
                + "LEFT JOIN gnode n ON n.id = g.node LEFT JOIN obj o ON o.ident = g.key "
                + "WHERE gname_fts MATCH $1 ORDER BY rank LIMIT 20", match)
            .Select(r => new NameHit((string)r[0]!, r[1] as string, r[2] as string, r[3] as string))
            .ToList();
        return new SearchAnswer(null, pages, lines, names);
    }

    /// <summary>
    /// The functions an address is in, when what was asked is an address: one, for an address
    /// relative to a binary; for an absolute one, every binary loaded at that base that has a
    /// function there - the two builds of the game both start at the same base. Null when what
    /// was asked is not an address at all.
    /// </summary>
    private static List<PageHit>? At(SqliteConnection db, string text)
    {
        var hits = new List<PageHit>();
        if (RelativeShape().Match(text) is { Success: true } relative)
        {
            var binary = relative.Groups["binary"].Value;
            var rva = long.Parse(relative.Groups["hex"].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            var known = Rows(db, "SELECT binary FROM func WHERE binary = $1 COLLATE NOCASE LIMIT 1", binary);
            if (known.Count == 0)
            {
                return null;
            }
            Contain(db, (string)known[0][0]!, rva, hits);
            return hits;
        }
        if (AbsoluteShape().Match(text) is { Success: true } absolute)
        {
            var va = long.Parse(absolute.Groups["hex"].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            foreach (var row in Rows(db, "SELECT DISTINCT binary FROM func"))
            {
                var binary = (string)row[0]!;
                var rva = va - Addresses.ImageBase(binary);
                if (rva >= 0)
                {
                    Contain(db, binary, rva, hits);
                }
            }
            return hits;
        }
        return null;
    }

    /// <summary>Adds the function of one binary that holds an address, if one does.</summary>
    private static void Contain(SqliteConnection db, string binary, long rva, List<PageHit> hits)
    {
        var found = Rows(db, "SELECT rva, size FROM func WHERE binary = $1 AND rva <= $2 ORDER BY rva DESC LIMIT 1", binary, rva);
        if (found.Count == 0)
        {
            return;
        }
        var start = (long)found[0][0]!;
        var size = (long)found[0][1]!;
        if (rva >= start + Math.Max(size, 1))
        {
            return;
        }
        var id = Addresses.Relative(binary, start);
        hits.Add(new PageHit("function", Routes.Route("function", id), id,
            rva == start ? null : "+0x" + (rva - start).ToString("X", System.Globalization.CultureInfo.InvariantCulture) + " into it"));
    }

    /// <summary>
    /// Lines of a language with no spaces: by runs of three characters where every word is that
    /// long, and by reading the lines where one is shorter.
    /// </summary>
    private static IEnumerable<LineHit> UnspacedLines(SqliteConnection db, string text, string? lang)
    {
        var words = text.Split((char[])[' ', '　', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [];
        }
        List<object?[]> rows;
        if (words.All(w => w.Length >= 3))
        {
            var match = string.Join(" AND ", words.Select(w => "\"" + w.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
            rows = Rows(db,
                "SELECT l.handle, l.lang, snippet(loca_cjk_fts, 0, '<em>', '</em>', '…', 24) FROM loca_cjk_fts f JOIN loca_cjk l ON l.id = f.rowid "
                + "WHERE loca_cjk_fts MATCH $1 AND ($2 IS NULL OR l.lang = $2) ORDER BY rank LIMIT 30", match, lang);
        }
        else
        {
            var like = string.Join(" AND ", words.Select((_, i) => $"instr(text, ${i + 2}) > 0"));
            var args = new List<object?> { lang };
            args.AddRange(words);
            rows = Rows(db, $"SELECT handle, lang, text FROM loca_cjk WHERE ($1 IS NULL OR lang = $1) AND {like} LIMIT 30", [.. args]);
            foreach (var row in rows)
            {
                var line = (string)row[2]!;
                row[2] = Mark(line.Length > 220 ? line[..220] + "…" : line, words);
            }
        }
        return rows.Select(r => new LineHit((string)r[0]!, (string)r[1]!, (string)r[2]!));
    }

    /// <summary>A line with each word wrapped in <c>&lt;em&gt;</c>, the way the full-text snippets come.</summary>
    private static string Mark(string line, string[] words)
    {
        foreach (var word in words.Distinct(StringComparer.Ordinal).OrderByDescending(w => w.Length))
        {
            line = line.Replace(word, "<em>" + word + "</em>", StringComparison.Ordinal);
        }
        return line;
    }

    private static List<object?[]> Rows(SqliteConnection db, string sql, params object?[] args)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
        {
            command.Parameters.AddWithValue("$" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), args[i] ?? DBNull.Value);
        }
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }
}
