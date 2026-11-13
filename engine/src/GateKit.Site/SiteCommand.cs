using System.CommandLine;
using System.Globalization;
using GateKit.GameData.Query;
using GateKit.Release;

namespace GateKit.Site;

/// <summary>The commands of <c>map</c>: build the site, check it, serve it, and a demo of it.</summary>
public static class SiteCommand
{
    /// <summary>A command holding every subcommand; <c>map</c> takes them as its own.</summary>
    public static Command CreateCommand()
    {
        var db = new Option<string>("--db")
        {
            Description = "the index of the game's content (default: data/gameindex.sqlite, or MAP_DATA)",
            Recursive = true,
            DefaultValueFactory = _ => MapPaths.IndexDatabase,
        };
        var symbols = new Option<string>("--symbols")
        {
            Description = "the symbol database of the engine (default: data/symbols.sqlite, or MAP_DATA)",
            Recursive = true,
            DefaultValueFactory = _ => MapPaths.SymbolDatabase,
        };
        var coverage = new Option<string?>("--coverage")
        {
            Description = "the folder of our component map (default: research/coverage of the gatekit submodule)",
            Recursive = true,
            DefaultValueFactory = _ => MapPaths.Coverage,
        };
        var site = new Command("map", "build, check and serve the GateKit map site: the game's content, the engine's symbols and our component map");
        site.Options.Add(db);
        site.Options.Add(symbols);
        site.Options.Add(coverage);
        site.Subcommands.Add(BuildCommand(db, symbols, coverage));
        site.Subcommands.Add(ServeCommand(db));
        site.Subcommands.Add(DemoCommand());
        site.Subcommands.Add(CheckCommand());
        site.Subcommands.Add(FetchCommand(db, symbols));
        return site;
    }

    private static Option<string> SiteFolder(string description) => new("--dir")
    {
        Description = description + " (default: site/ of this checkout)",
        DefaultValueFactory = _ => MapPaths.Site,
    };

