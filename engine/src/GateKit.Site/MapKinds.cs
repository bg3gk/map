using System.Globalization;
using GateKit.GameData.Query;

namespace GateKit.Site;

/// <summary>
/// A level: what the game places in it, and which of the engine's components were seen while it
/// was loaded. It is the shortest way between the game's content and our map of the engine — a
/// component is seen in a level, and a level is a list of objects.
/// </summary>
public sealed class LevelKind : IPageKind
{
    public string Kind => "level";

    public string About => "a level: the objects the game places in it, and the engine components seen while it was loaded";

    public bool Available(SiteSources sources) => sources.Game != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var index = sources.Game!;
        var db = index.Db;
        using var placed = Grouper.ByText(db,
            "SELECT level, uuid, name, type, template FROM entities WHERE level IS NOT NULL AND level <> '' ORDER BY level");
        using var counts = Grouper.ByText(db,
            "SELECT level, type, count(*) AS objects FROM entities WHERE level IS NOT NULL AND level <> '' GROUP BY level, type ORDER BY level");

        foreach (var level in index.Levels)
        {
            var objects = placed.Take(level);
            var byType = counts.Take(level);
            yield return new PageDoc
            {
                Kind = Kind,
                Id = level,
                Route = Routes.Route(Kind, index.Slugs.Of(Kind)[level]),
                Title = level,
                Subtitle = "level, " + objects.Count.ToString("#,0", CultureInfo.InvariantCulture) + " objects placed",
                Fields =
                [
                    new SiteField("name", V.Id(level)),
                    new SiteField("objects|objects this level places", V.Num((long)objects.Count)),
                    new SiteField("components|engine components seen alive while this level was loaded",
                        V.Num((long)(sources.Components?.InLevel(level).Count ?? 0))),
                ],
                Sections =
                [
                    PageKinds.Table("Objects by type", ["type", "objects"], byType, PageKinds.Aside(rowLimit),
                        r => [V.Text(r.S("type")), V.Num(r.L("objects"))]),
                    PageKinds.Table("Objects placed here", ["object", "name", "type", "template"], objects, rowLimit,
                        r => [index.Ref(r.S("uuid")), V.Text(r.S("name")), V.Text(r.S("type")), index.Ref(r.S("template"))]),
                    ComponentKind.SeenIn(sources, "while this level was loaded",
                        sources.Components?.InLevel(level), rowLimit),
                ],
            };
        }
    }
}

/// <summary>
/// One of the engine's component types, as we mapped it: what its memory holds, how sure we are
/// of each field, and where in the game it was actually seen.
///
/// This is the map that did not exist before: nothing Larian ships says what is at offset 4 of
/// <c>ecl::ActiveTurnComponent</c>. Every claim here was read out of a running game, and the
/// sightings are what backs it — which is why they are links and not a list.
/// </summary>
public sealed class ComponentKind : IPageKind
{
    public string Kind => "component";

    public string About => "an engine component: what its memory holds, field by field, and where in the game it was seen";

    /// <summary>What was measured is ours, and says so; it is not the game's own text.</summary>
    public SiteNotice Notice => SiteNotice.OurFindings;

