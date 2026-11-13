using GateKit.GameData.Query;

namespace GateKit.Site;

/// <summary>
/// The engine's binaries, as the symbol database holds them: which classes exist, what Script
/// Extender declares them to look like, and which exported function belongs to what.
///
/// Only the lookups a page of another map needs are read into memory here — whether a name has a
/// page, and what a class is in one line. Everything a symbol page itself shows is queried as it
/// is rendered, the same way the game's kinds do it.
/// </summary>
public sealed class SymbolIndex : IDisposable
{
    private readonly Dictionary<string, string> _about = new(StringComparer.Ordinal);

    public ReadOnlyDb Db { get; }

    /// <summary>Every class that gets a page, by its real name with <c>::</c>.</summary>
    public IReadOnlyList<string> Classes { get; }

    /// <summary>Every enumeration that gets a page.</summary>
    public IReadOnlyList<string> Enums { get; }

    /// <summary>What the symbol database says it was read from.</summary>
    public string Source { get; }

    public SymbolIndex(ReadOnlyDb db, SlugBook slugs)
    {
        Db = db;
        Source = db.One("SELECT value FROM meta WHERE key = 'source'")?.S("value")
            ?? db.One("SELECT value FROM meta WHERE key = 'built'")?.S("value")
            ?? "an unnamed build of the engine";

        // A class is worth a page when any of the four things that know about classes names it:
        // the exported symbols, Script Extender's declarations, the names the engine keeps as
        // text for its own reflection, and the static ECS registrations.
        var names = new SortedSet<string>(StringComparer.Ordinal);
        void Take(string sql)
        {
            foreach (var row in db.Rows(sql))
            {
                var name = Plain(row.S("name"));
                if (name != null)
                {
                    names.Add(name);
                }
            }
        }
        Take("SELECT DISTINCT CASE WHEN namespace IS NOT NULL AND namespace <> '' THEN namespace || '::' || class ELSE class END AS name "
            + "FROM symbols WHERE class IS NOT NULL AND class <> ''");
        Take("SELECT DISTINCT " + SeTypeName + " AS name FROM se_types WHERE name IS NOT NULL AND name <> ''");
        Take("SELECT DISTINCT type AS name FROM type_names WHERE type IS NOT NULL AND type <> ''");
        Take("SELECT DISTINCT type AS name FROM ecs_ids WHERE type IS NOT NULL AND type <> ''");
        Classes = [.. names];

        var enums = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in db.Rows("SELECT DISTINCT CASE WHEN namespace IS NOT NULL AND namespace <> '' THEN namespace || '::' || name ELSE name END AS name FROM enums WHERE name IS NOT NULL AND name <> ''"))
        {
            var name = row.S("name");
            if (name is { Length: > 0 } && Routes.IsPlainTypeName(name))
            {
                enums.Add(name);
            }
        }
        Enums = [.. enums];

        slugs.Register("symbol", Classes.Select(Routes.TypeId));
        slugs.Register("enum", Enums.Select(Routes.TypeId));
        ReadFunctions();
        // Script Extender's own lookups that are a thing rather than a member: `ls__GlobalSwitches`,
        // `ecl__EoCClient`, `RPGStats__Load` - the globals and entry points it reaches for.
        slugs.Register("global", db.Rows("SELECT DISTINCT name FROM se_symbols WHERE name IS NOT NULL AND name NOT LIKE '%::%' AND name <> ''")
            .Select(r => r.S("name")!));

