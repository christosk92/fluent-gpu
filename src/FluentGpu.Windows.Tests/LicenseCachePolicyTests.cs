using System;
using System.Collections.Generic;
using System.Text;
using FluentGpu.WindowsApi.Media.PlayReady;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The pure half of the protected runtime's license cache: <see cref="LicenseCachePolicy"/> (reuse / join / re-acquire,
/// the expiry guard, which row an extra KID evicts, whether a late completion still applies) and <see cref="LicenseKeyId"/>
/// (the cache KEY recovered from a manifest's PSSH, including the Microsoft-GUID → CENC byte-order swap). No CDM, no
/// native call, no clock of its own — every input is built by hand here.
/// </summary>
public sealed class LicenseCachePolicyTests
{
    private const long Now = 1_000_000;

    // ── Decide ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Decide_Usable_WithNoReportedExpiry_Reuses()
        => Assert.Equal(LicenseCacheAction.Reuse, LicenseCachePolicy.Decide(LicenseCacheState.Usable, 0, Now));

    [Fact]
    public void Decide_Usable_WellBeforeExpiry_Reuses()
        => Assert.Equal(LicenseCacheAction.Reuse,
            LicenseCachePolicy.Decide(LicenseCacheState.Usable, Now + LicenseCachePolicy.ExpiryGuardMs + 1, Now));

    [Theory]
    [InlineData(LicenseCachePolicy.ExpiryGuardMs)]   // exactly at the guard counts as expired
    [InlineData(1L)]                                  // about to expire
    [InlineData(-1L)]                                 // already expired
    public void Decide_Usable_WithinTheExpiryGuard_Reacquires(long expiresInMs)
        => Assert.Equal(LicenseCacheAction.Acquire, LicenseCachePolicy.Decide(LicenseCacheState.Usable, Now + expiresInMs, Now));

    [Theory]
    [InlineData(0L)]
    [InlineData(Now - 1)]   // even a "past" expiry never makes a join start a second challenge
    public void Decide_Pending_Awaits(long expiresAtMs)
        => Assert.Equal(LicenseCacheAction.Await, LicenseCachePolicy.Decide(LicenseCacheState.Pending, expiresAtMs, Now));

    [Theory]
    [InlineData(LicenseCacheState.Expired)]
    [InlineData(LicenseCacheState.Failed)]
    [InlineData(LicenseCacheState.None)]
    public void Decide_ExpiredFailedOrNone_Acquires(LicenseCacheState state)
        => Assert.Equal(LicenseCacheAction.Acquire, LicenseCachePolicy.Decide(state, 0, Now));

    [Theory]
    [InlineData(0L, false)]
    [InlineData(-5L, false)]
    [InlineData(Now + LicenseCachePolicy.ExpiryGuardMs, true)]
    [InlineData(Now + LicenseCachePolicy.ExpiryGuardMs + 1, false)]
    public void IsExpired_TreatsNonPositiveAsNoExpiry_AndTheGuardAsExpired(long expiresAtMs, bool expired)
        => Assert.Equal(expired, LicenseCachePolicy.IsExpired(expiresAtMs, Now));

    [Fact]
    public void HasRoom_BelowCapacityOnly()
    {
        Assert.True(LicenseCachePolicy.HasRoom(LicenseCachePolicy.Capacity - 1));
        Assert.False(LicenseCachePolicy.HasRoom(LicenseCachePolicy.Capacity));
    }

    // ── ChooseEviction ───────────────────────────────────────────────────────────────────────────────────────────────

    private static LicenseCacheEntry Row(int i, LicenseCacheState state = LicenseCacheState.Usable, long lastUsedMs = -1,
                                         bool inUse = false)
        => new("kid" + i, state, lastUsedMs < 0 ? 1_000 + i * 100 : lastUsedMs, 0, inUse);

