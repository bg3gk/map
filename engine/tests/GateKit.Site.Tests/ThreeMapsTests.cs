using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GateKit.Site.Tests;

/// <summary>
/// The point of the site: three maps that are one web. These tests walk the thread the project
/// spent weeks walking by hand — an engine component to where it was seen, the sighting to the
/// dialogue and to the object, the object to its level — and then walk it back the other way.
/// </summary>
public sealed class ThreeMapsTests(RenderedSite site) : IClassFixture<RenderedSite>
{
    private const string ComponentRoute = "component/ecl.TestComponent";
    private const string SymbolRoute = "symbol/ecl.TestComponent";
    private const string EnumRoute = "enum/ecl.TestFlags";
    private static readonly string DialogRoute = "dialog/" + DemoIndex.DialogId;
    private static readonly string EntityRoute = "entity/" + DemoIndex.EntityUuid;
    private static readonly string LevelRoute = "level/" + DemoCoverage.Level;

    [Fact]
    public void AllThreeMapsAreRendered()
    {
        Assert.Equal(1, site.Result.Pages["component"]);
        Assert.Equal(2, site.Result.Pages["symbol"]);
        Assert.Equal(1, site.Result.Pages["enum"]);
        Assert.Equal(1, site.Result.Pages["level"]);
        foreach (var route in new[] { ComponentRoute, SymbolRoute, EnumRoute, LevelRoute })
        {
            Assert.True(site.Has(route + ".json"), route + ".json");
        }
    }

    [Fact]
    public void AComponentLeadsToWhereItWasSeen()
    {
        var component = site.Json(ComponentRoute);
        Assert.Equal("ecl::TestComponent", component.GetProperty("title").GetString());
        Assert.Equal("ecl.TestComponent", component.GetProperty("id").GetString());

        var links = Links(component);
        // The class it is declared as, among the engine's own symbols.
        Assert.Contains(SymbolRoute, links);
        // The dialogue it was alive in, and the object it was alive on: the capture writes those
        // as `dialog:<name>_<uuid>` and `enemies:<object>`, and both become links.
        Assert.Contains(DialogRoute, links);
        Assert.Contains(EntityRoute, links);
        // And the level the sighting was taken in.
        Assert.Contains(LevelRoute, links);

        // The field whose type is an enumeration of the engine leads to that enumeration.
        Assert.Contains(EnumRoute, Links(site.Json(SymbolRoute)));
    }

    [Fact]
    public void AndEveryOneOfThoseLeadsBack()
    {
        foreach (var route in new[] { DialogRoute, EntityRoute, LevelRoute, SymbolRoute })
        {
            Assert.Contains(ComponentRoute, Links(site.Json(route)));
        }

        // Said in the words a reader sees, not only as a link in a list.
        Assert.Contains("Engine components seen on this object", Titles(site.Json(EntityRoute)));
        Assert.Contains("Engine components seen while this dialogue played", Titles(site.Json(DialogRoute)));
        Assert.Contains("Engine components seen while this level was loaded", Titles(site.Json(LevelRoute)));
        Assert.Equal(ComponentRoute, site.Json(SymbolRoute).GetProperty("fields").GetProperty("component").GetProperty("route").GetString());
    }

    [Fact]
    public void TheWholeThreadIsOneWalk()
    {
        // component -> the object it was seen on -> the level that places it, and the level back
        // to the component. Four hops, each one a link the page draws.
        var component = site.Json(ComponentRoute);
        var entity = Assert.Single(Links(component), r => r == EntityRoute);
        var level = Assert.Single(Links(site.Json(entity)), r => r == LevelRoute);
        Assert.Contains(ComponentRoute, Links(site.Json(level)));

    }

