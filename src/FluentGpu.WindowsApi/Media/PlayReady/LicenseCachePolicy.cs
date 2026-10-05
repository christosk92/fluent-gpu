using System;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>The lifecycle of one cached PlayReady license (one KID → one open CDM key session).</summary>
public enum LicenseCacheState : byte
{
    /// <summary>Nothing is known about this KID — nothing has been asked for.</summary>
    None = 0,
    /// <summary>A challenge is in flight: the CDM raised a KeyMessage, the relay is POSTing, and the key status has
    /// not yet said USABLE. A second request for the same KID must JOIN this one, never start a second POST.</summary>
    Pending,
    /// <summary>The key reached USABLE — an attach may use it immediately, with no network at all.</summary>
    Usable,
    /// <summary>The license expired (or its expiry is close enough that starting playback on it is a bet).</summary>
    Expired,
    /// <summary>The acquisition failed (a rejected challenge, an unreachable server, a relay fault).</summary>
    Failed,
}

/// <summary>What to do about a KID right now.</summary>
public enum LicenseCacheAction : byte
{
    /// <summary>The cached license is usable: hand its handle straight to the attach.</summary>
    Reuse,
    /// <summary>An acquisition is already in flight for this KID: wait for it, do NOT issue a second challenge.</summary>
    Await,
    /// <summary>Nothing usable or pending: open a key session and generate a request.</summary>
    Acquire,
}

/// <summary>One row of the KID-keyed license cache, as the policy sees it. Pure data — no handle, no CDM, no time
/// source of its own (every decision takes <c>nowMs</c>), so the whole cache discipline is unit-testable without a
/// CDM, a license server, a GPU or a window.</summary>
/// <param name="Kid">The content key id as 32 lower-case hex characters.</param>
/// <param name="State">Where this KID's acquisition stands.</param>
/// <param name="LastUsedMs">When this entry was last asked for (the LRU key).</param>
/// <param name="ExpiresAtMs">When the license expires, or 0 when the CDM did not say (treated as "does not expire").</param>
/// <param name="InUse">True while an ATTACHED session is decoding with this key — such a row is never evicted.</param>
public readonly record struct LicenseCacheEntry(string Kid, LicenseCacheState State, long LastUsedMs,
                                                long ExpiresAtMs, bool InUse);

/// <summary>
/// The decision half of <see cref="ProtectedVideoRuntime"/>'s license cache, extracted so it is testable with no CDM
/// and no native call (the engine's <c>LicenseCachePolicyTests</c>). The runtime owns the handles, the CDM key
/// sessions and the clock; this owns the RULES: reuse / join / re-acquire, when an expiry is close enough to count as
/// expired, when a Pending row is too old to join, which DEAD row a full cache trims, and whether a late
/// <c>LicenseUsable</c> still applies to a row that has since been evicted or replaced. Which LIVE row a ninth KID evicts is
/// NOT decided here: the native license table is the single eviction authority (it raises <c>EvLicenseEvicted</c> and the
/// runtime drops the row), so the two caches can never disagree about which key sessions exist.
/// <para>Why a cache at all: PlayReady's "proactive acquisition" — the license request is issued the moment a
/// manifest is known, not when the user asks for the video, so a song→video switch finds the key already usable and
/// pays no round trip. Keeping the key SESSION open (rather than just the bytes) is what lets the CDM answer an
/// attach synchronously.</para>
/// </summary>
public static class LicenseCachePolicy
{
    /// <summary>How many KIDs the cache holds. Eight covers the current track, the prefetched next, and a handful of
    /// back-and-forth toggles; beyond that the oldest unused row goes.</summary>
    public const int Capacity = 8;

    /// <summary>A license whose expiry is within this window counts as expired: attaching on it would start playback
    /// that dies mid-track, and re-acquiring costs one round trip we are already overlapping with the fetch.</summary>
    public const long ExpiryGuardMs = 10_000;

    /// <summary>How long a row may stay Pending before it is too old to join. A relay that hangs, or a CDM that took the
    /// license and never reported a key status, would otherwise keep the KID Pending for ever and every retry would join the
    /// attempt that already failed the open. Below the session's start budget (10 s), so the retry of an open that just
    /// failed at "Licensing" issues a fresh challenge. The native table applies the same number (<c>kPendingDeadlineMs</c>).</summary>
    public const long PendingDeadlineMs = 8_000;