    private static Command BuildCommand(Option<string> db, Option<string> symbols, Option<string?> coverage)
    {
        var output = SiteFolder("the folder to render into");
        var rows = new Option<int>("--rows")
        {
            Description = "rows of one table a page shows before it only counts the rest (default: all of them)",
            DefaultValueFactory = _ => int.MaxValue,
        };
        var without = new Option<string[]>("--without")
        {
            Description = "leave a whole map out: `symbols`, `components`, or `names` (the names of the objects without a page)",
            AllowMultipleArgumentsPerToken = true,
        };
        without.AcceptOnlyFromAmong("symbols", "components", "names");
        var only = new Option<string?>("--kind") { Description = "render only this kind of page" };
        only.AcceptOnlyFromAmong(Routes.Kinds);

        var build = new Command("build", "render the whole site into a folder: pages.sqlite and search.sqlite") { output, rows, without, only };
        build.SetAction(result =>
        {
            try
            {
                var left = result.GetValue(without) ?? [];
                var rendered = SiteBuilder.Build(new SiteOptions
                {
                    IndexDatabase = result.GetValue(db)!,
                    SymbolDatabase = left.Contains("symbols") ? null : Present(result.GetValue(symbols)),
                    Coverage = left.Contains("components") ? null : Present(result.GetValue(coverage)),
                    Out = result.GetValue(output)!,
                    RowLimit = result.GetValue(rows),
                    Names = !left.Contains("names"),
                    OnlyKind = result.GetValue(only),
                }, Console.Error.WriteLine);
                Report(rendered, result.GetValue(output)!);
                return 0;
            }
            catch (DataQueryException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        });
        return build;
    }

    private static Command ServeCommand(Option<string> db)
    {
        var folder = SiteFolder("the rendered site to serve");
        var port = new Option<int>("--port") { Description = "the port to listen on", DefaultValueFactory = _ => 8765 };
        var host = new Option<string>("--host")
        {
            Description = "the address to listen on; keep 127.0.0.1 behind a reverse proxy",
            DefaultValueFactory = _ => "127.0.0.1",
        };
        var latency = new Option<int>("--latency")
        {
            Description = "hold every answer back this many milliseconds, to see the site as a slow connection shows it",
            DefaultValueFactory = _ => 0,
        };
        var ask = new Option<int>("--ask-per-minute")
        {
            Description = "how many API questions one address may ask in a minute",
            DefaultValueFactory = _ => 60,
        };
        var proxy = new Option<string[]>("--proxy")
        {
            Description = "a network a reverse proxy sits in, such as 172.18.0.0/16 for one in Docker; its X-Forwarded-For is believed",
            AllowMultipleArgumentsPerToken = true,
        };
        var serve = new Command("serve", "serve a rendered site: every page, its JSON, /api/search and /api/refs") { folder, port, host, latency, ask, proxy };
        serve.SetAction(result =>
        {
            var dir = result.GetValue(folder)!;
            if (!File.Exists(Path.Combine(dir, SiteStore.FileName)))
            {
                Console.Error.WriteLine($"{dir} holds no rendered site; render one with `map build`");
                return 1;
            }
            Run(new ServeOptions
            {
                Folder = dir,
                Port = result.GetValue(port),
                Host = result.GetValue(host)!,
                Latency = result.GetValue(latency),
                AskPerMinute = result.GetValue(ask),
                IndexDatabase = result.GetValue(db),
                Proxies = result.GetValue(proxy) ?? [],
            });
            return 0;
        });
        return serve;
    }

    /// <summary><c>map demo</c>: dummy pages of every kind, in a second, with no game.</summary>
    private static Command DemoCommand()
    {
        var output = new Option<string>("--out") { Description = "the folder to render the demo into", Required = true };
        var serve = new Option<bool>("--serve") { Description = "serve it straight away" };
        var release = new Option<string?>("--release")
        {
            Description = "also write the demo's databases as a release into this folder, for trying `map fetch --from` and a deployment without the game",
        };
        var port = new Option<int>("--port") { Description = "the port to serve on", DefaultValueFactory = _ => 8765 };
        var latency = new Option<int>("--latency")
        {
            Description = "hold every answer back this many milliseconds, to see the site as a slow connection shows it",
            DefaultValueFactory = _ => 0,
        };
        var demo = new Command("demo", "render dummy pages of every kind and every awkward address, in a second and with no game - for checking the look of the site")
        {
            output, serve, port, latency, release,
        };
        demo.SetAction(result =>
        {
            var folder = result.GetValue(output)!;
            if (result.GetValue(release) is { Length: > 0 } into)
            {
                Demo.DemoSite.Release(into, Console.Error.WriteLine);
                Console.WriteLine($"a release of the demo's databases is in {into}; try: map fetch --from {into}");
            }
            var rendered = Demo.DemoSite.Build(folder, Console.Error.WriteLine);
            Report(rendered, folder);
            var root = $"http://127.0.0.1:{result.GetValue(port).ToString(CultureInfo.InvariantCulture)}/";
            Console.WriteLine("worth opening:");
            foreach (var address in Demo.DemoSite.Addresses)
            {
                Console.WriteLine("  " + root + address);
            }
            if (!result.GetValue(serve))
            {
                Console.WriteLine($"serve it with: map serve --dir {folder}");
                return 0;
            }
            Run(new ServeOptions { Folder = folder, Port = result.GetValue(port), Latency = result.GetValue(latency) });
            return 0;
        });
        return demo;
    }

    private static void Run(ServeOptions options)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        SiteServer.Serve(options, Console.Error.WriteLine, stop.Token).GetAwaiter().GetResult();
    }