    /// <summary>A full cache whose row i was last used at 1000 + 100·i — row 0 is the LRU row.</summary>
    private static LicenseCacheEntry[] FullCache()
    {
        var rows = new LicenseCacheEntry[LicenseCachePolicy.Capacity];
        for (int i = 0; i < rows.Length; i++) rows[i] = Row(i);
        return rows;
    }

    [Fact]
    public void ChooseEviction_BelowCapacity_EvictsNothing()
    {
        var rows = FullCache()[..(LicenseCachePolicy.Capacity - 1)];
        rows[0] = Row(0, LicenseCacheState.Failed);
        Assert.Equal(-1, LicenseCachePolicy.ChooseEviction(rows, keepKid: null));
    }

    [Fact]
    public void ChooseEviction_Full_EvictsTheLeastRecentlyUsedRow()
    {
        var rows = FullCache();
        rows[5] = Row(5, lastUsedMs: 10);   // the oldest stamp, not the first index
        Assert.Equal(5, LicenseCachePolicy.ChooseEviction(rows, keepKid: null));
    }

    [Theory]
    [InlineData(LicenseCacheState.Failed)]
    [InlineData(LicenseCacheState.Expired)]
    public void ChooseEviction_PrefersADeadRow_OverTheLruRow(LicenseCacheState dead)
    {
        var rows = FullCache();
        rows[6] = Row(6, dead, lastUsedMs: 999_999);   // the NEWEST row, but dead weight
        Assert.Equal(6, LicenseCachePolicy.ChooseEviction(rows, keepKid: null));
    }

    [Fact]
    public void ChooseEviction_TwoDeadRows_EvictsTheFirst()
    {
        var rows = FullCache();
        rows[3] = Row(3, LicenseCacheState.Expired);
        rows[7] = Row(7, LicenseCacheState.Failed);
        Assert.Equal(3, LicenseCachePolicy.ChooseEviction(rows, keepKid: null));
    }

    [Fact]
    public void ChooseEviction_NeverEvictsAnInUseRow_EvenTheLruOne()
    {
        var rows = FullCache();
        rows[0] = Row(0, inUse: true);
        Assert.Equal(1, LicenseCachePolicy.ChooseEviction(rows, keepKid: null));
    }

    [Fact]
    public void ChooseEviction_NeverEvictsAnInUseDeadRow()
    {
        var rows = FullCache();
        rows[4] = Row(4, LicenseCacheState.Failed, inUse: true);
        Assert.Equal(0, LicenseCachePolicy.ChooseEviction(rows, keepKid: null));   // falls back to the LRU row
    }

    [Fact]
    public void ChooseEviction_NeverEvictsTheKeepKid()
    {
        var rows = FullCache();
        Assert.Equal(1, LicenseCachePolicy.ChooseEviction(rows, keepKid: "kid0"));

        rows[2] = Row(2, LicenseCacheState.Failed);
        Assert.Equal(0, LicenseCachePolicy.ChooseEviction(rows, keepKid: "kid2"));   // a dead keepKid is still kept
    }

    [Fact]
    public void ChooseEviction_EveryRowProtected_ReturnsMinusOne()
    {
        var rows = FullCache();
        for (int i = 0; i < rows.Length; i++) rows[i] = Row(i, inUse: true);
        Assert.Equal(-1, LicenseCachePolicy.ChooseEviction(rows, keepKid: null));

        for (int i = 1; i < rows.Length; i++) rows[i] = Row(i, inUse: true);
        rows[0] = Row(0);
        Assert.Equal(-1, LicenseCachePolicy.ChooseEviction(rows, keepKid: "kid0"));   // the only unpinned row is the keep
    }

    [Fact]
    public void ChooseEviction_OverCapacity_StillChooses()
    {
        var rows = new LicenseCacheEntry[LicenseCachePolicy.Capacity + 1];
        for (int i = 0; i < rows.Length; i++) rows[i] = Row(i, inUse: i != 8);
        Assert.Equal(8, LicenseCachePolicy.ChooseEviction(rows, keepKid: null));
    }

