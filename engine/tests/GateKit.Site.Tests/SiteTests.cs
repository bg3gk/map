using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GateKit.Site.Tests;

/// <summary>A site rendered once from the synthetic index, shared by every test in the class.</summary>
public sealed class RenderedSite : IDisposable
{
    public string Root { get; }

    public SiteResult Result { get; }

    public RenderedSite()
    {
        Root = Path.Combine(Path.GetTempPath(), "gatekit-site-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        var index = DemoIndex.Write(Path.Combine(Root, "index.sqlite"));
        var symbols = DemoSymbols.Write(Path.Combine(Root, "symbols.sqlite"));
        var coverage = DemoCoverage.Write(Path.Combine(Root, "coverage"));
        Result = SiteBuilder.Build(new SiteOptions
        {
            IndexDatabase = index,
            SymbolDatabase = symbols,
            Coverage = coverage,
            Out = Path.Combine(Root, "site"),
            Base = "/",
            RowLimit = 2,
        }, _ => { });
    }

    public string Site => Path.Combine(Root, "site");

    /// <summary>The game index the site was built from.</summary>
    public string IndexDb => Path.Combine(Root, "index.sqlite");

    /// <summary>The database the server searches.</summary>
    public string SearchDb => Path.Combine(Site, SearchDatabase.FileName);

    /// <summary>The rendered site, read the way the server reads it.</summary>
    public SiteReader Reader => _reader ??= new SiteReader(Path.Combine(Site, SiteStore.FileName));

    private SiteReader? _reader;

    /// <summary>The shell every page is drawn in: the server sends it for every page's address.</summary>
    public string Shell => Text(SiteBuilder.Shell);

    /// <summary>A file of the site, as text.</summary>
    public string Text(string path) => Reader.Text(path) ?? throw new FileNotFoundException(path);

    /// <summary>Whether the site has a file at this path.</summary>
    public bool Has(string path) => Reader.Has(path);

    public JsonElement Json(string route)
    {
        using var document = JsonDocument.Parse(Reader.Read(route + ".json") ?? throw new FileNotFoundException(route + ".json"));
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
            // A temporary folder that will not go is not a failing test.
        }
    }
}

public sealed class SiteTests(RenderedSite site) : IClassFixture<RenderedSite>
{
    private static string Route(string kind, string id) => kind + "/" + id;

