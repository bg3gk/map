using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GateKit.Site.Tests;

/// <summary>
/// What the work is for, on the site: how much of what we wrote down is still the machine's name,
/// where each name came from, how far along the registers are, and our names in a form a
/// decompiler loads. And the one thing that is never on it: the engine's code.
/// </summary>
public sealed class AimTests(RenderedSite site) : IClassFixture<RenderedSite>
{
    private static string Field(JsonElement page, string name) => page.GetProperty("fields").GetProperty(name).GetRawText();

    [Fact]
    public void TheNamingMeasureIsCountedTheWayNamingPyCountsIt()
    {
        // Six fields: Timer and Flags are named; field_8, field_C, field_10 are owed; _Pad9 is padding.
        var naming = site.Json("measure/naming");
        Assert.Equal("6", Field(naming, "fields"));
        Assert.Equal("2", Field(naming, "ours"));
        Assert.Equal("4", Field(naming, "machine's"));
        Assert.Equal("1", Field(naming, "padding"));
        Assert.Equal("3", Field(naming, "owed a name"));
        Assert.Equal("1", Field(naming, "as fact"));

        var owed = site.Json("measure/owed");
        var row = owed.GetProperty("sections")[0].GetProperty("rows")[0];
        Assert.Equal("component/ecl.TestComponent", row[0].GetProperty("route").GetString());

        // The home page says the same, in its own words.
        Assert.Contains("still owed a name", site.Shell, StringComparison.Ordinal);
        Assert.Contains("href=\"/measure/owed\"", site.Shell, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRegistersAreNOfMAndEverySystemHasAPage()
    {
        var registers = site.Json("measure/registers");
        var rungs = registers.GetProperty("sections")[0].GetProperty("rows").EnumerateArray()
            .Select(r => r[2].GetString() + "=" + r[3].GetRawText() + "/" + r[1].GetRawText()).ToArray();
        Assert.Contains("verified against raw memory=1/1", rungs);
        Assert.Contains("declared by the extender=0/1", rungs);
        Assert.Contains("go by a name=2/3", rungs);
        Assert.Contains("have a twin in the other build=2/3", rungs);
        Assert.True(site.Has("system/ecl.TestSystem.json"));
    }

    [Fact]
    public void OurNamesTravelToADecompiler()
    {
        var csv = site.Text("export/names-bg3.csv").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("va,rva,name,source,build", csv[0]);
        Assert.Equal("0x14077F580,0x77F580,ecl::TestComponent::Refresh,matched to the Toolkit's export,the game 4.76.31.656", csv[1]);
        Assert.Equal(2, csv.Length);
        Assert.Contains("0x140780000,0x780000,", site.Text("export/names-bg3_dx11.csv"), StringComparison.Ordinal);
        Assert.Contains("setName", site.Text("export/ghidra_names.py"), StringComparison.Ordinal);
        Assert.Contains("idc.set_name", site.Text("export/ida_names.py"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("undefined8 uVar1;")]
    [InlineData("/* WARNING: Subroutine does not return */")]
    [InlineData("uVar1 = FUN_14077f580(param_1);\n  uVar2 = *(int *)(param_1 + 8);\n  lVar3 = uVar1 + uVar2;")]
    public void APageThatCarriesTheEnginesCodeIsRefused(string code)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "gatekit-code-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            foreach (var file in Directory.GetFiles(site.Site))
            {
                File.Copy(file, Path.Combine(scratch, Path.GetFileName(file)));
            }
            var page = site.Text("function/" + DemoSymbols.UnnamedFunction + ".json")
                .Replace("\"note\":null", "\"note\":" + JsonSerializer.Serialize(code), StringComparison.Ordinal);
            using (var db = new SqliteConnection($"Data Source={Path.Combine(scratch, SiteStore.FileName)};Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE file SET body = $b WHERE path = $p";
                command.Parameters.AddWithValue("$b", SiteStore.Gzip(System.Text.Encoding.UTF8.GetBytes(page)));
                command.Parameters.AddWithValue("$p", "function/" + DemoSymbols.UnnamedFunction + ".json");
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            var check = SiteCheck.Run(scratch, _ => { });
            Assert.Contains(check.Problems, p => p.StartsWith("a page that carries the engine's code", StringComparison.Ordinal));

            // A label in a sentence is a fact about the code, and stays - and so does a line of
            // the game's own text that ends its lines with semicolons.
            Assert.True(SiteCheck.Run(site.Site, _ => { }).Ok);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(scratch, true);
        }
    }
}
