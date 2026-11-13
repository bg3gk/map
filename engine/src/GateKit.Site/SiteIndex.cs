using System.Text.RegularExpressions;
using GateKit.GameData.Query;

namespace GateKit.Site;

/// <summary>An object the site has a page for.</summary>
public sealed record Target(string Kind, string Id, string Slug, string? Name)
{
    public string Route => Routes.Route(Kind, Slug);
}

/// <summary>
/// The index, read once into the few lookups every page needs: which uuid belongs to what, which
/// stat names exist, what each localization handle says, and which object of the game answers to
/// a given name. Everything else is queried per page.
/// </summary>
public sealed partial class SiteIndex : IDisposable
{
    private readonly Dictionary<string, Target> _byUuid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Target> _byStat = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Target> _byStatLoose = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _speakerGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _loca = new(StringComparer.Ordinal);

    // Every handle with a line in any language: only those are worth a link. The game also names
    // handles that have no text anywhere - `ls::TranslatedStringRepository::s_HandleUnknown` is
    // its own placeholder for "no line" - and 1,448 links to such pages led to an empty page.
    private readonly HashSet<string> _spoken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _entitiesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _dialogByLeaf = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex UuidShape();

    [GeneratedRegex("^h[0-9a-f]{8}g[0-9a-f]{4}g[0-9a-f]{4}g[0-9a-f]{4}g[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex HandleShape();

    public ReadOnlyDb Db { get; }

    /// <summary>The slugs of every page on the whole site, shared with the other two maps.</summary>
    public SlugBook Slugs { get; }

    /// <summary>Every level that places an object, in the order their names sort.</summary>
    public IReadOnlyList<string> Levels { get; }

    /// <summary>What the index says it was built from, for the footer of every page.</summary>
    public string Source { get; }

    /// <summary>The kinds this map owns, in the order the site lists them.</summary>
    public static readonly string[] Kinds = ["entity", "dialog", "timeline", "template", "stat", "level", "loca", "object", "source", "package", "osiris"];

    /// <summary>Every object of the index by its row, in row order, beside the id its page has.</summary>
    private readonly long[] _objectRows;
    private readonly string[] _objectIds;

    /// <summary>The language every page is written in; the others travel beside it.</summary>
    public const string Primary = "English";

    /// <summary>The languages the index holds, English first.</summary>
    public IReadOnlyList<string> Languages { get; }

    private readonly Microsoft.Data.Sqlite.SqliteCommand _texts;

    public SiteIndex(ReadOnlyDb db, SlugBook slugs)
    {
        Db = db;
        Slugs = slugs;
        // What the footer of every page says this was read from. The index's `game` key is the
        // folder it was read out of, which names a machine, so it is never used; `source` is the
        // build when the index carries one, and otherwise the date and the languages are honest.
        var meta = db.Rows("SELECT key, value FROM meta").ToDictionary(r => r.S("key") ?? "", r => r.S("value") ?? "", StringComparer.Ordinal);
        Source = meta.GetValueOrDefault("source") is { Length: > 0 } said
            ? said
            : meta.GetValueOrDefault("built") is { Length: > 0 } built
                ? "the index of " + built + (meta.GetValueOrDefault("languages") is { Length: > 0 } langs ? ", languages: " + langs : "")
                : "an unnamed build of the game";

        var entities = db.Rows("SELECT uuid, name, level FROM entities");
        var templates = db.Rows("SELECT uuid, name FROM templates");
        var timelines = db.Rows("SELECT uuid, name FROM timelines WHERE shadowed = 0 AND uuid IS NOT NULL");
        var dialogs = db.Rows("SELECT uuid, name FROM dialogs WHERE shadowed = 0");
        var stats = db.Rows("SELECT DISTINCT name FROM stats WHERE name IS NOT NULL AND name <> ''");
        Levels = [.. entities.Select(r => r.S("level")).Where(l => !string.IsNullOrEmpty(l)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)!];

        slugs.Register("entity", entities.Select(r => r.S("uuid")!).Where(u => u != null));
        slugs.Register("template", templates.Select(r => r.S("uuid")!).Where(u => u != null));
        slugs.Register("timeline", timelines.Select(r => r.S("uuid")!));
        slugs.Register("dialog", dialogs.Select(r => Routes.DialogId(r.S("name") ?? r.S("uuid")!)));
        slugs.Register("stat", stats.Select(r => r.S("name")!));
        slugs.Register("level", Levels);

        void Register(string kind, string id, string? name, string? uuid)
        {
            var slug = slugs.Of(kind)[id];
            var target = new Target(kind, id, slug, name);
            if (uuid != null)
            {
                _byUuid[uuid] = target;
            }
            if (kind == "stat")
            {
                _byStat[id] = target;
                _byStatLoose[id] = target;
            }
        }

        foreach (var row in entities)
        {
            var uuid = row.S("uuid")!;
            Register("entity", uuid, row.S("name"), uuid);
            // A name is not unique: the same object is placed in several levels, and 9,090 names
            // are shared. The sightings of a component name an object this way, so all of them
            // are kept and the link says when a name means more than one.
            if (row.S("name") is { Length: > 0 } name)
            {
                if (!_entitiesByName.TryGetValue(name, out var list))
                {
                    _entitiesByName[name] = list = [];
                }
                list.Add(uuid);
            }
        }
        foreach (var row in templates)
        {
            Register("template", row.S("uuid")!, row.S("name"), row.S("uuid"));
        }
        foreach (var row in timelines)
        {
            Register("timeline", row.S("uuid")!, row.S("name"), row.S("uuid"));
        }
        foreach (var row in dialogs)
        {
            var id = Routes.DialogId(row.S("name") ?? row.S("uuid")!);
            Register("dialog", id, row.S("name"), row.S("uuid"));
            _dialogByLeaf[id] = id;
        }
        foreach (var row in stats)
        {
            Register("stat", row.S("name")!, row.S("name"), null);
        }
        foreach (var row in db.Rows("SELECT uuid, name FROM speaker_groups"))
        {
            _speakerGroups[row.S("uuid") ?? ""] = row.S("name") ?? "";
        }
        foreach (var row in db.Rows("SELECT DISTINCT handle FROM loca WHERE variant = '' AND text IS NOT NULL AND text <> ''"))
        {
            _spoken.Add(row.S("handle") ?? "");
        }
        foreach (var row in db.Rows("SELECT handle, text FROM loca WHERE lang = 'English' AND variant = ''"))
        {
            _loca[row.S("handle") ?? ""] = row.S("text") ?? "";
        }
        slugs.Register("loca", _spoken.Where(h => h.Length > 0));

        // Every object the index read out of a file, by the key the file gives it. A key can be
        // given twice - the same uuid in the game and in a patch that overrides it - and some
        // objects have none: the first holder of a key is its page, the others and the keyless
        // ones are named by their row as well, so every object has a page and the id of each is
        // the same in every build of the same files.
        var rows = new List<long>(1 << 20);
        var ids = new List<string>(1 << 20);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in db.Rows("SELECT id, key FROM objects ORDER BY id"))
        {
            var rowId = row.L("id")!.Value;
            var key = row.S("key");
            rows.Add(rowId);
            ids.Add(key is { Length: > 0 } && taken.Add(key)
                ? key
                : (key is { Length: > 0 } ? key + "." : "row.") + rowId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        _objectRows = [.. rows];
        _objectIds = [.. ids];
        slugs.Register("object", _objectIds);
        slugs.Register("source", db.Rows("SELECT package, path FROM sources").Select(r => SourceId(r.S("package"), r.S("path"))));
        slugs.Register("package", db.Rows("SELECT DISTINCT package FROM files WHERE package IS NOT NULL AND package <> ''").Select(r => r.S("package")!));
        slugs.Register("osiris", db.Rows("SELECT name FROM osiris_functions WHERE name <> '' UNION SELECT name FROM osiris_uses WHERE name <> ''").Select(r => r.S("name")!));
        Languages = [.. db.Rows("SELECT DISTINCT lang FROM loca WHERE variant = '' AND lang IS NOT NULL AND lang <> ''")
            .Select(r => r.S("lang")!)
            .OrderBy(l => l == Primary ? 0 : 1)
            .ThenBy(l => l, StringComparer.Ordinal)];
        // The other languages are looked up a line at a time, through the key the table already
        // has: holding fifteen languages of half a million lines in memory would need gigabytes.
        _texts = db.Command("SELECT lang, text FROM loca WHERE variant = '' AND handle = ? AND lang <> 'English' ORDER BY lang", [""]);
    }

    public int Count(string kind) => Slugs.Count(kind);

    /// <summary>The id of an indexed file's page: its package, then its path inside it.</summary>
    public static string SourceId(string? package, string? path) => (package ?? "") + "/" + (path ?? "");

    /// <summary>The id of the page of the object on this row of the index.</summary>
    public string ObjectId(long row)
    {
        var at = Array.BinarySearch(_objectRows, row);
        return at >= 0 ? _objectIds[at] : "row." + row.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A link to the raw object with this key: every attribute the game's file gives it.</summary>
    public SiteValue Raw(string? key, string? text = null) =>
        string.IsNullOrEmpty(key) ? V.None : Slugs.Link("object", key, text ?? key);

    /// <summary>A link to a package's page.</summary>
    public SiteValue Package(string? name) => string.IsNullOrEmpty(name) ? V.None : Slugs.Link("package", name);

    /// <summary>What a line says in every language but English, by language, or null when it says nothing else.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Texts(string handle)
    {
        _texts.Parameters[0].Value = handle;
        List<KeyValuePair<string, string>>? texts = null;
        using var reader = _texts.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1) && reader.GetString(1).Length > 0)
            {
                (texts ??= []).Add(new(reader.GetString(0), reader.GetString(1)));
            }
        }
        return texts;
    }

    /// <summary>What a localization handle says in English, when the index has it.</summary>
    public string? Says(string? handle) => handle != null && _loca.TryGetValue(handle, out var text) ? text : null;

    public static bool IsUuid(string? text) => text != null && UuidShape().IsMatch(text);

    public static bool IsUuid(ReadOnlySpan<char> text) => UuidShape().IsMatch(text);

    public static bool IsHandle(string? text) => text != null && HandleShape().IsMatch(text);

    /// <summary>The page of a uuid, when there is one.</summary>
    public Target? Find(string? uuid) => uuid != null && _byUuid.TryGetValue(uuid, out var target) ? target : null;

    /// <summary>A uuid as a value: a link when the site has the object, a bare identifier otherwise.</summary>
    public SiteValue Ref(string? uuid, string? label = null)
    {
        if (string.IsNullOrEmpty(uuid))
        {
            return V.None;
        }
        if (_byUuid.TryGetValue(uuid, out var target))
        {
            return V.Link(target.Kind, target.Id, target.Route, label ?? uuid, label == null ? target.Name : null);
        }
        if (_speakerGroups.TryGetValue(uuid, out var group))
        {
            return V.Id(uuid, "speaker group " + group);
        }
        // Not one of the objects with a page of their own, but an object of the game all the same:
        // its raw page has every attribute the file gives it.
        if (Slugs.Route("object", uuid) is { } raw)
        {
            return V.Link("object", uuid, raw, label ?? uuid, label == null ? "raw object" : null);
        }
        return V.Id(uuid, label);
    }

    /// <summary>
    /// An object of the game by the name a level gives it, which is how a sighting of a component
    /// names what it was seen on. One name may be several placed objects; the link then leads to
    /// the first and says how many there are.
    /// </summary>
    public SiteValue Object(string? name)
    {
        if (string.IsNullOrEmpty(name) || !_entitiesByName.TryGetValue(name, out var uuids) || uuids.Count == 0)
        {
            return V.Id(name ?? "");
        }
        var target = _byUuid[uuids[0]];
        return V.Link(target.Kind, target.Id, target.Route, name,
            uuids.Count > 1 ? uuids.Count + " objects of this name" : null);
    }

    /// <summary>Every placed object that answers to a name.</summary>
    public IReadOnlyList<string> ObjectsNamed(string name) =>
        _entitiesByName.TryGetValue(name, out var uuids) ? uuids : [];

    /// <summary>A dialogue by its short name, the one the site files it under.</summary>
    public SiteValue Dialog(string? leaf)
    {
        if (string.IsNullOrEmpty(leaf))
        {
            return V.None;
        }
        var id = Routes.DialogId(leaf);
        return Slugs.Link("dialog", id);
    }

    /// <summary>Whether a dialogue of this short name exists.</summary>
    public bool HasDialog(string leaf) => _dialogByLeaf.ContainsKey(leaf);

    /// <summary>A stat name as a value.</summary>
    public SiteValue Stat(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return V.None;
        }
        if (_byStat.TryGetValue(name, out var target) || _byStatLoose.TryGetValue(name, out target))
        {
            return V.Link("stat", target.Id, target.Route, name);
        }
        return V.Id(name);
    }

