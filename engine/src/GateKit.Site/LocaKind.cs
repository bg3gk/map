namespace GateKit.Site;

/// <summary>
/// A line of the game's text: what it says in every language the index holds, with each gendered
/// variant, the voice recordings behind it, and every page that says it.
/// </summary>
public sealed class LocaKind : IPageKind
{
    public string Kind => "loca";

    public string About => "a line of the game's text in every language, its gendered variants, the recordings that voice it, and where it is said";

    public bool Available(SiteSources sources) => sources.Game != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var index = sources.Game!;
        var db = index.Db;
        using var texts = Grouper.ByText(db,
            "SELECT handle, lang, variant, version, text FROM loca WHERE handle IS NOT NULL ORDER BY handle, lang, variant");
        using var said = Grouper.ByText(db,
            "SELECT l.handle, d.name, l.node FROM dialog_lines l JOIN dialogs d ON d.id = l.dialog_id "
            + "WHERE d.shadowed = 0 AND l.handle IS NOT NULL ORDER BY l.handle, d.name");
        using var placed = Grouper.ByText(db,
            "SELECT display_handle, uuid FROM entities WHERE display_handle IS NOT NULL AND display_handle <> '' ORDER BY display_handle, uuid");
        using var templated = Grouper.ByText(db,
            "SELECT display_handle, uuid FROM templates WHERE display_handle IS NOT NULL AND display_handle <> '' ORDER BY display_handle, uuid");
        using var voiced = Grouper.ByText(db,
            "SELECT handle, speaker, file, length, language FROM voice_lines WHERE handle IS NOT NULL ORDER BY handle, language, file");

        foreach (var handle in index.Slugs.Of(Kind).Keys.Order(StringComparer.Ordinal))
        {
            var lines = texts.Take(handle);
            var english = index.Says(handle);
            var dialogs = said.Take(handle);
            var names = placed.Take(handle).Select(r => index.Ref(r.S("uuid")))
                .Concat(templated.Take(handle).Select(r => index.Ref(r.S("uuid"))))
                .ToArray();
            var voices = voiced.Take(handle);
            var title = english is { Length: > 0 } ? Shorten(english) : handle;
            yield return new PageDoc
            {
                Kind = Kind,
                Id = handle,
                Route = Routes.Route(Kind, index.Slugs.Of(Kind)[handle]),
                Title = title,
                Subtitle = "a line of the game's text",
                Fields =
                [
                    new SiteField("handle", V.Id(handle)),
                    new SiteField(SiteIndex.Primary, V.Text(english)),
                    new SiteField("languages", V.Num(lines.Where(l => string.IsNullOrEmpty(l.S("variant")) && !string.IsNullOrEmpty(l.S("text")))
                        .Select(l => l.S("lang")).Distinct(StringComparer.Ordinal).Count())),
                    new SiteField("voiced|how many recordings speak this line, in all languages", V.Num(voices.Count)),
                ],
                Sections =
                [
                    PageKinds.Table("In every language",
                        ["language", "variant|a gendered form: who speaks to whom, as the game's own file names it; empty for the line itself", "version", "text"],
                        lines, int.MaxValue,
                        r => [V.Text(r.S("lang")), V.Text(r.S("variant")), V.Num(r.L("version")), V.Text(r.S("text"))]),
                    PageKinds.Table("Said in", ["dialogue", "node"], dialogs, rowLimit,
                        r => [index.Dialog(r.S("name")), V.Id(r.S("node"))]),
                    PageKinds.Table("The name of", ["object"], names, rowLimit, v => [v]),
                    PageKinds.Table("Voiced by", ["speaker", "language", "file", "seconds"], voices, rowLimit,
                        r => [index.Ref(r.S("speaker")), V.Text(r.S("language")), V.Path(r.S("file")), V.Num(r.D("length"))]),
                ],
            };
        }
    }

    /// <summary>A line as a title: its first eighty characters, cut at a word.</summary>
    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        if (line.Length <= 80)
        {
            return line;
        }
        var cut = line.LastIndexOf(' ', 79);
        return line[..(cut > 40 ? cut : 80)] + "…";
    }
}
