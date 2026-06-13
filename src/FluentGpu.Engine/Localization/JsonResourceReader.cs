using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FluentGpu.Localization;

/// <summary>
/// Parses a FluentGpu localization JSON resource into a flat <c>dotted-key → value</c> table and (optionally) reads its
/// <c>"$culture"</c> metadata. This is the engine-blessed resource format — the deliberate, modern replacement for
/// WinUI's <c>.resw</c>/<c>ResourceManager</c> (forbidden here: <c>System.Resources.ResourceManager</c> + satellite
/// assemblies resolve via reflection over per-culture DLLs, hostile to <c>PublishAot</c> + <c>TrimMode full</c>).
///
/// <para><b>The format.</b> One JSON object per culture (<c>en-US.json</c>, <c>fr-FR.json</c>, <c>fr.json</c>,
/// <c>de-DE.json</c>, <c>pl-PL.json</c>). Nested objects model namespaces and <i>flatten to dotted keys</i> — so
/// <code>{ "app": { "title": "Hello" }, "player": { "queue": "Queue" } }</code>
/// loads as <c>app.title → "Hello"</c>, <c>player.queue → "Queue"</c>. Hand-editable, diff-friendly (one logical
/// string per line), and the dotted key is exactly what call sites pass to <see cref="Localization.Get"/>. String
/// values are message templates — named <c>{placeholder}</c> interpolation and the ICU plural/select mini-grammar are
/// applied at <i>resolve</i> time by <see cref="MessageFormatter"/>, never here. An optional reserved
/// <c>"$culture"</c> top-level key carries the culture name as in-file metadata (a self-describing-file convenience;
/// the loader keys by file name regardless). Keys beginning with <c>'$'</c> at ANY level are reserved metadata and are
/// NOT emitted into the string table (so <c>$culture</c>, future <c>$comment</c>/<c>$meta</c> annotations are free).
/// Non-string leaves (numbers/bools/null/arrays) are skipped — a string table holds strings; arrays are intentionally
/// unsupported (a localized list is N discrete keys, which stays diffable and pluralizable). JSON has no comments, so
/// translator notes live under a <c>$</c>-prefixed sibling key.</para>
///
/// <para><b>AOT/trim posture (load-bearing).</b> Parsing is a forward token walk with
/// <see cref="Utf8JsonReader"/> over the file bytes — NO reflection, NO <c>JsonSerializer</c>, NO source-generated
/// serializer context, NO DOM (<c>JsonDocument</c>) materialization. <see cref="Utf8JsonReader"/> is a <c>ref struct</c>
/// that reads UTF-8 in place, so it is fully NativeAOT- and <c>TrimMode full</c>-safe and allocates only the strings it
/// must intern (the keys/values). A small explicit prefix stack tracks the current dotted path as objects open/close.</para>
/// </summary>
public static class JsonResourceReader
{
    /// <summary>The reserved metadata key prefix. A key whose name starts with this at ANY nesting level is treated as
    /// file metadata (e.g. <c>$culture</c>, <c>$comment</c>) and excluded from the resolved string table.</summary>
    public const char MetaPrefix = '$';

    /// <summary>The reserved top-level key carrying the file's own culture name (e.g. <c>"$culture": "fr-FR"</c>).</summary>
    public const string CultureMetaKey = "$culture";

    /// <summary>Read a localization JSON file into a flat dotted-key table. A missing file ⇒ an empty table (callers
    /// treat the absence of a culture file as "no strings for that culture", which the fallback chain absorbs). A
    /// malformed file throws <see cref="JsonException"/> — a bad resource is loud, never silently empty.</summary>
    public static Dictionary<string, string> ReadFile(string path)
        => ReadFile(path, out _);

    /// <summary>As <see cref="ReadFile(string)"/>, additionally returning the file's <c>"$culture"</c> metadata value
    /// (or <see langword="null"/> when absent). Missing file ⇒ empty table, null culture.</summary>
    public static Dictionary<string, string> ReadFile(string path, out string? declaredCulture)
    {
        if (!File.Exists(path)) { declaredCulture = null; return new Dictionary<string, string>(System.StringComparer.Ordinal); }
        byte[] bytes = File.ReadAllBytes(path);
        return Read(bytes, out declaredCulture);
    }

