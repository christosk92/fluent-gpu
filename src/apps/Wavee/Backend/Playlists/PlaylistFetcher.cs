using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Playlists;

/// <summary>How a revision-gated <c>/diff</c> revalidation resolved (§2.6): ops applied in place / already current /
/// fell back to a full re-fetch (no baseline, stale revision (509), torn apply, or an unparseable response).</summary>
public enum DiffOutcome { Applied, UpToDate, FellBackToFull }

// ── The live membership fetch (SpotifyLive boundary, but Backend so the orchestration is unit-tested) ─────────────────
// GETs /playlist/v2/{path}?decorate=... → SelectedListContent, projects a THIN playlist header + the ordered membership
// into the Store, and hands the membership uris to a hydrate delegate (the facade at Identity) to fill the shared entities.
// The same path serves a playlist and the rootlist (the rootlist is just a playlist of playlist-uri + group markers).
public sealed class PlaylistFetcher
{
    const string Decorate = "?decorate=revision,attributes,length,owner,capabilities,picture";
    const string DecorateRevisionOnly = "?decorate=revision";

    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly Func<string> _account;
    readonly IStore _store;
    readonly Func<IReadOnlyList<string>, CancellationToken, Task> _hydrate;

    public PlaylistFetcher(IHttpExchange http, Func<string> baseUrl, IStore store, Func<IReadOnlyList<string>, CancellationToken, Task> hydrate, Func<string> account)
    {
        _http = http;
        _baseUrl = baseUrl;
        _store = store;
        _hydrate = hydrate;
        _account = account;
    }

    public async Task FetchPlaylistAsync(string playlistUri, CancellationToken ct = default)
    {
        var slc = await GetAsync(playlistUri, ct).ConfigureAwait(false);
        var members = AdoptSnapshot(playlistUri, slc);
        await HydrateAsync(members, ct).ConfigureAwait(false);
        _store.Bump(playlistUri);
    }

    /// <summary>Fetch + store ONLY a playlist's header (name / cover / owner / count) — no membership, no track hydration.
    /// Populates the rootlist playlists' names + covers for the home + sidebar without pulling every playlist's tracks.</summary>
    public async Task FetchPlaylistHeaderAsync(string playlistUri, CancellationToken ct = default)
    {
        var slc = await GetAsync(playlistUri, ct).ConfigureAwait(false);
        if (slc.Attributes is { } attr) _store.UpsertPlaylist(HeaderOf(playlistUri, attr, slc));
    }

    /// <summary>The playlist HEAD read: GET <c>/playlist/v2/{path}?decorate=revision</c> → the current playlist4 base
    /// revision (null when the server sends none). The cheapest authoritative answer to "has this playlist's content
    /// rolled over", and the only one available to a surface whose own transport carries no revision — Pathfinder
    /// <c>home</c> is GraphQL and exposes no playlist4 field, so the Home daylist hero has to ask here. Same
    /// decorate-revision idiom the rootlist bootstrap already uses (<c>RootlistOps.BootstrapRootlistAsync</c>).
    /// <para>Writes NOTHING to the store, deliberately: a head read must not be able to clobber a header, a membership
    /// baseline, or the revision the sync loop owns. The caller compares the answer itself.</para></summary>
    public async Task<byte[]?> FetchPlaylistRevisionAsync(string playlistUri, CancellationToken ct = default)
    {
        var url = _baseUrl() + "/playlist/v2/" + PathOf(playlistUri) + DecorateRevisionOnly;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/protobuf" };
        using var resp = await _http.SendAsync(new HttpReq("GET", url, headers, null), ct).ConfigureAwait(false);
        if (resp.Status != 200) throw new InvalidOperationException($"playlist revision fetch failed ({resp.Status}) for {playlistUri}");
        var slc = Pl.SelectedListContent.Parser.ParseFrom(resp.Body);
        return slc.HasRevision ? slc.Revision.ToByteArray() : null;
    }