    /// <summary>A localization handle as a value: the handle links, and carries what it says.</summary>
    public SiteValue Handle(string? handle)
    {
        if (string.IsNullOrEmpty(handle))
        {
            return V.None;
        }
        if (!_spoken.Contains(handle))
        {
            return V.Id(handle, "no text in any language");
        }
        return V.Link("loca", handle, "loca/" + handle, handle, Says(handle)) with { Texts = Texts(handle) };
    }

    /// <summary>
    /// A line of text that is what a table is about, such as a line of a dialogue: the text itself
    /// is the link, and the handle is only its address. A handle the index has no text for shows
    /// as the handle.
    /// </summary>
    public SiteValue Line(string? handle)
    {
        if (string.IsNullOrEmpty(handle))
        {
            return V.None;
        }
        if (!_spoken.Contains(handle))
        {
            return V.Id(handle, "no text in any language");
        }
        return V.Link("loca", handle, "loca/" + handle, Says(handle) ?? handle) with { Texts = Texts(handle) };
    }

    /// <summary>
    /// A value of unknown shape, as it appears in a stat field or a dialog attribute: a uuid, a
    /// handle or a stat name becomes a link, anything else stays text.
    /// </summary>
    public SiteValue Auto(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return V.None;
        }
        if (IsUuid(text))
        {
            return Ref(text);
        }
        if (IsHandle(text))
        {
            return Handle(text);
        }
        // The stat files write a handle with its version after it - `h2ee00891g…;2` - and the
        // version is not part of what it names.
        var semi = text.IndexOf(';', StringComparison.Ordinal);
        if (semi > 0 && IsHandle(text[..semi]) && int.TryParse(text.AsSpan(semi + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            var handle = text[..semi];
            return _spoken.Contains(handle)
                ? V.Link("loca", handle, "loca/" + handle, text, Says(handle)) with { Texts = Texts(handle) }
                : V.Id(text, "no text in any language");
        }
        if (_byStat.ContainsKey(text))
        {
            return Stat(text);
        }
        return V.Text(text);
    }

    public void Dispose()
    {
        _texts.Dispose();
        Db.Dispose();
    }
}
