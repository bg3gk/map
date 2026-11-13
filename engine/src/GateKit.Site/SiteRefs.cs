using Microsoft.Data.Sqlite;

namespace GateKit.Site;

/// <summary>An object that names something in one of its attributes.</summary>
public sealed record RefHit(string Route, string? Name, string Node, string Attribute);

/// <summary>
/// What names a thing: every object of the game with an attribute whose value is exactly this id,
/// handle or name. It is the one question no page can answer ahead of time - the pages list what an
/// object names, not who names it - so the server asks the full index, through the index it keeps
/// on attribute values.
/// </summary>
public sealed class SiteRefs
{
    /// <summary>The most an answer lists; the count says when there were more.</summary>
    public const int Most = 500;

    private readonly string _connection;

    public SiteRefs(string index)
    {
        if (!File.Exists(index))
        {
            throw new FileNotFoundException($"{index} is not the game index; fetch it with `map fetch`", index);
        }
        _connection = new SqliteConnectionStringBuilder { DataSource = index, Mode = SqliteOpenMode.ReadOnly, Pooling = true }.ToString();
    }

    public IReadOnlyList<RefHit> Find(string value)
    {
        // The index on attribute values covers values of 4 to 80 characters - every id, handle and
        // name worth asking about - and nothing else can be asked quickly.
        if (value.Length is < 4 or > 80)
        {
            return [];
        }
        using var db = new SqliteConnection(_connection);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
SELECT o.id, o.key, o.name, n.name, an.name,
       CASE WHEN o.key IS NULL OR o.key = '' THEN NULL ELSE (SELECT min(f.id) FROM objects f WHERE f.key = o.key) END
FROM attrs a
JOIN objects o ON o.id = a.object_id
JOIN names n ON n.id = o.node_id
JOIN names an ON an.id = a.attr_id
WHERE a.value = $v AND length(a.value) BETWEEN 4 AND 80
ORDER BY o.id
LIMIT $most
""";
        command.Parameters.AddWithValue("$v", value);
        command.Parameters.AddWithValue("$most", Most);
        using var reader = command.ExecuteReader();
        var hits = new List<RefHit>();
        while (reader.Read())
        {
            var row = reader.GetInt64(0);
            var key = reader.IsDBNull(1) ? null : reader.GetString(1);
            var first = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5);
            // The same rule SiteIndex gives every object its page by.
            var id = key is { Length: > 0 }
                ? (first == row ? key : key + "." + row.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : "row." + row.ToString(System.Globalization.CultureInfo.InvariantCulture);
            hits.Add(new RefHit(Routes.Route("object", Routes.Slug(id)), reader.IsDBNull(2) ? key : reader.GetString(2),
                reader.GetString(3), reader.GetString(4)));
        }
        return hits;
    }
}
