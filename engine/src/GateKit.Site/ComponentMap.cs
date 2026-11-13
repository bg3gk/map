using System.Text.Json;

namespace GateKit.Site;

/// <summary>One field of a component, as the layout files record it.</summary>
public sealed record LayoutField(string Name, long? Offset, string? Type, string? Source, string? Evidence);

/// <summary>A struct a component holds inside itself, laid out the same way.</summary>
public sealed record LayoutStruct(string Name, long? Size, IReadOnlyList<LayoutField> Fields);

/// <summary>What was worked out about one component: its size, its fields, and how sure we are.</summary>
public sealed record ComponentLayout
{
    public required string Name { get; init; }

    /// <summary>The batch it was mapped in, which is the folder it is filed under.</summary>
    public required string Batch { get; init; }

    /// <summary>The name Script Extender's Lua API gives it.</summary>
    public string? ExtName { get; init; }

    public long? Size { get; init; }

    public bool OneFrame { get; init; }

    public string? Status { get; init; }

    public string? Confidence { get; init; }

    public string? Summary { get; init; }

    /// <summary>What the layout was last checked against a running game with.</summary>
    public string? RawCheck { get; init; }

    public long? Minutes { get; init; }

    public long? FunctionsRead { get; init; }

    public IReadOnlyList<string> OpenQuestions { get; init; } = [];

    public IReadOnlyList<LayoutField> Fields { get; init; } = [];

    public IReadOnlyList<LayoutStruct> Structs { get; init; } = [];
}

/// <summary>Where a component was actually seen, on one side of the game.</summary>
public sealed record Sighting(string Side, IReadOnlyList<string> Labels, IReadOnlyList<string> Levels, long Max, long Samples);

/// <summary>What the extender's own census says about a component.</summary>
public sealed record Census(string Side, long? Index, long? InlineSize, long? TotalSize, bool OneFrame, bool Replicated, bool Mapped, string? Verified);

/// <summary>
/// Our own map of the engine's components: what each one is made of, and where it was seen.
///
/// It is read out of <c>research/coverage/</c>, which is working material committed to this
/// repository — the layouts are the product of weeks of reading a running game, and the sightings
/// are what backs each claim. Nothing of Larian's is in it beyond the identifiers a sighting
/// cannot be described without naming.
///
/// The sightings are also the seam between the three maps. A label like
/// <c>dialog:DEN_GoblinScouts_Captive_b452cf49-…</c> or <c>enemies:S_HAG_WoodWoad_001</c> names a
/// dialogue or an object of the game, so this class also keeps the thread the other way round:
/// which components were seen in a dialogue, on an object, in a level.
/// </summary>
public sealed class ComponentMap
{
    private readonly Dictionary<string, ComponentLayout> _layouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Sighting>> _sightings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Census>> _census = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedSet<string>> _inDialog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortedSet<string>> _onObject = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortedSet<string>> _inLevel = new(StringComparer.Ordinal);

    /// <summary>Every component with a page, in the order their names sort.</summary>
    public IReadOnlyList<string> Components { get; }

    /// <summary>The build of the game the census was collected from.</summary>
    public string Source { get; }

    /// <summary>When the census was collected, and which extender build it covers.</summary>
    public string? Collected { get; }

    public long Extender { get; }

    /// <summary>
    /// The folder of the coverage material, found by walking up from a starting folder. The map is
    /// committed, so it travels with the repository and needs no setting on any machine.
    /// </summary>
    public static string? Find(string? from = null)
    {
        var folder = new DirectoryInfo(from ?? Directory.GetCurrentDirectory());
        while (folder != null)
        {
            var coverage = Path.Combine(folder.FullName, "research", "coverage");
            if (File.Exists(Path.Combine(coverage, "coverage.json")))
            {
                return coverage;
            }
            folder = folder.Parent;
        }
        return null;
    }

    /// <summary>The folder the map was read from.</summary>
    public string Folder { get; }

    /// <summary>Every layout written down, by component.</summary>
    public IEnumerable<ComponentLayout> Layouts => _layouts.Values;

    public ComponentMap(string folder, SlugBook slugs)
    {
        Folder = folder;
        var names = new SortedSet<string>(StringComparer.Ordinal);

        var layouts = Path.Combine(folder, "layouts");
        if (Directory.Exists(layouts))
        {
            foreach (var file in Directory.EnumerateFiles(layouts, "*.json", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var layout = ReadLayout(file, Path.GetFileName(Path.GetDirectoryName(file)) ?? "");
                if (layout != null)
                {
                    _layouts[layout.Name] = layout;
                    names.Add(layout.Name);
                }
            }
        }

        var seen = Path.Combine(folder, "seen", "index.json");
        if (File.Exists(seen))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(seen));
            foreach (var component in document.RootElement.EnumerateObject())
            {
                names.Add(component.Name);
                var list = new List<Sighting>();
                foreach (var side in component.Value.EnumerateObject())
                {
                    var labels = Strings(side.Value, "labels");
                    var levels = Strings(side.Value, "levels");
                    list.Add(new Sighting(side.Name, labels, levels,
                        Number(side.Value, "max") ?? 0, Number(side.Value, "samples") ?? 0));
                    Thread(component.Name, labels, levels);
                }
                _sightings[component.Name] = list;
            }
        }

