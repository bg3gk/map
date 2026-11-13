using GateKit.GameData;
using Microsoft.Data.Sqlite;

namespace GateKit.Site.Demo;

/// <summary>
/// A whole index in a dozen rows: one of everything the site renders, wired to each other the way
/// the game wires them, and one of each address that has given trouble - a stat whose name needs
/// escaping, a dialogue line whose handle has no text anywhere. No game data and no machine is
/// involved, so the tests run anywhere and `map demo` renders in a second.
/// </summary>
public static class DemoIndex
{
    public const string TemplateUuid = "11111111-1111-1111-1111-111111111111";
    public const string EntityUuid = "22222222-2222-2222-2222-222222222222";
    public const string TimelineUuid = "44444444-4444-4444-4444-444444444444";
    public const string Handle = "h11111111g1111g1111g1111g111111111111";
    public const string DialogUuid = "33333333-3333-3333-3333-333333333333";
    public const string EntityName = "S_CAMP_Dagger";
    public const string Level = "TUT_Avernus_C";
    public const string DialogName = "Camp/CAMP_TestTalk";
    public const string DialogId = "CAMP_TestTalk";
    public const string StatName = "WPN_TestDagger";
    public const string ParentStat = "WPN_Base";
    public const string EnglishLine = "A dagger, very sharp indeed.";
    public const string RussianLine = "A dagger in another language.";

    /// <summary>The same line in a language with no spaces between its words.</summary>
    public const string JapaneseLine = "とても鋭い短剣です。";

    /// <summary>A line only English has, so the other language's pack must hold nothing for it.</summary>
    public const string EnglishOnlyHandle = "h22222222g2222g2222g2222g222222222222";

    /// <summary>A raw object with no page of its own among the game's content.</summary>
    public const string ObjectKey = "55555555-5555-5555-5555-555555555555";

    /// <summary>The file it was read out of, as its page's id: the package, then the path.</summary>
    public const string SourceId = "Shared.pak/Public/Shared/Tags/Tag.lsf";

    /// <summary>A name the story scripts use.</summary>
    public const string OsirisName = "DB_Test_Camp";

    /// <summary>A stat whose name has an apostrophe, which the address writes as <c>!27</c>.</summary>
    public const string OddStatName = "Background_LegendOfBaldur'sGate";

    /// <summary>The game's own placeholder for a line that has no text in any language.</summary>
    public const string UnknownHandle = "ls::TranslatedStringRepository::s_HandleUnknown";