    // ── AcceptCompletion ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AcceptCompletion_OnlyAPendingRowWithTheSameNonZeroHandle()
    {
        Assert.True(LicenseCachePolicy.AcceptCompletion(true, LicenseCacheState.Pending, 7, 7));

        Assert.False(LicenseCachePolicy.AcceptCompletion(false, LicenseCacheState.Pending, 7, 7));   // evicted/released
        Assert.False(LicenseCachePolicy.AcceptCompletion(true, LicenseCacheState.Pending, 7, 8));    // re-acquired since
        Assert.False(LicenseCachePolicy.AcceptCompletion(true, LicenseCacheState.Pending, 0, 0));    // no handle yet
    }

    [Theory]
    [InlineData(LicenseCacheState.None)]
    [InlineData(LicenseCacheState.Usable)]
    [InlineData(LicenseCacheState.Expired)]
    [InlineData(LicenseCacheState.Failed)]
    public void AcceptCompletion_ANonPendingRow_IsNeverReapplied(LicenseCacheState state)
        => Assert.False(LicenseCachePolicy.AcceptCompletion(true, state, 7, 7));

    // ── the Pending deadline (F011) ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, LicenseCacheAction.Await)]                                                       // just acquired
    [InlineData(LicenseCachePolicy.PendingDeadlineMs - 1, LicenseCacheAction.Await)]                 // one ms short of the deadline
    [InlineData(LicenseCachePolicy.PendingDeadlineMs, LicenseCacheAction.Acquire)]                   // the deadline itself: stale
    [InlineData(LicenseCachePolicy.PendingDeadlineMs * 5, LicenseCacheAction.Acquire)]
    public void Decide_Pending_JoinsUntilTheDeadline_ThenReacquires(long ageMs, LicenseCacheAction expected)
        => Assert.Equal(expected, LicenseCachePolicy.Decide(LicenseCacheState.Pending, 0, Now, acquiredMs: Now - ageMs));

    [Fact]
    public void Decide_Pending_WithNoKnownAcquireTime_NeverGoesStale()
        => Assert.Equal(LicenseCacheAction.Await, LicenseCachePolicy.Decide(LicenseCacheState.Pending, 0, Now + 10 * LicenseCachePolicy.PendingDeadlineMs));

    [Fact]
    public void PendingDeadline_IsBelowTheSessionStartBudget()
        => Assert.True(LicenseCachePolicy.PendingDeadlineMs < 10_000);   // the start budget's floor: a retry must not join the attempt that failed the open

    [Theory]
    [InlineData(LicenseCacheState.Usable)]
    [InlineData(LicenseCacheState.Expired)]
    [InlineData(LicenseCacheState.Failed)]
    public void TheDeadlineOnlyAppliesToPendingRows(LicenseCacheState state)
    {
        long old = Now - 10 * LicenseCachePolicy.PendingDeadlineMs;
        LicenseCacheAction fresh = LicenseCachePolicy.Decide(state, 0, Now);
        Assert.Equal(fresh, LicenseCachePolicy.Decide(state, 0, Now, acquiredMs: old));   // an old Usable row is still reused
    }

    // ── ChooseDeadEviction (F008: only dead rows are trimmed managed-side) ─────────────────────────────────────────────

    [Fact]
    public void ChooseDeadEviction_BelowCapacity_TrimsNothing()
    {
        var rows = new LicenseCacheEntry[LicenseCachePolicy.Capacity - 1];
        for (int i = 0; i < rows.Length; i++) rows[i] = Row(i, LicenseCacheState.Failed);
        Assert.Equal(-1, LicenseCachePolicy.ChooseDeadEviction(rows, keepKid: null));
    }

    [Fact]
    public void ChooseDeadEviction_FullOfLiveRows_TrimsNothing_NativeOwnsTheLruChoice()
    {
        var rows = FullCache();
        rows[2] = Row(2, LicenseCacheState.Pending);
        Assert.Equal(-1, LicenseCachePolicy.ChooseDeadEviction(rows, keepKid: null));
    }