        // One line about each class, shown beside a link to it from anywhere on the site.
        foreach (var row in db.Rows($"SELECT {SeTypeName} AS name, kind, size FROM se_types WHERE name IS NOT NULL AND name <> ''"))
        {
            var name = Plain(row.S("name"));
            if (name == null)
            {
                continue;
            }
            var kind = row.S("kind");
            var size = row.L("size");
            _about[name] = kind is { Length: > 0 }
                ? (size is > 0 ? kind + ", " + size + " bytes" : kind)
                : (size is > 0 ? size + " bytes" : "");
        }
    }

    /// <summary>
    /// The full name of a Script Extender declaration, in SQL. The engine's own name for the type
    /// wins where the declaration carries it, because that is the name every other map uses; the
    /// namespace the declaration is written in stands in only where it does not.
    /// </summary>
    internal const string SeTypeName =
        "COALESCE(NULLIF(engine_class, ''), CASE WHEN namespace IS NOT NULL AND namespace <> '' THEN namespace || '::' || name ELSE name END)";

    /// <summary>
    /// A name as the site files it, or null when it cannot have a page. The engine writes the
    /// names it keeps as text the way a declaration spells them - <c>struct ecs::TypeIdContext</c>
    /// - and the keyword is not part of the name.
    /// </summary>
    internal static string? Plain(string? name)
    {
        if (name == null)
        {
            return null;
        }
        var text = name.Trim();
        foreach (var keyword in (string[])["struct ", "class ", "union ", "enum ", "const "])
        {
            if (text.StartsWith(keyword, StringComparison.Ordinal))
            {
                text = text[keyword.Length..].TrimStart();
            }
        }
        return Routes.IsPlainTypeName(text) ? text : null;
    }

    /// <summary>A few words saying what a class is, when Script Extender declares it.</summary>
    private readonly Dictionary<long, (string Name, string Origin, string Build)> _binaries = [];
    private readonly HashSet<long> _functions = [];
    private readonly Dictionary<long, (string Name, string Source)> _named = [];
    private readonly Dictionary<long, (long Dll, long Rva, string Via)> _twins = [];
    private readonly Dictionary<long, string> _classOfSymbol = [];

    /// <summary>How many functions the binaries list.</summary>
    public int FunctionCount => _functions.Count;

    /// <summary>
    /// Reads what the function pages need of every binary at once: which functions there are, the
    /// names known for them, and the pairs that are one function in the two builds of the game.
    /// </summary>
    private void ReadFunctions()
    {
        var versions = Db.Rows("SELECT key, value FROM meta").ToDictionary(r => r.S("key") ?? "", r => r.S("value") ?? "", StringComparer.Ordinal);
        foreach (var row in Db.Rows("SELECT id, name, origin FROM dlls"))
        {
            var origin = row.S("origin") ?? "";
            var version = versions.GetValueOrDefault(origin == "game" ? "game_file_version" : "toolkit_file_version");
            _binaries[row.L("id")!.Value] = (row.S("name") ?? "", origin,
                (origin == "game" ? "the game" : "the Toolkit") + (version is { Length: > 0 } ? " " + version : ""));
        }
        foreach (var row in Db.Rows("SELECT dll_id, rva FROM functions"))
        {
            _functions.Add(Addresses.Key(row.L("dll_id")!.Value, row.L("rva")!.Value));
        }
        foreach (var row in Db.Rows("SELECT dll_id, rva, full_name FROM symbols WHERE rva IS NOT NULL AND full_name IS NOT NULL ORDER BY id"))
        {
            _named.TryAdd(Addresses.Key(row.L("dll_id")!.Value, row.L("rva")!.Value), (row.S("full_name")!, "the binary's own export"));
        }
        foreach (var row in Db.Rows("SELECT dll_id, rva, name FROM se_symbols WHERE rva IS NOT NULL AND name IS NOT NULL"))
        {
            _named.TryAdd(Addresses.Key(row.L("dll_id")!.Value, row.L("rva")!.Value), (row.S("name")!, "Script Extender's symbol"));
        }
        foreach (var row in Db.Rows("SELECT id, namespace, class FROM symbols WHERE class IS NOT NULL AND class <> ''"))
        {
            var ns = row.S("namespace");
            _classOfSymbol[row.L("id")!.Value] = (ns is { Length: > 0 } ? ns + "::" : "") + row.S("class");
        }

        // A game function takes the Toolkit's name when exactly one Toolkit function matches it;
        // and two game functions matched to the same Toolkit function, one in each build, are the
        // same function twice.
        var byToolkit = new Dictionary<long, List<long>>();
        var byGame = new Dictionary<long, List<long>>();
        foreach (var row in Db.Rows("SELECT toolkit_dll, toolkit_rva, game_dll, game_rva FROM matches"))
        {
            var toolkit = Addresses.Key(row.L("toolkit_dll")!.Value, row.L("toolkit_rva")!.Value);
            var game = Addresses.Key(row.L("game_dll")!.Value, row.L("game_rva")!.Value);
            (byToolkit.TryGetValue(toolkit, out var games) ? games : byToolkit[toolkit] = []).Add(game);
            (byGame.TryGetValue(game, out var toolkits) ? toolkits : byGame[game] = []).Add(toolkit);
        }
        foreach (var (game, toolkits) in byGame)
        {
            var distinct = toolkits.Distinct().ToArray();
            if (distinct.Length == 1 && _named.TryGetValue(distinct[0], out var name))
            {
                _named.TryAdd(game, (name.Name, "matched to the Toolkit's export"));
            }
        }
        foreach (var (toolkit, games) in byToolkit)
        {
            var distinct = games.Distinct().ToArray();
            if (distinct.Length != 2 || (distinct[0] >> 40) == (distinct[1] >> 40))
            {
                continue;
            }
            // Each must have no other Toolkit function, or the pair is a guess.
            if (byGame[distinct[0]].Distinct().Count() != 1 || byGame[distinct[1]].Distinct().Count() != 1)
            {
                continue;
            }
            var via = "both match " + Addresses.Relative(Binary(toolkit >> 40), toolkit & ((1L << 40) - 1));
            _twins[distinct[0]] = (distinct[1] >> 40, distinct[1] & ((1L << 40) - 1), via);
            _twins[distinct[1]] = (distinct[0] >> 40, distinct[0] & ((1L << 40) - 1), via);
        }
    }

    /// <summary>How many functions the game's own binaries have, in both builds together.</summary>
    public int GameFunctions => _functions.Count(f => IsGame(f >> 40));

    /// <summary>How many of those go by a name.</summary>
    public int NamedGameFunctions => _named.Keys.Count(k => IsGame(k >> 40) && _functions.Contains(k));

    /// <summary>How many functions of the game have a known twin in the other build.</summary>
    public int Twins => _twins.Count;

    /// <summary>Every name known for a function of the game's binaries, by binary, in address order.</summary>
    public IEnumerable<(string Binary, List<(long Rva, string Name, string Source, string Build)> Names)> KnownNames()
    {
        foreach (var (dll, binary) in _binaries.Where(b => b.Value.Origin == "game").OrderBy(b => b.Value.Name, StringComparer.Ordinal))
        {
            var names = _named.Where(n => n.Key >> 40 == dll && _functions.Contains(n.Key))
                .OrderBy(n => n.Key)
                .Select(n => (n.Key & ((1L << 40) - 1), n.Value.Name, n.Value.Source, binary.Build))
                .ToList();
            yield return (binary.Name, names);
        }
    }

    /// <summary>The file name of a binary.</summary>
    public string Binary(long dll) => _binaries.TryGetValue(dll, out var b) ? b.Name : "dll" + dll.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whether a binary is one of the game's own, rather than one of the Toolkit's.</summary>
    public bool IsGame(long dll) => _binaries.TryGetValue(dll, out var b) && b.Origin == "game";

    /// <summary>The build a binary's addresses are true for.</summary>
    public string Build(long dll) => _binaries.TryGetValue(dll, out var b) ? b.Build : "";

    /// <summary>The same function in the other build of the game, when one is known.</summary>
    public (long Dll, long Rva, string Via)? Twin(long dll, long rva) =>
        _twins.TryGetValue(Addresses.Key(dll, rva), out var twin) ? twin : null;

    /// <summary>A function as a link to its page, with its name beside it when one is known.</summary>
    public SiteValue Function(long? dll, long? rva, string? text = null, string? note = null)
    {
        if (dll is not { } d || rva is not { } r)
        {
            return V.None;
        }
        var id = Addresses.Relative(Binary(d), r);
        var key = Addresses.Key(d, r);
        var name = _named.TryGetValue(key, out var known) ? known.Name : null;
        if (!_functions.Contains(key))
        {
            return V.Id(text ?? id, note ?? name);
        }
        return V.Link("function", id, Routes.Route("function", id), text ?? id, note ?? name);
    }

    /// <summary>The class an exported symbol belongs to, as a link when the class has a page.</summary>
    public SiteValue ClassOf(SiteSources sources, long? symbol) =>
        symbol is { } s && _classOfSymbol.TryGetValue(s, out var name) ? sources.Symbol(name) : V.None;

    /// <summary>
    /// The name a function goes by and where it comes from, best first: the binary's own export,
    /// Script Extender's symbol, then the Toolkit's export it was matched to.
    /// </summary>
    public (string? Name, string? Source) NameOf(List<Row> exported, List<Row> extender, List<Row> matched)
    {
        if (exported.FirstOrDefault(r => r.S("full_name") is { Length: > 0 }) is { } export)
        {
            return (export.S("full_name"), "the binary's own export");
        }
        if (extender.FirstOrDefault() is { } se)
        {
            return (se.S("name"), "Script Extender's symbol" + (se.S("note") is { Length: > 0 } note ? " (" + note + ")" : ""));
        }
        var toolkits = matched.Select(r => Addresses.Key(r.L("toolkit_dll") ?? 0, r.L("toolkit_rva") ?? 0)).Distinct().ToArray();
        if (toolkits.Length == 1 && _named.TryGetValue(toolkits[0], out var name))
        {
            var how = matched.Select(r => r.S("method")).Where(m => m is { Length: > 0 }).Distinct(StringComparer.Ordinal);
            return (name.Name, "matched to the Toolkit's export by " + string.Join(", ", how));
        }
        return (null, null);
    }

    public string? About(string name) => _about.TryGetValue(name, out var about) && about.Length > 0 ? about : null;

    /// <summary>
    /// A type as it appears in a field's declaration: a link where the type has a page, the type
    /// spelled out where it has not. Pointers, references and arrays are peeled off so that
    /// <c>ls::Array&lt;eoc::Foo&gt;*</c> still leads to <c>eoc::Foo</c>.
    /// </summary>
    public SiteValue Type(SiteSources sources, string? type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return V.None;
        }
        var core = Core(type);
        if (core != null)
        {
            var route = sources.Slugs.Route("symbol", Routes.TypeId(core));
            if (route != null)
            {
                return V.Link("symbol", Routes.TypeId(core), route, type, core == type ? About(core) : null);
            }
            route = sources.Slugs.Route("enum", Routes.TypeId(core));
            if (route != null)
            {
                return V.Link("enum", Routes.TypeId(core), route, type, "enumeration");
            }
        }
        return V.Id(type);
    }

    /// <summary>The one named type inside a declaration, when there is exactly one to find.</summary>
    private static string? Core(string type)
    {
        // The Toolkit's accessors carry a whole declaration - `class ls::FixedString const &` -
        // and the keywords around the name are not part of it.
        var text = type.Trim();
        while (text.EndsWith('*') || text.EndsWith('&'))
        {
            text = text[..^1].TrimEnd();
        }
        if (text.EndsWith(" const", StringComparison.Ordinal))
        {
            text = text[..^6].TrimEnd();
        }
        foreach (var keyword in (string[])["struct ", "class ", "union ", "enum ", "const "])
        {
            if (text.StartsWith(keyword, StringComparison.Ordinal))
            {
                text = text[keyword.Length..].TrimStart();
            }
        }
        var open = text.IndexOf('<', StringComparison.Ordinal);
        if (open >= 0)
        {
            // A single-argument template: the argument is the interesting half of `Array<Foo>`.
            var close = text.LastIndexOf('>');
            if (close > open && !text.AsSpan(open + 1, close - open - 1).Contains(','))
            {
                return Core(text[(open + 1)..close]);
            }
            return null;
        }
        return Routes.IsPlainTypeName(text) ? text : null;
    }

    public void Dispose() => Db.Dispose();
}