    /// <summary>What to do about a KID in <paramref name="state"/> right now. <paramref name="expiresAtMs"/> is 0 when
    /// the CDM did not report an expiry (a non-expiring streaming license), which is treated as "does not expire".
    /// <paramref name="acquiredMs"/> is when the row was created (0 = unknown, so a Pending row never goes stale): a Pending
    /// row older than <see cref="PendingDeadlineMs"/> is re-acquired instead of joined.</summary>
    public static LicenseCacheAction Decide(LicenseCacheState state, long expiresAtMs, long nowMs, long acquiredMs = 0) => state switch
    {
        LicenseCacheState.Usable => IsExpired(expiresAtMs, nowMs) ? LicenseCacheAction.Acquire : LicenseCacheAction.Reuse,
        LicenseCacheState.Pending => IsPendingStale(acquiredMs, nowMs) ? LicenseCacheAction.Acquire : LicenseCacheAction.Await,
        _ => LicenseCacheAction.Acquire,
    };

    /// <summary>Whether a row created at <paramref name="acquiredMs"/> has been Pending for at least
    /// <see cref="PendingDeadlineMs"/> at <paramref name="nowMs"/>. An <paramref name="acquiredMs"/> of 0 or less means "unknown".</summary>
    public static bool IsPendingStale(long acquiredMs, long nowMs) => acquiredMs > 0 && nowMs - acquiredMs >= PendingDeadlineMs;

    /// <summary>Whether a license with <paramref name="expiresAtMs"/> is expired (or close enough to it) at
    /// <paramref name="nowMs"/>. An <paramref name="expiresAtMs"/> of 0 or less means "no expiry was reported".</summary>
    public static bool IsExpired(long expiresAtMs, long nowMs) => expiresAtMs > 0 && expiresAtMs - nowMs <= ExpiryGuardMs;

    /// <summary>Whether a new KID can be admitted without evicting anything.</summary>
    public static bool HasRoom(int count) => count < Capacity;

    /// <summary>
    /// Which row a KID beyond <see cref="Capacity"/> evicts, or -1 when the cache has room (or nothing is evictable). The order is:
    /// a Failed or Expired row first (it is dead weight), then the least-recently-used row. A row that is
    /// <see cref="LicenseCacheEntry.InUse"/> — an attached session is decoding with that key — is NEVER evicted, and
    /// neither is <paramref name="keepKid"/> (the KID the caller is about to attach), because evicting either closes
    /// the CDM key session under a live decoder.
    /// <para>The runtime no longer evicts live rows with this (the native table owns the LRU, see
    /// <see cref="ChooseDeadEviction"/>); it stays as the pure statement of the order the native table follows.</para>
    /// </summary>
    public static int ChooseEviction(ReadOnlySpan<LicenseCacheEntry> entries, string? keepKid)
    {
        if (entries.Length < Capacity) return -1;

        int dead = -1, lru = -1;
        long lruStamp = long.MaxValue;
        for (int i = 0; i < entries.Length; i++)
        {
            LicenseCacheEntry e = entries[i];
            if (e.InUse) continue;
            if (keepKid is not null && string.Equals(e.Kid, keepKid, StringComparison.Ordinal)) continue;
            if (dead < 0 && e.State is LicenseCacheState.Failed or LicenseCacheState.Expired) dead = i;
            if (e.LastUsedMs < lruStamp) { lruStamp = e.LastUsedMs; lru = i; }
        }
        return dead >= 0 ? dead : lru;
    }

    /// <summary>
    /// Which DEAD row (Failed or Expired, not in use, not <paramref name="keepKid"/>) a full cache trims to make room, or -1
    /// when it has room or holds none. Only dead rows: a LIVE row is never evicted from here, because the native license table
    /// owns eviction (its LRU closes the key session and raises <c>EvLicenseEvicted</c>, and the row goes then). Trimming a dead
    /// row is bookkeeping, not eviction: nothing a decoder or a later attach could use goes with it.
    /// </summary>
    public static int ChooseDeadEviction(ReadOnlySpan<LicenseCacheEntry> entries, string? keepKid)
    {
        if (entries.Length < Capacity) return -1;
        for (int i = 0; i < entries.Length; i++)
        {
            LicenseCacheEntry e = entries[i];
            if (e.InUse) continue;
            if (keepKid is not null && string.Equals(e.Kid, keepKid, StringComparison.Ordinal)) continue;
            if (e.State is LicenseCacheState.Failed or LicenseCacheState.Expired) return i;
        }
        return -1;
    }

