using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GateKit.Site;

/// <summary>
/// Renders a page as JSON - the only form a page is stored in. A person's browser draws the page
/// from this same file (<c>Assets/app.js</c>), so a reader and an agent see one thing by
/// construction.
/// </summary>
public static class JsonWriter
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// One value, as small as it can be said. Ordinary prose is a bare string, a number is a
    /// number, nothing is <c>null</c>, and only a value that names something else costs an object.
    /// A link carries its <c>route</c> alone: the kind is the first segment, the id is the rest,
    /// and the text shown is the id unless it says otherwise.
    /// </summary>
    private static void Write(Utf8JsonWriter json, SiteValue value)
    {
        switch (value.Kind)
        {
            case ValueKind.Missing:
                json.WriteNullValue();
                return;
            case ValueKind.Number when double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                json.WriteNumberValue(number);
                return;
            case ValueKind.Text when string.IsNullOrEmpty(value.Note):
                json.WriteStringValue(value.Text);
                return;
        }

        json.WriteStartObject();
        switch (value.Kind)
        {
            case ValueKind.Link:
                json.WriteString("route", value.Route!);
                // The text is left out when the last segment of the route reads back as it, which
                // is nearly always; the client and an agent both read it back with the same rule.
                if (value.Text != Routes.Unslug(value.Route![(value.Route!.IndexOf('/', StringComparison.Ordinal) + 1)..]))
                {
                    json.WriteString("text", value.Text);
                }
                break;
            case ValueKind.Identifier:
                json.WriteString("id", value.Text);
                break;
            case ValueKind.Path:
                json.WriteString("path", value.Text);
                break;
            default:
                json.WriteString("text", value.Text);
                break;
        }
        if (!string.IsNullOrEmpty(value.Note))
        {
            json.WriteString("note", value.Note);
        }
        if (value.Texts is { Count: > 0 } texts)
        {
            json.WriteStartObject("texts");
            foreach (var (language, text) in texts)
            {
                json.WriteString(language, text);
            }
            json.WriteEndObject();
        }
        json.WriteEndObject();
    }

    /// <summary>The whole page, as the bytes of its <c>.json</c> twin.</summary>
    public static byte[] Render(PageDoc page, SiteNotice notice, string baseUrl)
    {
        var buffer = new MemoryStream(2048);
        using (var json = new Utf8JsonWriter(buffer, Options))
        {
            json.WriteStartObject();
            json.WriteString("kind", page.Kind);
            json.WriteString("id", page.Id);
            json.WriteString("route", page.Route);
            json.WriteString("title", page.Title);
            if (page.Subtitle != null)
            {
                json.WriteString("subtitle", page.Subtitle);
            }

            // Field names are unique on a page and are written in the order the page shows them,
            // so an object says the same as an array of {name, value} pairs in half the bytes.
            json.WriteStartObject("fields");
            foreach (var field in page.Fields)
            {
                json.WritePropertyName(field.Name);
                Write(json, field.Value);
            }
            json.WriteEndObject();

            json.WriteStartArray("sections");
            foreach (var section in page.Sections.Where(s => s.Rows.Count > 0))
            {
                json.WriteStartObject();
                json.WriteString("title", section.Title);
                json.WriteStartArray("columns");
                foreach (var column in section.Names)
                {
                    json.WriteStringValue(column);
                }
                json.WriteEndArray();
                json.WriteNumber("shown", section.Rows.Count);
                json.WriteNumber("total", section.Total);
                json.WriteStartArray("rows");
                foreach (var row in section.Rows)
                {
                    json.WriteStartArray();
                    foreach (var cell in row)
                    {
                        Write(json, cell);
                    }
                    json.WriteEndArray();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();

            // The longer explanation of a short name, once per page, by name. A field and a column
            // that share a name share the explanation; the first one wins.
            var hints = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in page.Fields)
            {
                if (field.Hint != null)
                {
                    hints.TryAdd(field.Name, field.Hint);
                }
            }
            foreach (var (name, hint) in page.Sections.Where(s => s.Rows.Count > 0).SelectMany(s => s.ColumnHints))
            {
                hints.TryAdd(name, hint);
            }
            if (hints.Count > 0)
            {
                json.WriteStartObject("hints");
                foreach (var (name, hint) in hints)
                {
                    json.WriteString(name, hint);
                }
                json.WriteEndObject();
            }

            json.WriteStartArray("links");
            foreach (var link in page.Links())
            {
                Write(json, link);
            }
            json.WriteEndArray();

            // Deliberately two short strings and not a paragraph: this object is on every one of a
            // hundred thousand pages, and the sentences it would otherwise repeat are in llms.txt.
            json.WriteStartObject("index");
            json.WriteString("origin", notice.Origin);
            json.WriteString("fullIndex", notice.Download);
            json.WriteString("about", baseUrl + "llms.txt");
            json.WriteEndObject();

            json.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>A small JSON document from already-encoded pieces, for the files the site writes once.</summary>
    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
