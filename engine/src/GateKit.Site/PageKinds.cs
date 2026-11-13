using GateKit.GameData.Query;

namespace GateKit.Site;

/// <summary>
/// One kind of object, described once. Both renderers read what this produces, so the page a
/// person reads and the JSON an agent fetches are the same thing said twice.
/// </summary>
public interface IPageKind
{
    /// <summary>The first segment of the URL: <c>dialog</c>, <c>stat</c>, …</summary>
    string Kind { get; }

    /// <summary>What a page of this kind is, in one line, for <c>llms.txt</c> and the home page.</summary>
    string About { get; }

    /// <summary>
    /// Whose this kind's content is and where the rest of it lives. It is asked of the kind and not
    /// of the site, so a kind made of something other than the game's own text - the engine's
    /// symbols, our component map - needs no change to the renderer, only its own notice.
    /// </summary>
    SiteNotice Notice => SiteNotice.GameContent;

    /// <summary>Whether the map this kind reads from is open at all.</summary>
    bool Available(SiteSources sources);

    /// <summary>Every page of this kind, in the order their ids sort.</summary>
    IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit);

    /// <summary>How many pages this kind will render.</summary>
    int Count(SiteSources sources) => sources.Slugs.Count(Kind);
}

/// <summary>The kinds the site renders.</summary>
public static class PageKinds
{
    public static IReadOnlyList<IPageKind> All { get; } =
    [
        // The game's own content, then our map of the engine, then the engine's own symbols.
        new EntityKind(), new DialogKind(), new TimelineKind(), new TemplateKind(), new StatKind(),
        new LevelKind(), new LocaKind(), new ObjectKind(), new SourceKind(), new PackageKind(), new OsirisKind(), new ComponentKind(), new SystemKind(), new SymbolKind(), new EnumKind(), new FunctionKind(), new GlobalKind(), new MeasureKind(),
    ];

    public static IPageKind Of(string kind) =>
        All.FirstOrDefault(k => k.Kind == kind) ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "no such kind");

    /// <summary>A table, with the rows past the limit counted rather than shown.</summary>
    internal static SiteSection Table<T>(string title, string[] columns, IReadOnlyList<T> rows, int limit,
        Func<T, IReadOnlyList<SiteValue>> cell)
    {
        var shown = rows.Count <= limit ? rows : rows.Take(limit).ToArray();
        return new SiteSection(title, columns, [.. shown.Select(cell)]) { Total = rows.Count };
    }

    /// <summary>
    /// The limit for a table that is not what the page is about. A dialogue is read for its lines,
    /// not for two hundred rows of flags; the rest are in the full index either way.
    /// </summary>
    internal static int Aside(int rowLimit) => rowLimit == int.MaxValue ? rowLimit : Math.Max(10, rowLimit / 4);
}

/// <summary>A character or an item placed in a level or among the globals.</summary>
public sealed class EntityKind : IPageKind
{
    public string Kind => "entity";

    public string About => "a character or an item placed in a level, as the level file declares it";

    public bool Available(SiteSources sources) => sources.Game != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var index = sources.Game!;
        using var speakers = Grouper.ByText(index.Db,
            "SELECT s.object, d.name FROM dialog_speakers s JOIN dialogs d ON d.id = s.dialog_id WHERE d.shadowed = 0 ORDER BY s.object");
        foreach (var row in index.Db.Rows(
            "SELECT uuid, name, type, template, display_handle, stats, level, scope, package, mod FROM entities ORDER BY uuid"))
        {
            var uuid = row.S("uuid")!;
            var spoken = speakers.Take(uuid);
            yield return new PageDoc
            {
                Kind = Kind,
                Id = uuid,
                Route = Routes.Route(Kind, index.Slugs.Of(Kind)[uuid]),
                Title = row.S("name") ?? uuid,
                Subtitle = row.S("type") is { Length: > 0 } t ? t + " placed in " + (row.S("level") ?? "the globals") : null,
                Fields =
                [
                    new SiteField("uuid", V.Id(uuid)),
                    new SiteField("raw|every attribute the level's file gives it", index.Raw(uuid, "every attribute")),
                    new SiteField("name", V.Text(row.S("name"))),
                    new SiteField("type", V.Text(row.S("type"))),
                    new SiteField("template", index.Ref(row.S("template"))),
                    new SiteField("display name", index.Handle(row.S("display_handle"))),
                    new SiteField("stats", index.Stat(row.S("stats"))),
                    new SiteField("level", sources.Level(row.S("level"))),
                    new SiteField("scope", V.Text(row.S("scope"))),
                    new SiteField("package", index.Package(row.S("package"))),
                    new SiteField("mod", V.Text(row.S("mod"))),
                ],
                Sections =
                [
                    PageKinds.Table("Speaks in", ["dialogue"], spoken, rowLimit,
                        r => [index.Dialog(r.S("name"))]),
                    ComponentKind.SeenOn(sources, row.S("name"), rowLimit),
                ],
            };
        }
    }
}

