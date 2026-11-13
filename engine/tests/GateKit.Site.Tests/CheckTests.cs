using Microsoft.Data.Sqlite;

namespace GateKit.Site.Tests;

/// <summary>`map check`: what has to be true of a rendered site before it is served.</summary>
public sealed class CheckTests(RenderedSite site) : IClassFixture<RenderedSite>, IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "gatekit-check-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheRenderedSitePassesItsCheck()
    {
        var check = SiteCheck.Run(site.Site, _ => { });
        Assert.True(check.Ok, string.Join("\n", check.Problems));
        Assert.Equal(site.Result.Total, check.Pages);
        Assert.True(check.Links > 0);
    }

    [Fact]
    public void TheCheckFindsALinkThatLeadsNowhere()
    {
        // A copy of the site with one page taken away: the pages that link to it are reported.
        Directory.CreateDirectory(_scratch);
        foreach (var file in Directory.GetFiles(site.Site))
        {
            File.Copy(file, Path.Combine(_scratch, Path.GetFileName(file)));
        }
        using (var db = new SqliteConnection($"Data Source={Path.Combine(_scratch, SiteStore.FileName)};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM file WHERE path = $p";
            command.Parameters.AddWithValue("$p", "template/" + DemoIndex.TemplateUuid + ".json");
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var check = SiteCheck.Run(_scratch, _ => { });
        Assert.False(check.Ok);
        Assert.Contains(check.Problems, p => p.StartsWith("a link that leads nowhere", StringComparison.Ordinal)
            && p.Contains("template/" + DemoIndex.TemplateUuid, StringComparison.Ordinal));
    }

    [Fact]
    public void AFolderWithNoSiteIsSaidToBeOne()
    {
        Directory.CreateDirectory(_scratch);
        var check = SiteCheck.Run(_scratch, _ => { });
        Assert.False(check.Ok);
        Assert.Contains("is not a rendered site", check.Problems.Single(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, true);
            }
        }
        catch (IOException)
        {
        }
    }
}
