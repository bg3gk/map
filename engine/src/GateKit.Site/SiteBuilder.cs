using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GateKit.GameData.Query;
using Microsoft.Data.Sqlite;

namespace GateKit.Site;

/// <summary>What <c>map build</c> was asked for.</summary>
public sealed record SiteOptions
{
    /// <summary>The built index of the game's own content, or null to leave that map out.</summary>
    public string? IndexDatabase { get; init; }

    /// <summary>The symbol database of the engine's binaries, or null to leave that map out.</summary>
    public string? SymbolDatabase { get; init; }

    /// <summary>The folder of our component map, or null to leave that map out.</summary>
    public string? Coverage { get; init; }

    /// <summary>The folder the site is written into.</summary>
    public required string Out { get; init; }

    /// <summary>
    /// How many rows of one table a page shows before it says how many there were. Everything, by
    /// default: nothing is held back, and the client shows a long table a screenful at a time.
    /// </summary>
    public int RowLimit { get; init; } = int.MaxValue;

    /// <summary>Where the site is served from, with a leading and a trailing slash; the server serves it at the root.</summary>
    public string Base { get; init; } = "/";

    /// <summary>Whether the search database carries the names of the objects without a page.</summary>
    public bool Names { get; init; } = true;

    /// <summary>Only render this kind; everything when null.</summary>
    public string? OnlyKind { get; init; }
}

/// <summary>What was rendered.</summary>
public sealed record SiteResult(IReadOnlyDictionary<string, int> Pages, int Files, long Bytes, long Stored, long SearchBytes, TimeSpan Took)
{
    public int Total => Pages.Values.Sum();
}

/// <summary>
/// Renders the whole site into a folder: <see cref="SiteStore.FileName"/>, every page's JSON and
/// the client, and <see cref="SearchDatabase.FileName"/>, what the server searches. Both are built
/// under another name and put in place at the end, so a server reading the folder never sees half
/// a site.
/// </summary>
public static class SiteBuilder
{
    /// <summary>The assets that are copied into the site as they are.</summary>
    private static readonly string[] Copied = ["style.css", "app.js"];

    /// <summary>The path the shell is kept under; the server sends it for every page's address.</summary>
    public const string Shell = "shell.html";