    /// <summary>Parse a localization JSON document (UTF-8 bytes) into a flat dotted-key table. Used by the file path and
    /// by embedded/in-memory resources (the loader's <c>AddStrings</c> bulk form / tests).</summary>
    public static Dictionary<string, string> Read(ReadOnlySpan<byte> utf8Json)
        => Read(utf8Json, out _);

    /// <summary>Parse UTF-8 JSON bytes into a flat dotted-key table, also surfacing the <c>"$culture"</c> metadata.</summary>
    public static Dictionary<string, string> Read(ReadOnlySpan<byte> utf8Json, out string? declaredCulture)
    {
        var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
        declaredCulture = null;

        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            // Tolerate trailing commas + JSONC-style comments so hand-edited files are forgiving (System.Text.Json
            // skips comment tokens entirely — they never reach the value walk). Notes still belong in $-keys for
            // structured diffing, but a stray // comment won't fail a translator's edit.
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        // Explicit prefix stack: each entry is the dotted path of the CURRENTLY OPEN object (root = ""). When we read a
        // PropertyName inside an object we form childPath = prefix + name (+ '.'); a following StartObject pushes it, a
        // value leaf writes map[childPath]. A '$'-prefixed property name is skipped wholesale (its value/subtree too).
        var pathStack = new List<string> { string.Empty };
        var sb = new StringBuilder(64);
        string pendingKey = string.Empty;   // dotted key formed from the last non-meta PropertyName at this level
        bool skipNext = false;              // the last PropertyName was meta ($-prefixed) → skip its value/subtree

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                {
                    string name = reader.GetString()!;
                    string prefix = pathStack[^1];
                    if (name.Length > 0 && name[0] == MetaPrefix)
                    {
                        // Metadata key. Capture $culture at the TOP level (prefix == "") as the declared culture; in all
                        // cases the value/subtree is excluded from the table.
                        skipNext = true;
                        if (prefix.Length == 0 && name == CultureMetaKey)
                        {
                            if (reader.Read() && reader.TokenType == JsonTokenType.String)
                                declaredCulture = reader.GetString();
                            else
                                SkipChildren(ref reader);   // $culture wasn't a string — skip whatever it is
                            skipNext = false;
                        }
                        break;
                    }
                    skipNext = false;
                    sb.Clear();
                    sb.Append(prefix).Append(name);
                    pendingKey = sb.ToString();
                    break;
                }

                case JsonTokenType.StartObject:
                {
                    if (skipNext) { SkipChildren(ref reader); skipNext = false; break; }
                    // pendingKey is "" for the root object (no property name preceded it); otherwise push the namespace.
                    pathStack.Add(pendingKey.Length == 0 ? string.Empty : pendingKey + ".");
                    pendingKey = string.Empty;
                    break;
                }

                case JsonTokenType.EndObject:
                    if (pathStack.Count > 1) pathStack.RemoveAt(pathStack.Count - 1);
                    break;

                case JsonTokenType.StartArray:
                    // Arrays are not part of the string-table model (a localized list is N keys). Skip the whole array.
                    SkipChildren(ref reader);
                    skipNext = false;
                    break;

                case JsonTokenType.String:
                    if (skipNext) { skipNext = false; break; }
                    if (pendingKey.Length != 0) { map[pendingKey] = reader.GetString()!; pendingKey = string.Empty; }
                    break;

                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    // Non-string leaf — a string table holds only strings. Drop it (clear the pending key).
                    skipNext = false;
                    pendingKey = string.Empty;
                    break;
            }
        }
        return map;
    }

    /// <summary>The reader is positioned ON a container start token (StartObject/StartArray) OR is about to read a
    /// scalar to skip: advance past the entire current value (its whole subtree). <see cref="Utf8JsonReader.Skip"/> is
    /// the in-box, allocation-free subtree skip.</summary>
    private static void SkipChildren(ref Utf8JsonReader reader)
    {
        // Utf8JsonReader.Skip() requires being positioned on the container start; we are (StartObject/StartArray cases),
        // so skip its children to the matching End token.
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            reader.Skip();
    }
}
