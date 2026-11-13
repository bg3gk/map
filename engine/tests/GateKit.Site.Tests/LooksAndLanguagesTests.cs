using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GateKit.Site.Tests;

/// <summary>
/// What a reader sees before reading anything: the name of the site, the names of columns, and the
/// languages it can show. The client draws all of it, so these tests hold the data it draws from.
/// </summary>
public sealed class LooksAndLanguagesTests(RenderedSite site) : IClassFixture<RenderedSite>
{
    [Fact]
    public void TheHeaderSaysGateKitMapBesideAYellowMarker()
    {
        var shell = site.Shell;
        Assert.Contains("GateKit Map</a>", shell, StringComparison.Ordinal);
        Assert.Contains("<svg class=\"pin\"", shell, StringComparison.Ordinal);
        Assert.Contains("#F5C518", shell, StringComparison.Ordinal);
        // The same marker is the tab's icon.
        Assert.Contains("<link rel=\"icon\" href=\"data:image/svg+xml,", shell, StringComparison.Ordinal);
        Assert.StartsWith("# GateKit Map", site.Text("llms.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureHasADieToLandOnAndACodeThatMeansSomething()
    {
        // The client turns the die red and writes the code where the 20 is; the pieces it needs
        // are in the shell, and the codes it uses are written down for whoever reads them.
        var shell = site.Shell;
        foreach (var piece in (string[])["id=\"d20n\"", "id=\"why\"", "id=\"again\"", "id=\"dismiss\"", "id=\"d20r\""])
        {
            Assert.Contains(piece, shell, StringComparison.Ordinal);
        }
        var llms = site.Text("llms.txt");
        Assert.Contains("## When something fails", llms, StringComparison.Ordinal);
        // The client's own list and the written one are the same list.
        var script = site.Text("app.js");
        var block = script[script.IndexOf("var FAILED = {", StringComparison.Ordinal)..];
        block = block[..block.IndexOf("};", StringComparison.Ordinal)];
        var inClient = System.Text.RegularExpressions.Regex.Matches(block, @"^\s*(\d+):", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToArray();
        var written = System.Text.RegularExpressions.Regex.Matches(llms, @"^\| `(\d+)` \|", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(["1", "2", "4"], inClient);
        Assert.Equal(inClient, written);
    }

    [Fact]
    public void NoColumnAndNoFieldHasALongName()
    {
        // A long name widens a table and turns a record into prose; the explanation belongs in the
        // hint, which the page shows as a tooltip. Every kind on the synthetic site is checked.
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (route, bytes) in site.Reader.Pages())
        {
            using var document = JsonDocument.Parse(bytes);
            var page = document.RootElement;
            kinds.Add(page.GetProperty("kind").GetString()!);
            foreach (var field in page.GetProperty("fields").EnumerateObject())
            {
                Assert.True(field.Name.Length <= Hints.MaxName, $"{route}: field \"{field.Name}\"");
            }
            foreach (var column in page.GetProperty("sections").EnumerateArray()
                         .SelectMany(s => s.GetProperty("columns").EnumerateArray()))
            {
                Assert.True(column.GetString()!.Length <= Hints.MaxName, $"{route}: column \"{column.GetString()}\"");
                Assert.DoesNotContain("|", column.GetString()!, StringComparison.Ordinal);
            }
        }
        Assert.Equal(Routes.Kinds.Order(StringComparer.Ordinal), kinds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheShellAsksForTheClientByWhatItHolds()
    {
        // A browser keeps the client for minutes and never keeps the shell; after a new build, an
        // old script must not draw the new shell.
        Assert.Matches(@"src=""/app\.js\?v=[0-9a-f]{10}""", site.Shell);
        Assert.Matches(@"href=""/style\.css\?v=[0-9a-f]{10}""", site.Shell);
    }

    [Fact]
    public void ThereIsNoLanguageToPick()
    {
        // A line of text has every language on its own page, and search covers all of them, so
        // the header has nothing to choose.
        Assert.DoesNotContain("id=\"lang\"", site.Shell, StringComparison.Ordinal);
        Assert.DoesNotContain("SITE_LANGS", site.Shell, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLanguageIsInTheSearchDatabase()
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = site.SearchDb,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT (SELECT group_concat(lang) FROM (SELECT DISTINCT lang FROM loca ORDER BY lang)) || '|' || "
            + "(SELECT group_concat(lang) FROM (SELECT DISTINCT lang FROM loca_cjk ORDER BY lang))";
        Assert.Equal("English,Russian|Japanese", command.ExecuteScalar()?.ToString());
    }
}