    public async Task FetchRootlistAsync(string rootlistUri, CancellationToken ct = default)
    {
        var slc = await GetAsync(rootlistUri, ct).ConfigureAwait(false);
        var uris = new List<string>();
        var timestamps = new List<long>();
        if (slc.Contents is { } contents)
            foreach (var item in contents.Items)
            {
                uris.Add(item.Uri);
                // The ADD timestamp is what a folder rename has to resend verbatim (golden b037) — capture it here or
                // the rename has nothing but "now" to send.
                timestamps.Add(item.Attributes is { HasTimestamp: true } a ? a.Timestamp : 0);
            }
        // the flat-marker parse lives once in RootlistTreeBuilder (shared with LibrarySync + RootlistFollowStrategy).
        var entries = RootlistTreeBuilder.EntriesFromUris(uris, timestamps);
        var rev = slc.HasRevision ? slc.Revision.ToByteArray() : null;
        // I1 — the rootlist revision is stored ONLY when it is the 24-byte head (§2.6); anything else keeps the value we
        // already trust (the 1-arg overload) so a malformed head can never become the base of the next write.
        if (PlaylistRevisions.IsWellFormed(rev)) _store.SetRootlist(entries, rev);
        else
        {
            if (rev is not null) PlaylistMutationDiagnostics.RootlistBadRevision(rev.Length, "rootlist-fetch");
            _store.SetRootlist(entries);
        }
    }

