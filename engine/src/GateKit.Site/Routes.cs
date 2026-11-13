using System.Text;

namespace GateKit.Site;

/// <summary>
/// Turns the game's own ids into path segments. A segment has to survive being a file name on
/// every operating system and a URL at the same time, so anything outside a small safe set is
/// written as <c>!</c> and two hex digits of its UTF-8 bytes. Not <c>%</c>: a server decodes
/// <c>%27</c> in a request before it looks for the file, so a file whose name holds
/// <c>%27</c> can never be asked for, and <c>!</c> means nothing to either. The result is still
/// readable, because the game's ids are nearly all words, digits and underscores.
/// </summary>
public static class Routes
{
    /// <summary>The kinds that get a page, in the order the site lists them.</summary>
    public static readonly string[] Kinds =
        ["entity", "dialog", "timeline", "template", "stat", "level", "loca", "object", "source", "package", "osiris",
         "component", "system", "symbol", "enum", "function", "global", "measure"];

    /// <summary>
    /// The id of a C++ type in an address. The engine's names are qualified with <c>::</c>, which
    /// is not a usable path segment on every file system, so a dot stands in for it: the component
    /// <c>ecl::ActiveTurnComponent</c> is at <c>/component/ecl.ActiveTurnComponent</c>. It is the
    /// same spelling the layout files in <c>research/coverage/layouts</c> already use.
    /// </summary>
    public static string TypeId(string name) => name.Replace("::", ".", StringComparison.Ordinal);

    /// <summary>
    /// Whether a C++ name can have a page here. Over half of the names the engine's binaries carry
    /// are template instantiations — <c>ls::AnyArray&lt;ls::PagedArray, …&gt;</c>, one of them
    /// 18,108 characters long — and they are not classes a reader looks up: they are spellings of
    /// one. They appear on pages as identifiers; only plain qualified names get an address.
    /// </summary>
    public static bool IsPlainTypeName(string name)
    {
        if (name.Length == 0 || name.Length > 160)
        {
            return false;
        }
        foreach (var c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or ':' or '.'))
            {
                return false;
            }
        }
        return true;
    }

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    // `/` and `+` are kept as they are: a page is a row in one file now, not a file on a disk, so
    // a path inside the game's packages reads as a path (`source/Public/Shared/...`) and an
    // address in a binary as one (`function/bg3.exe+0x77F580`).
    private static bool Safe(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.' or '/' or '+';

    /// <summary>One path segment for an id: readable where it can be, encoded where it cannot.</summary>
    public static string Slug(string id)
    {
        var text = new StringBuilder(id.Length);
        foreach (var c in id)
        {
            if (Safe(c))
            {
                text.Append(c);
            }
            else
            {
                foreach (var b in Encoding.UTF8.GetBytes([c]))
                {
                    text.Append('!').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }
        // A segment that begins or ends with a dot, or is empty, is not a usable folder name.
        var slug = text.ToString();
        if (slug.Length == 0)
        {
            slug = "!00";
        }
        if (slug[0] == '.')
        {
            slug = "!2E" + slug[1..];
        }
        if (slug[^1] == '.')
        {
            slug = slug[..^1] + "!2E";
        }
        var stem = slug.Split('.')[0];
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            slug = "_" + slug;
        }
        return slug;
    }

    /// <summary>
    /// Slugs for a whole kind at once. Two ids that differ only in case would be one folder on
    /// Windows and macOS, so the later one (in ordinal order, so the result does not depend on the
    /// machine) gets <c>~2</c>, <c>~3</c>, … appended.
    /// </summary>
    public static Dictionary<string, string> SlugsFor(IEnumerable<string> ids)
    {
        var slugs = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids.OrderBy(i => i, StringComparer.Ordinal))
        {
            var slug = Slug(id);
            if (taken.TryGetValue(slug, out var used))
            {
                taken[slug] = used + 1;
                slugs[id] = slug + "~" + (used + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                taken[slug] = 1;
                slugs[id] = slug;
            }
        }
        return slugs;
    }

    /// <summary>
    /// The id a slug was made from, as far as the slug alone can say: every <c>!xx</c> read back
    /// as a byte of UTF-8. A slug that also carries <c>~2</c> or a <c>_</c> in front of a reserved
    /// name does not come back to its id, and a link to it says its text out loud instead.
    /// </summary>
    public static string Unslug(string slug)
    {
        if (!slug.Contains('!', StringComparison.Ordinal))
        {
            return slug;
        }
        var bytes = new List<byte>(slug.Length);
        for (var i = 0; i < slug.Length; i++)
        {
            if (slug[i] == '!' && i + 2 < slug.Length
                && byte.TryParse(slug.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(slug[i].ToString()));
            }
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>The route of a page: <c>dialog/DEN_AttackOnDen_Sapper_003</c>.</summary>
    public static string Route(string kind, string slug) => kind + "/" + slug;

    /// <summary>The leaf of a dialog's name: the game files them in folders, the site does not.</summary>
    public static string DialogId(string name)
    {
        var cut = name.LastIndexOf('/');
        return cut < 0 ? name : name[(cut + 1)..];
    }
}
