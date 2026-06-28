using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Wavee.Tests;

// Shared Connect test harness: build real dealer WS frame JSON (message + request shapes), gzip/base64 helpers, a tiny
// observer, and a GC-allocation gate for the ingest/projection hot path. Grows across Stages A→I.
static class ConnectHarness
{
    public static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true)) gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    public static string B64(byte[] data) => Convert.ToBase64String(data);

    /// <summary>A dealer MESSAGE frame: one (optionally gzipped) payload + headers (where the Spotify-Connection-Id lives).</summary>
    public static byte[] MessageFrame(string uri, byte[] payload, bool gzip = false, (string, string)[]? headers = null)
    {
        var buf = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("type", "message");
            w.WriteString("uri", uri);
            w.WriteStartObject("headers");
            if (gzip) w.WriteString("Transfer-Encoding", "gzip");
            if (headers != null) foreach (var (k, v) in headers) w.WriteString(k, v);
            w.WriteEndObject();
            w.WriteStartArray("payloads");
            w.WriteStringValue(B64(gzip ? Gzip(payload) : payload));
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>A dealer REQUEST frame: key + message_ident + a SINGULAR payload {compressed: base64(gzip(json))}.</summary>
    public static byte[] RequestFrame(string key, string ident, string commandJson, (string, string)[]? headers = null)
    {
        var buf = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("type", "request");
            w.WriteString("key", key);
            w.WriteString("message_ident", ident);
            if (headers != null) { w.WriteStartObject("headers"); foreach (var (k, v) in headers) w.WriteString(k, v); w.WriteEndObject(); }
            w.WriteStartObject("payload");
            w.WriteString("compressed", B64(Gzip(Encoding.UTF8.GetBytes(commandJson))));
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>GC allocation delta (bytes) for a hot-path action, after a warm-up pass. The Connect ingest gate budgets
    /// against this (bounded, not zero — the dealer/projection layer is the event-driven app tier, outside frame phases 6-13).</summary>
    public static long AllocDelta(Action action, int iters = 1)
    {
        action();   // JIT warm-up + first-touch allocations excluded
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    public static IObserver<T> Obs<T>(Action<T> onNext) => new Inline<T>(onNext);

    sealed class Inline<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext(value);
    }
}
