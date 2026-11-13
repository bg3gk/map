using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GateKit.Site;

/// <summary>
/// The database the server searches. It holds what people actually search - every line of the
/// game's text in every language, the name of every object in the game, and the id of every
/// page - with a full-text index over each.
///
/// The text is indexed twice over, by script. Words of a language that puts spaces between them
/// are indexed as words, so a word is found by its beginning. Chinese, Japanese and Korean have no
/// spaces to cut at, so their lines are indexed as every run of three characters instead, which
/// finds any part of a line three characters or longer; a shorter search there is a scan.
/// </summary>
public sealed class SearchDatabase : IDisposable
{
    /// <summary>The file the database is built under, inside the site folder.</summary>
    public const string FileName = "search.sqlite";

    /// <summary>The languages whose lines are indexed by runs of characters rather than by words.</summary>
    public static readonly string[] Unspaced = ["Chinese", "ChineseTraditional", "Japanese", "Korean"];

    private readonly SqliteConnection _db;
    private readonly SqliteCommand _addObject;
    private readonly SqliteTransaction _work;

    public SearchDatabase(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode = off; PRAGMA synchronous = off;");
        Exec("""
CREATE TABLE obj (id INTEGER PRIMARY KEY, kind TEXT, ident TEXT, route TEXT, name TEXT, note TEXT, words TEXT);
CREATE VIRTUAL TABLE obj_fts USING fts5(name, words, content = 'obj', content_rowid = 'id');
CREATE TABLE loca (id INTEGER PRIMARY KEY, handle TEXT, lang TEXT, text TEXT);
CREATE VIRTUAL TABLE loca_fts USING fts5(text, content = 'loca', content_rowid = 'id', tokenize = 'unicode61 remove_diacritics 2');
CREATE TABLE loca_cjk (id INTEGER PRIMARY KEY, handle TEXT, lang TEXT, text TEXT);
CREATE VIRTUAL TABLE loca_cjk_fts USING fts5(text, content = 'loca_cjk', content_rowid = 'id', tokenize = 'trigram');
-- The kind of node an object came from is one of 81 words, written 892,162 times; it is kept
-- once here and named by number in `gname`.
CREATE TABLE gnode (id INTEGER PRIMARY KEY, name TEXT UNIQUE);
CREATE TABLE gname (id INTEGER PRIMARY KEY, name TEXT, node INTEGER, key TEXT);
-- `detail = none` drops the position lists: nothing asks a name where inside itself a word
-- was, and for the 892,162 names of the objects without a page that is tens of megabytes.
CREATE VIRTUAL TABLE gname_fts USING fts5(name, content = 'gname', content_rowid = 'id', detail = none);
-- Every function of every binary, so an address a decompiler shows finds the function it is in.
CREATE TABLE func (binary TEXT, rva INTEGER, size INTEGER);
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
""");
        _work = _db.BeginTransaction();
        _addObject = _db.CreateCommand();
        _addObject.CommandText = "INSERT INTO obj (kind, ident, route, name, note, words) VALUES ($k, $i, $r, $n, $o, $w)";
        foreach (var name in (string[])["$k", "$i", "$r", "$n", "$o", "$w"])
        {
            _addObject.Parameters.Add(name, SqliteType.Text);
        }
    }

    private void Exec(string sql, SqliteTransaction? work = null)
    {
        using var command = _db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = work;
        command.ExecuteNonQuery();
    }