    /// <summary>
    /// Whether a <c>LicenseUsable</c> / <c>LicenseFailed</c> event that has just arrived still applies. The CDM
    /// answers asynchronously, so an event can land for a KID whose row was evicted, released or already re-acquired
    /// while the challenge was in flight; applying it then would resurrect a closed key session (or mark a fresh
    /// Pending row Usable on the strength of an old handle). The row must still exist, still be Pending, and still
    /// carry the handle the event names.
    /// </summary>
    public static bool AcceptCompletion(bool rowExists, LicenseCacheState rowState, ulong rowHandle, ulong eventHandle)
        => rowExists && rowState == LicenseCacheState.Pending && rowHandle != 0 && rowHandle == eventHandle;

    /// <summary>
    /// Whether a <c>LicenseExpired</c> event applies to a row. An expiry is about a key session that EXISTS — one that
    /// became usable, or is still pending — so it must name that row's own non-zero handle. An expiry for a handle the
    /// row does not carry (an evicted predecessor's key session, a re-acquired KID's old one) must not mark the live
    /// row Expired and force a needless re-acquisition; nor can an expiry resurrect a Failed row's state into a
    /// different dead state.
    /// </summary>
    public static bool AcceptExpiry(bool rowExists, LicenseCacheState rowState, ulong rowHandle, ulong eventHandle)
        => rowExists && (rowState is LicenseCacheState.Usable or LicenseCacheState.Pending)
           && rowHandle != 0 && rowHandle == eventHandle;

    /// <summary>
    /// Whether a <c>LicenseRevoked</c> event applies to a row: the CDM says a key that WAS usable (INTERNAL_ERROR, RELEASED,
    /// OUTPUT_NOT_ALLOWED) will never decrypt again. Unlike a <c>LicenseFailed</c> after Usable (a late renewal round trip, which
    /// leaves the key in place and is ignored), this one is about the key itself, so a Usable row turns Failed. A row still
    /// marked Pending can be revoked too (the usable event may not have been applied yet). It must name the row's own handle:
    /// a revoked predecessor's key session says nothing about the fresh one.
    /// </summary>
    public static bool AcceptRevocation(bool rowExists, LicenseCacheState rowState, ulong rowHandle, ulong eventHandle)
        => rowExists && (rowState is LicenseCacheState.Usable or LicenseCacheState.Pending)
           && rowHandle != 0 && rowHandle == eventHandle;
}

/// <summary>
/// The content key id a PlayReady PSSH names — the license cache's KEY, recovered from the init data a manifest
/// carries (Spotify's <c>encryption_data</c> is the PSSH and no separate KID), so the license can be acquired at
/// manifest time instead of after the init segment has been fetched and parsed. Pure; no CDM.
/// <para>Two places a KID can be: a version-1 <c>pssh</c> box lists KIDs directly (already in CENC byte order); a
/// version-0 box (and a bare PlayReady Object) carries them only inside the UTF-16 <c>WRMHEADER</c> XML — as
/// <c>&lt;KID&gt;base64&lt;/KID&gt;</c> (header v4.0) or <c>&lt;KID VALUE="base64" …&gt;</c> (v4.1+) — encoded in the
/// Microsoft GUID layout, whose first three fields are little-endian and must be swapped to the CENC order.</para>
/// </summary>
public static class LicenseKeyId
{
    private static ReadOnlySpan<byte> PlayReadySystemId =>
        [0x9a, 0x04, 0xf0, 0x79, 0x98, 0x40, 0x42, 0x86, 0xab, 0x92, 0xe6, 0x5b, 0xe0, 0x88, 0x5f, 0x95];