    public bool Available(SiteSources sources) => sources.Components != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var map = sources.Components!;
        foreach (var name in map.Components)
        {
            var layout = map.Layout(name);
            var census = map.Census(name);
            var sightings = map.Sightings(name);
            var sections = new List<SiteSection>
            {
                PageKinds.Table("Fields", ["offset", "name", "type", "source|how we know it: inferred from the engine's code, confirmed live, or unknown", "evidence"],
                    Rows(layout?.Fields), rowLimit, r => r),
            };
            foreach (var nested in layout?.Structs ?? [])
            {
                sections.Add(PageKinds.Table(
                    "Struct " + nested.Name + (nested.Size is { } size ? " · " + size + " bytes" : ""),
                    ["offset", "name", "type", "source|how we know it: inferred from the engine's code, confirmed live, or unknown", "evidence"], Rows(nested.Fields), rowLimit, r => r));
            }
            sections.Add(new SiteSection("Still open",
                ["question"], [.. (layout?.OpenQuestions ?? []).Select(q => (IReadOnlyList<SiteValue>)[V.Text(q)])])
            { Total = layout?.OpenQuestions.Count ?? 0 });
            sections.Add(new SiteSection("Seen", ["side", "levels", "max|the most instances seen on one entity", "samples"],
                [.. sightings.Select(s => (IReadOnlyList<SiteValue>)
                [
                    V.Text(s.Side),
                    V.Text(string.Join(", ", s.Levels)),
                    V.Num(s.Max),
                    V.Num(s.Samples),
                ])])
            { Total = sightings.Count });
            var levels = sightings.SelectMany(s => s.Levels).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            sections.Add(new SiteSection("Levels it was seen in", ["level"],
                [.. levels.Select(l => (IReadOnlyList<SiteValue>)[sources.Level(l)])]) { Total = levels.Length });
            sections.Add(Where(sources, sightings, rowLimit));
            // The census lists every type from the point of view of each side, so a component is
            // in it twice; what the two rows say about it is not the same, and that is the point.
            sections.Add(new SiteSection("What the extender's census says",
                ["side", "type id", "inline|size of the part stored inline", "total|size with what it owns", "mapped|Script Extender maps it on this side", "replicated", "checked"],
                [.. census.Select(c => (IReadOnlyList<SiteValue>)
                [
                    V.Text(c.Side), V.Num(c.Index), V.Num(c.InlineSize), V.Num(c.TotalSize),
                    V.Flag(c.Mapped), V.Flag(c.Replicated), V.Text(c.Verified),
                ])])
            { Total = census.Count });

            yield return new PageDoc
            {
                Kind = Kind,
                Id = Routes.TypeId(name),
                Route = Routes.Route(Kind, sources.Slugs.Of(Kind)[Routes.TypeId(name)]),
                Title = name,
                Subtitle = "engine component, on the " + Side(name)
                    + (layout?.Size is { } bytes ? ", " + bytes + " bytes" : "")
                    + (layout?.OneFrame == true ? ", lives one frame" : ""),
                Fields =
                [
                    new SiteField("name", V.Id(name)),
                    new SiteField("class|the class of the engine this component is declared as", sources.Symbol(name)),
                    new SiteField("Lua name|the name Script Extender gives it in Lua", layout?.ExtName is { Length: > 0 } ext ? V.Id(ext, "Script Extender") : V.None),
                    new SiteField("size", V.Num(layout?.Size)),
                    new SiteField("one frame|a one-frame component: it lives for a single frame", V.Flag(layout?.OneFrame ?? census.FirstOrDefault()?.OneFrame)),
                    new SiteField("replicated|replicated from the server to the client", V.Flag(census.Count > 0 ? census.Any(c => c.Replicated) : null)),
                    new SiteField("mapped|Script Extender already maps this component", V.Flag(census.Count > 0 ? census.Any(c => c.Mapped) : null)),
                    new SiteField("status|how far the mapping got", V.Text(layout?.Status)),
                    new SiteField("confidence|how sure we are of the layout", V.Text(layout?.Confidence)),
                    new SiteField("purpose|what it is for", V.Text(layout?.Summary)),
                    new SiteField("checked live|checked against a running game: how, and on which build", V.Text(layout?.RawCheck ?? census.FirstOrDefault()?.Verified)),
                    new SiteField("batch|the batch of the coverage work it was mapped in", layout?.Batch is { Length: > 0 } batch ? V.Id(batch) : V.None),
                    new SiteField("minutes|minutes the mapping took", V.Num(layout?.Minutes)),
                    new SiteField("functions|functions of the engine read to map it", V.Num(layout?.FunctionsRead)),
                ],
                Sections = sections,
            };
        }
    }

    /// <summary>
    /// Which half of the game a component belongs to, as the engine's own namespaces say it:
    /// <c>ecl</c> is the client, <c>esv</c> the server, and everything else is shared between
    /// them. The census lists every type from both sides, so it cannot answer this.
    /// </summary>
    internal static string Side(string name) =>
        name.StartsWith("ecl::", StringComparison.Ordinal) ? "client"
        : name.StartsWith("esv::", StringComparison.Ordinal) ? "server"
        : "client and the server";

    private static List<IReadOnlyList<SiteValue>> Rows(IReadOnlyList<LayoutField>? fields) =>
        [.. (fields ?? []).Select(f => (IReadOnlyList<SiteValue>)
        [
            f.Offset is { } offset
                ? V.Id("+0x" + offset.ToString("X", CultureInfo.InvariantCulture))
                : V.None,
            V.Id(f.Name),
            V.Id(f.Type ?? ""),
            V.Text(f.Source),
            V.Text(f.Evidence),
        ])];

    /// <summary>
    /// Where a component was seen, as the thread it is: every capture names the situation it was
    /// playing and the dialogue or the object it was looking at, and both of those have a page.
    /// </summary>
    private static SiteSection Where(SiteSources sources, IReadOnlyList<Sighting> sightings, int rowLimit)
    {
        var leading = new List<IReadOnlyList<SiteValue>>();
        var bare = new List<IReadOnlyList<SiteValue>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sighting in sightings)
        {
            foreach (var label in sighting.Labels)
            {
                if (!seen.Add(label))
                {
                    continue;
                }
                var (situation, dialog, target) = ComponentMap.Parse(label);
                SiteValue what = dialog != null
                    ? sources.Game?.Dialog(dialog) ?? V.Id(dialog)
                    : target != null
                        ? sources.Game?.Object(target) ?? V.Id(target)
                        : V.None;
                // A capture that names a dialogue or an object is a step of the thread; one that
                // names only the situation is a note. A component seen in nine hundred captures
                // shows a hundred of them, and they should be the ones that lead somewhere.
                (what.Kind == ValueKind.Link ? leading : bare).Add([V.Text(sighting.Side), V.Id(situation), what]);
            }
        }
        var rows = leading;
        rows.AddRange(bare);
        var shown = rows.Count <= rowLimit ? rows : rows.GetRange(0, rowLimit);
        return new SiteSection("Where it was seen", ["side", "situation", "target|the dialogue or object being looked at when it was seen"], shown)
        {
            Total = rows.Count,
            Note = "Each row is one capture: the game was driven into that situation and this component was alive.",
        };
    }

    /// <summary>The components seen on an object of the game, for that object's own page.</summary>
    internal static SiteSection SeenOn(SiteSources sources, string? name, int rowLimit)
    {
        var components = name is { Length: > 0 } ? sources.Components?.OnObject(name) : null;
        return SeenIn(sources, "on this object", components, rowLimit);
    }

    /// <summary>A list of components as a section of someone else's page.</summary>
    internal static SiteSection SeenIn(SiteSources sources, string what, IReadOnlyCollection<string>? components, int rowLimit)
    {
        var all = components ?? [];
        var rows = all.Take(PageKinds.Aside(rowLimit))
            .Select(c => (IReadOnlyList<SiteValue>)[sources.Component(c), V.Text(sources.Components?.Layout(c)?.Summary)])
            .ToArray();
        return new SiteSection("Engine components seen " + what, ["component", "purpose|what it is for"], rows)
        {
            Total = all.Count,
            Note = rows.Length == 0 ? null : "Seen alive here while the game was driven; the layouts are ours, not the game's.",
        };
    }
}