        Source = "an unnamed build of the game";
        Collected = null;
        var census = Path.Combine(folder, "coverage.json");
        if (File.Exists(census))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(census));
            var root = document.RootElement;
            Source = Text(root, "game") ?? Source;
            Collected = Text(root, "collected");
            Extender = Number(root, "extender") ?? 0;
            if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in components.EnumerateArray())
                {
                    var name = Text(entry, "name");
                    if (name == null)
                    {
                        continue;
                    }
                    names.Add(name);
                    string? verified = null;
                    if (entry.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.Object)
                    {
                        verified = string.Join(", ", new[] { Text(v, "date"), Text(v, "stage"), Text(v, "scenario") }
                            .Where(s => !string.IsNullOrEmpty(s)));
                    }
                    if (!_census.TryGetValue(name, out var list))
                    {
                        _census[name] = list = [];
                    }
                    list.Add(new Census(Text(entry, "side") ?? "", Number(entry, "index"),
                        Number(entry, "inlineSize"), Number(entry, "totalSize"),
                        Flag(entry, "oneFrame"), Flag(entry, "replicated"), Flag(entry, "mapped"), verified));
                }
            }
        }

        Components = [.. names];
        slugs.Register("component", Components.Select(Routes.TypeId));
    }

    /// <summary>What was worked out about a component, when a layout file records it.</summary>
    public ComponentLayout? Layout(string name) => _layouts.GetValueOrDefault(name);

    /// <summary>Where a component was seen, one entry per side of the game.</summary>
    public IReadOnlyList<Sighting> Sightings(string name) => _sightings.GetValueOrDefault(name) ?? [];

    /// <summary>What the extender's census says about a component, one entry per side.</summary>
    public IReadOnlyList<Census> Census(string name) => _census.GetValueOrDefault(name) ?? [];

    /// <summary>The components seen while a dialogue was playing, by the dialogue's short name.</summary>
    public IReadOnlyCollection<string> InDialog(string dialog) =>
        _inDialog.GetValueOrDefault(dialog) ?? (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>The components seen on an object of the game, by the name the level gives it.</summary>
    public IReadOnlyCollection<string> OnObject(string name) =>
        _onObject.GetValueOrDefault(name) ?? (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>The components seen while a level was loaded.</summary>
    public IReadOnlyCollection<string> InLevel(string level) =>
        _inLevel.GetValueOrDefault(level) ?? (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>The levels any component was seen in, which is what gets a level page a section.</summary>
    public IEnumerable<string> Levels => _inLevel.Keys;

    /// <summary>
    /// What a sighting label names. The capture writes <c>&lt;situation&gt;:&lt;what was looked
    /// at&gt;</c>, and the right half is a dialogue or an object of the game; a label with no colon
    /// is a situation that named nothing in particular (<c>fight</c>, <c>idle</c>).
    /// </summary>
    public static (string Situation, string? Dialog, string? Object) Parse(string label)
    {
        var cut = label.IndexOf(':', StringComparison.Ordinal);
        if (cut < 0)
        {
            return (label, null, null);
        }
        var situation = label[..cut];
        var target = label[(cut + 1)..];
        if (target.Length == 0)
        {
            return (situation, null, null);
        }
        // A dialogue is captured as its name with the uuid of the instance that played it.
        if (situation is "dialog" or "tree1" or "tree2" or "tree3")
        {
            return (situation, StripUuid(target), null);
        }
        return (situation, null, target);
    }

    /// <summary>The name without the trailing uuid the capture appends to it.</summary>
    private static string StripUuid(string text)
    {
        const int Uuid = 36;
        if (text.Length > Uuid + 1 && text[^(Uuid + 1)] == '_' && SiteIndex.IsUuid(text[^Uuid..]))
        {
            return text[..^(Uuid + 1)];
        }
        return text;
    }

    private void Thread(string component, IReadOnlyList<string> labels, IReadOnlyList<string> levels)
    {
        static void Add(Dictionary<string, SortedSet<string>> into, string key, string component)
        {
            if (!into.TryGetValue(key, out var set))
            {
                into[key] = set = new SortedSet<string>(StringComparer.Ordinal);
            }
            set.Add(component);
        }
        foreach (var label in labels)
        {
            var (_, dialog, target) = Parse(label);
            if (dialog != null)
            {
                Add(_inDialog, dialog, component);
            }
            else if (target != null)
            {
                Add(_onObject, target, component);
            }
        }
        foreach (var level in levels)
        {
            Add(_inLevel, level, component);
        }
    }

    private static ComponentLayout? ReadLayout(string file, string batch)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(file));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Text(root, "component") is not { Length: > 0 } name)
        {
            return null;
        }
        return new ComponentLayout
        {
            Name = name,
            Batch = batch,
            ExtName = Text(root, "ext_name"),
            Size = Number(root, "size"),
            OneFrame = Flag(root, "oneFrame"),
            Status = Text(root, "status"),
            Confidence = Text(root, "confidence"),
            Summary = Text(root, "summary"),
            RawCheck = Text(root, "raw_check"),
            Minutes = Number(root, "minutes"),
            FunctionsRead = Number(root, "functions_read"),
            OpenQuestions = Strings(root, "open_questions"),
            Fields = ReadFields(root),
            Structs = root.TryGetProperty("structs", out var structs) && structs.ValueKind == JsonValueKind.Array
                ? [.. structs.EnumerateArray()
                    .Where(s => s.ValueKind == JsonValueKind.Object)
                    .Select(s => new LayoutStruct(Text(s, "name") ?? "", Number(s, "size"), ReadFields(s)))]
                : [],
        };
    }

    private static IReadOnlyList<LayoutField> ReadFields(JsonElement owner)
    {
        if (!owner.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return [.. fields.EnumerateArray()
            .Where(f => f.ValueKind == JsonValueKind.Object)
            .Select(f => new LayoutField(Text(f, "name") ?? "", Number(f, "offset"), Text(f, "type"),
                Text(f, "source"), Text(f, "evidence")))];
    }

    private static string? Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static bool Flag(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string> Strings(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return [.. value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)];
    }
}
