using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HIB.Core.Link;

/// <summary>
/// The phone hand-builds some JSON (device, status, appIcons…) without escaping titles.
/// This parser first tries strict JSON, then repairs unescaped quotes / control characters.
/// </summary>
public static class LenientJson
{
    static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 128,
    };

    public static JsonDocument? TryParse(string json)
    {
        try { return JsonDocument.Parse(json, Options); }
        catch (JsonException) { }
        try { return JsonDocument.Parse(Repair(json), Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Escapes a quote inside a string unless it is followed (after whitespace) by a structural
    /// character that can legally follow a string value or key: , } ] :
    /// Raw control characters inside strings are escaped too.
    /// </summary>
    public static string Repair(string json)
    {
        var sb = new StringBuilder(json.Length + 16);
        var inString = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (!inString)
            {
                sb.Append(c);
                if (c == '"') inString = true;
                continue;
            }
            if (c == '\\' && i + 1 < json.Length)
            {
                sb.Append(c).Append(json[++i]);
                continue;
            }
            if (c == '"')
            {
                var j = i + 1;
                while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                if (j >= json.Length || json[j] is ',' or '}' or ']' or ':')
                {
                    sb.Append('"');
                    inString = false;
                }
                else sb.Append("\\\"");
                continue;
            }
            if (c < 0x20)
            {
                sb.Append(c switch
                {
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                });
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- small typed accessors that tolerate missing / mistyped fields ----

    public static string? Str(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static long? Long(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number)
        {
            if (p.TryGetInt64(out var l)) return l;
            if (p.TryGetDouble(out var d)) return (long)d;
        }
        if (p.ValueKind == JsonValueKind.String &&
            long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    public static int? Int(this JsonElement e, string name) => e.Long(name) is { } l ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : null;

    public static double? Double(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return d;
        if (p.ValueKind == JsonValueKind.String &&
            double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    public static bool? Bool(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(p.GetString(), out var b) ? b : null,
            JsonValueKind.Number => p.TryGetInt32(out var n) ? n != 0 : null,
            _ => null,
        };
    }

    public static JsonElement? Obj(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.Object ? p : null;
    }

    public static List<string> StrList(this JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in p.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
            else if (item.ValueKind == JsonValueKind.Number) list.Add(item.GetRawText());
        }
        return list;
    }

    /// <summary>Decodes base64 (optionally with a data: URI prefix); null on failure/empty.</summary>
    public static byte[]? Base64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var idx = value.IndexOf("base64,", StringComparison.Ordinal);
        if (idx >= 0) value = value[(idx + 7)..];
        try { return Convert.FromBase64String(value.Trim()); }
        catch (FormatException) { return null; }
    }
}