/// <summary>A dialogue: its speakers, its lines and the timelines that stage it.</summary>
public sealed class DialogKind : IPageKind
{
    public string Kind => "dialog";

    public string About => "a dialogue: who speaks in it, every line it can say, and the timelines that stage it";

    public bool Available(SiteSources sources) => sources.Game != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var index = sources.Game!;
        var db = index.Db;
        using var speakers = Grouper.ByLong(db, "SELECT dialog_id, idx, object, mapping FROM dialog_speakers ORDER BY dialog_id, idx");
        using var lines = Grouper.ByLong(db,
            "SELECT l.dialog_id, l.node, l.handle, l.line_id, n.speaker, n.constructor "
            + "FROM dialog_lines l LEFT JOIN dialog_nodes n ON n.dialog_id = l.dialog_id AND n.uuid = l.node ORDER BY l.dialog_id");
        using var flags = Grouper.ByLong(db,
            "SELECT dialog_id, node, kind, flag_type, flag, value FROM dialog_flags ORDER BY dialog_id");
        using var staged = Grouper.ByLong(db,
            "SELECT dialog_id, uuid, name, duration FROM timelines WHERE shadowed = 0 AND dialog_id IS NOT NULL ORDER BY dialog_id");
        using var nodes = Grouper.ByLong(db, "SELECT dialog_id, uuid, constructor, speaker FROM dialog_nodes ORDER BY dialog_id, rowid");
        using var links = Grouper.ByLong(db, "SELECT dialog_id, node, idx, child FROM dialog_links ORDER BY dialog_id, rowid");
        using var nodeAttributes = Grouper.ByLong(db, "SELECT dialog_id, node, name, value FROM dialog_node_attrs ORDER BY dialog_id, rowid");

        foreach (var row in db.Rows(
            "SELECT id, uuid, name, category, timeline_uuid, nodes, package, mod, path FROM dialogs WHERE shadowed = 0 ORDER BY id"))
        {
            var dialogId = row.L("id")!.Value;
            var id = Routes.DialogId(row.S("name") ?? row.S("uuid")!);
            yield return new PageDoc
            {
                Kind = Kind,
                Id = id,
                Route = Routes.Route(Kind, index.Slugs.Of(Kind)[id]),
                Title = id,
                Subtitle = "dialogue" + (row.S("category") is { Length: > 0 } c ? ", " + c : "")
                           + ", " + (row.L("nodes") ?? 0) + " nodes",
                Fields =
                [
                    new SiteField("name", V.Text(row.S("name"))),
                    new SiteField("uuid", V.Id(row.S("uuid"))),
                    new SiteField("category", V.Text(row.S("category"))),
                    new SiteField("timeline", index.Ref(row.S("timeline_uuid"))),
                    new SiteField("nodes", V.Num(row.L("nodes"))),
                    new SiteField("package", index.Package(row.S("package"))),
                    new SiteField("mod", V.Text(row.S("mod"))),
                    new SiteField("file", V.Path(row.S("path"))),
                ],
                Sections =
                [
                    PageKinds.Table("Speakers", ["slot", "object", "mapping"], speakers.Take(dialogId), PageKinds.Aside(rowLimit),
                        r => [V.Num(r.L("idx")), index.Ref(r.S("object")), V.Text(r.S("mapping"))]),
                    PageKinds.Table("Lines",
                        ["node", "speaker", "line|the line as English has it; it opens the line in every language, and the language picked at the top shows under it"],
                        lines.Take(dialogId), rowLimit,
                        r => [V.Id(r.S("node")), V.Num(r.L("speaker")), index.Line(r.S("handle"))]),
                    PageKinds.Table("Flags", ["node", "when", "type", "flag", "value"], flags.Take(dialogId), PageKinds.Aside(rowLimit),
                        r =>
                        [
                            V.Id(r.S("node")), V.Text(r.S("kind")), V.Text(r.S("flag_type")),
                            V.Id(r.S("flag")), V.Num(r.L("value")),
                        ]),
                    PageKinds.Table("Staged by", ["timeline", "name", "seconds"], staged.Take(dialogId), PageKinds.Aside(rowLimit),
                        r => [index.Ref(r.S("uuid")), V.Text(r.S("name")), V.Num(r.D("duration"))]),
                    PageKinds.Table("Nodes", ["node", "type|the node's constructor in the file", "speaker"], nodes.Take(dialogId), rowLimit,
                        r => [V.Id(r.S("uuid")), V.Text(r.S("constructor")), V.Num(r.L("speaker"))]),
                    PageKinds.Table("Where each node leads", ["node", "order", "next"], links.Take(dialogId), rowLimit,
                        r => [V.Id(r.S("node")), V.Num(r.L("idx")), V.Id(r.S("child"))]),
                    PageKinds.Table("Node attributes", ["node", "attribute", "value"], nodeAttributes.Take(dialogId), rowLimit,
                        r => [V.Id(r.S("node")), V.Text(r.S("name")), index.Auto(r.S("value"))]),
                    ComponentKind.SeenIn(sources, "while this dialogue played",
                        sources.Components?.InDialog(id), rowLimit),
                ],
            };
        }
    }
}

