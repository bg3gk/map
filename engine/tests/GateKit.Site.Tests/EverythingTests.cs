using System.Text.Json;

namespace GateKit.Site.Tests;

/// <summary>
/// Nothing held back: every object the index read with every attribute, every file and package,
/// every name of the story scripts, and every function of the engine's binaries, with the
/// addresses a reader with a decompiler open needs.
/// </summary>
public sealed class EverythingTests(RenderedSite site) : IClassFixture<RenderedSite>
{
    private static string[] Column(JsonElement page, string section, int column) =>
        [.. page.GetProperty("sections").EnumerateArray().First(s => s.GetProperty("title").GetString() == section)
            .GetProperty("rows").EnumerateArray().Select(r => Shown(r[column]))];

    private static string Shown(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Null => "",
        _ => value.TryGetProperty("route", out var route) ? route.GetString()! : value.TryGetProperty("id", out var id) ? id.GetString()! : value.GetProperty("path").GetString()!,
    };

    private static string Field(JsonElement page, string name) => Shown(page.GetProperty("fields").GetProperty(name));

    [Fact]
    public void EveryObjectHasAPageWithEveryAttribute()
    {
        // The template's own object: every attribute, each a link where it names something.
        var raw = site.Json("object/" + DemoIndex.TemplateUuid);
        Assert.Equal(["DisplayName", "Tags", "Stats"], Column(raw, "Attributes", 0));
        Assert.Equal(["loca/" + DemoIndex.Handle, "object/" + DemoIndex.ObjectKey, "stat/" + DemoIndex.StatName], Column(raw, "Attributes", 1));
        Assert.Equal("template/" + DemoIndex.TemplateUuid, Field(raw, "page"));
        Assert.Equal("source/" + DemoIndex.SourceId, Field(raw, "file"));

        // And the template's page leads to it.
        Assert.Equal("object/" + DemoIndex.TemplateUuid, Field(site.Json("template/" + DemoIndex.TemplateUuid), "raw"));

        // A key given twice is two pages, the second named by its row as well; an object with no
        // key has a page too.
        Assert.True(site.Has("object/" + DemoIndex.ObjectKey + ".json"));
        Assert.True(site.Has("object/" + DemoIndex.ObjectKey + ".3.json"));
        Assert.True(site.Has("object/row.4.json"));
    }

    [Fact]
    public void AFileAndItsPackageListWhatIsInThem()
    {
        var source = site.Json("source/" + DemoIndex.SourceId);
        // The fixture shows two rows of a table; the rest are counted.
        Assert.Equal(4, source.GetProperty("sections").EnumerateArray().First(s => s.GetProperty("title").GetString() == "Objects")
            .GetProperty("total").GetInt32());
        Assert.Equal("package/Shared.pak", Field(source, "package"));

        var package = site.Json("package/Shared.pak");
        Assert.Equal(["Public/Shared/Assets/dagger.GR2", "Public/Shared/Tags/Tag.lsf"], Column(package, "Every file", 0));
        Assert.Equal(["source/" + DemoIndex.SourceId], Column(package, "Files the index read", 0));
    }

    [Fact]
    public void ANameOfTheStoryScriptsSaysWhereItIsUsed()
    {
        var osiris = site.Json("osiris/" + DemoIndex.OsirisName);
        Assert.Equal("database", Field(osiris, "kind"));
        Assert.Equal(["Mods/Gustav/Story/RawFiles/Goals/Camp_Test.txt"], Column(osiris, "Used in", 0));
    }

    [Fact]
    public void AFunctionIsWhereADecompilerShowsItAndNamedWithWhereTheNameComesFrom()
    {
        var function = site.Json("function/" + DemoSymbols.GameFunction);
        Assert.Equal(DemoSymbols.ClassName + "::" + DemoSymbols.Method, function.GetProperty("title").GetString());
        Assert.StartsWith("matched to the Toolkit's export by strings", Field(function, "named by"), StringComparison.Ordinal);
        Assert.Equal(DemoSymbols.GameFunction, Field(function, "address"));
        Assert.Equal("0x14077F580", Field(function, "VA"));
        Assert.Equal("FUN_14077f580", Field(function, "label"));
        Assert.Equal("the game 4.76.31.656", Field(function, "build"));
        // The same function in the other build of the game.
        Assert.Equal("function/" + DemoSymbols.Dx11Function, Field(function, "twin"));
        Assert.Equal("function/" + DemoSymbols.GameFunction, Field(site.Json("function/" + DemoSymbols.Dx11Function), "twin"));
        // Our own two names have a place, empty until we give them.
        Assert.Equal("", Field(function, "title"));
        Assert.Equal("", Field(function, "note"));

        Assert.Equal(["function/" + DemoSymbols.UnnamedFunction], Column(function, "Called by", 0));
        Assert.Equal(["KERNEL32.dll!Sleep"], Column(function, "Calls", 0));
        Assert.Equal(["TestComponent"], Column(function, "Strings it uses", 0));
        Assert.Equal(["symbol/ecl.TestComponent"], Column(function, "In virtual tables", 2));

        // A function nobody has named is called what a decompiler calls it, and says so.
        var unnamed = site.Json("function/" + DemoSymbols.UnnamedFunction);
        Assert.Equal("FUN_14077f600", unnamed.GetProperty("title").GetString());
        Assert.Equal("unknown", Field(unnamed, "named by"));

        // The class page's exported method leads to the Toolkit's function.
        var symbol = site.Json("symbol/ecl.TestComponent");
        Assert.Contains("function/" + DemoSymbols.Dll + "+0x1000", symbol.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("route").GetString()));
    }

    [Fact]
    public void AnAddressFromADecompilerFindsTheFunctionItIsIn()
    {
        var search = new SiteSearch(site.SearchDb);
        // Relative to a binary, exactly at the start: straight to the page.
        Assert.Equal("function/" + DemoSymbols.GameFunction, search.Find(DemoSymbols.GameFunction).Exact);
        // Inside a function: the function, and how far into it.
        var inside = search.Find("bg3.exe+0x77F5A0");
        Assert.Null(inside.Exact);
        Assert.Equal("+0x20 into it", inside.Pages.Single().Note);
        // An absolute address or a decompiler's label: each build of the game that has a function
        // there, because both are loaded at the same base.
        Assert.Equal("function/" + DemoSymbols.GameFunction, search.Find("FUN_14077f580").Exact);
        Assert.Equal("function/" + DemoSymbols.UnnamedFunction, search.Find("0x14077F600").Exact);
        // A named function is found by its name as well.
        Assert.Contains(search.Find("Refresh").Pages, p => p.Route == "function/" + DemoSymbols.GameFunction);
    }
}
