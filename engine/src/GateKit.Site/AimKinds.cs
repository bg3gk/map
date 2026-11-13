using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GateKit.Site;

/// <summary>
/// How far the work has got, measured the way <c>docs/design/what-this-is-for.md</c> says it is
/// measured - and deliberately with no single percentage. Where nothing says how many things there
/// are, the measure is how much of what we wrote down still carries a name the machine invented;
/// where the game itself counts them, it is plainly N of M.
/// </summary>
public sealed partial class Aim
{
    /// <summary>A name the machine made up rather than one that says something: the same rule as <c>research/coverage/naming.py</c>.</summary>
    [GeneratedRegex(@"^(field_|_Pad|unknown|unk_|pad\d|dummy)", RegexOptions.IgnoreCase)]
    private static partial Regex MachineName();

    /// <summary>The layouts that count: the ones that are in the extender's build.</summary>
    private static readonly string[] InBuild = ["confirmed", "declared"];

    public int Fields { get; }

    public int Named { get; }

    public int Machine { get; }

    public int Padding { get; }

    public int Owed => Machine - Padding;

    /// <summary>Where each name came from: <c>game</c>, <c>toolkit</c>, <c>inferred</c>, <c>unknown</c>, ...</summary>
    public IReadOnlyList<(string Source, int Fields)> Sources { get; }

    /// <summary>The namespaces carrying the most machine names.</summary>
    public IReadOnlyList<(string Namespace, int Machine, int Fields)> Namespaces { get; }

    /// <summary>The components where a reader would pay off most: six fields or more, most of them the machine's.</summary>
    public IReadOnlyList<(string Component, int Machine, int Fields)> Worst { get; }

    /// <summary>Component types the game registers, and how far along each rung they are.</summary>
    public int ComponentTypes { get; }

    public int Seen { get; }

    public int Mapped { get; }

    public int Verified { get; }

    /// <summary>Systems the game registers, and how many we have described.</summary>
    public IReadOnlyList<SystemEntry> Systems { get; }

    /// <summary>Whether there was anything to measure at all.</summary>
    public bool Any => Fields > 0 || ComponentTypes > 0;

    public sealed record SystemEntry(string Name, string Side, long? Index, bool Mapped, string? Verified);

    public Aim(ComponentMap? map)
    {
        var sources = new Dictionary<string, int>(StringComparer.Ordinal);
        var machineIn = new Dictionary<string, int>(StringComparer.Ordinal);
        var fieldsIn = new Dictionary<string, int>(StringComparer.Ordinal);
        var worst = new List<(string, int, int)>();
        foreach (var layout in map?.Layouts ?? [])
        {
            if (layout.Status is not { } status || !InBuild.Contains(status) || layout.Fields.Count == 0)
            {
                continue;
            }
            var space = string.Join("::", layout.Name.Split("::").Take(2));
            var here = 0;
            foreach (var field in layout.Fields)
            {
                Fields++;
                sources[field.Source ?? "(none)"] = sources.GetValueOrDefault(field.Source ?? "(none)") + 1;
                fieldsIn[space] = fieldsIn.GetValueOrDefault(space) + 1;
                if (field.Name.StartsWith("_Pad", StringComparison.Ordinal))
                {
                    Padding++;
                }
                if (MachineName().IsMatch(field.Name))
                {
                    Machine++;
                    here++;
                    machineIn[space] = machineIn.GetValueOrDefault(space) + 1;
                }
                else
                {
                    Named++;
                }
            }
            worst.Add((layout.Name, here, layout.Fields.Count));
        }
        Sources = [.. sources.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).Select(s => (s.Key, s.Value))];
        Namespaces = [.. machineIn.OrderByDescending(m => m.Value).ThenBy(m => m.Key, StringComparer.Ordinal).Take(30)
            .Select(m => (m.Key, m.Value, fieldsIn[m.Key]))];
        Worst = [.. worst.Where(w => w.Item3 >= 6 && w.Item2 > 0)
            .OrderByDescending(w => (double)w.Item2 / w.Item3).ThenByDescending(w => w.Item2).ThenBy(w => w.Item1, StringComparer.Ordinal)];

