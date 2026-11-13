namespace GateKit.Site.Tests;

/// <summary>The URL scheme: what an id becomes, and that it stays the same on every machine.</summary>
public sealed class RoutesTests
{
    [Theory]
    [InlineData("WPN_Shortsword", "WPN_Shortsword")]
    [InlineData("Background_LegendOfBaldur'sGate", "Background_LegendOfBaldur!27sGate")]
    [InlineData("GENERIC PASSIVES", "GENERIC!20PASSIVES")]
    [InlineData("Target_CRE_MageHandShove[Deprecated]", "Target_CRE_MageHandShove!5BDeprecated!5D")]
    [InlineData("a/b", "a/b")]
    [InlineData("a!b", "a!21b")]
    [InlineData("..", "!2E!2E")]
    [InlineData("", "!00")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    public void AnIdBecomesOneSafeSegment(string id, string slug) => Assert.Equal(slug, Routes.Slug(id));

    [Fact]
    public void ASlugIsOnlyEverSafeCharacters()
    {
        foreach (var id in new[] { "éclair", "a\\b", "a:b", "a*b", "a?b", "a|b", "a\"b", " leading", "trailing " })
        {
            Assert.All(Routes.Slug(id), c =>
                Assert.True(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '!' or '~', $"{id} -> {Routes.Slug(id)}"));
        }
    }

    [Theory]
    [InlineData("Background_LegendOfBaldur'sGate")]
    [InlineData("éclair")]
    [InlineData("a!b")]
    [InlineData("GENERIC PASSIVES")]
    public void ASlugReadsBackAsItsId(string id) => Assert.Equal(id, Routes.Unslug(Routes.Slug(id)));

    [Fact]
    public void ASlugIsAskedForAsItIsWritten()
    {
        // A server decodes `%27` in a request before it looks for the file, so a file whose name
        // holds `%27` cannot be asked for. Nothing in a slug is changed by that decoding.
        foreach (var id in new[] { "Background_LegendOfBaldur'sGate", "a b", "x[1]", "éclair" })
        {
            var slug = Routes.Slug(id);
            Assert.Equal(slug, Uri.UnescapeDataString(slug));
        }
    }

    [Fact]
    public void IdsThatDifferOnlyInCaseGetSeparateFolders()
    {
        // Two files called Hairy and HAIRY are one file on Windows and on macOS.
        var slugs = Routes.SlugsFor(["Hairy", "HAIRY", "hairy"]);
        Assert.Equal(3, slugs.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // The order is ordinal, so the same index renders the same addresses everywhere.
        Assert.Equal("HAIRY", slugs["HAIRY"]);
        Assert.Equal("Hairy~2", slugs["Hairy"]);
        Assert.Equal("hairy~3", slugs["hairy"]);
    }

    [Fact]
    public void ADialogIsFiledByItsNameWithoutItsFolder()
    {
        Assert.Equal("CAMP_SleepCutscene", Routes.DialogId("Camp/CAMP_SleepCutscene"));
        Assert.Equal("DEN_AttackOnDen_Sapper_003", Routes.DialogId("DEN_AttackOnDen_Sapper_003"));
    }

    [Fact]
    public void ARouteIsTheKindAndThenTheId() =>
        Assert.Equal("stat/WPN_Shortsword", Routes.Route("stat", Routes.Slug("WPN_Shortsword")));

    [Fact]
    public void EveryKindThatHasPagesIsNamedInTheScheme() =>
        Assert.Equal(Routes.Kinds.Order(StringComparer.Ordinal), PageKinds.All.Select(k => k.Kind).Order(StringComparer.Ordinal));
}

/// <summary>What a value of unknown shape becomes, read against the synthetic index.</summary>
public sealed class AutoValueTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gatekit-auto-" + Guid.NewGuid().ToString("N"));
    private readonly SiteIndex _index;

    public AutoValueTests()
    {
        Directory.CreateDirectory(_folder);
        var path = DemoIndex.Write(Path.Combine(_folder, "index.sqlite"));
        _index = new SiteIndex(GateKit.GameData.Query.ReadOnlyDb.Open(path, "missing"), new SlugBook());
    }

    [Fact]
    public void AHandleWithItsVersionIsStillAHandle()
    {
        // The stat files write `DisplayName` as `<handle>;<version>`.
        var value = _index.Auto(DemoIndex.Handle + ";2");
        Assert.Equal(ValueKind.Link, value.Kind);
        Assert.Equal("loca/" + DemoIndex.Handle, value.Route);
        Assert.Equal(DemoIndex.Handle + ";2", value.Text);
        Assert.Equal(DemoIndex.EnglishLine, value.Note);

        // Something that only looks like it stays text.
        Assert.Equal(ValueKind.Text, _index.Auto(DemoIndex.Handle + ";two").Kind);
    }

    [Fact]
    public void AHandleWithNoTextAnywhereIsNotALink()
    {
        // The game names handles that have no line in any language - this one is its own
        // placeholder - and a link to one leads to an empty page.
        const string Unknown = "ls::TranslatedStringRepository::s_HandleUnknown";
        Assert.Equal(ValueKind.Identifier, _index.Handle(Unknown).Kind);
        Assert.Equal(ValueKind.Identifier, _index.Line(Unknown).Kind);
        Assert.Equal(ValueKind.Identifier, _index.Handle("h99999999g9999g9999g9999g999999999999").Kind);

        // One that has a line in some language is.
        Assert.Equal(ValueKind.Link, _index.Line(DemoIndex.Handle).Kind);
        Assert.Equal(ValueKind.Link, _index.Handle(DemoIndex.EnglishOnlyHandle).Kind);
    }

    public void Dispose()
    {
        _index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
        }
    }
}