/// <summary>A timeline: the staging of a dialogue, its actors and what its tracks do.</summary>
public sealed class TimelineKind : IPageKind
{
    public string Kind => "timeline";

    public string About => "a timeline: the staging of a dialogue — its actors, its phases and what its tracks do";

    public bool Available(SiteSources sources) => sources.Game != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var index = sources.Game!;
        var db = index.Db;
        using var actors = Grouper.ByLong(db,
            "SELECT timeline_id, uuid, speaker, kind, is_player, resource, scene_template FROM timeline_actors ORDER BY timeline_id");
        using var phases = Grouper.ByLong(db,
            "SELECT timeline_id, idx, duration, dialog_node FROM timeline_phases ORDER BY timeline_id, idx");
        using var summary = Grouper.ByLong(db,
            "SELECT t.timeline_id, y.name AS type, count(*) AS tracks, sum(t.hides) AS hides, sum(t.shows) AS shows "
            + "FROM tracks t JOIN track_types y ON y.id = t.type_id GROUP BY t.timeline_id, y.name ORDER BY t.timeline_id");
        using var gear = Grouper.ByLong(db,
            "SELECT k.timeline_id, y.name AS type, a.uuid AS actor, k.phase, k.start, k.end, k.hides, k.shows "
            + "FROM tracks k JOIN track_types y ON y.id = k.type_id LEFT JOIN timeline_actors a ON a.id = k.actor_id "
            + "WHERE k.hides > 0 OR k.shows > 0 ORDER BY k.timeline_id");
        using var tracks = Grouper.ByLong(db,
            "SELECT k.timeline_id, k.id, y.name AS type, a.uuid AS actor, k.phase, k.start, k.end, k.keys "
            + "FROM tracks k JOIN track_types y ON y.id = k.type_id LEFT JOIN timeline_actors a ON a.id = k.actor_id ORDER BY k.timeline_id, k.id");
        using var keys = Grouper.ByLong(db,
            "SELECT t.timeline_id, k.track_id, k.idx, k.channel, k.time, k.value, k.attrs "
            + "FROM track_keys k JOIN tracks t ON t.id = k.track_id ORDER BY t.timeline_id, k.track_id, k.idx");

