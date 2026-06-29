using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Wavee.Backend;

// ── The proto-free context-resolve JSON reader ────────────────────────────────────────────────────────────────────────
// Streams the /context-resolve response with Utf8JsonReader (no full-doc materialization): top.metadata["sorting.criteria"]
// + every pages[].tracks[] {uri,uid} in order + the first next_page_url. Only the interesting subtrees are descended into;
// everything else is Skip()'d. Field names are the wire's snake_case (matches the decoded play-command captures). Lives in
// Backend (proto-free; deps = System.Text.Json) so it's unit-tested directly; LiveContextResolver (SpotifyLive) calls it.
public static class ContextJson
{
    public static void Parse(ReadOnlySpan<byte> json, List<QueuedRef> refs, ref string? sorting, ref string? nextPage)
    {
        var r = new Utf8JsonReader(json);
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            bool isPages = r.ValueTextEquals("pages");
            bool isMeta = !isPages && r.ValueTextEquals("metadata");
            r.Read();   // → value
            if (isPages && r.TokenType == JsonTokenType.StartArray)
            {
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    if (r.TokenType == JsonTokenType.StartObject) ReadPage(ref r, refs, ref nextPage);
                    else r.Skip();
                }
            }
            else if (isMeta && r.TokenType == JsonTokenType.StartObject) ReadMetadata(ref r, ref sorting);
            else r.Skip();
        }
    }

    static void ReadPage(ref Utf8JsonReader r, List<QueuedRef> refs, ref string? nextPage)
    {
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            bool isTracks = r.ValueTextEquals("tracks");
            bool isNext = !isTracks && r.ValueTextEquals("next_page_url");
            r.Read();   // → value
            if (isTracks && r.TokenType == JsonTokenType.StartArray)
            {
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    if (r.TokenType == JsonTokenType.StartObject) ReadTrack(ref r, refs);
                    else r.Skip();
                }
            }
            else if (isNext && r.TokenType == JsonTokenType.String)
            {
                if (string.IsNullOrEmpty(nextPage)) { var s = r.GetString(); if (!string.IsNullOrEmpty(s)) nextPage = s; }
            }
            else r.Skip();
        }
    }

    static void ReadTrack(ref Utf8JsonReader r, List<QueuedRef> refs)
    {
        string uri = "", uid = "";
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            bool isUri = r.ValueTextEquals("uri");
            bool isUid = !isUri && r.ValueTextEquals("uid");
            r.Read();   // → value
            if (isUri && r.TokenType == JsonTokenType.String) uri = r.GetString() ?? "";
            else if (isUid && r.TokenType == JsonTokenType.String) uid = r.GetString() ?? "";
            else r.Skip();
        }
        if (uri.Length > 0) refs.Add(new QueuedRef(uri, uid));
    }

    static void ReadMetadata(ref Utf8JsonReader r, ref string? sorting)
    {
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            bool isSort = r.ValueTextEquals("sorting.criteria");
            r.Read();   // → value
            if (isSort && r.TokenType == JsonTokenType.String) sorting = r.GetString();
            else r.Skip();
        }
    }
}