    [Theory]
    [InlineData(LicenseCacheState.Failed)]
    [InlineData(LicenseCacheState.Expired)]
    public void ChooseDeadEviction_Full_TrimsTheFirstDeadRow(LicenseCacheState dead)
    {
        var rows = FullCache();
        rows[5] = Row(5, dead);
        rows[6] = Row(6, dead);
        Assert.Equal(5, LicenseCachePolicy.ChooseDeadEviction(rows, keepKid: null));
    }

    [Fact]
    public void ChooseDeadEviction_NeverTrimsAnInUseOrKeptRow()
    {
        var rows = FullCache();
        rows[1] = Row(1, LicenseCacheState.Failed, inUse: true);
        rows[2] = Row(2, LicenseCacheState.Failed);
        Assert.Equal(2, LicenseCachePolicy.ChooseDeadEviction(rows, keepKid: null));
        Assert.Equal(-1, LicenseCachePolicy.ChooseDeadEviction(rows, keepKid: "kid2"));
    }

    // ── AcceptRevocation (F045) ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, LicenseCacheState.Usable, 7ul, 7ul, true)]       // a key that WAS usable died
    [InlineData(true, LicenseCacheState.Pending, 7ul, 7ul, true)]      // the usable event was not applied yet
    [InlineData(true, LicenseCacheState.Failed, 7ul, 7ul, false)]
    [InlineData(true, LicenseCacheState.Expired, 7ul, 7ul, false)]
    [InlineData(true, LicenseCacheState.Usable, 7ul, 8ul, false)]      // a predecessor's key session says nothing about this row
    [InlineData(true, LicenseCacheState.Usable, 0ul, 0ul, false)]      // no handle
    [InlineData(false, LicenseCacheState.Usable, 7ul, 7ul, false)]     // no row
    public void AcceptRevocation_OnlyALiveRowsOwnNonZeroHandle(bool rowExists, LicenseCacheState state, ulong rowHandle,
                                                                ulong eventHandle, bool accepted)
        => Assert.Equal(accepted, LicenseCachePolicy.AcceptRevocation(rowExists, state, rowHandle, eventHandle));

    // ── LicenseKeyId ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A GUID whose first three fields are asymmetric, so a missing (or doubled) byte swap cannot pass.</summary>
    private static readonly Guid ContentKey = new("4060a865-8878-4267-9cbf-91ae5bae1e72");
    private static readonly Guid SecondKey = new("0badc0de-1234-5678-9abc-def012345678");

    private static readonly byte[] PlayReadySystemId = Convert.FromHexString("9a04f07998404286ab92e65be0885f95");
    private static readonly byte[] WidevineSystemId = Convert.FromHexString("edef8ba979d64acea3c827dcd51d21ed");

    private const string HeaderNs = "http://schemas.microsoft.com/DRM/2007/03/PlayReadyHeader";

    /// <summary>The KID as a WRMHEADER carries it: base64 of the MICROSOFT GUID layout (Data1..Data3 little-endian).</summary>
    private static string HeaderB64(Guid g) => Convert.ToBase64String(g.ToByteArray());

    private static string HeaderV40(Guid kid) =>
        $"""<WRMHEADER xmlns="{HeaderNs}" version="4.0.0.0"><DATA><PROTECTINFO><KEYLEN>16</KEYLEN><ALGID>AESCTR</ALGID></PROTECTINFO><KID>{HeaderB64(kid)}</KID><LA_URL>https://license.test/rightsmanager.asmx</LA_URL></DATA></WRMHEADER>""";

    private static string HeaderV41(Guid kid) =>
        $"""<WRMHEADER xmlns="{HeaderNs}" version="4.1.0.0"><DATA><PROTECTINFO><KID ALGID="AESCTR" VALUE="{HeaderB64(kid)}"></KID></PROTECTINFO></DATA></WRMHEADER>""";