        var census = map == null ? null : Path.Combine(map.Folder, "coverage.json");
        var systems = new List<SystemEntry>();
        if (census != null && File.Exists(census))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(census));
            var types = new Dictionary<string, (bool Mapped, bool Verified)>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("components").EnumerateArray())
            {
                var name = entry.GetProperty("name").GetString() ?? "";
                var mapped = entry.TryGetProperty("mapped", out var m) && m.ValueKind == JsonValueKind.True;
                var verified = entry.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.Object;
                var was = types.GetValueOrDefault(name);
                types[name] = (was.Mapped || mapped, was.Verified || verified);
            }
            ComponentTypes = types.Count;
            Mapped = types.Values.Count(t => t.Mapped);
            Verified = types.Values.Count(t => t.Verified);
            Seen = map!.Components.Count(c => map.Sightings(c).Count > 0);
            if (document.RootElement.TryGetProperty("systems", out var list))
            {
                foreach (var entry in list.EnumerateArray())
                {
                    systems.Add(new SystemEntry(
                        entry.GetProperty("name").GetString() ?? "",
                        entry.TryGetProperty("side", out var side) ? side.GetString() ?? "" : "",
                        entry.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number ? index.GetInt64() : null,
                        entry.TryGetProperty("mapped", out var mapped) && mapped.ValueKind == JsonValueKind.True,
                        entry.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.Object
                            ? verified.GetRawText() : null));
                }
            }
        }
        Systems = [.. systems.GroupBy(s => s.Name, StringComparer.Ordinal).Select(g => g.First()).OrderBy(s => s.Name, StringComparer.Ordinal)];
    }

    private static string N(int n) => n.ToString("#,0", CultureInfo.InvariantCulture);

    private static string Share(int part, int whole) => whole == 0 ? "" : ((double)part / whole).ToString("0.0%", CultureInfo.InvariantCulture);

    /// <summary>The two measures as a few lines of HTML, for the home page.</summary>
    public string Html(SymbolIndex? symbols)
    {
        if (!Any)
        {
            return "";
        }
        var facts = Sources.Where(s => s.Source is "game" or "toolkit").Sum(s => s.Fields);
        var text = new StringBuilder();
        text.Append("<h2>How far we have got</h2><p class=\"lead\">Measured, not estimated, and with no single percentage - ")
            .Append("<a href=\"/measure/naming\">how and why</a>.</p><ul class=\"aim\">");
        if (Fields > 0)
        {
            text.Append("<li><b>").Append(N(Fields)).Append("</b> fields written down; <b>").Append(N(Owed))
                .Append("</b> still owed a name (").Append(Share(Owed, Fields)).Append(") - <a href=\"/measure/owed\">where a reader pays off most</a></li>");
            text.Append("<li>where the names come from: ").Append(string.Join(", ", Sources.Select(s =>
                HtmlWriter.Escape(s.Source) + " " + N(s.Fields)))).Append(" - ").Append(Share(facts, Fields)).Append(" established as fact</li>");
        }
        if (ComponentTypes > 0)
        {
            text.Append("<li><b>").Append(N(Seen)).Append("</b> of ").Append(N(ComponentTypes)).Append(" component types seen alive, <b>")
                .Append(N(Verified)).Append("</b> verified against raw memory - <a href=\"/measure/registers\">every register</a></li>");
        }
        if (symbols != null)
        {
            text.Append("<li><b>").Append(N(symbols.NamedGameFunctions)).Append("</b> of ").Append(N(symbols.GameFunctions))
                .Append(" functions of the game's binaries go by a name; the rest are still a decompiler's label</li>");
        }
        return text.Append("</ul>").ToString();
    }
}

/// <summary>The measures of how far the work has got, each a page of its own.</summary>
public sealed class MeasureKind : IPageKind
{
    public string Kind => "measure";

    public string About => "a measure of how far the work has got: names still owed, the registers, and the extender's reach";

    public SiteNotice Notice => SiteNotice.OurFindings;

    public bool Available(SiteSources sources) => sources.Components != null || sources.Symbols != null;