    /// <summary>Copies where every function of every binary begins and ends, for finding one by an address inside it.</summary>
    public void Functions(SymbolIndex symbols, Action<string> log)
    {
        log("search database: where every function is");
        using var write = _db.CreateCommand();
        write.Transaction = _work;
        write.CommandText = "INSERT INTO func (binary, rva, size) VALUES ($b, $r, $s)";
        write.Parameters.Add("$b", SqliteType.Text);
        write.Parameters.Add("$r", SqliteType.Integer);
        write.Parameters.Add("$s", SqliteType.Integer);
        using var read = symbols.Db.Command("SELECT d.name, f.rva, f.size FROM functions f JOIN dlls d ON d.id = f.dll_id ORDER BY d.name, f.rva", []);
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            write.Parameters["$b"].Value = reader.GetString(0);
            write.Parameters["$r"].Value = reader.GetInt64(1);
            write.Parameters["$s"].Value = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            write.ExecuteNonQuery();
        }
    }

    /// <summary>Records a page, so searching finds it and an id resolves to its URL.</summary>
    public void Add(PageDoc page)
    {
        _addObject.Parameters["$k"].Value = page.Kind;
        _addObject.Parameters["$i"].Value = page.Id;
        _addObject.Parameters["$r"].Value = page.Route;
        _addObject.Parameters["$n"].Value = page.Title;
        _addObject.Parameters["$o"].Value = (object?)page.Subtitle ?? DBNull.Value;
        _addObject.Parameters["$w"].Value = Words(page.Title);
        _addObject.ExecuteNonQuery();
    }

    /// <summary>
    /// A name cut where a reader would cut it. The full-text tokenizer splits on punctuation and
    /// nothing else, so <c>ecl::ActiveTurnComponent</c> is two words and looking for "turn" finds
    /// nothing. Cutting at every change from lower case to upper, and at every digit, makes the
    /// names of the engine and of the game searchable by any word inside them.
    /// </summary>
    internal static string Words(string name)
    {
        var text = new System.Text.StringBuilder(name.Length + 12);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var boundary = i > 0
                && (char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]))
                    || char.IsDigit(c) && char.IsLetter(name[i - 1])
                    || char.IsUpper(c) && i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]));
            if (boundary)
            {
                text.Append(' ');
            }
            text.Append(c);
        }
        return text.ToString();
    }

    /// <summary>
    /// Copies the text and the names out of the index and builds the full-text indexes.
    /// </summary>
    public void Finish(SiteIndex index, bool withNames, IReadOnlyDictionary<string, string> meta, Action<string> log)
    {
        var unspaced = string.Join(", ", Unspaced.Select(l => "'" + l + "'"));
        log("search database: the game's text, in every language");
        Copy("INSERT INTO loca (handle, lang, text) VALUES ($1, $2, $3)",
            index, $"SELECT handle, lang, text FROM loca WHERE variant = '' AND text <> '' AND lang NOT IN ({unspaced}) ORDER BY lang, handle", 3);
        Copy("INSERT INTO loca_cjk (handle, lang, text) VALUES ($1, $2, $3)",
            index, $"SELECT handle, lang, text FROM loca WHERE variant = '' AND text <> '' AND lang IN ({unspaced}) ORDER BY lang, handle", 3);

        if (withNames)
        {
            log("search database: names of every object");
            using (var nodes = _db.CreateCommand())
            {
                nodes.Transaction = _work;
                nodes.CommandText = "INSERT OR IGNORE INTO gnode (name) VALUES ($n)";
                var node = nodes.Parameters.Add("$n", SqliteType.Text);
                foreach (var row in index.Db.Rows("SELECT DISTINCT node FROM v_objects WHERE name IS NOT NULL AND name <> ''"))
                {
                    node.Value = (object?)row.S("node") ?? DBNull.Value;
                    nodes.ExecuteNonQuery();
                }
            }
            Copy("INSERT INTO gname (name, node, key) VALUES ($1, (SELECT id FROM gnode WHERE name = $2), $3)",
                index, "SELECT name, node, key FROM v_objects WHERE name IS NOT NULL AND name <> ''", 3);
        }

        _work.Commit();

        using (var work = _db.BeginTransaction())
        {
            foreach (var (key, value) in meta)
            {
                using var command = _db.CreateCommand();
                command.Transaction = work;
                command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)";
                command.Parameters.AddWithValue("$k", key);
                command.Parameters.AddWithValue("$v", value);
                command.ExecuteNonQuery();
            }
            work.Commit();
        }

        log("search database: full-text indexes");
        Exec("INSERT INTO obj_fts(obj_fts) VALUES('rebuild');");
        Exec("INSERT INTO loca_fts(loca_fts) VALUES('rebuild');");
        Exec("INSERT INTO loca_cjk_fts(loca_cjk_fts) VALUES('rebuild');");
        if (withNames)
        {
            Exec("INSERT INTO gname_fts(gname_fts) VALUES('rebuild');");
        }

        log("search database: indexes and layout");
        Exec("CREATE INDEX obj_ident ON obj(ident); CREATE INDEX obj_route ON obj(route); CREATE INDEX func_at ON func(binary, rva);");
        Exec("ANALYZE;");
    }

    private void Copy(string insert, SiteIndex index, string select, int columns)
    {
        using var write = _db.CreateCommand();
        write.Transaction = _work;
        write.CommandText = insert;
        for (var i = 1; i <= columns; i++)
        {
            write.Parameters.Add("$" + i.ToString(CultureInfo.InvariantCulture), SqliteType.Text);
        }
        using var read = index.Db.Command(select, []);
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            for (var i = 0; i < columns; i++)
            {
                write.Parameters[i].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            }
            write.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _addObject.Dispose();
        _work.Dispose();
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