/// <summary>
/// A class of the engine, as its binaries and Script Extender's declarations describe it: what it
/// is called, what it is made of, and which of its methods the Toolkit exports.
/// </summary>
public sealed class SymbolKind : IPageKind
{
    public string Kind => "symbol";

    public string About => "a class of the engine: its declared layout, its fields and the methods its binaries export";

    public SiteNotice Notice => SiteNotice.OurFindings;

    public bool Available(SiteSources sources) => sources.Symbols != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var symbols = sources.Symbols!;
        var db = symbols.Db;
        foreach (var name in symbols.Classes)
        {
            var cut = name.LastIndexOf("::", StringComparison.Ordinal);
            var ns = cut < 0 ? "" : name[..cut];
            var bare = cut < 0 ? name : name[(cut + 2)..];

            var declared = db.One($"SELECT id, ext_name, kind, base, file, line, size, namespace FROM se_types WHERE {SymbolIndex.SeTypeName} = ?", name);
            var fields = declared == null
                ? []
                : db.Rows("SELECT idx, name, type, legacy, hidden, offset, size, checked FROM se_fields WHERE type_id = ? ORDER BY idx", declared.L("id"));
            var exported = db.Rows(
                "SELECT s.method, s.kind, s.signature, s.rva, s.dll_id, d.name AS dll FROM symbols s JOIN dlls d ON d.id = s.dll_id "
                + "WHERE s.class = ? AND s.namespace = ? ORDER BY s.method, d.name", bare, ns);
            var accessors = db.Rows(
                "SELECT field, kind, offset, width, type, game_offset FROM accessors WHERE class = ? ORDER BY offset, field", name);
            var registered = db.Rows(
                "SELECT e.context, e.index_rva, e.source, d.name AS dll FROM ecs_ids e JOIN dlls d ON d.id = e.dll_id "
                + "WHERE e.type = ? ORDER BY d.name, e.context", name);
            var known = db.Rows(
                "SELECT DISTINCT t.kind, d.name AS dll FROM type_names t JOIN dlls d ON d.id = t.dll_id "
                + "WHERE t.type IN (?, 'struct ' || ?, 'class ' || ?) ORDER BY d.name", name, name, name);

            yield return new PageDoc
            {
                Kind = Kind,
                Id = Routes.TypeId(name),
                Route = Routes.Route(Kind, sources.Slugs.Of(Kind)[Routes.TypeId(name)]),
                Title = name,
                Subtitle = (declared?.S("kind") is { Length: > 0 } k ? k : "class")
                    + (declared?.L("size") is { } size ? ", " + size + " bytes" : "")
                    + (exported.Count > 0 ? ", " + exported.Count + " exported methods" : ""),
                Fields =
                [
                    new SiteField("name", V.Id(name)),
                    new SiteField("namespace", ns.Length > 0 ? V.Id(ns) : V.None),
                    new SiteField("class", V.Id(bare)),
                    new SiteField("what it is", V.Text(declared?.S("kind"))),
                    new SiteField("Lua name|the name Script Extender gives it in Lua", declared?.S("ext_name") is { Length: > 0 } ext ? V.Id(ext, "Script Extender") : V.None),
                    new SiteField("inherits from", symbols.Type(sources, declared?.S("base"))),
                    new SiteField("size", V.Num(declared?.L("size"))),
                    new SiteField("declared in", declared?.S("file") is { Length: > 0 } file
                        ? V.Path(file + (declared.L("line") is { } line ? ":" + line : ""))
                        : V.None),
                    new SiteField("component|our map of this class as an engine component", sources.Component(name)),
                ],
                Sections =
                [
                    PageKinds.Table("Fields, as Script Extender declares them",
                        ["offset", "name", "type", "size", "legacy|the field's earlier name, often field_<offset>", "hidden|hidden from Lua", "checked|1: the computed offset equals the one in the field's name; 2: taken from the name"],
                        fields, rowLimit,
                        r =>
                        [
                            r.L("offset") is { } offset ? V.Id("+0x" + offset.ToString("X", CultureInfo.InvariantCulture)) : V.None,
                            V.Id(r.S("name") ?? ""),
                            symbols.Type(sources, r.S("type")),
                            V.Num(r.L("size")),
                            V.Id(r.S("legacy") ?? ""),
                            V.Flag(r.L("hidden") == 1),
                            V.Num(r.L("checked")),
                        ]),
                    PageKinds.Table("Fields read from the Toolkit's own accessors",
                        ["offset", "field", "how", "width", "type", "game offset|the same field's offset in the game's own binary"],
                        accessors, PageKinds.Aside(rowLimit),
                        r =>
                        [
                            r.L("offset") is { } offset ? V.Id("+0x" + offset.ToString("X", CultureInfo.InvariantCulture)) : V.None,
                            V.Id(r.S("field") ?? ""), V.Text(r.S("kind")), V.Num(r.L("width")),
                            symbols.Type(sources, r.S("type")),
                            r.L("game_offset") is { } g ? V.Id("+0x" + g.ToString("X", CultureInfo.InvariantCulture)) : V.None,
                        ]),
                    PageKinds.Table("Registered in the entity component system",
                        ["binary", "context", "id global|the global variable that receives the type's id at start-up", "found by"],
                        registered, PageKinds.Aside(rowLimit),
                        r =>
                        [
                            V.Id(r.S("dll") ?? ""), symbols.Type(sources, r.S("context")),
                            r.L("index_rva") is { } rva ? V.Id("0x" + rva.ToString("X", CultureInfo.InvariantCulture)) : V.None,
                            V.Text(r.S("source")),
                        ]),
                    PageKinds.Table("Methods the Toolkit exports", ["method", "what", "binary", "address", "declaration"],
                        exported, rowLimit,
                        r =>
                        [
                            V.Id(r.S("method") ?? ""), V.Text(r.S("kind")), V.Id(r.S("dll") ?? ""),
                            symbols.Function(r.L("dll_id"), r.L("rva")),
                            V.Text(r.S("signature")),
                        ]),
                    PageKinds.Table("The engine names this type in", ["binary", "as"], known, PageKinds.Aside(rowLimit),
                        r => [V.Id(r.S("dll") ?? ""), V.Text(r.S("kind"))]),
                ],
            };
        }
    }
}