        foreach (var row in db.Rows(
            "SELECT id, uuid, name, dialog_id, duration, phases, components, has_scene, package, mod, path "
            + "FROM timelines WHERE shadowed = 0 AND uuid IS NOT NULL ORDER BY id"))
        {
            var timelineId = row.L("id")!.Value;
            var uuid = row.S("uuid")!;
            var dialog = row.L("dialog_id") is { } d
                ? db.One("SELECT name FROM dialogs WHERE id = ?", d)?.S("name")
                : null;
            yield return new PageDoc
            {
                Kind = Kind,
                Id = uuid,
                Route = Routes.Route(Kind, index.Slugs.Of(Kind)[uuid]),
                Title = row.S("name") ?? uuid,
                Subtitle = "timeline, " + (row.L("phases") ?? 0) + " phases, " + (row.L("components") ?? 0) + " components",
                Fields =
                [
                    new SiteField("uuid", V.Id(uuid)),
                    new SiteField("name", V.Text(row.S("name"))),
                    new SiteField("dialogue", index.Dialog(dialog)),
                    new SiteField("seconds", V.Num(row.D("duration"))),
                    new SiteField("phases", V.Num(row.L("phases"))),
                    new SiteField("components", V.Num(row.L("components"))),
                    new SiteField("scene file|whether the timeline has a scene file", V.Flag(row.L("has_scene") == 1)),
                    new SiteField("package", index.Package(row.S("package"))),
                    new SiteField("mod", V.Text(row.S("mod"))),
                    new SiteField("file", V.Path(row.S("path"))),
                ],
                Sections =
                [
                    PageKinds.Table("Actors", ["uuid", "slot|the speaker slot in the dialogue", "kind", "player", "stand-in|the template that stands in for the actor in the scene"],
                        actors.Take(timelineId), rowLimit,
                        r =>
                        [
                            index.Ref(r.S("uuid")), V.Num(r.L("speaker")), V.Text(r.S("kind")),
                            V.Flag(r.L("is_player") == 1), index.Ref(r.S("scene_template")),
                        ]),
                    PageKinds.Table("Phases", ["phase", "seconds", "dialogue node"], phases.Take(timelineId), PageKinds.Aside(rowLimit),
                        r => [V.Num(r.L("idx")), V.Num(r.D("duration")), V.Id(r.S("dialog_node"))]),
                    PageKinds.Table("Tracks by type", ["type", "tracks", "hides|keys that hide something", "shows|keys that show something"],
                        summary.Take(timelineId), PageKinds.Aside(rowLimit),
                        r => [V.Text(r.S("type")), V.Num(r.L("tracks")), V.Num(r.L("hides")), V.Num(r.L("shows"))]),
                    PageKinds.Table("Tracks that hide or show something",
                        ["type", "actor", "phase", "from", "to", "hides", "shows"], gear.Take(timelineId), rowLimit,
                        r =>
                        [
                            V.Text(r.S("type")), index.Ref(r.S("actor")), V.Num(r.L("phase")),
                            V.Num(r.D("start")), V.Num(r.D("end")), V.Num(r.L("hides")), V.Num(r.L("shows")),
                        ]),
                    PageKinds.Table("Every track", ["track|its number in the index, which the keys below name", "type", "actor", "phase", "from", "to", "keys"],
                        tracks.Take(timelineId), rowLimit,
                        r =>
                        [
                            V.Num(r.L("id")), V.Text(r.S("type")), index.Ref(r.S("actor")), V.Num(r.L("phase")),
                            V.Num(r.D("start")), V.Num(r.D("end")), V.Num(r.L("keys")),
                        ]),
                    PageKinds.Table("Keys", ["track", "key", "channel|for a track that shows armour: which piece", "time", "value", "attributes"],
                        keys.Take(timelineId), rowLimit,
                        r =>
                        [
                            V.Num(r.L("track_id")), V.Num(r.L("idx")), V.Num(r.L("channel")), V.Num(r.D("time")),
                            index.Auto(r.S("value")), V.Text(r.S("attrs")),
                        ]),
                ],
            };
        }
    }

}

/// <summary>A root template: what an object is made of before it is placed anywhere.</summary>
public sealed class TemplateKind : IPageKind
{
    public string Kind => "template";

    public string About => "a root template: what an object is made of before any level places it";

    public bool Available(SiteSources sources) => sources.Game != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var index = sources.Game!;
        var db = index.Db;
        using var children = Grouper.ByText(db,
            "SELECT parent, uuid, name, type FROM templates WHERE parent IS NOT NULL AND parent <> '' ORDER BY parent");
        using var placed = Grouper.ByText(db,
            "SELECT template, uuid, name, level FROM entities WHERE template IS NOT NULL AND template <> '' ORDER BY template");
        using var speakers = Grouper.ByText(db,
            "SELECT s.object, d.name FROM dialog_speakers s JOIN dialogs d ON d.id = s.dialog_id WHERE d.shadowed = 0 ORDER BY s.object");
        using var standIns = Grouper.ByText(db,
            "SELECT s.template, t.uuid, t.name FROM scene_actors s JOIN timelines t ON t.id = s.timeline_id "
            + "WHERE t.shadowed = 0 AND s.template IS NOT NULL AND s.template <> '' ORDER BY s.template");