    /// <summary>Writes the index into <paramref name="path"/> and returns it.</summary>
    public static string Write(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        db.Open();
        Run(db, Schema.Tables);
        Run(db, $"""
INSERT INTO meta (key, value) VALUES ('source', 'the dummy data of map demo');

INSERT INTO templates (uuid, name, type, parent, display_handle, stats, visual, icon, package, mod)
VALUES ('{TemplateUuid}', 'WPN_TestDagger_A', 'item', NULL, '{Handle}', '{StatName}', 'vis', 'ico', 'Shared.pak', 'Shared');

INSERT INTO entities (uuid, name, type, template, display_handle, stats, level, scope, package, mod)
VALUES ('{EntityUuid}', '{EntityName}', 'item', '{TemplateUuid}', '{Handle}', '{StatName}', '{Level}', 'level', 'Gustav.pak', 'Gustav');

INSERT INTO dialogs (id, uuid, name, package, mod, path, category, timeline_uuid, nodes, shadowed)
VALUES (1, '{DialogUuid}', '{DialogName}', 'Gustav.pak', 'Gustav', 'Mods/Gustav/Story/Dialogs/Camp/CAMP_TestTalk.lsj', 'companion', '{TimelineUuid}', 2, 0);
INSERT INTO dialog_speakers (dialog_id, idx, object, mapping) VALUES (1, 0, '{EntityUuid}', 'speaker');
INSERT INTO dialog_nodes (dialog_id, uuid, constructor, speaker) VALUES (1, 'node-1', 'TagAnswer', 0);
INSERT INTO dialog_lines (dialog_id, node, handle, line_id) VALUES (1, 'node-1', '{Handle}', 'line-1');
INSERT INTO dialog_flags (dialog_id, node, kind, flag_type, flag, value, target) VALUES (1, 'node-1', 'set', 'Global', 'CAMP_TestTalk_Done', 1, 0);

INSERT INTO timelines (id, name, package, mod, path, uuid, dialog_resource, dialog_id, duration, phases, components, has_scene, shadowed)
VALUES (1, 'CAMP_TestTalk', 'Gustav.pak', 'Gustav', 'Mods/Gustav/Timeline/CAMP_TestTalk.lsf', '{TimelineUuid}', NULL, 1, 12.5, 2, 3, 1, 0);
INSERT INTO timeline_phases (timeline_id, idx, duration, dialog_node) VALUES (1, 0, 6.0, 'node-1');
INSERT INTO timeline_actors (id, timeline_id, uuid, speaker, kind, is_player, resource, scene_actor_type, scene_actor_index, scene_template)
VALUES (1, 1, '{EntityUuid}', 0, 'character', 0, NULL, NULL, NULL, '{TemplateUuid}');
INSERT INTO scene_actors (timeline_id, idx, actor_type, type_index, template) VALUES (1, 0, 1, 0, '{TemplateUuid}');
INSERT INTO track_types (id, name) VALUES (1, 'TLShowArmor');
INSERT INTO tracks (id, timeline_id, type_id, actor_id, phase, start, end, keys, hides, shows, attrs)
VALUES (1, 1, 1, 1, 0, 0.0, 6.0, 2, 1, 1, NULL);

INSERT INTO stats (id, name, type, parent, package, mod, file) VALUES (1, '{StatName}', 'Weapon', '{ParentStat}', 'Shared.pak', 'Shared', 'Public/Shared/Stats/Generated/Data/Weapon.txt');
INSERT INTO stats (id, name, type, parent, package, mod, file) VALUES (2, '{ParentStat}', 'Weapon', NULL, 'Shared.pak', 'Shared', 'Public/Shared/Stats/Generated/Data/Weapon.txt');
INSERT INTO stat_fields (stat_id, field, value) VALUES (1, 'Damage', '1d4');
INSERT INTO stat_fields (stat_id, field, value) VALUES (1, 'RootTemplate', '{TemplateUuid}');
INSERT INTO stat_fields (stat_id, field, value) VALUES (1, 'DisplayName', '{Handle}');
INSERT INTO stats (id, name, type, parent, package, mod, file) VALUES (3, 'Background_LegendOfBaldur''sGate', 'PassiveData', NULL, 'Gustav.pak', 'GustavDev', 'Public/GustavDev/Stats/Generated/Data/Passive.txt');
INSERT INTO stat_fields (stat_id, field, value) VALUES (3, 'DisplayName', '{Handle};2');
INSERT INTO dialog_nodes (dialog_id, uuid, constructor, speaker) VALUES (1, 'node-2', 'TagAnswer', 0);
INSERT INTO dialog_lines (dialog_id, node, handle, line_id) VALUES (1, 'node-2', '{UnknownHandle}', 'line-2');

INSERT INTO loca (lang, variant, handle, version, text) VALUES ('English', '', '{Handle}', 1, '{EnglishLine}');
INSERT INTO loca (lang, variant, handle, version, text) VALUES ('Russian', '', '{Handle}', 1, '{RussianLine}');
INSERT INTO loca (lang, variant, handle, version, text) VALUES ('Japanese', '', '{Handle}', 1, '{JapaneseLine}');
INSERT INTO loca (lang, variant, handle, version, text) VALUES ('English', '', '{EnglishOnlyHandle}', 1, 'Only ever said in English.');

INSERT INTO sources (id, package, mod, path, region, objects) VALUES (1, 'Shared.pak', 'Shared', 'Public/Shared/Tags/Tag.lsf', 'Tags', 1);
INSERT INTO names (id, name) VALUES (1, 'Tags');
INSERT INTO objects (id, source_id, node_id, key, name, truncated) VALUES (1, 1, 1, '{ObjectKey}', 'CAMP_Flammable_Tag', 0);
-- The template's own object, a second object that gives the same key again, and one with no key.
INSERT INTO names (id, name) VALUES (2, 'GameObjects');
INSERT INTO names (id, name) VALUES (3, 'DisplayName');
INSERT INTO names (id, name) VALUES (5, 'Stats');
INSERT INTO objects (id, source_id, node_id, key, name, truncated) VALUES (2, 1, 2, '{TemplateUuid}', 'WPN_TestDagger_A', 0);
INSERT INTO objects (id, source_id, node_id, key, name, truncated) VALUES (3, 1, 1, '{ObjectKey}', 'CAMP_Flammable_Tag', 0);
INSERT INTO objects (id, source_id, node_id, key, name, truncated) VALUES (4, 1, 1, NULL, NULL, 1);
INSERT INTO attrs (object_id, attr_id, value) VALUES (2, 3, '{Handle};1');
INSERT INTO attrs (object_id, attr_id, value) VALUES (2, 1, '{ObjectKey}');
INSERT INTO attrs (object_id, attr_id, value) VALUES (2, 5, '{StatName}');
INSERT INTO files (package, path, ext, size) VALUES ('Shared.pak', 'Public/Shared/Tags/Tag.lsf', 'lsf', 120);
INSERT INTO files (package, path, ext, size) VALUES ('Shared.pak', 'Public/Shared/Assets/dagger.GR2', 'gr2', 4096);
INSERT INTO files (package, path, ext, size) VALUES ('Gustav.pak', 'Mods/Gustav/Story/Dialogs/Camp/CAMP_TestTalk.lsj', 'lsj', 900);
INSERT INTO texts (id, package, mod, path, kind, bytes) VALUES (1, 'Gustav.pak', 'Gustav', 'Mods/Gustav/Story/RawFiles/Goals/Camp_Test.txt', 'goal', 300);
INSERT INTO osiris_functions (name, kind, params, ids, arity) VALUES ('{OsirisName}', 'database', '(GUIDSTRING)_Object', '0,0,0,0', 1);
INSERT INTO osiris_uses (text_id, name, role, uses) VALUES (1, '{OsirisName}', 'init', 2);
""");
        Run(db, Schema.Link);
        Run(db, Schema.Finish);
        db.Dispose();
        SqliteConnection.ClearAllPools();
        return path;
    }

    private static void Run(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