/// <summary>An enumeration of the engine, with every value it can take.</summary>
public sealed class EnumKind : IPageKind
{
    public string Kind => "enum";

    public string About => "an enumeration of the engine, with every value it can take";

    public SiteNotice Notice => SiteNotice.OurFindings;

    public bool Available(SiteSources sources) => sources.Symbols != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var db = sources.Symbols!.Db;
        foreach (var name in sources.Symbols!.Enums)
        {
            var cut = name.LastIndexOf("::", StringComparison.Ordinal);
            var ns = cut < 0 ? "" : name[..cut];
            var bare = cut < 0 ? name : name[(cut + 2)..];
            var declared = db.One("SELECT id, underlying, bitmask, file FROM enums WHERE name = ? AND COALESCE(namespace, '') = ?", bare, ns);
            var values = declared == null
                ? []
                : db.Rows("SELECT name, value FROM enum_values WHERE enum_id = ? ORDER BY value, name", declared.L("id"));

            yield return new PageDoc
            {
                Kind = Kind,
                Id = Routes.TypeId(name),
                Route = Routes.Route(Kind, sources.Slugs.Of(Kind)[Routes.TypeId(name)]),
                Title = name,
                Subtitle = "enumeration, " + values.Count + " values"
                    + (declared?.L("bitmask") == 1 ? ", a set of flags" : ""),
                Fields =
                [
                    new SiteField("name", V.Id(name)),
                    new SiteField("namespace", ns.Length > 0 ? V.Id(ns) : V.None),
                    new SiteField("kept as", V.Id(declared?.S("underlying") ?? "")),
                    new SiteField("flags|a bitmask: its values combine", V.Flag(declared?.L("bitmask") == 1)),
                    new SiteField("declared in", V.Path(declared?.S("file"))),
                ],
                Sections =
                [
                    PageKinds.Table("Values", ["name", "value"], values, rowLimit,
                        r => [V.Id(r.S("name") ?? ""), V.Num(r.L("value"))]),
                ],
            };
        }
    }
}
