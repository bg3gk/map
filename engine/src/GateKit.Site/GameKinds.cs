namespace GateKit.Site;

/// <summary>
/// Every object the index read out of the game's files, with every attribute the file gives it:
/// the raw rows behind every other page, and the million objects that have no other page at all.
/// </summary>
public sealed class ObjectKind : IPageKind
{
    public string Kind => "object";

    public string About => "an object as the game's file declares it, with every one of its attributes";

    public bool Available(SiteSources sources) => sources.Game != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var index = sources.Game!;
        var db = index.Db;
        using var attributes = Grouper.ByLong(db,
            "SELECT a.object_id, n.name, a.value FROM attrs a JOIN names n ON n.id = a.attr_id ORDER BY a.object_id, a.rowid");
        foreach (var row in db.Rows(
            "SELECT o.id, o.key, o.name, o.truncated, n.name AS node, s.package, s.mod, s.path "
            + "FROM objects o JOIN names n ON n.id = o.node_id JOIN sources s ON s.id = o.source_id ORDER BY o.id"))
        {
            var rowId = row.L("id")!.Value;
            var id = index.ObjectId(rowId);
            var key = row.S("key");
            var name = row.S("name");
            var node = row.S("node");
            var path = row.S("path");
            var shown = index.Find(key);
            yield return new PageDoc
            {
                Kind = Kind,
                Id = id,
                Route = Routes.Route(Kind, index.Slugs.Of(Kind)[id]),
                Title = name is { Length: > 0 } ? name : key is { Length: > 0 } ? key : node + " " + rowId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Subtitle = node + " in " + path,
                Fields =
                [
                    new SiteField("key|the MapKey, UUID, ID or Name the node has, whichever it has", V.Id(key)),
                    new SiteField("name", V.Text(name)),
                    new SiteField("node|the kind of node in the file", V.Text(node)),
                    new SiteField("page|the page this object has among the game's content", shown == null
                        ? V.None
                        : V.Link(shown.Kind, shown.Id, shown.Route, shown.Name ?? shown.Id, shown.Kind)),
                    new SiteField("file", index.Slugs.Link("source", SiteIndex.SourceId(row.S("package"), path), path)),
                    new SiteField("package", index.Package(row.S("package"))),
                    new SiteField("mod", V.Text(row.S("mod"))),
                    new SiteField("cut|1 when the node had more nested attributes than the index keeps", V.Num(row.L("truncated"))),
                ],
                Sections =
                [
                    PageKinds.Table("Attributes", ["attribute", "value"], attributes.Take(rowId), int.MaxValue,
                        r => [V.Text(r.S("name")), index.Auto(r.S("value"))]),
                ],
            };
        }
    }
}

/// <summary>A file the index read objects out of, and every object it declares.</summary>
public sealed class SourceKind : IPageKind
{
    public string Kind => "source";

    public string About => "a file inside the game's packages that the index read, and every object it declares";

    public bool Available(SiteSources sources) => sources.Game != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var index = sources.Game!;
        var db = index.Db;
        using var objects = Grouper.ByLong(db,
            "SELECT o.source_id, o.id, o.key, o.name, n.name AS node FROM objects o JOIN names n ON n.id = o.node_id ORDER BY o.source_id, o.id");
        foreach (var row in db.Rows("SELECT id, package, mod, path, region, objects FROM sources ORDER BY id"))
        {
            var id = SiteIndex.SourceId(row.S("package"), row.S("path"));
            var listed = objects.Take(row.L("id")!.Value);
            yield return new PageDoc
            {
                Kind = Kind,
                Id = id,
                Route = Routes.Route(Kind, index.Slugs.Of(Kind)[id]),
                Title = row.S("path") ?? id,
                Subtitle = "a file of " + row.S("package"),
                Fields =
                [
                    new SiteField("path", V.Path(row.S("path"))),
                    new SiteField("package", index.Package(row.S("package"))),
                    new SiteField("mod", V.Text(row.S("mod"))),
                    new SiteField("region|the region of the file the objects were read from", V.Text(row.S("region"))),
                    new SiteField("objects", V.Num(listed.Count)),
                ],
                Sections =
                [
                    PageKinds.Table("Objects", ["object", "node", "key"], listed, rowLimit,
                        r =>
                        {
                            var objectId = index.ObjectId(r.L("id")!.Value);
                            var label = r.S("name") is { Length: > 0 } n ? n : r.S("key") is { Length: > 0 } k ? k : objectId;
                            return [index.Slugs.Link("object", objectId, label), V.Text(r.S("node")), V.Id(r.S("key"))];
                        }),
                ],
            };
        }
    }
}