    public int Count(SiteSources sources) => 3;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var aim = new Aim(sources.Components);
        var symbols = sources.Symbols;
        var facts = aim.Sources.Where(s => s.Source is "game" or "toolkit").Sum(s => s.Fields);
        yield return new PageDoc
        {
            Kind = Kind,
            Id = "naming",
            Route = "measure/naming",
            Title = "How much is still the machine's name",
            Subtitle = "among the fields we have written down, how many still carry a label the machine invented",
            Fields =
            [
                new SiteField("fields|every field of every layout in the extender's build", V.Num(aim.Fields)),
                new SiteField("ours|named with a name that says something", V.Num(aim.Named)),
                new SiteField("machine's|field_8, _Pad5, unknown_...", V.Num(aim.Machine)),
                new SiteField("padding|not a name we failed to find: the absence of a field", V.Num(aim.Padding)),
                new SiteField("owed a name|the real debt", V.Num(aim.Owed)),
                new SiteField("as fact|the game's own name or a Toolkit accessor's", V.Num(facts)),
            ],
            Sections =
            [
                PageKinds.Table("Where each name came from", ["source|game: the macOS build's FieldMeta; toolkit: a Toolkit accessor; inferred: read out of the code; unknown: we do not know, and say so", "fields"],
                    aim.Sources, int.MaxValue, s => [V.Text(s.Source), V.Num(s.Fields)]),
                PageKinds.Table("Namespaces carrying the most machine names", ["namespace", "machine's", "fields"],
                    aim.Namespaces, int.MaxValue, n => [V.Id(n.Namespace), V.Num(n.Machine), V.Num(n.Fields)]),
            ],
        };
        yield return new PageDoc
        {
            Kind = Kind,
            Id = "owed",
            Route = "measure/owed",
            Title = "Still owed a name",
            Subtitle = "components of six fields or more, the most machine-named first: where a reader pays off most",
            Fields =
            [
                new SiteField("components", V.Num(aim.Worst.Count)),
                new SiteField("owed a name", V.Num(aim.Owed)),
            ],
            Sections =
            [
                PageKinds.Table("Where to read next", ["component", "machine's", "fields"], aim.Worst, rowLimit,
                    w => [sources.Component(w.Component), V.Num(w.Machine), V.Num(w.Fields)]),
            ],
        };
        var registers = new List<IReadOnlyList<SiteValue>>();
        if (aim.ComponentTypes > 0)
        {
            registers.Add([V.Text("component types"), V.Num(aim.ComponentTypes), V.Text("seen alive"), V.Num(aim.Seen)]);
            registers.Add([V.Text("component types"), V.Num(aim.ComponentTypes), V.Text("declared by the extender"), V.Num(aim.Mapped)]);
            registers.Add([V.Text("component types"), V.Num(aim.ComponentTypes), V.Text("verified against raw memory"), V.Num(aim.Verified)]);
        }
        if (aim.Systems.Count > 0)
        {
            registers.Add([V.Text("systems"), V.Num(aim.Systems.Count), V.Text("declared by the extender"), V.Num(aim.Systems.Count(s => s.Mapped))]);
        }
        if (symbols != null)
        {
            registers.Add([V.Text("functions of the game's binaries"), V.Num(symbols.GameFunctions), V.Text("go by a name"), V.Num(symbols.NamedGameFunctions)]);
            registers.Add([V.Text("functions of the game's binaries"), V.Num(symbols.GameFunctions), V.Text("have a twin in the other build"), V.Num(symbols.Twins)]);
        }
        yield return new PageDoc
        {
            Kind = Kind,
            Id = "registers",
            Route = "measure/registers",
            Title = "Everything the game is made of, and how far we have got",
            Subtitle = "where the game itself says how many there are, progress is N of M",
            Fields =
            [
                new SiteField("collected|when the census of components and systems was taken", V.Text(sources.Components?.Collected)),
            ],
            Sections =
            [
                new SiteSection("Registers", ["register", "how many", "rung", "reached"], registers) { Total = registers.Count },
                PageKinds.Table("Systems", ["system", "side", "declared|whether the extender declares it"], aim.Systems, rowLimit,
                    s => [sources.Slugs.Link("system", Routes.TypeId(s.Name), s.Name), V.Text(s.Side), V.Flag(s.Mapped)]),
            ],
        };
    }
}

/// <summary>A system of the entity component system, as the game registers it.</summary>
public sealed class SystemKind : IPageKind
{
    public string Kind => "system";

    public string About => "a system of the entity component system, as the game registers it, and what we know of it";

    public SiteNotice Notice => SiteNotice.OurFindings;

    public bool Available(SiteSources sources) => sources.Components != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var aim = new Aim(sources.Components);
        foreach (var system in aim.Systems)
        {
            var id = Routes.TypeId(system.Name);
            if (sources.Slugs.Route(Kind, id) is not { } route)
            {
                continue;
            }
            yield return new PageDoc
            {
                Kind = Kind,
                Id = id,
                Route = route,
                Title = system.Name,
                Subtitle = "a " + system.Side + " system",
                Fields =
                [
                    new SiteField("side", V.Text(system.Side)),
                    new SiteField("index|its place in the game's registry", V.Num(system.Index)),
                    new SiteField("declared|whether the extender declares it", V.Flag(system.Mapped)),
                    new SiteField("verified", V.Text(system.Verified)),
                    new SiteField("class", sources.Symbol(system.Name)),
                    new SiteField("reads|which components it reads, when it runs - not described yet", V.Text("not described yet")),
                ],
            };
        }
    }

    /// <summary>Registers the systems' pages, so a link to one is known before it is rendered.</summary>
    public static void Register(SiteSources sources)
    {
        if (sources.Components != null)
        {
            sources.Slugs.Register("system", new Aim(sources.Components).Systems.Select(s => Routes.TypeId(s.Name)));
        }
    }
}

/// <summary>An engine global Script Extender reaches for: where it is in each build, and how it is found.</summary>
public sealed class GlobalKind : IPageKind
{
    public string Kind => "global";

    public string About => "an engine global or entry point Script Extender looks up: its address in each binary, and how it is found";