    [Fact]
    public void WhatWeWorkedOutSaysItIsOursAndNotTheGames()
    {
        // The game's own text is published under a promise; what we measured is not the game's at
        // all, and its pages say the other thing. Both are on the same site.
        // Each page names whose it is; the shell holds the two sentences and draws the right one.
        foreach (var route in new[] { ComponentRoute, SymbolRoute, EnumRoute })
        {
            var index = site.Json(route).GetProperty("index");
            Assert.Equal("ours", index.GetProperty("origin").GetString());
            Assert.Equal("map fetch", index.GetProperty("fullIndex").GetString());
        }
        foreach (var route in new[] { DialogRoute, EntityRoute, LevelRoute })
        {
            var index = site.Json(route).GetProperty("index");
            Assert.Equal("game", index.GetProperty("origin").GetString());
            Assert.Equal("map fetch", index.GetProperty("fullIndex").GetString());
        }

        var notices = SiteNotice.All.ToDictionary(n => n.Origin);
        Assert.Contains("our own research", notices["ours"].WhoseHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("© Larian", notices["ours"].WhoseHtml, StringComparison.Ordinal);
        Assert.Contains("© Larian Studios", notices["game"].WhoseHtml, StringComparison.Ordinal);
        Assert.Contains("our own research, not game content", site.Shell, StringComparison.Ordinal);

        // What the site is, and a way to reach us, on every page whichever map it is. Said once
        // and plainly: the footer used to announce "legal questions and takedown requests", which
        // told a reader more about our nerves than about the site.
        Assert.Contains("A free fan project for Baldur", site.Shell, StringComparison.Ordinal);
        Assert.Contains("Not affiliated with Larian Studios", site.Shell, StringComparison.Ordinal);
        Assert.Contains("how to reach us", site.Shell, StringComparison.Ordinal);
        Assert.Contains("github.com/bg3gk/map#readme", site.Shell, StringComparison.Ordinal);
    }

    [Fact]
    public void AComponentPageCarriesWhatWasMeasured()
    {
        var json = site.Json(ComponentRoute);
        var fields = json.GetProperty("fields");
        Assert.Equal(12, fields.GetProperty("size").GetInt32());
        Assert.Equal("confirmed", fields.GetProperty("status").GetString());
        Assert.Equal(DemoSymbols.ExtName, fields.GetProperty("Lua name").GetProperty("id").GetString());

        // A short name, and the longer explanation behind it, once per page.
        var hints = json.GetProperty("hints");
        Assert.Equal("how far the mapping got", hints.GetProperty("status").GetString());
        Assert.StartsWith("how we know it", hints.GetProperty("source").GetString(), StringComparison.Ordinal);

        var sections = json.GetProperty("sections").EnumerateArray().ToArray();
        var layout = sections.First(s => s.GetProperty("title").GetString() == "Fields");
        Assert.Equal(6, layout.GetProperty("total").GetInt32());
        Assert.Equal("+0x0", layout.GetProperty("rows")[0][0].GetProperty("id").GetString());

        // A struct the component holds is a table of its own, and an open question is said out loud.
        Assert.Contains(sections, s => s.GetProperty("title").GetString()!.StartsWith("Struct TestPair", StringComparison.Ordinal));
        Assert.Contains(sections, s => s.GetProperty("title").GetString() == "Still open");

        // The census lists a component from both sides; the side it belongs to comes from its name.
        Assert.Contains("on the client", json.GetProperty("subtitle").GetString(), StringComparison.Ordinal);
        var census = sections.First(s => s.GetProperty("title").GetString() == "What the extender's census says");
        Assert.Equal(2, census.GetProperty("total").GetInt32());
    }

    [Fact]
    public void AddressesOfTheEngineWriteQualifiedNamesWithADot()
    {
        Assert.Equal("ecl.TestComponent", Routes.TypeId("ecl::TestComponent"));
        Assert.True(Routes.IsPlainTypeName("ecl::TestComponent"));
        // A template instantiation is a spelling of a class, not a class: it gets no address.
        Assert.False(Routes.IsPlainTypeName("ls::AnyArray<ls::PagedArray, ls::TaggedAllocator>"));
        Assert.False(Routes.IsPlainTypeName(new string('x', 200)));

        // And llms.txt says the rule, so an agent builds the address instead of guessing it.
        var llms = site.Text("llms.txt");
        Assert.Contains("component/ecl.ActiveTurnComponent", llms, StringComparison.Ordinal);
        Assert.Contains("/symbol/<name>", llms, StringComparison.Ordinal);
        Assert.Contains("/component/<name>", llms, StringComparison.Ordinal);
        Assert.Contains("1 component pages", llms, StringComparison.Ordinal);
    }

    [Fact]
    public void ASightingLabelIsReadTheWayTheCaptureWroteIt()
    {
        var (situation, dialog, target) = ComponentMap.Parse(DemoCoverage.DialogLabel);
        Assert.Equal("dialog", situation);
        Assert.Equal(DemoIndex.DialogId, dialog);
        Assert.Null(target);

        (situation, dialog, target) = ComponentMap.Parse(DemoCoverage.ObjectLabel);
        Assert.Equal("enemies", situation);
        Assert.Null(dialog);
        Assert.Equal(DemoIndex.EntityName, target);

        // A label with no colon is a situation that named nothing in particular.
        Assert.Equal(("idle", null, null), ComponentMap.Parse("idle"));
    }

    [Fact]
    public void TheSearchDatabaseFindsAllThreeMaps()
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = site.SearchDb,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        db.Open();

        // A name of the engine is cut where a reader would cut it, so a word inside it finds it:
        // the tokenizer alone would see `ecl` and `TestComponent` and nothing else.
        Assert.Equal(ComponentRoute, Find(db, "\"test\"*", "component"));
        Assert.Equal(SymbolRoute, Find(db, "\"test\"*", "symbol"));
        Assert.Equal(LevelRoute, Find(db, "\"avernus\"*", "level"));
        Assert.Equal("dialog/" + DemoIndex.DialogId, Find(db, "\"talk\"*", "dialog"));

        // Two words are joined with AND, not written next to each other. A space would make them
        // one phrase - the words adjacent, in that order - and the index of the names that have
        // no page is built without positions, so a phrase query over it is refused outright.
        Assert.Equal("CAMP_Flammable_Tag", Scalar(db,
            "SELECT g.name FROM gname_fts f JOIN gname g ON g.id = f.rowid "
            + "WHERE gname_fts MATCH '\"camp\"* AND \"flammable\"*' LIMIT 1"));
        Assert.Equal(ComponentRoute, Find(db, "\"ecl\"* AND \"component\"*", "component"));

        // A name hit says what kind of node it came from; the word is kept once and named by number.
        Assert.Equal("Tags", Scalar(db, "SELECT n.name FROM gname g JOIN gnode n ON n.id = g.node WHERE g.name = 'CAMP_Flammable_Tag'"));

        // And the id of a class resolves straight to its address, with the dot the URL uses.
        Assert.Equal(ComponentRoute, Scalar(db, "SELECT route FROM obj WHERE ident = 'ecl.TestComponent' AND kind = 'component'"));
    }

    [Fact]
    public void TheWordSplitterCutsNamesTheWayAReaderDoes()
    {
        Assert.Equal("ecl::Active Turn Component", SearchDatabase.Words("ecl::ActiveTurnComponent"));
        Assert.Equal("TL Show Armor", SearchDatabase.Words("TLShowArmor"));
        Assert.Equal("CAMP_Test Talk_002", SearchDatabase.Words("CAMP_TestTalk_002"));
        // Underscores already end a word for the tokenizer, so nothing is added around them.
        Assert.Equal("TUT_Avernus_C", SearchDatabase.Words("TUT_Avernus_C"));
    }

    private static string? Find(SqliteConnection db, string match, string kind) =>
        Scalar(db, "SELECT o.route FROM obj_fts f JOIN obj o ON o.id = f.rowid "
            + $"WHERE obj_fts MATCH '{match}' AND o.kind = '{kind}' ORDER BY rank LIMIT 1");

    private static string? Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    /// <summary>The titles of a page's sections.</summary>
    private static string[] Titles(JsonElement page) =>
        [.. page.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("title").GetString()!)];

    /// <summary>Every route the JSON page says it leads to.</summary>
    private static string[] Links(JsonElement page) =>
        [.. page.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("route").GetString()!)];
}
