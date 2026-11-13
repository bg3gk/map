using GateKit.Release;
using Microsoft.Data.Sqlite;

namespace GateKit.Site.Tests;

/// <summary>
/// `map fetch`: the databases come from a release, every byte checked, into the place `map build`
/// reads them - so a server needs no copy of the game. Tested against a release written to a
/// folder, which is what `--from` reads; nothing leaves the machine.
/// </summary>
public sealed class FetchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gatekit-fetch-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ADatabaseComesBackFromAReleaseByteForByte()
    {
        var built = Path.Combine(_root, "built");
        Directory.CreateDirectory(built);
        var index = DemoIndex.Write(Path.Combine(built, "gameindex.sqlite"));
        var symbols = DemoSymbols.Write(Path.Combine(built, "symbols.sqlite"));
        var release = Path.Combine(_root, "release");
        Directory.CreateDirectory(release);
        DatabaseExport.Export(DatabaseKind.Index, index, release, new ExportOptions(), TextWriter.Null);
        DatabaseExport.Export(DatabaseKind.Symbols, symbols, release, new ExportOptions(), TextWriter.Null);

        var data = Path.Combine(_root, "data");
        var fetched = Path.Combine(data, "gameindex.sqlite");
        var command = SiteCommand.CreateCommand();
        var code = await command.Parse(["fetch", "--from", release, "--db", fetched, "--symbols", Path.Combine(data, "symbols.sqlite")]).InvokeAsync();
        Assert.Equal(0, code);
        Assert.Equal(await File.ReadAllBytesAsync(index), await File.ReadAllBytesAsync(fetched));
        Assert.Equal(await File.ReadAllBytesAsync(symbols), await File.ReadAllBytesAsync(Path.Combine(data, "symbols.sqlite")));

        // And the site builds from what was fetched.
        var site = SiteBuilder.Build(new SiteOptions
        {
            IndexDatabase = fetched,
            SymbolDatabase = Path.Combine(data, "symbols.sqlite"),
            Out = Path.Combine(_root, "site"),
        }, _ => { });
        Assert.True(site.Total > 0);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }
}