    public SiteNotice Notice => SiteNotice.OurFindings;

    public bool Available(SiteSources sources) => sources.Symbols != null;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var symbols = sources.Symbols!;
        using var where = Grouper.ByText(symbols.Db, "SELECT name, dll_id, rva, mapping, note FROM se_symbols ORDER BY name, dll_id, rva");
        foreach (var name in sources.Slugs.Of(Kind).Keys.Order(StringComparer.Ordinal))
        {
            var places = where.Take(name);
            yield return new PageDoc
            {
                Kind = Kind,
                Id = name,
                Route = Routes.Route(Kind, sources.Slugs.Of(Kind)[name]),
                Title = name.Replace("__", "::", StringComparison.Ordinal),
                Subtitle = "what Script Extender looks up as " + name,
                Fields =
                [
                    new SiteField("as SE names it", V.Id(name)),
                    new SiteField("places|where it was found, in all binaries", V.Num(places.Count)),
                    new SiteField("Lua|whether Lua can read it through a getter - not recorded yet", V.Text("not recorded yet")),
                ],
                Sections =
                [
                    PageKinds.Table("Where it is", ["binary", "address", "VA", "found by|the pattern Script Extender matches", "note"], places, rowLimit,
                        r =>
                        {
                            var binary = symbols.Binary(r.L("dll_id") ?? 0);
                            var rva = r.L("rva") ?? 0;
                            return
                            [
                                V.Id(binary), symbols.Function(r.L("dll_id"), r.L("rva")),
                                V.Id(Addresses.Absolute(binary, rva)), V.Text(r.S("mapping")), V.Text(r.S("note")),
                            ];
                        }),
                ],
            };
        }
    }
}

/// <summary>
/// The names we know, as files a decompiler loads: one CSV per binary of the game - address,
/// name, where the name comes from, the build - and two small scripts that apply them in Ghidra
/// and in IDA. Names and addresses only: they are ours to give; the code they name is not.
/// </summary>
public static class NamesExport
{
    public static IEnumerable<KeyValuePair<string, byte[]>> Files(SymbolIndex symbols)
    {
        foreach (var (binary, rows) in symbols.KnownNames())
        {
            var csv = new StringBuilder("va,rva,name,source,build\n");
            foreach (var row in rows)
            {
                csv.Append(Addresses.Absolute(binary, row.Rva)).Append(',')
                    .Append("0x").Append(row.Rva.ToString("X", CultureInfo.InvariantCulture)).Append(',')
                    .Append(Csv(row.Name)).Append(',').Append(Csv(row.Source)).Append(',').Append(Csv(row.Build)).Append('\n');
            }
            yield return new("export/names-" + Path.GetFileNameWithoutExtension(binary) + ".csv", Encoding.UTF8.GetBytes(csv.ToString()));
        }
        yield return new("export/ghidra_names.py", Encoding.UTF8.GetBytes(Ghidra));
        yield return new("export/ida_names.py", Encoding.UTF8.GetBytes(Ida));
    }

    private static string Csv(string text) =>
        text.IndexOfAny([',', '"', '\n']) < 0 ? text : "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private const string Ghidra = """
# Applies the GateKit map's names to the binary open in Ghidra.
# Script Manager -> run, and pick names-bg3.csv or names-bg3_dx11.csv for the binary that is open.
# Each function gets our name as a label, and where the name comes from as a plate comment.
# @category GateKit
import csv
from ghidra.program.model.symbol import SourceType

path = askFile("names CSV from the GateKit map", "Apply").getAbsolutePath()
listing = currentProgram.getListing()
done = 0
with open(path) as handle:
    for row in csv.DictReader(handle):
        address = toAddr(row["va"])
        name = row["name"].replace(" ", "_")
        function = getFunctionAt(address)
        if function is not None:
            function.setName(name, SourceType.USER_DEFINED)
        else:
            createLabel(address, name, True)
        listing.setComment(address, 3, "GateKit map: " + row["name"] + " (" + row["source"] + ")")
        done += 1
print("named %d places" % done)
""";

    private const string Ida = """
# Applies the GateKit map's names to the binary open in IDA.
# File -> Script file..., and pick names-bg3.csv or names-bg3_dx11.csv for the binary that is open.
import csv
import ida_kernwin
import idc

path = ida_kernwin.ask_file(0, "*.csv", "names CSV from the GateKit map")
done = 0
with open(path) as handle:
    for row in csv.DictReader(handle):
        address = int(row["va"], 16)
        idc.set_name(address, row["name"], idc.SN_NOWARN | idc.SN_NOCHECK | idc.SN_FORCE)
        idc.set_func_cmt(address, "GateKit map: " + row["name"] + " (" + row["source"] + ")", 1)
        done += 1
print("named %d places" % done)
""";
}
