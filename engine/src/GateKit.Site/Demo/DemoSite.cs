using Microsoft.Data.Sqlite;

namespace GateKit.Site.Demo;

/// <summary>
/// A site of dummy pages: one page of every kind and each address that has given trouble, built
/// from three databases of a dozen rows each. It renders in about a second and needs no game, so
/// a change to the look of the site is seen at once rather than after a four-minute render of
/// the real one. The tests render the same data.
/// </summary>
public static class DemoSite
{
    /// <summary>Writes the demo databases into a scratch folder and renders the site into <paramref name="output"/>.</summary>
    public static SiteResult Build(string output, Action<string> log)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "gatekit-site-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            log("writing the demo databases");
            var index = DemoIndex.Write(Path.Combine(scratch, "index.sqlite"));
            var symbols = DemoSymbols.Write(Path.Combine(scratch, "symbols.sqlite"));
            var coverage = DemoCoverage.Write(Path.Combine(scratch, "coverage"));
            return SiteBuilder.Build(new SiteOptions
            {
                IndexDatabase = index,
                SymbolDatabase = symbols,
                Coverage = coverage,
                Out = output,
            }, log);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(scratch, true);
            }
            catch (IOException)
            {
                // A scratch folder that will not go is not a failed demo.
            }
        }
    }

    /// <summary>Writes the demo's two databases as a release - the files `map fetch` reads - into a folder.</summary>
    public static void Release(string into, Action<string> log)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "gatekit-site-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(into);
        try
        {
            var index = DemoIndex.Write(Path.Combine(scratch, "gameindex.sqlite"));
            var symbols = DemoSymbols.Write(Path.Combine(scratch, "symbols.sqlite"));
            using var writer = new StringWriter();
            GateKit.Release.DatabaseExport.Export(GateKit.Release.DatabaseKind.Index, index, into, new GateKit.Release.ExportOptions(), writer);
            GateKit.Release.DatabaseExport.Export(GateKit.Release.DatabaseKind.Symbols, symbols, into, new GateKit.Release.ExportOptions(), writer);
            log(writer.ToString().TrimEnd());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(scratch, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The addresses worth opening first: one of each kind, and the awkward ones.</summary>
    public static IReadOnlyList<string> Addresses =>
    [
        "",
        "?q=dagger",
        "entity/" + DemoIndex.EntityUuid,
        "dialog/" + DemoIndex.DialogId,
        "timeline/" + DemoIndex.TimelineUuid,
        "template/" + DemoIndex.TemplateUuid,
        "stat/" + DemoIndex.StatName,
        "stat/" + Routes.Slug(DemoIndex.OddStatName),
        "level/" + DemoIndex.Level,
        "component/" + Routes.TypeId(DemoCoverage.Component),
        "symbol/" + Routes.TypeId(DemoSymbols.ClassName),
        "enum/" + Routes.TypeId(DemoSymbols.EnumName),
        "loca/" + DemoIndex.Handle,
        "object/" + DemoIndex.ObjectKey,
        "object/" + DemoIndex.TemplateUuid,
        "source/" + DemoIndex.SourceId,
        "package/Shared.pak",
        "osiris/" + DemoIndex.OsirisName,
        "function/" + DemoSymbols.GameFunction,
        "function/" + DemoSymbols.UnnamedFunction,
        "?q=" + Uri.EscapeDataString("FUN_14077f5a0"),
        "component/ecl.NoSuchThing",
    ];
}