    /// <summary>The first KID <paramref name="pssh"/> names, as 32 lower-case hex characters, or null when it names
    /// none (or is not PlayReady init data). Accepts a whole <c>pssh</c> box or a bare PlayReady Object.
    /// <para>The input is untrusted manifest data (it runs at manifest time, on bytes from the network), so it NEVER
    /// throws: every count and size in the box is an unsigned field, and each is checked against the bytes actually left
    /// in 64-bit arithmetic before anything is sliced — a claimed KID list or data size that does not fit is simply not
    /// there.</para></summary>
    public static string? FromPssh(ReadOnlySpan<byte> pssh)
    {
        if (pssh.Length < 8) return null;

        ReadOnlySpan<byte> pro = pssh;
        if (pssh[4] == (byte)'p' && pssh[5] == (byte)'s' && pssh[6] == (byte)'s' && pssh[7] == (byte)'h')
        {
            // size(4) 'pssh'(4) version(1) flags(3) systemId(16) [kidCount(4) kids(16n)] dataSize(4) data
            if (pssh.Length < 32) return null;
            byte version = pssh[8];
            if (!pssh.Slice(12, 16).SequenceEqual(PlayReadySystemId)) return null;
            long at = 28;
            if (version > 0)
            {
                if (pssh.Length - at < 4) return null;
                uint count = ReadUInt32BE(pssh, (int)at);
                at += 4;
                if (count > 0 && pssh.Length - at >= 16) return Hex(pssh.Slice((int)at, 16));
                long kidBytes = (long)count * 16;
                if (pssh.Length - at < kidBytes) return null;   // a KID list the box does not hold
                at += kidBytes;
            }
            if (pssh.Length - at < 4) return null;
            uint dataSize = ReadUInt32BE(pssh, (int)at);
            at += 4;
            if (dataSize == 0 || pssh.Length - at < dataSize) return null;
            pro = pssh.Slice((int)at, (int)dataSize);
        }
        return FromPlayReadyObject(pro);
    }

    /// <summary>The KID inside a PlayReady Object's rights-management header (record type 1), or null.</summary>
    public static string? FromPlayReadyObject(ReadOnlySpan<byte> pro)
    {
        // length(4, LE) recordCount(2, LE) { type(2, LE) length(2, LE) value }*
        if (pro.Length < 6) return null;
        int records = pro[4] | (pro[5] << 8);
        int at = 6;
        for (int r = 0; r < records && at + 4 <= pro.Length; r++)
        {
            int type = pro[at] | (pro[at + 1] << 8);
            int len = pro[at + 2] | (pro[at + 3] << 8);
            at += 4;
            if (len < 0 || at + len > pro.Length) return null;
            if (type == 1)
            {
                string xml = System.Text.Encoding.Unicode.GetString(pro.Slice(at, len & ~1));
                return FromHeaderXml(xml);
            }
            at += len;
        }
        return null;
    }

    /// <summary>The first KID in a <c>WRMHEADER</c> XML string, or null.</summary>
    public static string? FromHeaderXml(string xml)
    {
        int kid = xml.IndexOf("<KID", StringComparison.Ordinal);
        while (kid >= 0)
        {
            int tagEnd = xml.IndexOf('>', kid);
            if (tagEnd < 0) return null;
            char next = kid + 4 < xml.Length ? xml[kid + 4] : '\0';
            if (next == '>' || next == ' ' || next == '/')
            {
                string? b64 = null;
                int value = xml.IndexOf("VALUE=\"", kid, tagEnd - kid, StringComparison.Ordinal);
                if (value >= 0)
                {
                    int start = value + 7;
                    int end = xml.IndexOf('"', start);
                    if (end > start) b64 = xml[start..end];
                }
                else if (next == '>' && xml[tagEnd - 1] != '/')
                {
                    int close = xml.IndexOf("</KID>", tagEnd, StringComparison.Ordinal);
                    if (close > tagEnd) b64 = xml[(tagEnd + 1)..close].Trim();
                }
                if (b64 is { Length: > 0 } && GuidLayoutToHex(b64) is { } hex) return hex;
            }
            kid = xml.IndexOf("<KID", kid + 4, StringComparison.Ordinal);
        }
        return null;
    }

    private static string? GuidLayoutToHex(string base64)
    {
        Span<byte> g = stackalloc byte[16];
        if (!Convert.TryFromBase64String(base64, g, out int written) || written != 16) return null;
        // Microsoft GUID layout → CENC (big-endian) order: swap Data1 (4), Data2 (2), Data3 (2); Data4 (8) as-is.
        Span<byte> k = stackalloc byte[16];
        k[0] = g[3]; k[1] = g[2]; k[2] = g[1]; k[3] = g[0];
        k[4] = g[5]; k[5] = g[4];
        k[6] = g[7]; k[7] = g[6];
        g.Slice(8, 8).CopyTo(k.Slice(8));
        return Hex(k);
    }

    private static uint ReadUInt32BE(ReadOnlySpan<byte> b, int at)
        => ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);
}
