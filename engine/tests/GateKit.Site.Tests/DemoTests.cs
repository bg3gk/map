namespace GateKit.Site.Tests;

/// <summary>`map demo`: dummy pages of every kind, for checking the look in a second.</summary>
public sealed class DemoTests : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), "gatekit-demo-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EveryAddressTheDemoNamesIsThere()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = DemoSite.Build(_out, _ => { });
        var reader = new SiteReader(Path.Combine(_out, SiteStore.FileName));

        // One page of every kind, and the awkward addresses with them.
        Assert.Equal(Routes.Kinds.Order(StringComparer.Ordinal), result.Pages.Keys.Order(StringComparer.Ordinal));
        foreach (var address in DemoSite.Addresses)
        {
            var kind = address.Split('/')[0];
            if (!Routes.Kinds.Contains(kind) || address.EndsWith("NoSuchThing", StringComparison.Ordinal))
            {
                continue;
            }
            Assert.True(reader.Has(address + ".json"), address);
        }
        Assert.True(reader.Has(SiteBuilder.Shell));

        // The point of it is that it is quick; a generous bound, so a busy machine does not fail it.
        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 30);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_out, true);
        }
        catch (IOException)
        {
        }
    }
}
