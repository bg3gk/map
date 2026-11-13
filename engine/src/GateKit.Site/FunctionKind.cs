using System.Globalization;

namespace GateKit.Site;

/// <summary>
/// Addresses in the engine's binaries, written the two ways a reader with a decompiler meets them:
/// relative to the binary (<c>bg3.exe+0x77F580</c>, what a debugger takes) and absolute
/// (<c>0x14077F580</c>, what Ghidra and IDA show, and what their <c>FUN_14077f580</c> labels are
/// made of).
/// </summary>
public static class Addresses
{
    /// <summary>
    /// Where a binary is loaded when it is opened in a decompiler: the executables at
    /// <c>0x140000000</c> and libraries at <c>0x180000000</c>, the defaults for 64-bit Windows -
    /// the same rule GateKit's own decompiling tools use.
    /// </summary>
    public static long ImageBase(string binary) =>
        binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 0x140000000 : 0x180000000;

    public static string Relative(string binary, long rva) => binary + "+0x" + rva.ToString("X", CultureInfo.InvariantCulture);

    public static string Absolute(string binary, long rva) => "0x" + (ImageBase(binary) + rva).ToString("X", CultureInfo.InvariantCulture);

    /// <summary>The label a decompiler gives a function it knows no name for.</summary>
    public static string MachineLabel(string binary, long rva) => "FUN_" + (ImageBase(binary) + rva).ToString("x", CultureInfo.InvariantCulture);

    /// <summary>One long for a place in one binary, so a table of them sorts the way the binaries and addresses do.</summary>
    public static long Key(long dll, long rva) => (dll << 40) + rva;
}

/// <summary>
/// A function of one of the engine's binaries: where it is in both decompilers' terms, what we
/// know it is called and how we know, what it calls and what calls it, the strings and data it
/// touches, and its twin in the other build of the game.
/// </summary>
public sealed class FunctionKind : IPageKind
{
    public string Kind => "function";

    public string About => "a function of one of the engine's binaries: its address, its name and where the name comes from, its calls both ways, its strings and data, and its twin in the other build";

    public SiteNotice Notice => SiteNotice.OurFindings;

    public bool Available(SiteSources sources) => sources.Symbols != null;

    public int Count(SiteSources sources) => sources.Symbols?.FunctionCount ?? 0;

