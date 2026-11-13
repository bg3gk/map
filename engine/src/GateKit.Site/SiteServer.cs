using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GateKit.Site;

/// <summary>What <c>map serve</c> was asked for.</summary>
public sealed record ServeOptions
{
    /// <summary>The site folder <c>map build</c> rendered: <c>pages.sqlite</c> and <c>search.sqlite</c>.</summary>
    public required string Folder { get; init; }

    public int Port { get; init; } = 8765;

    /// <summary>The address to listen on. The default answers only this machine; a reverse proxy in front of it answers the world.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>Holds every answer back this many milliseconds, to see the site as a slow connection shows it.</summary>
    public int Latency { get; init; }

    /// <summary>How many questions one address may ask the API in a minute.</summary>
    public int AskPerMinute { get; init; } = 60;

    /// <summary>
    /// Networks a reverse proxy may sit in, whose word on where a request came from is taken - such
    /// as a Docker network, where the proxy is not on this machine's loopback. Without one, only a
    /// proxy on the loopback is believed.
    /// </summary>
    public IReadOnlyList<string> Proxies { get; init; } = [];

    /// <summary>The full game index, for what no page answers ahead of time: what names a thing. Null leaves that question out.</summary>
    public string? IndexDatabase { get; init; }
}

/// <summary>
/// The site's server. Every address of a page answers 200 with the shell, which fetches the page's
/// <c>.json</c> and draws it; the <c>.json</c> itself, the client and <c>llms.txt</c> come out of
/// <see cref="SiteStore"/> still gzip-compressed, the way a browser takes them. Searching is a
/// question to <c>/api/search</c>. It is meant to sit behind a reverse proxy that holds the
/// certificate - <c>deploy/Caddyfile</c> - and it reads the address the proxy says a request came
/// from, so the limit on questions is per reader and not per proxy.
/// </summary>
public static class SiteServer
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".py"] = "text/x-python; charset=utf-8",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The site, as one application ready to run; the tests run it on a free port.</summary>
    public static WebApplication Create(ServeOptions options, Action<string> log)
    {
        var folder = Path.GetFullPath(options.Folder);
        var site = new LiveSite(folder);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = folder });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Listen(IPAddress.Parse(options.Host), options.Port);
        });
        builder.Services.Configure<ForwardedHeadersOptions>(f =>
        {
            f.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var network in options.Proxies)
            {
                f.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
        builder.Services.AddRateLimiter(limits =>
        {
            limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limits.AddPolicy("ask", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.AskPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        var app = builder.Build();
        app.UseForwardedHeaders();
        if (options.Latency > 0)
        {
            app.Use(async (context, next) =>
            {
                await Task.Delay(options.Latency, context.RequestAborted);
                await next(context);
            });
        }
        app.Use(async (context, next) =>
        {
            // Out of the search engines; only llms.txt and robots.txt are for robots to read.
            var path = context.Request.Path.Value ?? "/";
            if (path is not "/llms.txt" and not "/robots.txt")
            {
                context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            }
            await next(context);
        });
        app.UseRateLimiter();

        app.MapGet("/api/search", (HttpContext context, string? q, string? lang) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.BadRequest(new { error = "ask with ?q=<words>" });
            }
            if (q.Length > 200)
            {
                return Results.BadRequest(new { error = "a search is at most 200 characters" });
            }
            try
            {
                return Results.Json(site.Search.Find(q, string.IsNullOrWhiteSpace(lang) ? null : lang), Json);
            }
            catch (SqliteException e)
            {
                log($"search {q}: {e.Message}");
                return Results.BadRequest(new { error = "that search could not be read" });
            }
        }).RequireRateLimiting("ask");

        var refs = options.IndexDatabase is { } index && File.Exists(index) ? new SiteRefs(index) : null;
        app.MapGet("/api/refs", (string? value) =>
        {
            if (refs == null)
            {
                return Results.Json(new { error = "this server has no game index to ask" }, Json, statusCode: StatusCodes.Status501NotImplemented);
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                return Results.BadRequest(new { error = "ask with ?value=<an id, a handle or a name>" });
            }
            var found = refs.Find(value.Trim());
            return Results.Json(new { value, most = SiteRefs.Most, objects = found }, Json);
        }).RequireRateLimiting("ask");

        app.MapMethods("/{**path}", ["GET", "HEAD"], (HttpContext context, string? path) => Answer(site, context, path ?? ""));
        log($"serving {folder} at http://{options.Host}:{options.Port.ToString(CultureInfo.InvariantCulture)}/");
        return app;
    }

    /// <summary>Serves until <paramref name="stop"/> is cancelled.</summary>
    public static async Task Serve(ServeOptions options, Action<string> log, CancellationToken stop)
    {
        await using var app = Create(options, log);
        await app.StartAsync(stop);
        try
        {
            await Task.Delay(Timeout.Infinite, stop);
        }
        catch (OperationCanceledException)
        {
        }
        await app.StopAsync(CancellationToken.None);
    }

    private static IResult Answer(LiveSite site, HttpContext context, string path)
    {
        var reader = site.Pages;
        var headers = context.Response.Headers;
        // A file of the site: a page's JSON, the client, llms.txt.
        if (path.Length > 0 && path != SiteBuilder.Shell && reader.Packed(path) is { } packed)
        {
            var type = Types.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
            headers.CacheControl = "public, max-age=300";
            headers.Vary = "Accept-Encoding";
            if (AcceptsGzip(context.Request))
            {
                headers.ContentEncoding = "gzip";
                return Results.Bytes(packed, type);
            }
            return Results.Bytes(SiteStore.Gunzip(packed), type);
        }
        // Every other address gets the shell, which draws the page. An address with no page is
        // answered 404 all the same, and the shell says so and searches for what it may have meant.
        var shell = reader.Packed(SiteBuilder.Shell) ?? throw new InvalidOperationException("the site has no shell");
        var known = path.Length == 0 || IsPage(reader, path);
        context.Response.StatusCode = known ? 200 : 404;
        headers.CacheControl = "no-cache";
        headers.Vary = "Accept-Encoding";
        if (AcceptsGzip(context.Request))
        {
            headers.ContentEncoding = "gzip";
            return Results.Bytes(shell, Types[".html"]);
        }
        return Results.Bytes(SiteStore.Gunzip(shell), Types[".html"]);
    }

    /// <summary>Whether an address is a page: a kind, one segment, and a JSON behind it.</summary>
    private static bool IsPage(SiteReader reader, string path)
    {
        path = path.TrimEnd('/');
        var cut = path.IndexOf('/', StringComparison.Ordinal);
        return cut > 0 && cut < path.Length - 1 && Routes.Kinds.Contains(path[..cut]) && reader.Has(path + ".json");
    }

    private static bool AcceptsGzip(HttpRequest request) =>
        request.Headers.AcceptEncoding.Any(v => v != null && v.Contains("gzip", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The site folder as it is now. <c>map build</c> replaces both files with one rename each; when
    /// it has, the connections still open on the old files are let go, and the next request reads
    /// the new site.
    /// </summary>
    private sealed class LiveSite(string folder)
    {
        private readonly object _lock = new();
        private DateTime _seen;
        private SiteReader? _pages;
        private SiteSearch? _search;

        public SiteReader Pages
        {
            get
            {
                Check();
                return _pages!;
            }
        }

        public SiteSearch Search
        {
            get
            {
                Check();
                return _search!;
            }
        }

        private void Check()
        {
            var file = Path.Combine(folder, SiteStore.FileName);
            var stamp = File.GetLastWriteTimeUtc(file);
            if (_pages != null && stamp == _seen)
            {
                return;
            }
            lock (_lock)
            {
                if (_pages != null && stamp == _seen)
                {
                    return;
                }
                if (_pages != null)
                {
                    SqliteConnection.ClearAllPools();
                }
                _pages = new SiteReader(file);
                _search = new SiteSearch(Path.Combine(folder, SearchDatabase.FileName));
                _seen = stamp;
            }
        }
    }
}