    private static string HeaderV42(Guid kid) =>
        $"""<WRMHEADER xmlns="{HeaderNs}" version="4.2.0.0"><DATA><PROTECTINFO><KIDS><KID ALGID="AESCTR" VALUE="{HeaderB64(kid)}"></KID></KIDS></PROTECTINFO><LA_URL>https://license.test/rightsmanager.asmx</LA_URL></DATA></WRMHEADER>""";

    private static byte[] BE32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    private static byte[] LE32(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
    private static byte[] LE16(int v) => [(byte)v, (byte)(v >> 8)];

    /// <summary>A PlayReady Object: length(4 LE) recordCount(2 LE) { type(2 LE) length(2 LE) value }* — the rights
    /// management header is record type 1 (UTF-16LE XML); type 3 is an embedded license store.</summary>
    private static byte[] PlayReadyObject(string headerXml, bool leadingLicenseStoreRecord = false)
    {
        var records = new List<byte>();
        int count = 0;
        if (leadingLicenseStoreRecord)
        {
            records.AddRange(LE16(3));
            records.AddRange(LE16(4));
            records.AddRange(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
            count++;
        }
        byte[] xml = Encoding.Unicode.GetBytes(headerXml);
        records.AddRange(LE16(1));
        records.AddRange(LE16(xml.Length));
        records.AddRange(xml);
        count++;

        var pro = new List<byte>();
        pro.AddRange(LE32(6 + records.Count));
        pro.AddRange(LE16(count));
        pro.AddRange(records);
        return pro.ToArray();
    }

    /// <summary>A whole <c>pssh</c> box: size(4) 'pssh' version(1) flags(3) systemId(16) [kidCount(4) kids(16n)] dataSize(4) data.</summary>
    private static byte[] PsshBox(byte version, byte[] systemId, byte[][] kids, byte[] data)
    {
        var body = new List<byte> { version, 0, 0, 0 };
        body.AddRange(systemId);
        if (version > 0)
        {
            body.AddRange(BE32(kids.Length));
            foreach (byte[] k in kids) body.AddRange(k);
        }
        body.AddRange(BE32(data.Length));
        body.AddRange(data);

        var box = new List<byte>();
        box.AddRange(BE32(8 + body.Count));
        box.AddRange("pssh"u8.ToArray());
        box.AddRange(body);
        return box.ToArray();
    }

    [Fact]
    public void FromPssh_V1Box_ReturnsTheFirstListedKid_InCencOrder()
    {
        // A v1 box lists KIDs already in CENC (big-endian) order: no swap applies.
        byte[] box = PsshBox(1, PlayReadySystemId,
            [ContentKey.ToByteArray(bigEndian: true), SecondKey.ToByteArray(bigEndian: true)], Array.Empty<byte>());

        Assert.Equal(ContentKey.ToString("N"), LicenseKeyId.FromPssh(box));
    }

    [Fact]
    public void FromPssh_V1BoxWithNoKids_FallsBackToTheHeader()
    {
        byte[] box = PsshBox(1, PlayReadySystemId, Array.Empty<byte[]>(), PlayReadyObject(HeaderV42(ContentKey)));
        Assert.Equal(ContentKey.ToString("N"), LicenseKeyId.FromPssh(box));
    }

    [Fact]
    public void FromPssh_V0Box_Header40_SwapsTheGuidLayoutToCencOrder()
    {
        byte[] box = PsshBox(0, PlayReadySystemId, Array.Empty<byte[]>(), PlayReadyObject(HeaderV40(ContentKey)));

        string? kid = LicenseKeyId.FromPssh(box);

        // Guid.ToString("N") prints Data1..Data3 big-endian — the CENC order. Equality with it IS the byte-swap rule.
        Assert.Equal(ContentKey.ToString("N"), kid);
        Assert.NotEqual(Convert.ToHexStringLower(ContentKey.ToByteArray()), kid);   // the raw (unswapped) layout is wrong
    }

    [Fact]
    public void FromPssh_V0Box_Header42KidsList_SwapsTheGuidLayoutToCencOrder()
    {
        byte[] box = PsshBox(0, PlayReadySystemId, Array.Empty<byte[]>(), PlayReadyObject(HeaderV42(ContentKey)));
        Assert.Equal(ContentKey.ToString("N"), LicenseKeyId.FromPssh(box));
    }

    [Fact]
    public void FromPssh_BarePlayReadyObject_IsAccepted()
        => Assert.Equal(ContentKey.ToString("N"), LicenseKeyId.FromPssh(PlayReadyObject(HeaderV40(ContentKey))));

    [Fact]
    public void FromPlayReadyObject_SkipsANonHeaderRecord()
        => Assert.Equal(ContentKey.ToString("N"),
            LicenseKeyId.FromPlayReadyObject(PlayReadyObject(HeaderV41(ContentKey), leadingLicenseStoreRecord: true)));

    [Fact]
    public void FromPssh_NonPlayReadySystemId_ReturnsNull()
    {
        byte[] v0 = PsshBox(0, WidevineSystemId, Array.Empty<byte[]>(), PlayReadyObject(HeaderV40(ContentKey)));
        byte[] v1 = PsshBox(1, WidevineSystemId, [ContentKey.ToByteArray(bigEndian: true)], Array.Empty<byte>());

        Assert.Null(LicenseKeyId.FromPssh(v0));
        Assert.Null(LicenseKeyId.FromPssh(v1));
    }

    [Fact]
    public void FromPssh_EmptyOrTiny_ReturnsNull()
    {
        Assert.Null(LicenseKeyId.FromPssh(ReadOnlySpan<byte>.Empty));
        Assert.Null(LicenseKeyId.FromPssh(new byte[] { 0, 0, 0, 8, (byte)'p', (byte)'s', (byte)'s' }));
    }

    [Fact]
    public void FromPssh_EveryTruncationOfAV0Box_ReturnsNull_NeverThrows()
    {
        byte[] box = PsshBox(0, PlayReadySystemId, Array.Empty<byte[]>(), PlayReadyObject(HeaderV40(ContentKey)));
        for (int n = 0; n < box.Length; n++)
            Assert.Null(LicenseKeyId.FromPssh(box.AsSpan(0, n)));
        Assert.Equal(ContentKey.ToString("N"), LicenseKeyId.FromPssh(box));
    }

    [Fact]
    public void FromPssh_EveryTruncationOfAV1Box_ReturnsNullUntilTheFirstKidIsWhole_NeverThrows()
    {
        byte[] box = PsshBox(1, PlayReadySystemId, [ContentKey.ToByteArray(bigEndian: true)], Array.Empty<byte>());
        const int firstKidEnd = 4 + 4 + 4 + 16 + 4 + 16;   // size, type, version+flags, system id, kid count, kid
        for (int n = 0; n < box.Length; n++)
        {
            string? kid = LicenseKeyId.FromPssh(box.AsSpan(0, n));
            if (n < firstKidEnd) Assert.Null(kid);
            else Assert.Equal(ContentKey.ToString("N"), kid);
        }
    }

    [Fact]
    public void FromPssh_EveryTruncationOfABarePlayReadyObject_ReturnsNull_NeverThrows()
    {
        byte[] pro = PlayReadyObject(HeaderV42(ContentKey));
        for (int n = 0; n < pro.Length; n++)
            Assert.Null(LicenseKeyId.FromPssh(pro.AsSpan(0, n)));
    }

    [Theory]
    [InlineData(0x08000001)]   // × 16 wraps to a large negative offset
    [InlineData(0x10000000)]   // × 16 wraps to exactly 0
    [InlineData(0x7FFFFFFF)]   // × 16 wraps to -16
    public void FromPssh_AHostileKidCountOnATruncatedV1Box_ReturnsNull_NeverThrows(int kidCount)
    {
        // A manifest-supplied v1 box that claims an enormous KID list and then ends: the count must never turn into an
        // out-of-range read (this runs at manifest time, on untrusted network data).
        var box = new List<byte>();
        box.AddRange(BE32(32));
        box.AddRange("pssh"u8.ToArray());
        box.AddRange(new byte[] { 1, 0, 0, 0 });
        box.AddRange(PlayReadySystemId);
        box.AddRange(BE32(kidCount));

        Assert.Null(LicenseKeyId.FromPssh(box.ToArray()));
    }

    [Fact]
    public void FromHeaderXml_ReadsBothHeaderVersions()
    {
        Assert.Equal(ContentKey.ToString("N"), LicenseKeyId.FromHeaderXml(HeaderV40(ContentKey)));
        Assert.Equal(ContentKey.ToString("N"), LicenseKeyId.FromHeaderXml(HeaderV41(ContentKey)));
        Assert.Equal(ContentKey.ToString("N"), LicenseKeyId.FromHeaderXml(HeaderV42(ContentKey)));
    }

    [Fact]
    public void FromHeaderXml_SkipsAnUnreadableKid_AndTakesTheNextOne()
    {
        string xml = $"""<WRMHEADER version="4.3.0.0"><DATA><PROTECTINFO><KIDS><KID ALGID="AESCTR" VALUE="@@not-base64@@"></KID><KID ALGID="AESCTR" VALUE="{HeaderB64(SecondKey)}"></KID></KIDS></PROTECTINFO></DATA></WRMHEADER>""";
        Assert.Equal(SecondKey.ToString("N"), LicenseKeyId.FromHeaderXml(xml));
    }

    [Theory]
    [InlineData("""<WRMHEADER version="4.0.0.0"><DATA><KEYLEN>16</KEYLEN></DATA></WRMHEADER>""")]   // no KID at all
    [InlineData("""<WRMHEADER version="4.0.0.0"><DATA><KID></KID></DATA></WRMHEADER>""")]           // empty KID
    [InlineData("""<WRMHEADER version="4.0.0.0"><DATA><KID>AAAA</KID></DATA></WRMHEADER>""")]       // not 16 bytes
    [InlineData("""<WRMHEADER version="4.2.0.0"><DATA><KIDS></KIDS></DATA></WRMHEADER>""")]         // <KIDS> is not <KID>
    [InlineData("<WRMHEADER><DATA><KID")]                                                             // truncated tag
    public void FromHeaderXml_NoUsableKid_ReturnsNull(string xml)
        => Assert.Null(LicenseKeyId.FromHeaderXml(xml));

    // ── ProtectedVideoSession.KeyIdFor (the cache key a request resolves to) ─────────────────────────────────────────

    [Fact]
    public void KeyIdFor_NormalizesTheDeclaredKid()
    {
        var request = new ProtectedVideoRequest { DefaultKid = "4060A865-8878-4267-9CBF-91AE5BAE1E72" };
        Assert.Equal("4060a865887842679cbf91ae5bae1e72", ProtectedVideoSession.KeyIdFor(request));
    }

    [Fact]
    public void KeyIdFor_FallsBackToThePsshKid_ThenNull()
    {
        byte[] box = PsshBox(0, PlayReadySystemId, Array.Empty<byte[]>(), PlayReadyObject(HeaderV40(ContentKey)));
        Assert.Equal(ContentKey.ToString("N"), ProtectedVideoSession.KeyIdFor(new ProtectedVideoRequest { Pssh = box }));
        Assert.Null(ProtectedVideoSession.KeyIdFor(new ProtectedVideoRequest()));
    }

    [Fact]
    public void KeyIdFor_TheDeclaredKidWinsOverThePssh()
    {
        byte[] box = PsshBox(0, PlayReadySystemId, Array.Empty<byte[]>(), PlayReadyObject(HeaderV40(ContentKey)));
        var request = new ProtectedVideoRequest { Pssh = box, DefaultKid = SecondKey.ToString("D") };
        Assert.Equal(SecondKey.ToString("N"), ProtectedVideoSession.KeyIdFor(request));
    }
}