    [Fact]
    public void RendersAPageAndATwinForEveryObject()
    {
        Assert.Equal(1, site.Result.Pages["entity"]);
        Assert.Equal(1, site.Result.Pages["dialog"]);
        Assert.Equal(1, site.Result.Pages["timeline"]);
        Assert.Equal(1, site.Result.Pages["template"]);
        // Two weapons and one stat whose name needs escaping.
        Assert.Equal(3, site.Result.Pages["stat"]);
        Assert.True(site.Has("stat/" + Routes.Slug(DemoIndex.OddStatName) + ".json"));
        foreach (var route in new[]
        {
            Route("entity", DemoIndex.EntityUuid),
            Route("dialog", DemoIndex.DialogId),
            Route("timeline", DemoIndex.TimelineUuid),
            Route("template", DemoIndex.TemplateUuid),
            Route("stat", DemoIndex.StatName),
        })
        {
            Assert.True(site.Has(route + ".json"), route + ".json");
            // No HTML of its own: the address is drawn by the shell from the JSON.
            Assert.False(site.Has(route), route);
        }
        Assert.Equal([SiteBuilder.Shell], site.Reader.Files().Select(f => f.Path).Where(p => p.EndsWith(".html", StringComparison.Ordinal)));
        // The whole site is two files: its pages, and what the server searches.
        Assert.Equal([SiteStore.FileName, SearchDatabase.FileName],
            Directory.GetFiles(site.Site).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ClientAndFallbackAndLlmsTxtAreThere()
    {
        foreach (var file in new[] { SiteBuilder.Shell, "style.css", "app.js", "llms.txt", "robots.txt" })
        {
            Assert.True(site.Has(file), file);
        }
        var llms = site.Text("llms.txt");
        Assert.Contains("/dialog/<name>", llms, StringComparison.Ordinal);
        Assert.Contains("\"links\"", llms, StringComparison.Ordinal);
        Assert.Contains("{\"route\": \"template/<uuid>\"}", llms, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", llms, StringComparison.Ordinal);

        // Out of the search engines, as Norbyte's is: the shell says noindex, and robots.txt
        // keeps everything but llms.txt from being crawled at all.
        Assert.Contains("<meta name=\"robots\" content=\"noindex, nofollow\">", site.Shell, StringComparison.Ordinal);
        var robots = site.Text("robots.txt");
        Assert.Contains("Disallow: /", robots, StringComparison.Ordinal);
        Assert.Contains("Allow: /llms.txt", robots, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageIsItsJsonAndTheShellHasWhatItTakesToDrawIt()
    {
        // The shell carries the list of kinds it draws, and both notices, as data the builder
        // decided - nothing about a page's frame is a second copy kept in the script.
        var shell = site.Shell;
        foreach (var kind in Routes.Kinds)
        {
            Assert.Contains("\"" + kind + "\"", shell, StringComparison.Ordinal);
        }
        Assert.Contains("window.SITE_NOTICES = {\"game\":", shell, StringComparison.Ordinal);
        Assert.Contains("\"ours\":", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", shell, StringComparison.Ordinal);
        // The notices hold HTML, and inside a script `</` must never appear written out.
        var script = shell[shell.IndexOf("window.SITE_NOTICES", StringComparison.Ordinal)..];
        script = script[..script.IndexOf("</script>", StringComparison.Ordinal)];
        Assert.DoesNotContain("</", script, StringComparison.Ordinal);

        foreach (var route in new[]
        {
            Route("dialog", DemoIndex.DialogId),
            Route("timeline", DemoIndex.TimelineUuid),
            Route("template", DemoIndex.TemplateUuid),
            Route("entity", DemoIndex.EntityUuid),
            Route("stat", DemoIndex.StatName),
        })
        {
            var json = site.Json(route);
            Assert.Equal(route, json.GetProperty("route").GetString());
            Assert.Equal("game", json.GetProperty("index").GetProperty("origin").GetString());

            // `links` is every link the page draws, and nothing else: an agent following the list
            // walks exactly what a person clicking through the page can reach.
            var drawn = new HashSet<string>(StringComparer.Ordinal);
            void Walk(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("route", out var r))
                {
                    drawn.Add(r.GetString()!);
                }
            }
            foreach (var field in json.GetProperty("fields").EnumerateObject())
            {
                Walk(field.Value);
            }
            foreach (var cell in json.GetProperty("sections").EnumerateArray()
                         .SelectMany(s => s.GetProperty("rows").EnumerateArray()).SelectMany(r => r.EnumerateArray()))
            {
                Walk(cell);
            }
            var listed = json.GetProperty("links").EnumerateArray()
                .Select(l => l.GetProperty("route").GetString()!).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(drawn, listed);
        }
    }

    [Fact]
    public void FollowingThreeLinksGetsFromADialogueToATemplate()
    {
        // A dialogue names its timeline; the timeline names the template standing in for an actor;
        // the template names the object placed from it. Clicking through is the whole point.
        var dialog = site.Json(Route("dialog", DemoIndex.DialogId));
        var toTimeline = Follow(dialog, "timeline");
        Assert.Equal(Route("timeline", DemoIndex.TimelineUuid), toTimeline);

        var timeline = site.Json(toTimeline);
        var toTemplate = Follow(timeline, "template");
        Assert.Equal(Route("template", DemoIndex.TemplateUuid), toTemplate);

        var template = site.Json(toTemplate);
        Assert.Equal(Route("entity", DemoIndex.EntityUuid), Follow(template, "entity"));

    }

    /// <summary>A link's kind is the first segment of its route; that is the whole rule.</summary>
    private static string Follow(JsonElement page, string kind) =>
        page.GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("route").GetString()!)
            .First(r => r.StartsWith(kind + "/", StringComparison.Ordinal));

    [Fact]
    public void ALocalizationHandleCarriesWhatItSays()
    {
        var dialog = site.Json(Route("dialog", DemoIndex.DialogId));
        var handle = dialog.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("route").GetString()!.StartsWith("loca/", StringComparison.Ordinal));
        Assert.Equal("loca/" + DemoIndex.Handle, handle.GetProperty("route").GetString());
        // In a dialogue's lines the text is the link itself, and every other language rides with it.
        Assert.Equal(DemoIndex.EnglishLine, handle.GetProperty("text").GetString());
        var texts = handle.GetProperty("texts");
        Assert.Equal(DemoIndex.RussianLine, texts.GetProperty("Russian").GetString());
        Assert.Equal(DemoIndex.JapaneseLine, texts.GetProperty("Japanese").GetString());
        Assert.False(texts.TryGetProperty("English", out _));

        // The line has a page of its own, with every language and where it is said.
        var line = site.Json("loca/" + DemoIndex.Handle);
        Assert.Equal(DemoIndex.EnglishLine, line.GetProperty("title").GetString());
        var languages = line.GetProperty("sections").EnumerateArray().First(s => s.GetProperty("title").GetString() == "In every language")
            .GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()!).ToArray();
        Assert.Equal(["English", "Japanese", "Russian"], languages);
        Assert.Contains(line.GetProperty("links").EnumerateArray(), l => l.GetProperty("route").GetString() == "dialog/" + DemoIndex.DialogId);
    }

    [Fact]
    public void EveryPageSaysWhatIsMissingAndWhatWasPromised()
    {
        // The page says it is the game's; the shell holds what the game's pages say at their foot.
        Assert.Equal("game", site.Json(Route("stat", DemoIndex.StatName)).GetProperty("index").GetProperty("origin").GetString());
        var shell = site.Shell;
        Assert.Contains("map fetch", shell, StringComparison.Ordinal);
        Assert.Contains("how to reach us", shell, StringComparison.Ordinal);
        Assert.Contains("github.com/bg3gk/map", shell, StringComparison.Ordinal);

        // The JSON twin keeps it to two strings, because it is repeated on every page; the
        // sentences they stand for are in llms.txt, which they point at.
        var json = site.Json(Route("stat", DemoIndex.StatName)).GetProperty("index");
        Assert.Equal("map fetch", json.GetProperty("fullIndex").GetString());
        Assert.Equal("/llms.txt", json.GetProperty("about").GetString());
        Assert.Contains("ask for it to be taken down",
            site.Text("llms.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public void ATableThatWasCutSaysSo()
    {
        // The fixture renders with a limit of two rows; the stat has three fields.
        var json = site.Json(Route("stat", DemoIndex.StatName));
        var fields = json.GetProperty("sections").EnumerateArray().First(s => s.GetProperty("title").GetString() == "Fields");
        Assert.Equal(2, fields.GetProperty("shown").GetInt32());
        Assert.Equal(3, fields.GetProperty("total").GetInt32());
    }

    [Fact]
    public void TheSearchDatabaseAnswersASearch()
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = site.SearchDb,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        db.Open();

        // A name, the way the client asks for it.
        Assert.Equal(
            "dialog/" + DemoIndex.DialogId,
            Query(db, "SELECT o.route FROM obj_fts f JOIN obj o ON o.id = f.rowid WHERE obj_fts MATCH '\"testtalk\"*' ORDER BY rank LIMIT 1"));

        // A line of the game's text, in any language it was built with.
        Assert.Equal(
            DemoIndex.Handle,
            Query(db, "SELECT l.handle FROM loca_fts f JOIN loca l ON l.id = f.rowid WHERE loca_fts MATCH '\"sharp\"*' LIMIT 1"));

        // The name of an object that has no page: found, and honest about having none.
        Assert.Equal(
            "CAMP_Flammable_Tag",
            Query(db, "SELECT g.name FROM gname_fts f JOIN gname g ON g.id = f.rowid WHERE gname_fts MATCH '\"flammable\"*' LIMIT 1"));

        // An id resolves straight to an address, which is how the client jumps to a page.
        Assert.Equal("template/" + DemoIndex.TemplateUuid,
            Query(db, $"SELECT route FROM obj WHERE ident = '{DemoIndex.TemplateUuid}'"));

        // Every language is searchable, and a line of Japanese is found by any part of it.
        Assert.Equal("Russian", Query(db, "SELECT l.lang FROM loca_fts f JOIN loca l ON l.id = f.rowid WHERE loca_fts MATCH '\"another\"*' LIMIT 1"));
        Assert.Equal(DemoIndex.Handle,
            Query(db, "SELECT l.handle FROM loca_cjk_fts f JOIN loca_cjk l ON l.id = f.rowid WHERE loca_cjk_fts MATCH '\"鋭い短\"' LIMIT 1"));
    }

    private static string? Query(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }
}