    public static SiteResult Build(SiteOptions options, Action<string> log)
    {
        var clock = Stopwatch.StartNew();
        var root = Path.GetFullPath(options.Out);
        Directory.CreateDirectory(root);

        // The game's own content is the spine: the other two maps link into it. The other two are
        // optional, so a reader who has only the index still gets a site, with one map on it.
        if (options.IndexDatabase == null)
        {
            throw new DataQueryException("the site needs the game index; pass --db, or fetch it with `map fetch`");
        }
        var db = ReadOnlyDb.Open(options.IndexDatabase,
            $"{options.IndexDatabase} does not exist; fetch it with `map fetch`, or build it with `gatekit data build`");
        var sources = new SiteSources();
        log("reading the game's own content");
        sources = sources with { Game = new SiteIndex(db, sources.Slugs) };
        if (options.SymbolDatabase != null)
        {
            log("reading the engine's symbols");
            sources = sources with
            {
                Symbols = new SymbolIndex(ReadOnlyDb.Open(options.SymbolDatabase,
                    $"{options.SymbolDatabase} does not exist; fetch it with `map fetch`, or build it with `gatekit symbols build`"),
                    sources.Slugs),
            };
        }
        if (options.Coverage != null)
        {
            log("reading our map of the engine's components");
            sources = sources with { Components = new ComponentMap(options.Coverage, sources.Slugs) };
        }
        using var open = sources;
        SystemKind.Register(sources);
        var aim = new Aim(sources.Components);
        var index = sources.Game!;
        var languages = index.Languages;

        var pagesFile = Path.Combine(root, SiteStore.FileName);
        var searchFile = Path.Combine(root, SearchDatabase.FileName);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int files;
        long bytes, stored;
        using (var store = SiteStore.Create(pagesFile + ".building"))
        {
            foreach (var asset in Copied)
            {
                store.Put(asset, Asset(asset));
            }
            store.Put(Shell, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Asset("shell.html"))
                // The shell is never cached and the client is, for minutes: so the shell asks for
                // the client by what it holds, and a new build is never drawn by an old script.
                .Replace("{{BASE}}app.js", options.Base + "app.js?v=" + Version("app.js"), StringComparison.Ordinal)
                .Replace("{{BASE}}style.css", options.Base + "style.css?v=" + Version("style.css"), StringComparison.Ordinal)
                .Replace("{{BASE}}", options.Base, StringComparison.Ordinal)
                .Replace("{{SOURCE}}", HtmlWriter.Escape(sources.Source), StringComparison.Ordinal)
                // Both notices and the list of kinds travel inside the shell, so the client draws a
                // page's frame from what the builder decided and not from a copy of it in JavaScript.
                // The default encoder writes `<` as <, so none of it can close the script early.
                .Replace("{{NOTICES}}", JsonSerializer.Serialize(SiteNotice.All.ToDictionary(
                    n => n.Origin,
                    n => new Dictionary<string, string>(StringComparer.Ordinal) { ["missing"] = n.MissingHtml, ["whose"] = n.WhoseHtml })),
                    StringComparison.Ordinal)
                .Replace("{{AIM}}", aim.Html(sources.Symbols), StringComparison.Ordinal)
                .Replace("{{KINDS}}", JsonSerializer.Serialize(Routes.Kinds), StringComparison.Ordinal)));
            // Kept out of the search engines, the way Norbyte keeps his: the game's text is
            // published under a promise to withdraw it the same day, and a server is emptied in a
            // minute while a copy in someone else's index is not. llms.txt stays open, because it
            // is how an agent learns the addresses and it carries nothing of the game's.
            if (sources.Symbols != null)
            {
                foreach (var (path, content) in NamesExport.Files(sources.Symbols))
                {
                    store.Put(path, content);
                }
            }
            store.Put("robots.txt", Encoding.UTF8.GetBytes("User-agent: *\nAllow: /llms.txt\nDisallow: /\n"));
            store.Put("llms.txt", Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Asset("llms.txt"))
                .Replace("{{BASE}}", options.Base, StringComparison.Ordinal)
                .Replace("{{SOURCE}}", sources.Source, StringComparison.Ordinal)
                .Replace("{{LANGUAGES}}", string.Join(", ", languages), StringComparison.Ordinal)
                .Replace("{{COUNTS}}", Counts(sources), StringComparison.Ordinal)));

            using (var search = new SearchDatabase(searchFile + ".building"))
            {
                foreach (var kind in PageKinds.All.Where(k => options.OnlyKind == null || k.Kind == options.OnlyKind))
                {
                    if (!kind.Available(sources))
                    {
                        continue;
                    }
                    log($"rendering {kind.Count(sources)} {kind.Kind} pages");
                    var count = 0;
                    foreach (var page in kind.Pages(sources, options.RowLimit))
                    {
                        store.Put(page.Route + ".json", JsonWriter.Render(page, kind.Notice, options.Base));
                        // A line of text is found through the lines themselves, in every language,
                        // and a function nobody has named yet is found by its address.
                        if (page.Kind != "loca" && !(page.Kind == "function" && page.Title.StartsWith("FUN_", StringComparison.Ordinal)))
                        {
                            search.Add(page);
                        }
                        count++;
                    }
                    counts[kind.Kind] = count;
                }

                if (sources.Symbols != null)
                {
                    search.Functions(sources.Symbols, log);
                }
                search.Finish(index, options.Names, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source"] = sources.Source,
                    ["pages"] = counts.Values.Sum().ToString(CultureInfo.InvariantCulture),
                    ["built"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                }, log);
            }
            store.Meta("source", sources.Source);
            store.Meta("built", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            store.Meta("languages", string.Join(",", languages));
            foreach (var (kind, count) in counts)
            {
                store.Meta("pages." + kind, count.ToString(CultureInfo.InvariantCulture));
            }
            files = store.Files;
            bytes = store.Bytes;
            stored = store.Stored;
        }

        // In place at once: a server reading the folder goes from the old site to the new one.
        SqliteConnection.ClearAllPools();
        File.Move(searchFile + ".building", searchFile, overwrite: true);
        File.Move(pagesFile + ".building", pagesFile, overwrite: true);
        return new SiteResult(counts, files, bytes, stored, new FileInfo(searchFile).Length, clock.Elapsed);
    }

    /// <summary>How many pages of each kind this build holds, for llms.txt.</summary>
    private static string Counts(SiteSources sources) => string.Join(", ",
        PageKinds.All.Where(k => k.Available(sources) && k.Count(sources) > 0)
            .Select(k => k.Count(sources).ToString("#,0", CultureInfo.InvariantCulture) + " " + k.Kind + " pages"));

    /// <summary>A few hex digits that change whenever an asset does.</summary>
    private static string Version(string asset) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Asset(asset)))[..10];

    /// <summary>One of the files that travel inside this assembly.</summary>
    internal static byte[] Asset(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        // The build writes the folder part of a resource name with the separator of the machine
        // that built it, so a site rendered on Windows and one rendered on Linux agree anyway.
        using var stream = assembly.GetManifestResourceStream("Assets/" + name)
            ?? assembly.GetManifestResourceStream("Assets/" + name.Replace('/', '\\'))
            ?? throw new FileNotFoundException($"the asset Assets/{name} is missing from {assembly.GetName().Name}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
