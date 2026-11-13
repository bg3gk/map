using System.Net;
using System.Text.Json;

namespace GateKit.Site.Tests;

/// <summary>
/// `map serve`: every page's address answers with the shell, every page's JSON and the client come
/// out of the one site file, and a search is one question to the API - limited per address, so one
/// reader cannot take the server for everyone else.
/// </summary>
public sealed class ServerTests(RenderedSite site) : IClassFixture<RenderedSite>
{
    private async Task Served(Func<HttpClient, Uri, Task> ask, int latency = 0, int askPerMinute = 60, string[]? proxies = null)
    {
        var port = Free();
        await using var app = SiteServer.Create(new ServeOptions
        {
            Folder = site.Site,
            Port = port,
            Latency = latency,
            AskPerMinute = askPerMinute,
            IndexDatabase = site.IndexDb,
            Proxies = proxies ?? [],
        }, _ => { });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None });
            await ask(client, new Uri($"http://127.0.0.1:{port}/"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>A port nobody is listening on, borrowed and given straight back.</summary>
    private static int Free()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task APageIsTheShellAndItsJsonIsWhatTheShellDraws() =>
        await Served(async (client, root) =>
        {
            // The address of a page answers 200 with the shell, which fetches the JSON and draws it.
            var page = await client.GetAsync(new Uri(root, "dialog/" + DemoIndex.DialogId));
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("window.SITE_KINDS", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal("noindex, nofollow", page.Headers.GetValues("X-Robots-Tag").Single());

            var json = await client.GetAsync(new Uri(root, "dialog/" + DemoIndex.DialogId + ".json"));
            Assert.Equal(HttpStatusCode.OK, json.StatusCode);
            Assert.Equal("application/json", json.Content.Headers.ContentType?.MediaType);
            Assert.Contains(DemoIndex.DialogId, await json.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // An address with no page is 404, and still the shell, which says so and searches.
            var missing = await client.GetAsync(new Uri(root, "component/ecl.NoSuchThing"));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Contains("Three maps of one game", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri(root, "dialog/Nope.json"))).StatusCode);

            // llms.txt is for robots to read; nothing else is.
            var llms = await client.GetAsync(new Uri(root, "llms.txt"));
            Assert.Equal(HttpStatusCode.OK, llms.StatusCode);
            Assert.False(llms.Headers.Contains("X-Robots-Tag"));
        });

    [Fact]
    public async Task AFileIsSentStillCompressedToABrowserThatTakesIt() =>
        await Served(async (client, root) =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "app.js"));
            request.Headers.AcceptEncoding.ParseAdd("gzip");
            var packed = await client.SendAsync(request);
            Assert.Equal("gzip", packed.Content.Headers.ContentEncoding.Single());
            var bytes = await packed.Content.ReadAsByteArrayAsync();
            Assert.Equal(site.Text("app.js"), System.Text.Encoding.UTF8.GetString(SiteStore.Gunzip(bytes)));

            // And unpacked to one that does not.
            var plain = await client.GetAsync(new Uri(root, "app.js"));
            Assert.Empty(plain.Content.Headers.ContentEncoding);
            Assert.Equal(site.Text("app.js"), await plain.Content.ReadAsStringAsync());
        });

    [Fact]
    public async Task ASearchIsOneQuestionInAnyLanguage() =>
        await Served(async (client, root) =>
        {
            async Task<JsonElement> Ask(string query)
            {
                var answer = await client.GetAsync(new Uri(root, "api/search?" + query));
                Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
                using var document = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
                return document.RootElement.Clone();
            }

            // An id of exactly one page is answered with where it is.
            Assert.Equal("template/" + DemoIndex.TemplateUuid, (await Ask("q=" + DemoIndex.TemplateUuid)).GetProperty("exact").GetString());
            Assert.Equal("loca/" + DemoIndex.Handle, (await Ask("q=" + DemoIndex.Handle)).GetProperty("exact").GetString());
            Assert.Equal("enum/ecl.TestFlags", (await Ask("q=ecl::TestFlags")).GetProperty("exact").GetString());

            // A plain word is a search, even when it is also the id of one page.
            var word = await Ask("q=" + DemoSymbols.BaseName);
            Assert.Equal(JsonValueKind.Null, word.GetProperty("exact").ValueKind);
            Assert.Contains(word.GetProperty("pages").EnumerateArray(), p => p.GetProperty("route").GetString() == "symbol/" + DemoSymbols.BaseName);

            // Words find pages and lines in every language, or in the one asked for.
            var dagger = await Ask("q=dagger");
            Assert.Contains(dagger.GetProperty("pages").EnumerateArray(), p => p.GetProperty("route").GetString() == "stat/" + DemoIndex.StatName);
            Assert.Equal(["English", "Russian"], dagger.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("lang").GetString()!).Order(StringComparer.Ordinal));
            Assert.Equal(["Russian"], (await Ask("q=dagger&lang=Russian")).GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("lang").GetString()!));

            // A language with no spaces is found by any part of a line: by runs of three characters,
            // and by reading the lines when what was asked is shorter.
            foreach (var part in (string[])["鋭い短剣", "短剣"])
            {
                var line = (await Ask("q=" + Uri.EscapeDataString(part))).GetProperty("lines").EnumerateArray().Single();
                Assert.Equal("Japanese", line.GetProperty("lang").GetString());
                Assert.Contains("<em>", line.GetProperty("text").GetString(), StringComparison.Ordinal);
            }

            // Nothing to ask is refused, not answered with everything.
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(new Uri(root, "api/search?q="))).StatusCode);
        });

    [Fact]
    public async Task WhatNamesAThingIsAskedOfTheWholeIndex() =>
        await Served(async (client, root) =>
        {
            // The raw object a tag is: the template's own object names it in its Tags attribute.
            var answer = await client.GetAsync(new Uri(root, "api/refs?value=" + DemoIndex.ObjectKey));
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            using var document = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
            var found = document.RootElement.GetProperty("objects").EnumerateArray().Single();
            Assert.Equal("object/" + DemoIndex.TemplateUuid, found.GetProperty("route").GetString());
            Assert.Equal("Tags", found.GetProperty("attribute").GetString());
            Assert.True(site.Has(found.GetProperty("route").GetString() + ".json"));
        });

    [Fact]
    public async Task OneAddressCannotAskWithoutEnd() =>
        await Served(async (client, root) =>
        {
            var answers = new List<HttpStatusCode>();
            for (var i = 0; i < 5; i++)
            {
                answers.Add((await client.GetAsync(new Uri(root, "api/search?q=dagger"))).StatusCode);
            }
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], answers);
            // Pages are not questions, and are never refused.
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri(root, "dialog/" + DemoIndex.DialogId + ".json"))).StatusCode);
        }, askPerMinute: 3);

    [Fact]
    public async Task BehindAProxyEachReaderHasALimitOfTheirOwn() =>
        // In a Docker network the proxy is not on the loopback; once its network is named, the
        // address it passes on is the reader's, and one reader's questions do not use up another's.
        await Served(async (client, root) =>
        {
            async Task<HttpStatusCode> Ask(string from)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "api/search?q=dagger"));
                request.Headers.Add("X-Forwarded-For", from);
                return (await client.SendAsync(request)).StatusCode;
            }
            Assert.Equal(HttpStatusCode.OK, await Ask("203.0.113.1"));
            Assert.Equal(HttpStatusCode.TooManyRequests, await Ask("203.0.113.1"));
            Assert.Equal(HttpStatusCode.OK, await Ask("203.0.113.2"));
        }, askPerMinute: 1, proxies: ["127.0.0.0/8"]);

    [Fact]
    public async Task ASlowConnectionCanBeImitated() =>
        // Locally a page loads in milliseconds, so what a reader sees while one is loading can
        // only be checked with the answers held back on purpose; held back, they still come back
        // together rather than one after another.
        await Served(async (client, root) =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                client.GetAsync(new Uri(root, "dialog/" + DemoIndex.DialogId + ".json"))));
            Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
            Assert.InRange(clock.ElapsedMilliseconds, 400, 1500);
        }, latency: 450);
}
