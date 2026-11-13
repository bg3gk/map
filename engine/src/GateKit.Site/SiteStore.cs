using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace GateKit.Site;

/// <summary>
/// The rendered site as one file: every page's <c>.json</c>, the client and <c>llms.txt</c>, each
/// kept gzip-compressed under the path it is served at.
///
/// A folder of files was the first shape, and it does not survive the whole game: a page for each
/// of 1.1 million objects, 2 million functions and half a million lines of text is 3.6 million
/// small files, more than the inodes a small server's disk has, and a copy of it is an afternoon.
/// One SQLite file is copied in seconds, replaced in one rename, and read by the server a page
/// at a time. Gzip is what a browser takes as it is, so a page is sent without being unpacked.
/// </summary>
public sealed class SiteStore : IDisposable
{
    /// <summary>The file a site is rendered into, inside the site folder.</summary>
    public const string FileName = "pages.sqlite";

    /// <summary>How many files go into one transaction while the site is written.</summary>
    private const int Batch = 20_000;

    private readonly SqliteConnection _db;
    private readonly SqliteCommand _put;
    private SqliteTransaction _work;
    private int _pending;

    /// <summary>How many files have been written.</summary>
    public int Files { get; private set; }

    /// <summary>How many bytes they are before compression.</summary>
    public long Bytes { get; private set; }

    /// <summary>How many bytes they are as stored.</summary>
    public long Stored { get; private set; }

    private SiteStore(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _db.Open();
        using (var setup = _db.CreateCommand())
        {
            setup.CommandText = """
PRAGMA journal_mode = off; PRAGMA synchronous = off; PRAGMA page_size = 8192;
CREATE TABLE file (path TEXT PRIMARY KEY, size INTEGER NOT NULL, body BLOB NOT NULL);
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
""";
            setup.ExecuteNonQuery();
        }
        _work = _db.BeginTransaction();
        _put = _db.CreateCommand();
        _put.CommandText = "INSERT OR REPLACE INTO file (path, size, body) VALUES ($p, $s, $b)";
        _put.Parameters.Add("$p", SqliteType.Text);
        _put.Parameters.Add("$s", SqliteType.Integer);
        _put.Parameters.Add("$b", SqliteType.Blob);
    }

    /// <summary>Starts a new, empty store at <paramref name="path"/>, replacing any file there.</summary>
    public static SiteStore Create(string path) => new(path);

    /// <summary>Writes one file under the path it is served at.</summary>
    public void Put(string path, byte[] content)
    {
        var packed = Gzip(content);
        _put.Transaction = _work;
        _put.Parameters["$p"].Value = path;
        _put.Parameters["$s"].Value = content.LongLength;
        _put.Parameters["$b"].Value = packed;
        _put.ExecuteNonQuery();
        Files++;
        Bytes += content.LongLength;
        Stored += packed.LongLength;
        if (++_pending >= Batch)
        {
            _work.Commit();
            _work.Dispose();
            _work = _db.BeginTransaction();
            _pending = 0;
        }
    }

    /// <summary>Records one fact about the build: its source, when it was made, how many pages.</summary>
    public void Meta(string key, string value)
    {
        using var command = _db.CreateCommand();
        command.Transaction = _work;
        command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _work.Commit();
        _work.Dispose();
        _put.Dispose();
        _db.Dispose();
    }

    internal static byte[] Gzip(byte[] data)
    {
        var output = new MemoryStream(data.Length / 3 + 64);
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data);
        }
        return output.ToArray();
    }

    internal static byte[] Gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(data.Length * 4);
        gzip.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>Reads a rendered site's files; safe to use from many requests at once.</summary>
public sealed class SiteReader
{
    private readonly string _connection;

    public string Path { get; }

    public SiteReader(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(Path))
        {
            throw new FileNotFoundException($"{Path} is not a rendered site; render one with `map build`", Path);
        }
        _connection = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
        }.ToString();
    }

    /// <summary>A file as stored, gzip-compressed, or null when the site has no such file.</summary>
    public byte[]? Packed(string path)
    {
        using var db = new SqliteConnection(_connection);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT body FROM file WHERE path = $p";
        command.Parameters.AddWithValue("$p", path);
        return command.ExecuteScalar() as byte[];
    }

    /// <summary>A file as it was written, or null when the site has no such file.</summary>
    public byte[]? Read(string path) => Packed(path) is { } packed ? SiteStore.Gunzip(packed) : null;

    /// <summary>A text file, or null.</summary>
    public string? Text(string path) => Read(path) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;

    /// <summary>Whether the site has a file at this path.</summary>
    public bool Has(string path)
    {
        using var db = new SqliteConnection(_connection);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT 1 FROM file WHERE path = $p";
        command.Parameters.AddWithValue("$p", path);
        return command.ExecuteScalar() != null;
    }

    /// <summary>Every path, with its size before compression, in the order paths sort.</summary>
    public IEnumerable<(string Path, long Size)> Files()
    {
        using var db = new SqliteConnection(_connection);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT path, size FROM file ORDER BY path";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return (reader.GetString(0), reader.GetInt64(1));
        }
    }

    /// <summary>Every page's JSON, in the order routes sort, unpacked.</summary>
    public IEnumerable<(string Route, byte[] Json)> Pages()
    {
        using var db = new SqliteConnection(_connection);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT path, body FROM file WHERE path LIKE '%.json' ORDER BY path";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            var cut = path.IndexOf('/', StringComparison.Ordinal);
            if (cut > 0 && Routes.Kinds.Contains(path[..cut]))
            {
                yield return (path[..^".json".Length], SiteStore.Gunzip((byte[])reader.GetValue(1)));
            }
        }
    }

    /// <summary>What the build recorded about itself.</summary>
    public IReadOnlyDictionary<string, string> Meta()
    {
        using var db = new SqliteConnection(_connection);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT key, value FROM meta";
        using var reader = command.ExecuteReader();
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            meta[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }
        return meta;
    }
}