    public IEnumerable<PageDoc> Pages(SiteSources sources, int rowLimit)
    {
        var symbols = sources.Symbols!;
        var db = symbols.Db;
        const string K = "(({0}) << 40) + ({1})";
        string Key(string dll, string rva) => string.Format(CultureInfo.InvariantCulture, K, dll, rva);

        using var exported = Grouper.ByLong(db,
            $"SELECT {Key("dll_id", "rva")} AS k, full_name, mangled FROM symbols WHERE rva IS NOT NULL ORDER BY dll_id, rva");
        using var extender = Grouper.ByLong(db,
            $"SELECT {Key("dll_id", "rva")} AS k, name, mapping, note FROM se_symbols ORDER BY dll_id, rva");
        using var calls = Grouper.ByLong(db,
            $"SELECT {Key("dll_id", "caller_rva")} AS k, callee_dll, callee_rva, import, sites FROM calls ORDER BY dll_id, caller_rva");
        using var callers = Grouper.ByLong(db,
            $"SELECT {Key("callee_dll", "callee_rva")} AS k, dll_id, caller_rva, sites FROM calls WHERE callee_rva IS NOT NULL ORDER BY callee_dll, callee_rva");
        using var strings = Grouper.ByLong(db,
            $"SELECT {Key("s.dll_id", "r.func_rva")} AS k, s.text, s.rva FROM string_refs r JOIN strings s ON s.id = r.string_id ORDER BY k, s.rva");
        using var data = Grouper.ByLong(db,
            $"SELECT {Key("dll_id", "func_rva")} AS k, target_rva, kind, uses FROM xrefs ORDER BY dll_id, func_rva, target_rva");
        using var slots = Grouper.ByLong(db,
            $"SELECT {Key("v.dll_id", "s.rva")} AS k, v.rva AS vtable, s.idx, v.symbol_id FROM vtab_slots s JOIN vtabs v ON v.id = s.vtab_id ORDER BY k, v.rva, s.idx");
        using var asGame = Grouper.ByLong(db,
            $"SELECT {Key("game_dll", "game_rva")} AS k, toolkit_dll, toolkit_rva, method, evidence, relation FROM matches ORDER BY game_dll, game_rva");
        using var asToolkit = Grouper.ByLong(db,
            $"SELECT {Key("toolkit_dll", "toolkit_rva")} AS k, game_dll, game_rva, method, evidence, relation FROM matches ORDER BY toolkit_dll, toolkit_rva");
        using var pieces = Grouper.ByLong(db,
            $"SELECT {Key("dll_id", "parent")} AS k, rva FROM chunks ORDER BY dll_id, parent, rva");
        using var names = Grouper.ByLong(db,
            $"SELECT {Key("dll_id", "func_rva")} AS k, type, kind FROM type_names WHERE func_rva IS NOT NULL ORDER BY k");

        foreach (var row in db.Rows("SELECT dll_id, rva, size, leaf FROM functions ORDER BY dll_id, rva"))
        {
            var dll = row.L("dll_id")!.Value;
            var rva = row.L("rva")!.Value;
            var key = Addresses.Key(dll, rva);
            var binary = symbols.Binary(dll);
            var id = Addresses.Relative(binary, rva);
            var export = exported.Take(key);
            var se = extender.Take(key);
            var matchedAsGame = asGame.Take(key);
            var matchedAsToolkit = asToolkit.Take(key);
            var (name, source) = symbols.NameOf(export, se, matchedAsGame);
            var twin = symbols.Twin(dll, rva);

            yield return new PageDoc
            {
                Kind = Kind,
                Id = id,
                Route = Routes.Route(Kind, id),
                Title = name ?? Addresses.MachineLabel(binary, rva),
                Subtitle = "a function of " + binary + (name == null ? ", not yet named" : ""),
                Fields =
                [
                    new SiteField("name|the identifier a decompiler takes; empty while nobody knows it", V.Id(name)),
                    new SiteField("named by|where the name comes from: the binary's own export, Script Extender, or a match to the Toolkit's export - and how", V.Text(source ?? "unknown")),
                    new SiteField("title|a readable name for people, in our own words", V.None),
                    new SiteField("note|what it does, in a line or two of our own", V.None),
                    new SiteField("binary", V.Id(binary)),
                    new SiteField("address|relative to the binary, as a debugger takes it", V.Id(id)),
                    new SiteField("VA|the absolute address a decompiler shows, at the binary's default base", V.Id(Addresses.Absolute(binary, rva))),
                    new SiteField("label|what a decompiler calls it while it has no name", V.Id(Addresses.MachineLabel(binary, rva))),
                    new SiteField("size|bytes, as the exception table gives them", V.Num(row.L("size"))),
                    new SiteField("leaf|1 when it calls nothing and keeps nothing on the stack", V.Num(row.L("leaf"))),
                    new SiteField("build|the version of the binary these addresses are true for", V.Text(symbols.Build(dll))),
                    new SiteField("twin|the same function in the other build of the game, found through the Toolkit function both match", twin is { } t
                        ? symbols.Function(t.Dll, t.Rva, null, t.Via)
                        : symbols.IsGame(dll) ? V.Text("no twin known") : V.None),
                ],
                Sections =
                [
                    PageKinds.Table("Calls", ["function", "sites|how many places in it make the call"], calls.Take(key), rowLimit,
                        r => [r.S("import") is { Length: > 0 } import
                                ? V.Id(import, "imported")
                                : symbols.Function(r.L("callee_dll"), r.L("callee_rva")),
                            V.Num(r.L("sites"))]),
                    PageKinds.Table("Called by", ["function", "sites"], callers.Take(key), rowLimit,
                        r => [symbols.Function(r.L("dll_id"), r.L("caller_rva")), V.Num(r.L("sites"))]),
                    PageKinds.Table("Strings it uses", ["text", "address"], strings.Take(key), rowLimit,
                        r => [V.Text(r.S("text")), V.Id(Addresses.Relative(binary, r.L("rva") ?? 0))]),
                    PageKinds.Table("Data it touches", ["address", "how|r: read, w: written, a: its address taken", "uses"], data.Take(key), rowLimit,
                        r => [V.Id(Addresses.Relative(binary, r.L("target_rva") ?? 0)), V.Text(r.S("kind")), V.Num(r.L("uses"))]),
                    PageKinds.Table("In virtual tables", ["vtable", "slot", "class"], slots.Take(key), rowLimit,
                        r => [V.Id(Addresses.Relative(binary, r.L("vtable") ?? 0)), V.Num(r.L("idx")), symbols.ClassOf(sources, r.L("symbol_id"))]),
                    PageKinds.Table("Matched to the Toolkit", ["function", "method|what the match rests on", "evidence", "relation"], matchedAsGame, rowLimit,
                        r => [symbols.Function(r.L("toolkit_dll"), r.L("toolkit_rva")), V.Text(r.S("method")), V.Num(r.L("evidence")), V.Text(r.S("relation"))]),
                    PageKinds.Table("Matched in the game", ["function", "method", "evidence", "relation"], matchedAsToolkit, rowLimit,
                        r => [symbols.Function(r.L("game_dll"), r.L("game_rva")), V.Text(r.S("method")), V.Num(r.L("evidence")), V.Text(r.S("relation"))]),
                    PageKinds.Table("Exported as", ["name", "decorated"], export, rowLimit,
                        r => [V.Id(r.S("full_name")), V.Id(r.S("mangled"))]),
                    PageKinds.Table("Script Extender knows it as", ["name", "mapping", "note"], se, rowLimit,
                        r => [V.Id(r.S("name")), V.Text(r.S("mapping")), V.Text(r.S("note"))]),
                    PageKinds.Table("Names a type", ["type", "as"], names.Take(key), rowLimit,
                        r => [symbols.Type(sources, r.S("type")), V.Text(r.S("kind"))]),
                    PageKinds.Table("Pieces elsewhere", ["address|a part of this function the compiler moved away from it"], pieces.Take(key), rowLimit,
                        r => [V.Id(Addresses.Relative(binary, r.L("rva") ?? 0))]),
                ],
            };
        }
    }
}