    /// <summary>Revision-gated revalidation via GET <c>/playlist/v2/{path}/diff</c> (§2.6, fixes RC5): a resident,
    /// unchanged playlist costs one up-to-date round-trip (or a 304); a changed one applies ONLY the server's ops onto the
    /// resident baseline and hydrates ONLY the added uris. No baseline / no stored revision / a stale revision (509) /
    /// a torn apply / an unparseable body all fall back to the full <see cref="FetchPlaylistAsync"/> — always converges.</summary>
    public async Task<DiffOutcome> FetchPlaylistDiffAsync(string playlistUri, CancellationToken ct = default)
    {
        var rev = _store.PlaylistRevision(playlistUri);
        var baseline = _store.Membership(playlistUri);
        if (rev is null || rev.Length < 5 || baseline.Count == 0)   // rev = 4B counter + hash; nothing to gate on → full
        {
            await FetchPlaylistAsync(playlistUri, ct).ConfigureAwait(false);
            return DiffOutcome.FellBackToFull;
        }

        // revision wire string "counter,hexhash" — the comma MUST be %2C-encoded or the gateway 509s (§2.6).
        var enc = Uri.EscapeDataString(FormatRevision(rev));
        var url = _baseUrl() + "/playlist/v2/" + PathOf(playlistUri) + "/diff?revision=" + enc + "&handlesContent=&hint_revision=" + enc;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/protobuf" };
        byte[] body;
        int status;
        using (var resp = await _http.SendAsync(new HttpReq("GET", url, headers, null), ct).ConfigureAwait(false))
        {
            status = resp.Status;
            if (status == 304) return DiffOutcome.UpToDate;   // Not Modified = our revision is current
            if (status != 200)                                // 509 (revision too stale — editorial mixes) or anything else
            {
                await FetchPlaylistAsync(playlistUri, ct).ConfigureAwait(false);
                return DiffOutcome.FellBackToFull;
            }
            using var ms = new MemoryStream();
            await resp.Body.CopyToAsync(ms, ct).ConfigureAwait(false);   // diff bodies are small — buffer for the zstd sniff
            body = ms.ToArray();
        }

        Pl.SelectedListContent slc;
        try { slc = Pl.SelectedListContent.Parser.ParseFrom(SpotifyZstd.MaybeDecompressZstd(body)); }
        catch
        {
            await FetchPlaylistAsync(playlistUri, ct).ConfigureAwait(false);
            return DiffOutcome.FellBackToFull;
        }

        if (slc.HasUpToDate && slc.UpToDate) return DiffOutcome.UpToDate;

        if (slc.Diff is { } diff)
        {
            var list = new List<PlaylistMember>(baseline);
            var before = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < baseline.Count; i++) before.Add(baseline[i].ItemUri);
            IReadOnlyList<PlaylistOp> mappedOps;
            // Torn apply — the resident baseline drifted, or the diff carries an op shape this client cannot express.
            // Either way a full re-fetch converges.
            try
            {
                mappedOps = PlaylistWireMapper.MapOps(diff.Ops);
                PlaylistDiffApplier.Apply(list, mappedOps);
            }
            catch (ArgumentOutOfRangeException)
            {
                await FetchPlaylistAsync(playlistUri, ct).ConfigureAwait(false);
                return DiffOutcome.FellBackToFull;
            }
            _store.SetMembership(playlistUri, list,
                StorableRevision(playlistUri, diff.HasToRevision ? diff.ToRevision.ToByteArray() : rev, "diff"));
            var added = new List<string>();
            for (int i = 0; i < list.Count; i++) { var u = list[i].ItemUri; if (!before.Contains(u)) added.Add(u); }
            if (added.Count > 0) { await HydrateUrisAsync(added, ct).ConfigureAwait(false); _store.Bump(playlistUri); }
            if (ContainsUpdateList(mappedOps))
            {
                try { await FetchPlaylistHeaderAsync(playlistUri, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }
            return DiffOutcome.Applied;
        }

        if (slc.Contents is not null)   // some responses carry the full contents instead of ops — treat as a full refresh
        {
            var members = AdoptSnapshot(playlistUri, slc, rev);
            await HydrateAsync(members, ct).ConfigureAwait(false);
            _store.Bump(playlistUri);
            return DiffOutcome.FellBackToFull;
        }

        return DiffOutcome.UpToDate;   // 200 with nothing actionable — nothing changed that we can see
    }

    /// <summary>The playlist4 revision wire string: 4-byte big-endian counter + the remaining bytes as lowercase hex,
    /// joined with a comma (percent-encode when it rides a query string).</summary>
    internal static string FormatRevision(byte[] rev)
        => BinaryPrimitives.ReadInt32BigEndian(rev.AsSpan(0, 4)) + "," + Convert.ToHexStringLower(rev.AsSpan(4));

    /// <summary>Hydrate the entities behind a specific uri list (the LibrarySync in-place-apply path fills ONLY the added
    /// track/episode uris without a full re-fetch). Non-track/episode uris are skipped, mirroring <see cref="HydrateAsync"/>.</summary>
    public async Task HydrateUrisAsync(IReadOnlyList<string> uris, CancellationToken ct = default)
    {
        var filtered = new List<string>(uris.Count);
        for (int i = 0; i < uris.Count; i++)
        {
            var u = uris[i];
            if (EntityUri.KindOf(u) is EntityKind.Track or EntityKind.Episode) filtered.Add(u);
        }
        if (filtered.Count > 0) await _hydrate(filtered, ct).ConfigureAwait(false);
    }

    /// <summary>Atomically adopts a full playlist response. Revision, header, and membership land before metadata
    /// hydration, so a failed hydrator cannot roll back an accepted server mutation.</summary>
    public IReadOnlyList<PlaylistMember> AdoptSnapshot(
        string playlistUri,
        Pl.SelectedListContent slc,
        byte[]? fallbackRevision = null)
    {
        var (members, revision) = PlaylistWireMapper.ParseContents(slc);
        using (_store.BeginBulk())
        {
            if (slc.Attributes is { } attr) _store.UpsertPlaylist(HeaderOf(playlistUri, attr, slc));
            _store.SetMembership(playlistUri, members, StorableRevision(playlistUri, revision ?? fallbackRevision, "full-get"));
            _store.Bump(playlistUri);
        }
        return members;
    }

    /// <summary>I1 — the gate every membership-revision write passes through. A candidate that is not the 24-byte
    /// playlist4 head is refused (with a logged reason) and the revision we already trust is kept: adopting a malformed
    /// head would make it the base of the next /changes POST and of every echo-suppression comparison.</summary>
    byte[]? StorableRevision(string playlistUri, byte[]? candidate, string source)
    {
        if (PlaylistRevisions.IsWellFormed(candidate)) return candidate;
        if (candidate is not null) PlaylistMutationDiagnostics.RootlistBadRevision(candidate.Length, source);
        return _store.PlaylistRevision(playlistUri);
    }

    /// <summary>Hydrates the current authoritative membership; safe to retry after another snapshot has landed.</summary>
    public Task HydrateMembershipAsync(string playlistUri, CancellationToken ct = default)
        => HydrateAsync(_store.Membership(playlistUri), ct);

    async Task<Pl.SelectedListContent> GetAsync(string uri, CancellationToken ct)
    {
        var url = _baseUrl() + "/playlist/v2/" + PathOf(uri) + Decorate;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/protobuf" };
        using var resp = await _http.SendAsync(new HttpReq("GET", url, headers, null), ct).ConfigureAwait(false);
        if (resp.Status != 200) throw new InvalidOperationException($"playlist fetch failed ({resp.Status}) for {uri}");
        return Pl.SelectedListContent.Parser.ParseFrom(resp.Body);   // stream-parse: a 10k-item body never lands on the LOH
    }

    async Task HydrateAsync(IReadOnlyList<PlaylistMember> members, CancellationToken ct)
    {
        var uris = new List<string>(members.Count);
        for (int i = 0; i < members.Count; i++)
        {
            var u = members[i].ItemUri;
            if (EntityUri.KindOf(u) is EntityKind.Track or EntityKind.Episode) uris.Add(u);
        }
        if (uris.Count > 0) await _hydrate(uris, ct).ConfigureAwait(false);
    }

    static bool ContainsUpdateList(IReadOnlyList<PlaylistOp> ops)
    {
        for (int i = 0; i < ops.Count; i++)
            if (ops[i].Kind == PlaylistOpKind.UpdateList) return true;
        return false;
    }

    // "spotify:playlist:abc" → "playlist/abc"; "spotify:user:bob:rootlist" → "user/bob/rootlist".
    static string PathOf(string uri) => uri.StartsWith("spotify:", StringComparison.Ordinal) ? uri.Substring(8).Replace(':', '/') : uri.Replace(':', '/');

    Playlist HeaderOf(string uri, Pl.ListAttributes attr, Pl.SelectedListContent slc)
    {
        string name = attr.HasName ? attr.Name : "";
        string? desc = attr.HasDescription ? attr.Description : null;
        string owner = slc.HasOwnerUsername ? slc.OwnerUsername : "";
        int len = slc.HasLength ? slc.Length : 0;
        // Seed a minimal owner chip from the header's owner username (id + name = the username, avatar null) so the owner
        // chip renders its NAME immediately on first paint instead of a bare monogram. This does NOT change owner
        // resolution: StoreLibrarySource.RawOwnerId already returned this same username (its OwnerName fallback), so the
        // UserProfileService overlay (StoreLibrarySource.OverlayOwner) still WINS — Get(raw) ?? header.Owner upgrades this
        // seed to the resolved display name + avatar; the null-avatar seed only fills the gap until the profile lands.
        Owner? ownerChip = owner.Length > 0 ? new Owner(owner, owner, null) : null;
        var daylist = DaylistWindowOf(attr);
        return new Playlist(EntityUri.IdOf(uri), uri, name, desc, owner, CoverOf(attr), len,
            Owner: ownerChip,
            Capabilities: CapabilitiesOf(attr, slc, owner),
            Format: attr.HasFormat ? attr.Format : null,
            Source: "spotify",
            Tuning: TuningOf(attr, slc),
            DaylistExpiresAtMs: daylist.ExpiresAtMs,
            DaylistCreatedAtMs: daylist.CreatedAtMs,
            // A full GET / diff of a playlist the owner deleted still answers 200 — with deleted_by_owner set. The sync
            // loop turns a header carrying this into the same eviction the dealer tombstone push takes.
            DeletedByOwner: attr.HasDeletedByOwner && attr.DeletedByOwner);
    }

    /// <summary>The daylist rollover window from the header's format_attributes — (expires, created) as unix ms,
    /// (0, 0) for every other format or when the keys are absent/unparsable. The Pathfinder home feed states these
    /// attributes as ISO-8601 instants; the playlist4 shape for this format is unpinned by any capture, so the parse
    /// accepts an epoch (seconds or ms) as well — whichever arrives, the same window comes out.</summary>
    internal static (long ExpiresAtMs, long CreatedAtMs) DaylistWindowOf(Pl.ListAttributes attr)
    {
        if (!attr.HasFormat || !string.Equals(attr.Format, "daylist", StringComparison.Ordinal)) return (0, 0);
        long expires = 0, created = 0;
        for (int i = 0; i < attr.FormatAttributes.Count; i++)
        {
            var item = attr.FormatAttributes[i];
            if (!item.HasKey || !item.HasValue) continue;
            if (item.Key == "expires") expires = InstantMs(item.Value);
            else if (item.Key == "created") created = InstantMs(item.Value);
        }
        return (expires, created);
    }

    /// <summary>Epoch seconds / epoch ms / ISO-8601 → unix ms; 0 when unparsable.</summary>
    internal static long InstantMs(string value)
    {
        if (value.Length == 0) return 0;
        if (long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long n))
            return n <= 0 ? 0 : n < 100_000_000_000L ? n * 1000L : n;   // 11+ digits ⇒ already ms
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToUnixTimeMilliseconds() : 0;
    }

