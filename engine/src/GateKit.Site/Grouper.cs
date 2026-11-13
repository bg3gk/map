using GateKit.GameData.Query;
using Microsoft.Data.Sqlite;

namespace GateKit.Site;

/// <summary>
/// A child table read once, in the order of its parent key, and handed out group by group.
/// Asking for a page's children one query at a time is fine where the index exists and ruinous
/// where it does not (there are three million track rows); this walks each table exactly once
/// instead, so the caller must ask for keys in ascending order.
/// </summary>
internal sealed class Grouper<TKey> : IDisposable
{
    private readonly SqliteCommand _command;
    private readonly SqliteDataReader _reader;
    private readonly string[] _columns;
    private readonly IComparer<TKey> _order;
    private readonly Func<SqliteDataReader, TKey> _keyOf;
    private bool _more;
    private TKey _key = default!;
    private object?[] _values = [];

    public Grouper(ReadOnlyDb db, string sql, Func<SqliteDataReader, TKey> keyOf, IComparer<TKey> order)
    {
        _command = db.Command(sql, []);
        _reader = _command.ExecuteReader();
        _columns = Enumerable.Range(0, _reader.FieldCount).Select(_reader.GetName).ToArray();
        _keyOf = keyOf;
        _order = order;
        Advance();
    }

    private void Advance()
    {
        _more = _reader.Read();
        if (!_more)
        {
            return;
        }
        _key = _keyOf(_reader);
        _values = new object?[_columns.Length];
        for (var i = 0; i < _values.Length; i++)
        {
            _values[i] = _reader.IsDBNull(i) ? null : _reader.GetValue(i);
        }
    }

    /// <summary>The rows whose key is this one. Keys must be asked for in ascending order.</summary>
    public List<Row> Take(TKey key)
    {
        while (_more && _order.Compare(_key, key) < 0)
        {
            Advance();
        }
        var rows = new List<Row>();
        while (_more && _order.Compare(_key, key) == 0)
        {
            rows.Add(new Row(_columns, _values));
            Advance();
        }
        return rows;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _command.Dispose();
    }
}

internal static class Grouper
{
    public static Grouper<long> ByLong(ReadOnlyDb db, string sql) =>
        new(db, sql, r => r.GetInt64(0), Comparer<long>.Default);

    public static Grouper<string> ByText(ReadOnlyDb db, string sql) =>
        new(db, sql, r => r.IsDBNull(0) ? "" : r.GetString(0), StringComparer.Ordinal);
}