/// <summary>A package of the game: every file inside it, and the files the index read objects out of.</summary>
public sealed class PackageKind : IPageKind
{
    public string Kind => "package";

    public string About => "a package of the game: every file inside it, and the ones the index read";

    public bool Available(SiteSources sources) => sources.Game != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var index = sources.Game!;
        var db = index.Db;
        using var files = Grouper.ByText(db, "SELECT package, path, ext, size FROM files WHERE package IS NOT NULL ORDER BY package, path");
        using var read = Grouper.ByText(db, "SELECT package, path, objects FROM sources WHERE package IS NOT NULL ORDER BY package, path");
        foreach (var name in index.Slugs.Of(Kind).Keys.Order(StringComparer.Ordinal))
        {
            var inside = files.Take(name);
            var indexed = read.Take(name);
            yield return new PageDoc
            {
                Kind = Kind,
                Id = name,
                Route = Routes.Route(Kind, index.Slugs.Of(Kind)[name]),
                Title = name,
                Subtitle = "a package of the game",
                Fields =
                [
                    new SiteField("files", V.Num(inside.Count)),
                    new SiteField("bytes|the files' sizes added up, as the package lists them", V.Num(inside.Sum(f => f.L("size") ?? 0))),
                    new SiteField("read|how many of its files the index read objects out of", V.Num(indexed.Count)),
                ],
                Sections =
                [
                    PageKinds.Table("Files the index read", ["file", "objects"], indexed, rowLimit,
                        r => [index.Slugs.Link("source", SiteIndex.SourceId(name, r.S("path")), r.S("path")), V.Num(r.L("objects"))]),
                    PageKinds.Table("Every file", ["path", "type", "bytes"], inside, rowLimit,
                        r => [V.Path(r.S("path")), V.Text(r.S("ext")), V.Num(r.L("size"))]),
                ],
            };
        }
    }
}

/// <summary>A name of the game's story scripts: a query, a call, an event or a database, and every script file that uses it.</summary>
public sealed class OsirisKind : IPageKind
{
    public string Kind => "osiris";

    public string About => "a name of the story scripts - a query, a call, an event or a database - and every script that uses it";

    public bool Available(SiteSources sources) => sources.Game != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var index = sources.Game!;
        var db = index.Db;
        var declared = db.Rows("SELECT name, kind, params, ids, arity FROM osiris_functions")
            .GroupBy(r => r.S("name") ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        using var uses = Grouper.ByText(db,
            "SELECT u.name, t.package, t.path, u.role, u.uses FROM osiris_uses u LEFT JOIN texts t ON t.id = u.text_id ORDER BY u.name, t.path");
        foreach (var name in index.Slugs.Of(Kind).Keys.Order(StringComparer.Ordinal))
        {
            declared.TryGetValue(name, out var row);
            var used = uses.Take(name);
            yield return new PageDoc
            {
                Kind = Kind,
                Id = name,
                Route = Routes.Route(Kind, index.Slugs.Of(Kind)[name]),
                Title = name,
                Subtitle = row?.S("kind") is { Length: > 0 } kind ? "a story " + kind : "a name the story scripts use",
                Fields =
                [
                    new SiteField("kind|what the story header declares it as; empty for a database the scripts make themselves", V.Text(row?.S("kind"))),
                    new SiteField("params", V.Text(row?.S("params"))),
                    new SiteField("arity", V.Num(row?.L("arity"))),
                    new SiteField("ids|the numbers the story header gives it", V.Id(row?.S("ids"))),
                    new SiteField("scripts|how many script files use it", V.Num(used.Count)),
                ],
                Sections =
                [
                    PageKinds.Table("Used in", ["script", "package", "role|what the script does with it: defines it, fills it at start, reads it in a rule, ...", "uses"], used, rowLimit,
                        r => [V.Path(r.S("path")), index.Package(r.S("package")), V.Text(r.S("role")), V.Num(r.L("uses"))]),
                ],
            };
        }
    }
}