    internal static PlaylistTuning? TuningOf(Pl.ListAttributes attr, Pl.SelectedListContent slc)
    {
        if (!slc.HasRevision || slc.Revision.Length != 24 || slc.Contents is not { } contents
            || contents.AvailableSignals.Count == 0) return null;

        var format = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < attr.FormatAttributes.Count; i++)
        {
            var item = attr.FormatAttributes[i];
            if (item.HasKey && item.HasValue) format[item.Key] = item.Value;
        }
        format.TryGetValue("session_control.selected_signals", out var selected);
        if (string.IsNullOrWhiteSpace(selected)) selected = null;

        var options = new List<PlaylistTuningOption>(contents.AvailableSignals.Count);
        for (int i = 0; i < contents.AvailableSignals.Count; i++)
        {
            var signal = contents.AvailableSignals[i];
            if (!signal.HasIdentifier || string.IsNullOrWhiteSpace(signal.Identifier)) continue;
            string id = signal.Identifier;
            var kind = string.Equals(id, "session-control-reset", StringComparison.Ordinal)
                ? PlaylistTuningOptionKind.Reset : PlaylistTuningOptionKind.Choice;
            string? label = null;
            int split = id.LastIndexOf('$');
            if (kind == PlaylistTuningOptionKind.Choice && split >= 0 && split + 1 < id.Length)
                format.TryGetValue("session_control_display.displayName." + id[(split + 1)..], out label);
            options.Add(new PlaylistTuningOption(id, string.IsNullOrWhiteSpace(label) ? null : label, kind));
        }
        return options.Count == 0
            ? null
            : new PlaylistTuning(slc.Revision.ToByteArray(), options, selected);
    }

    PlaylistCapabilities CapabilitiesOf(Pl.ListAttributes attr, Pl.SelectedListContent slc, string ownerUsername)
    {
        var cap = slc.Capabilities;
        string account = _account();
        // Compare bare ids: either side may arrive as "spotify:user:<id>" or the bare canonical username.
        bool isOwner = ownerUsername.Length > 0 && account.Length > 0 && string.Equals(
            Wavee.Core.UserProfileIds.BareId(ownerUsername), Wavee.Core.UserProfileIds.BareId(account),
            StringComparison.OrdinalIgnoreCase);
        bool canAdmin = cap?.CanAdministratePermissions ?? false;
        // Server admin flag is authoritative; username match is the fallback when the decorate payload omits it.
        bool effectiveOwner = isOwner || canAdmin;
        return new PlaylistCapabilities(
            CanView: cap?.CanView ?? false,
            CanEditItems: cap?.CanEditItems ?? false,
            CanEditMetadata: cap?.CanEditMetadata ?? false,
            IsCollaborative: attr.HasCollaborative && attr.Collaborative,
            IsOwner: effectiveOwner,
            CanAdministratePermissions: canAdmin || isOwner);
    }

    // The playlist cover: the server's pre-sized URLs first (largest), else the raw picture file id → the image CDN.
    static Image? CoverOf(Pl.ListAttributes attr)
    {
        for (int i = attr.PictureSize.Count - 1; i >= 0; i--)
            if (!string.IsNullOrEmpty(attr.PictureSize[i].Url)) return new Image(attr.PictureSize[i].Url);
        if (attr.Picture.Length > 0) return new Image("https://i.scdn.co/image/" + Convert.ToHexStringLower(attr.Picture.Span));
        return null;
    }

}