    /// <summary><c>map check</c>: everything that has to be true before a site is served.</summary>
    private static Command CheckCommand()
    {
        var folder = SiteFolder("the rendered site to check");
        var check = new Command("check", "check a rendered site: every link, every page's address, short names")
        {
            folder,
        };
        check.SetAction(result =>
        {
            var found = SiteCheck.Run(result.GetValue(folder)!, Console.Error.WriteLine);
            Console.WriteLine($"{found.Pages.ToString("#,0", CultureInfo.InvariantCulture)} pages, {found.Links.ToString("#,0", CultureInfo.InvariantCulture)} links, "
                + $"{found.Files.ToString("#,0", CultureInfo.InvariantCulture)} files, {Size(found.Bytes)} before compression");
            if (found.Ok)
            {
                Console.WriteLine("ok");
                return 0;
            }
            foreach (var problem in found.Problems)
            {
                Console.WriteLine("  " + problem);
            }
            return 1;
        });
        return check;
    }

    /// <summary>
    /// <c>map fetch</c>: the databases the site is built from, downloaded from the release this
    /// checkout pins in <c>release.json</c>, every byte checked, put where <c>map build</c> reads
    /// them. The server that hosts the site needs no copy of the game.
    /// </summary>
    private static Command FetchCommand(Option<string> db, Option<string> symbols)
    {
        var from = new Option<string?>("--from")
        {
            Description = "a folder, file:// or http(s):// URL holding the assets, instead of the release release.json pins",
        };
        var force = new Option<bool>("--force") { Description = "replace databases that are already here" };
        var fetch = new Command("fetch", "download the databases the site is built from, and check every byte") { from, force };
        fetch.SetAction(async (result, cancel) =>
        {
            try
            {
                var pinFile = Path.Combine(MapPaths.Root, "release.json");
                var pin = result.GetValue(from) is { Length: > 0 } ? null : ReleasePin.Read(pinFile);
                if (pin is { Tag: null, BaseUrl: null })
                {
                    Console.Error.WriteLine($"{pinFile} pins no release of {pin.Repository} yet: nothing has been published.");
                    Console.Error.WriteLine("Build the databases with gatekit (`gatekit data build`, `gatekit symbols build`) and put them in "
                        + MapPaths.Data + ", or pass --from with a folder or URL holding the assets.");
                    return 1;
                }
                var source = pin?.Source() ?? AssetSources.For(result.GetValue(from)!);
                foreach (var (kind, path) in new[] { (DatabaseKind.Index, result.GetValue(db)!), (DatabaseKind.Symbols, result.GetValue(symbols)!) })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    await DatabaseInstall.InstallAsync(kind, source, pin?.Manifest ?? DatabaseManifest.FileName, path,
                        result.GetValue(force), Console.Out, cancel);
                }
                Console.WriteLine("render the site with: map build");
                return 0;
            }
            catch (ReleaseException e)
            {
                Console.Error.WriteLine("ERROR: " + e.Message);
                return 1;
            }
        });
        return fetch;
    }

    private static void Report(SiteResult site, string folder)
    {
        Console.WriteLine($"{folder}: {site.Total.ToString("#,0", CultureInfo.InvariantCulture)} pages, "
            + $"{site.Files.ToString("#,0", CultureInfo.InvariantCulture)} files, {Size(site.Bytes)} ({Size(site.Stored)} stored) "
            + $"in {site.Took.TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture)} min");
        foreach (var (kind, count) in site.Pages)
        {
            Console.WriteLine($"  {kind,-10} {count.ToString("#,0", CultureInfo.InvariantCulture),9}");
        }
        Console.WriteLine($"  {"search",-10} {Size(site.SearchBytes),9}");
    }

    /// <summary>
    /// A map that is simply not on this machine is left out rather than refused: a reader who has
    /// only the index should still get a site.
    /// </summary>
    private static string? Present(string? path) =>
        path is { Length: > 0 } && (File.Exists(path) || Directory.Exists(path)) ? path : null;

    internal static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => ((double)bytes / (1L << 30)).ToString("0.00", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => ((double)bytes / (1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => ((double)bytes / (1L << 10)).ToString("0.0", CultureInfo.InvariantCulture) + " kB",
        _ => bytes + " B",
    };
}
