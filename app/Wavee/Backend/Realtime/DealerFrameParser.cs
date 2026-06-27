using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Wavee.Backend.Realtime;

public enum DealerFrameType { Unknown, Ping, Pong, Message, Request }

/// <summary>A decoded dealer WebSocket frame: the <see cref="Type"/>, the message <see cref="Uri"/> (the hm:// topic), and
/// the decoded <see cref="Payload"/> (base64 → bytes, gunzipped if the frame is Transfer-Encoding: gzip).</summary>
public readonly record struct DealerFrame(DealerFrameType Type, string? Uri, byte[] Payload);

// Reflection-free (AOT-safe) Utf8JsonReader parse of a dealer frame — ported from the reference protocol. Only the fields
// the library/playlist firehose needs are read (type, uri, headers' Transfer-Encoding, payloads[0]).
public static class DealerFrameParser
{
    public static DealerFrame Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty) return new DealerFrame(DealerFrameType.Unknown, null, Array.Empty<byte>());
        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip });
            var type = DealerFrameType.Unknown;
            string? uri = null;
            byte[]? payload = null;
            bool gzip = false;

            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    type = reader.ValueTextEquals("ping"u8) ? DealerFrameType.Ping
                         : reader.ValueTextEquals("pong"u8) ? DealerFrameType.Pong
                         : reader.ValueTextEquals("message"u8) ? DealerFrameType.Message
                         : reader.ValueTextEquals("request"u8) ? DealerFrameType.Request
                         : DealerFrameType.Unknown;
                }
                else if (reader.ValueTextEquals("uri"u8)) { reader.Read(); uri = reader.GetString(); }
                else if (reader.ValueTextEquals("headers"u8)) { reader.Read(); gzip = ReadHeadersForGzip(ref reader); }
                else if (reader.ValueTextEquals("payloads"u8)) { reader.Read(); payload = ReadFirstPayload(ref reader); }
            }

            if (payload is not null && gzip) payload = Gunzip(payload);
            return new DealerFrame(type, uri, payload ?? Array.Empty<byte>());
        }
        catch
        {
            return new DealerFrame(DealerFrameType.Unknown, null, Array.Empty<byte>());
        }
    }

    static bool ReadHeadersForGzip(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return false;
        bool gzip = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isTransferEncoding = reader.ValueTextEquals("Transfer-Encoding"u8);
                reader.Read();
                if (isTransferEncoding && reader.ValueTextEquals("gzip"u8)) gzip = true;
            }
        }
        return gzip;
    }

    static byte[]? ReadFirstPayload(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) return null;
        byte[]? first = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;
            if (first is null && reader.TokenType == JsonTokenType.String) first = reader.GetBytesFromBase64();
        }
        return first;
    }

    static byte[] Gunzip(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        gz.CopyTo(outMs);
        return outMs.ToArray();
    }
}
