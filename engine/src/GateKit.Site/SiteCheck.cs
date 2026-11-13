using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GateKit.Site;

/// <summary>What <see cref="SiteCheck"/> found.</summary>
public sealed record SiteCheckResult
{
    public required int Pages { get; init; }

    public required int Links { get; init; }

    public required int Files { get; init; }

    /// <summary>The size of every file before compression.</summary>
    public required long Bytes { get; init; }

    /// <summary>Everything wrong, one line each; empty when the site may be served.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Everything that has to be true of a rendered site before it is served, checked on what was
/// rendered: every link a page draws leads to a page, every page says the address it is at, no
/// column or field name is long, and nothing of the engine's code is on any page. It reads every
/// page, so it takes a while on the real site.
/// </summary>
public static partial class SiteCheck
{
    /// <summary>
    /// What a decompiler's output looks like and a sentence about it does not: Ghidra's types
    /// declaring a variable, its warning comments, or one statement after another assigning its numbered
    /// temporaries. A label such as <c>FUN_14077f580</c>, or <c>*puVar4 = 1</c> quoted in a sentence, is a fact about the code; the code is not.
    /// </summary>
    [GeneratedRegex(@"\b(?:undefined[1248]?|longlong|ulonglong)\s+\**[A-Za-z_]\w*\s*[=;(]|/\*\s*WARNING:|(?:\b[a-z]{1,3}Var\d+\s*=[^=][^;""]{0,120};[^""]{0,200}){3}")]
    private static partial Regex DecompilerOutput();

    /// <summary>How many problems of one kind are listed before the rest are only counted.</summary>
    private const int Shown = 5;

    /// <summary>The files every site has besides its pages.</summary>
    private static readonly string[] Required = [SiteBuilder.Shell, "app.js", "style.css", "llms.txt", "robots.txt"];

    public static SiteCheckResult Run(string folder, Action<string> log)
    {
        var problems = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Problem(string kind, string example)
        {
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            if (counts[kind] <= Shown)
            {
                problems.Add(kind + ": " + example);
            }
        }

        var pagesFile = Path.Combine(Path.GetFullPath(folder), SiteStore.FileName);
        if (!File.Exists(pagesFile) || !File.Exists(Path.Combine(Path.GetFullPath(folder), SearchDatabase.FileName)))
        {
            return new SiteCheckResult
            {
                Pages = 0, Links = 0, Files = 0, Bytes = 0,
                Problems = [$"{folder} is not a rendered site: it needs {SiteStore.FileName} and {SearchDatabase.FileName}"],
            };
        }
        var reader = new SiteReader(pagesFile);

        log("listing the files");
        long bytes = 0;
        var files = 0;
        var pages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, size) in reader.Files())
        {
            files++;
            bytes += size;
            var cut = path.IndexOf('/', StringComparison.Ordinal);
            if (cut > 0 && path.EndsWith(".json", StringComparison.Ordinal) && Routes.Kinds.Contains(path[..cut]))
            {
                pages.Add(path[..^".json".Length]);
            }
        }
        foreach (var file in Required)
        {
            if (!reader.Has(file))
            {
                problems.Add("a file every site has is missing: " + file);
            }
        }

        log($"following the links of {pages.Count} pages");
        var links = 0;
        foreach (var (page, bytesOfPage) in reader.Pages())
        {
            if (DecompilerOutput().IsMatch(Encoding.UTF8.GetString(bytesOfPage)))
            {
                Problem("a page that carries the engine's code, which is never published", page);
            }
            using var document = JsonDocument.Parse(bytesOfPage);
            var json = document.RootElement;
            if (json.GetProperty("route").GetString() != page)
            {
                Problem("a page that names another address", page);
            }
            foreach (var field in json.GetProperty("fields").EnumerateObject())
            {
                if (field.Name.Length > Hints.MaxName)
                {
                    Problem("a long field name", $"{page}: \"{field.Name}\"");
                }
            }
            foreach (var section in json.GetProperty("sections").EnumerateArray())
            {
                foreach (var column in section.GetProperty("columns").EnumerateArray())
                {
                    if (column.GetString()!.Length > Hints.MaxName)
                    {
                        Problem("a long column name", $"{page}: \"{column.GetString()}\"");
                    }
                }
            }
            foreach (var link in json.GetProperty("links").EnumerateArray())
            {
                links++;
                var route = link.GetProperty("route").GetString()!;
                if (!pages.Contains(route))
                {
                    Problem("a link that leads nowhere", $"{page} -> {route}");
                }
            }
        }

        foreach (var (kind, count) in counts.Where(c => c.Value > Shown))
        {
            problems.Add($"{kind}: {count - Shown} more");
        }
        return new SiteCheckResult { Pages = pages.Count, Links = links, Files = files, Bytes = bytes, Problems = problems };
    }
}
