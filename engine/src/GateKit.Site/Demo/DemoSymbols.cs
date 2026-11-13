using Microsoft.Data.Sqlite;

namespace GateKit.Site.Demo;

/// <summary>
/// A symbol database with one class in it, and only the columns the site reads. It is written out
/// by hand rather than through the schema of <c>GateKit.Symbols</c> on purpose: if the site ever
/// starts needing a column that is not here, this file has to say so, which is the point of
/// writing a test against a shape instead of against a machine's database.
/// </summary>
public static class DemoSymbols
{
    public const string ClassName = "ecl::TestComponent";
    public const string ExtName = "ClientTest";
    public const string BaseName = "BaseComponent";
    public const string EnumName = "ecl::TestFlags";
    public const string Method = "Refresh";
    public const string Dll = "EoCClient.dll";

    /// <summary>The game function the Toolkit's method is matched to, in the Vulkan build.</summary>
    public const string GameFunction = "bg3.exe+0x77F580";

    /// <summary>The same function in the DirectX 11 build.</summary>
    public const string Dx11Function = "bg3_dx11.exe+0x780000";

    /// <summary>A function nobody has named, which only calls the one above.</summary>
    public const string UnnamedFunction = "bg3.exe+0x77F600";

    public static string Write(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        db.Open();
        Run(db, """
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE dlls (id INTEGER PRIMARY KEY, name TEXT, path TEXT, origin TEXT);
CREATE TABLE functions (dll_id INTEGER, rva INTEGER, size INTEGER, leaf INTEGER);
CREATE TABLE calls (dll_id INTEGER, caller_rva INTEGER, callee_dll INTEGER, callee_rva INTEGER, import TEXT, sites INTEGER);
CREATE TABLE strings (id INTEGER PRIMARY KEY, dll_id INTEGER, rva INTEGER, text TEXT);
CREATE TABLE string_refs (string_id INTEGER, func_rva INTEGER);
CREATE TABLE xrefs (dll_id INTEGER, func_rva INTEGER, target_rva INTEGER, kind TEXT, uses INTEGER);
CREATE TABLE vtabs (id INTEGER PRIMARY KEY, dll_id INTEGER, rva INTEGER, slots INTEGER, symbol_id INTEGER);
CREATE TABLE vtab_slots (vtab_id INTEGER, idx INTEGER, rva INTEGER);
CREATE TABLE matches (toolkit_dll INTEGER, toolkit_rva INTEGER, game_dll INTEGER, game_rva INTEGER,
                      evidence INTEGER, sample TEXT, method TEXT, toolkit_size INTEGER, game_size INTEGER, relation TEXT);
CREATE TABLE chunks (dll_id INTEGER, rva INTEGER, parent INTEGER);
CREATE TABLE se_symbols (dll_id INTEGER, name TEXT, rva INTEGER, mapping TEXT, note TEXT);
CREATE TABLE symbols (id INTEGER PRIMARY KEY, dll_id INTEGER, rva INTEGER, mangled TEXT, full_name TEXT,
                      namespace TEXT, class TEXT, method TEXT, signature TEXT, access TEXT, kind TEXT);
CREATE TABLE se_types (id INTEGER PRIMARY KEY, name TEXT, namespace TEXT, ext_name TEXT, engine_class TEXT,
                       kind TEXT, base TEXT, file TEXT, line INTEGER, size INTEGER);
CREATE TABLE se_fields (type_id INTEGER, idx INTEGER, name TEXT, type TEXT, legacy TEXT, hidden INTEGER,
                        offset INTEGER, size INTEGER, checked INTEGER);
CREATE TABLE type_names (dll_id INTEGER, string_id INTEGER, type TEXT, kind TEXT, func_rva INTEGER);
CREATE TABLE ecs_ids (dll_id INTEGER, type TEXT, context TEXT, index_rva INTEGER, context_rva INTEGER, source TEXT);
CREATE TABLE accessors (symbol_id INTEGER, dll_id INTEGER, class TEXT, field TEXT, kind TEXT, offset INTEGER,
                        width INTEGER, type TEXT, game_dll INTEGER, game_rva INTEGER, game_offset INTEGER, bit INTEGER);
CREATE TABLE enums (id INTEGER PRIMARY KEY, name TEXT, namespace TEXT, underlying TEXT, bitmask INTEGER, file TEXT);
CREATE TABLE enum_values (enum_id INTEGER, name TEXT, value INTEGER);
""");
        Run(db, $"""
INSERT INTO meta (key, value) VALUES ('source', 'a synthetic symbol database written by the tests');
INSERT INTO meta (key, value) VALUES ('game_file_version', '4.76.31.656');
INSERT INTO meta (key, value) VALUES ('toolkit_file_version', '4.69.31.813');
INSERT INTO dlls (id, name, path, origin) VALUES (1, '{Dll}', 'bin/{Dll}', 'toolkit');
INSERT INTO dlls (id, name, path, origin) VALUES (2, 'bg3.exe', 'bin/bg3.exe', 'game');
INSERT INTO dlls (id, name, path, origin) VALUES (3, 'bg3_dx11.exe', 'bin/bg3_dx11.exe', 'game');

-- The Toolkit's exported method, and the one game function it is matched to in each build: the
-- game function takes the Toolkit's name, and the two game functions are one function twice.
INSERT INTO functions (dll_id, rva, size, leaf) VALUES (1, 4096, 64, 0);
INSERT INTO functions (dll_id, rva, size, leaf) VALUES (2, 7861632, 128, 0);
INSERT INTO functions (dll_id, rva, size, leaf) VALUES (2, 7861760, 32, 0);
INSERT INTO functions (dll_id, rva, size, leaf) VALUES (3, 7864320, 128, 0);
INSERT INTO matches (toolkit_dll, toolkit_rva, game_dll, game_rva, evidence, sample, method, relation)
VALUES (1, 4096, 2, 7861632, 3, 'TestComponent', 'strings', 'same');
INSERT INTO matches (toolkit_dll, toolkit_rva, game_dll, game_rva, evidence, sample, method, relation)
VALUES (1, 4096, 3, 7864320, 3, 'TestComponent', 'strings', 'same');
INSERT INTO calls (dll_id, caller_rva, callee_dll, callee_rva, import, sites) VALUES (2, 7861760, 2, 7861632, NULL, 2);
INSERT INTO calls (dll_id, caller_rva, callee_dll, callee_rva, import, sites) VALUES (2, 7861632, NULL, NULL, 'KERNEL32.dll!Sleep', 1);
INSERT INTO strings (id, dll_id, rva, text) VALUES (1, 2, 16777216, 'TestComponent');
INSERT INTO string_refs (string_id, func_rva) VALUES (1, 7861632);
INSERT INTO xrefs (dll_id, func_rva, target_rva, kind, uses) VALUES (2, 7861632, 16777300, 'r', 1);
INSERT INTO vtabs (id, dll_id, rva, slots, symbol_id) VALUES (1, 2, 16777472, 1, 1);
INSERT INTO vtab_slots (vtab_id, idx, rva) VALUES (1, 0, 7861632);
INSERT INTO chunks (dll_id, rva, parent) VALUES (2, 7861700, 7861632);
-- A global Script Extender reaches for: data, not a function.
INSERT INTO se_symbols (dll_id, name, rva, mapping, note) VALUES (2, 'ls__GlobalSwitches', 16777300, 'ls__GlobalSwitches', 'first of 1 matches');

-- The class our component map also knows, declared the way Script Extender declares one. Its
-- engine name carries the namespace and its own `namespace` column does not, which is the shape
-- the real database has and the reason the site keys on the engine's name.
INSERT INTO se_types (id, name, namespace, ext_name, engine_class, kind, base, file, line, size)
VALUES (1, 'TestComponent', '', '{ExtName}', '{ClassName}', 'component', '{BaseName}',
        'BG3Extender/GameDefinitions/Components/Test.h', 11, 12);
INSERT INTO se_fields (type_id, idx, name, type, legacy, hidden, offset, size, checked)
VALUES (1, 0, 'Timer', 'float', NULL, 0, 0, 4, 1);
INSERT INTO se_fields (type_id, idx, name, type, legacy, hidden, offset, size, checked)
VALUES (1, 1, 'Flags', '{EnumName}', 'field_4', 0, 4, 4, 1);

INSERT INTO se_types (id, name, namespace, ext_name, engine_class, kind, base, file, line, size)
VALUES (2, '{BaseName}', '', NULL, '{BaseName}', 'struct', NULL, 'BG3Extender/GameDefinitions/Base.h', 3, 0);

INSERT INTO symbols (id, dll_id, rva, full_name, namespace, class, method, signature, kind)
VALUES (1, 1, 4096, '{ClassName}::{Method}', 'ecl', 'TestComponent', '{Method}', 'public: void ecl::TestComponent::{Method}(void)', 'method');

INSERT INTO type_names (dll_id, string_id, type, kind, func_rva)
VALUES (1, 1, 'struct {ClassName}', 'component', 4096);
INSERT INTO ecs_ids (dll_id, type, context, index_rva, context_rva, source)
VALUES (1, '{ClassName}', 'ecs::ComponentTypeIdContext', 1048576, 1048580, 'static registrant');
INSERT INTO accessors (dll_id, class, field, kind, offset, width, type, game_offset)
VALUES (1, '{ClassName}', 'Timer', 'get', 0, 4, 'float', 0);

INSERT INTO enums (id, name, namespace, underlying, bitmask, file)
VALUES (1, 'TestFlags', 'ecl', 'uint32_t', 1, 'BG3Extender/GameDefinitions/Enumerations/Test.inl');
INSERT INTO enum_values (enum_id, name, value) VALUES (1, 'None', 0);
INSERT INTO enum_values (enum_id, name, value) VALUES (1, 'Running', 1);
""");
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
