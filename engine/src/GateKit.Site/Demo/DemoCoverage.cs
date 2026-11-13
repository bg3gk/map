namespace GateKit.Site.Demo;

/// <summary>
/// A component map of one component, written into a folder shaped like
/// <c>research/coverage/</c>. Its sightings name the dialogue, the object and the level that
/// <see cref="DemoIndex"/> puts in the game, which is what makes the thread between the
/// three maps testable without any game data at all.
/// </summary>
public static class DemoCoverage
{
    public const string Component = DemoSymbols.ClassName;

    /// <summary>A system of the census, which nothing has described yet.</summary>
    public const string System = "ecl::TestSystem";
    public const string Level = "TUT_Avernus_C";

    /// <summary>The capture appends the uuid of the instance that played a dialogue to its name.</summary>
    public const string DialogLabel = "dialog:" + DemoIndex.DialogId + "_" + DemoIndex.DialogUuid;

    public const string ObjectLabel = "enemies:" + DemoIndex.EntityName;

    public static string Write(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "layouts", "alive"));
        Directory.CreateDirectory(Path.Combine(folder, "seen"));

        File.WriteAllText(Path.Combine(folder, "layouts", "alive", "ecl.TestComponent.json"), $$"""
{
  "component": "{{Component}}",
  "ext_name": "{{DemoSymbols.ExtName}}",
  "size": 12,
  "seen": true,
  "oneFrame": false,
  "status": "confirmed",
  "confidence": "medium",
  "summary": "A countdown the client keeps while a turn is running.",
  "raw_check": "3 instances, research/coverage/check.py raw stage",
  "minutes": 7,
  "functions_read": 4,
  "open_questions": ["Whether field_8 is a flag or a counter."],
  "fields": [
    { "name": "Timer", "offset": 0, "type": "float", "source": "inferred", "evidence": "A countdown in seconds." },
    { "name": "Flags", "offset": 4, "type": "{{DemoSymbols.EnumName}}", "source": "game", "evidence": "The macOS build's FieldMeta names it." },
    { "name": "field_8", "offset": 8, "type": "bool", "source": "unknown", "evidence": "Set when the countdown ends." },
    { "name": "_Pad9", "offset": 9, "type": "uint8_t[3]", "source": "unknown", "evidence": "padding" },
    { "name": "field_C", "offset": 12, "type": "int32_t", "source": "unknown", "evidence": "No reader seen." },
    { "name": "field_10", "offset": 16, "type": "int32_t", "source": "unknown", "evidence": "No reader seen." }
  ],
  "structs": [
    { "name": "TestPair", "size": 8, "fields": [
      { "name": "Amount", "offset": 0, "type": "int32_t", "source": "inferred", "evidence": "Copied from a confirmed layout." }
    ] }
  ]
}
""");

        File.WriteAllText(Path.Combine(folder, "seen", "index.json"), $$"""
{
  "{{Component}}": {
    "client": {
      "labels": ["idle", "{{DialogLabel}}", "{{ObjectLabel}}"],
      "levels": ["{{Level}}"],
      "max": 8,
      "samples": 48
    }
  }
}
""");

        File.WriteAllText(Path.Combine(folder, "coverage.json"), $$"""
{
  "collected": "2026-11-13 11:09:09",
  "game": "a synthetic build written by the tests",
  "extender": 34,
  "components": [
    { "side": "client", "name": "{{Component}}", "index": 0, "inlineSize": 12, "totalSize": 12,
      "oneFrame": false, "replicated": false, "mapped": true,
      "verified": { "date": "2026-11-13", "scenario": "research/coverage/check.py", "stage": "mapped fields equal raw memory", "instances": 4 } },
    { "side": "server", "name": "{{Component}}", "index": 0, "inlineSize": null, "totalSize": null,
      "oneFrame": false, "replicated": false, "mapped": false, "verified": null }
  ],
  "systems": [
    { "side": "client", "name": "{{System}}", "index": 0, "mapped": false, "verified": null }
  ]
}
""");
        return folder;
    }
}
