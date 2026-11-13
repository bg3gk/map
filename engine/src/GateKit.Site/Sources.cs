namespace GateKit.Site;

/// <summary>
/// Which slug every object of every kind has, written once by whichever source owns that kind and
/// read by all of them. It is the one place that knows whether an id has a page, which is what
/// lets a page of one map link into another without either map knowing the other's shape.
/// </summary>
public sealed class SlugBook
{
    private readonly Dictionary<string, Dictionary<string, string>> _slugs = new(StringComparer.Ordinal);

    /// <summary>Gives every id of a kind its slug. Called once per kind, by the source that owns it.</summary>
    public void Register(string kind, IEnumerable<string> ids) => _slugs[kind] = Routes.SlugsFor(ids);

    /// <summary>The slugs of one kind, by id.</summary>
    public IReadOnlyDictionary<string, string> Of(string kind) =>
        _slugs.TryGetValue(kind, out var slugs) ? slugs : (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>How many pages a kind has.</summary>
    public int Count(string kind) => _slugs.TryGetValue(kind, out var slugs) ? slugs.Count : 0;

    /// <summary>The kinds that have been registered, in the order the site lists them.</summary>
    public IEnumerable<string> Kinds => Routes.Kinds.Where(k => _slugs.ContainsKey(k));

    /// <summary>The route of an object, or null when nothing here has a page for it.</summary>
    public string? Route(string kind, string id) =>
        _slugs.TryGetValue(kind, out var slugs) && slugs.TryGetValue(id, out var slug)
            ? Routes.Route(kind, slug)
            : null;

    /// <summary>A link to an object when it has a page, and a bare identifier when it has not.</summary>
    public SiteValue Link(string kind, string id, string? text = null, string? note = null)
    {
        var route = Route(kind, id);
        return route == null
            ? V.Id(text ?? id, note)
            : V.Link(kind, id, route, text ?? id, note);
    }
}

/// <summary>
/// The three maps, open together. A page of any one of them can name an object of any other, and
/// does: an engine component is seen on entities, which belong to dialogues and to levels, and
/// the class the component is declared as has its own page among the symbols. Each source may be
/// missing — a reader may have built only the index — and a link into a map that is not here
/// degrades to a plain identifier rather than to a broken address.
/// </summary>
public sealed record SiteSources : IDisposable
{
    public SlugBook Slugs { get; } = new();

    /// <summary>The game's own content, from the index built out of the packages.</summary>
    public SiteIndex? Game { get; init; }

    /// <summary>The engine's binaries, from the symbol database.</summary>
    public SymbolIndex? Symbols { get; init; }

    /// <summary>Our own map of the engine's components, from <c>research/coverage/</c>.</summary>
    public ComponentMap? Components { get; init; }

    /// <summary>What the index says it was built from, for the footer of every page.</summary>
    public string Source => Game?.Source ?? Components?.Source ?? Symbols?.Source ?? "an unnamed build of the game";

    /// <summary>A link to the page of an engine class, when the symbol map has one.</summary>
    public SiteValue Symbol(string? name, string? note = null) =>
        string.IsNullOrEmpty(name) ? V.None : Slugs.Link("symbol", Routes.TypeId(name), name, note);

    /// <summary>A link to the page of an engine component, when our map has one.</summary>
    public SiteValue Component(string? name, string? note = null) =>
        string.IsNullOrEmpty(name) ? V.None : Slugs.Link("component", Routes.TypeId(name), name, note);

    /// <summary>A link to the page of an enumeration of the engine.</summary>
    public SiteValue Enum(string? name, string? note = null) =>
        string.IsNullOrEmpty(name) ? V.None : Slugs.Link("enum", Routes.TypeId(name), name, note);

    /// <summary>A link to a level's page.</summary>
    public SiteValue Level(string? name, string? note = null) =>
        string.IsNullOrEmpty(name) ? V.None : Slugs.Link("level", name, name, note);

    public void Dispose()
    {
        Game?.Dispose();
        Symbols?.Dispose();
    }
}