        foreach (var row in db.Rows(
            "SELECT uuid, name, type, parent, display_handle, stats, visual, icon, package, mod FROM templates ORDER BY uuid"))
        {
            var uuid = row.S("uuid")!;
            yield return new PageDoc
            {
                Kind = Kind,
                Id = uuid,
                Route = Routes.Route(Kind, index.Slugs.Of(Kind)[uuid]),
                Title = row.S("name") ?? uuid,
                Subtitle = row.S("type") is { Length: > 0 } t ? t + " root template" : "root template",
                Fields =
                [
                    new SiteField("uuid", V.Id(uuid)),
                    new SiteField("raw|every attribute the template's file gives it", index.Raw(uuid, "every attribute")),
                    new SiteField("name", V.Text(row.S("name"))),
                    new SiteField("type", V.Text(row.S("type"))),
                    new SiteField("inherits from", index.Ref(row.S("parent"))),
                    new SiteField("display name", index.Handle(row.S("display_handle"))),
                    new SiteField("stats", index.Stat(row.S("stats"))),
                    new SiteField("visual", V.Id(row.S("visual"))),
                    new SiteField("icon", V.Id(row.S("icon"))),
                    new SiteField("package", index.Package(row.S("package"))),
                    new SiteField("mod", V.Text(row.S("mod"))),
                ],
                Sections =
                [
                    PageKinds.Table("Inherited by", ["template", "name", "type"], children.Take(uuid), rowLimit,
                        r => [index.Ref(r.S("uuid")), V.Text(r.S("name")), V.Text(r.S("type"))]),
                    PageKinds.Table("Placed as", ["object", "name", "level"], placed.Take(uuid), rowLimit,
                        r => [index.Ref(r.S("uuid")), V.Text(r.S("name")), sources.Level(r.S("level"))]),
                    PageKinds.Table("Speaks in", ["dialogue"], speakers.Take(uuid), PageKinds.Aside(rowLimit),
                        r => [index.Dialog(r.S("name"))]),
                    PageKinds.Table("Stands in for an actor of", ["timeline", "name"], standIns.Take(uuid), PageKinds.Aside(rowLimit),
                        r => [index.Ref(r.S("uuid")), V.Text(r.S("name"))]),
                    ComponentKind.SeenOn(sources, row.S("name"), rowLimit),
                ],
            };
        }
    }
}

/// <summary>A stat entry: one named row of the game's stat files, with its fields.</summary>
public sealed class StatKind : IPageKind
{
    public string Kind => "stat";

    public string About => "a stat entry: one named row of the game's stat files, with every field it sets";

    public bool Available(SiteSources sources) => sources.Game != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var index = sources.Game!;
        var db = index.Db;
        using var defs = Grouper.ByText(db, "SELECT name, type, parent, package, mod, file FROM stats ORDER BY name");
        using var fields = Grouper.ByText(db,
            "SELECT s.name, f.field, f.value FROM stats s JOIN stat_fields f ON f.stat_id = s.id ORDER BY s.name");
        using var byTemplates = Grouper.ByText(db,
            "SELECT stats, uuid, name FROM templates WHERE stats IS NOT NULL AND stats <> '' ORDER BY stats");
        using var byEntities = Grouper.ByText(db,
            "SELECT stats, uuid, name, level FROM entities WHERE stats IS NOT NULL AND stats <> '' ORDER BY stats");

        foreach (var row in db.Rows("SELECT DISTINCT name FROM stats WHERE name IS NOT NULL AND name <> '' ORDER BY name"))
        {
            var name = row.S("name")!;
            var definitions = defs.Take(name);
            var first = definitions.FirstOrDefault();
            yield return new PageDoc
            {
                Kind = Kind,
                Id = name,
                Route = Routes.Route(Kind, index.Slugs.Of(Kind)[name]),
                Title = name,
                Subtitle = (first?.S("type") ?? "stat") + (definitions.Count > 1
                    ? ", defined " + definitions.Count + " times" : ""),
                Fields =
                [
                    new SiteField("name", V.Id(name)),
                    new SiteField("type", V.Text(first?.S("type"))),
                    new SiteField("inherits from", index.Stat(first?.S("parent"))),
                    new SiteField("package", V.Text(first?.S("package"))),
                    new SiteField("mod", V.Text(first?.S("mod"))),
                    new SiteField("file", V.Path(first?.S("file"))),
                ],
                Sections =
                [
                    PageKinds.Table("Fields", ["field", "value"], fields.Take(name), rowLimit,
                        r => [V.Id(r.S("field")), index.Auto(r.S("value"))]),
                    PageKinds.Table("Definitions", ["type", "inherits from", "package", "mod", "file"], definitions, PageKinds.Aside(rowLimit),
                        r =>
                        [
                            V.Text(r.S("type")), index.Stat(r.S("parent")), V.Text(r.S("package")),
                            V.Text(r.S("mod")), V.Path(r.S("file")),
                        ]),
                    PageKinds.Table("Templates with these stats", ["template", "name"], byTemplates.Take(name), PageKinds.Aside(rowLimit),
                        r => [index.Ref(r.S("uuid")), V.Text(r.S("name"))]),
                    PageKinds.Table("Placed objects with these stats", ["object", "name", "level"],
                        byEntities.Take(name), PageKinds.Aside(rowLimit),
                        r => [index.Ref(r.S("uuid")), V.Text(r.S("name")), sources.Level(r.S("level"))]),
                ],
            };
        }
    }
}
