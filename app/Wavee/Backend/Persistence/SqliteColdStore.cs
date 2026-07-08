using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Channels = System.Threading.Channels;   // alias: 'Channel' alone collides with Wavee.Backend.Channel (transport enum)
using Microsoft.Data.Sqlite;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Playlists;

namespace Wavee.Backend.Persistence;

// The SQLite cold tier. WAL mode + a single-reader WRITE-BEHIND queue that batches upserts into transactions, so the
// caller (UI thread) never blocks on disk. Disk reads happen ONLY at startup (LoadAll bulk into the in-memory tier-1).
//
// Schema is versioned through meta(schema_version) + an ordered migration runner that runs once on open, BEFORE the writer
// task starts (so it never races the queue). The account column is carried on every per-account table for the (deferred)
// per-account-DB-file split; until then a single file holds one logical account (DefaultAccount).
public sealed class SqliteColdStore : IColdStore, IMutationOutbox
{
    public const string DefaultAccount = "default";

    readonly SqliteConnection _conn;
    readonly object _connLock = new();
    readonly string _account;
    readonly Channels.Channel<WriteOp> _queue = Channels.Channel.CreateUnbounded<WriteOp>(new Channels.UnboundedChannelOptions { SingleReader = true });
    readonly Task _writer;

    // Prepared once, reused across batches: Microsoft.Data.Sqlite has no cross-command statement cache, so rebuilding the
    // commands + parameters every drain re-compiles statements and allocates per batch (and the steady-state drain often
    // processes a batch of 1).
    SqliteCommand? _entityCmd, _savedUpCmd, _savedDelCmd, _revCmd, _videoCmd;
    SqliteParameter _eu = null!, _ek = null!, _ep = null!;
    SqliteParameter _vu = null!, _vp = null!;
    SqliteParameter _sa = null!, _ss = null!, _su = null!, _sy = null!, _st = null!;
    SqliteParameter _da = null!, _ds = null!, _du = null!;
    SqliteParameter _ra = null!, _rs = null!, _rr = null!, _rt = null!;

    public SqliteColdStore(string path) : this(path, DefaultAccount) { }

    public SqliteColdStore(string path, string account)
    {
        _account = account;
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("CREATE TABLE IF NOT EXISTS entities(uri TEXT PRIMARY KEY, kind INTEGER NOT NULL, payload BLOB NOT NULL);");
        // Video↔audio associations: own table (shares the track uri with `entities`, so it can't reuse that PK).
        Exec("CREATE TABLE IF NOT EXISTS video_assoc(uri TEXT PRIMARY KEY, payload BLOB NOT NULL);");
        Exec("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);");
        Exec("CREATE TABLE IF NOT EXISTS collection_items(account TEXT NOT NULL, set_id TEXT NOT NULL, item_uri TEXT NOT NULL, " +
             "added_at INTEGER NOT NULL DEFAULT 0, position INTEGER, sync INTEGER NOT NULL, PRIMARY KEY(account, set_id, item_uri));");
        Exec("CREATE INDEX IF NOT EXISTS ix_collection_added ON collection_items(account, set_id, added_at);");
        Exec("CREATE TABLE IF NOT EXISTS collection_rev(account TEXT NOT NULL, set_id TEXT NOT NULL, revision TEXT, synced_at INTEGER, PRIMARY KEY(account, set_id));");
        // Ordered playlists: the header lives in `entities` (kind=Playlist, thin); `playlists` carries only the opaque
        // revision; `playlist_items` is the ordered membership. (No header columns here → no duplication with the entity.)
        Exec("CREATE TABLE IF NOT EXISTS playlists(uri TEXT PRIMARY KEY, base_rev BLOB);");
        Exec("CREATE TABLE IF NOT EXISTS playlist_items(playlist_uri TEXT NOT NULL, position INTEGER NOT NULL, item_id TEXT, " +
             "item_uri TEXT NOT NULL, added_by TEXT, added_at INTEGER, PRIMARY KEY(playlist_uri, position));");
        Exec("CREATE TABLE IF NOT EXISTS rootlist(account TEXT NOT NULL, position INTEGER NOT NULL, kind INTEGER, uri TEXT, group_name TEXT, depth INTEGER, PRIMARY KEY(account, position));");
        // Durable mutation outbox: pending intents survive a restart. `op` holds the wire ListChanges body for oprebase edits.
        Exec("CREATE TABLE IF NOT EXISTS outbox(id INTEGER PRIMARY KEY, type TEXT NOT NULL, entity_key TEXT NOT NULL, set_id TEXT, target_saved INTEGER, op BLOB, base_rev BLOB, attempts INTEGER NOT NULL DEFAULT 0);");
        Exec("CREATE TABLE IF NOT EXISTS dead_letter(id INTEGER PRIMARY KEY, type TEXT, entity_key TEXT, reason TEXT, created_at INTEGER);");
        Migrate();
        _writer = Task.Run(WriteLoopAsync);
    }

    public string Account => _account;

    void Exec(string sql)
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }
    }

    // Ordered, one-time schema migrations. Runs in the ctor before the writer starts, so there is no queue contention.
    void Migrate()
    {
        lock (_connLock)
        {
            string? ver;
            using (var c = _conn.CreateCommand()) { c.CommandText = "SELECT value FROM meta WHERE key='schema_version';"; ver = c.ExecuteScalar() as string; }
            if (ver is null)
            {

            using var tx = _conn.BeginTransaction();
            // v0 → v1: fold a legacy `saved(setid,uri,sync)` table into collection_items, then drop it.
            bool legacySaved;
            using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='saved';"; legacySaved = c.ExecuteScalar() is not null; }
            if (legacySaved)
            {
                using (var c = _conn.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = "INSERT OR IGNORE INTO collection_items(account,set_id,item_uri,added_at,position,sync) SELECT $a,setid,uri,0,NULL,sync FROM saved;";
                    c.Parameters.AddWithValue("$a", _account);
                    c.ExecuteNonQuery();
                }
                using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DROP TABLE saved;"; c.ExecuteNonQuery(); }
            }
            using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','1');"; c.ExecuteNonQuery(); }
            tx.Commit();
            ver = "1";
            }

            if (ver == "1")
            {
                using var tx = _conn.BeginTransaction();
                using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "UPDATE playlists SET base_rev = NULL;"; c.ExecuteNonQuery(); }
                using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM meta WHERE key='rootlist_rev';"; c.ExecuteNonQuery(); }
                using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','2');"; c.ExecuteNonQuery(); }
                tx.Commit();
            }
        }
    }

    public IEnumerable<ColdEntity> LoadAllEntities()
    {
        var list = new List<ColdEntity>(4096);   // the app targets 10k+ entities — pre-size, skip the doubling-realloc chain
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT uri, kind, payload FROM entities;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ColdEntity(r.GetString(0), (EntityKind)r.GetInt32(1), r.GetFieldValue<byte[]>(2)));
        }
        return list;
    }

    public IEnumerable<ColdVideoAssoc> LoadAllVideoAssociations()
    {
        var list = new List<ColdVideoAssoc>(256);
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT uri, payload FROM video_assoc;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ColdVideoAssoc(r.GetString(0), r.GetFieldValue<byte[]>(1)));
        }
        return list;
    }

    public IEnumerable<ColdSaved> LoadAllSaved()
    {
        var list = new List<ColdSaved>(512);
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT set_id, item_uri, sync, added_at FROM collection_items WHERE account=$a;";
            c.Parameters.AddWithValue("$a", _account);
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ColdSaved(r.GetString(0), r.GetString(1), (SyncState)r.GetInt32(2), r.GetInt64(3)));
        }
        return list;
    }

    public string? GetCollectionRevision(string setId)
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT revision FROM collection_rev WHERE account=$a AND set_id=$s;";
            c.Parameters.AddWithValue("$a", _account);
            c.Parameters.AddWithValue("$s", setId);
            return c.ExecuteScalar() as string;   // null for no-row OR a NULL revision column
        }
    }

    // The rootlist revision lives in the shared meta(key,value) table as hex text under 'rootlist_rev' (no revision column
    // on the rootlist table). Synchronous, like ReplaceRootlist — a rootlist sync is a coarse op, not a hot per-item write.
    public byte[]? GetRootlistRevision()
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT value FROM meta WHERE key='rootlist_rev';";
            return c.ExecuteScalar() is string s && s.Length > 0 ? Convert.FromHexString(s) : null;
        }
    }

    public void SetRootlistRevision(byte[]? rev)
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            if (rev is null || rev.Length == 0)
                c.CommandText = "DELETE FROM meta WHERE key='rootlist_rev';";
            else
            {
                c.CommandText = "INSERT INTO meta(key,value) VALUES('rootlist_rev',$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
                c.Parameters.AddWithValue("$v", Convert.ToHexString(rev));
            }
            c.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ColdPlaylistItem> LoadMembership(string playlistUri)
    {
        var list = new List<ColdPlaylistItem>(64);
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT item_id, item_uri, added_by, added_at FROM playlist_items WHERE playlist_uri=$p ORDER BY position;";
            c.Parameters.AddWithValue("$p", playlistUri);
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ColdPlaylistItem(
                    r.IsDBNull(0) ? "" : r.GetString(0), r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? 0 : r.GetInt64(3)));
        }
        return list;
    }

    public byte[]? GetPlaylistRevision(string playlistUri)
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT base_rev FROM playlists WHERE uri=$p;";
            c.Parameters.AddWithValue("$p", playlistUri);
            return c.ExecuteScalar() as byte[];
        }
    }

    // Synchronous + atomic: a bulk membership replace is a coarse op (one playlist sync), not a hot per-item write, so it
    // runs in its own transaction rather than through the per-entity write-behind queue. Delete-all + reinsert + bump rev,
    // all-or-nothing, so a torn write can never leave a half-applied membership.
    public void ReplaceMembership(string playlistUri, IReadOnlyList<ColdPlaylistItem> rows, byte[]? baseRev)
    {
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM playlist_items WHERE playlist_uri=$p;"; del.Parameters.AddWithValue("$p", playlistUri); del.ExecuteNonQuery(); }
            using (var ins = _conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO playlist_items(playlist_uri,position,item_id,item_uri,added_by,added_at) VALUES($p,$pos,$id,$u,$by,$at);";
                var pp = ins.Parameters.Add("$p", SqliteType.Text); pp.Value = playlistUri;
                var ppos = ins.Parameters.Add("$pos", SqliteType.Integer);
                var pid = ins.Parameters.Add("$id", SqliteType.Text);
                var pu = ins.Parameters.Add("$u", SqliteType.Text);
                var pby = ins.Parameters.Add("$by", SqliteType.Text);
                var pat = ins.Parameters.Add("$at", SqliteType.Integer);
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    ppos.Value = i;
                    pid.Value = string.IsNullOrEmpty(row.ItemId) ? DBNull.Value : row.ItemId;
                    pu.Value = row.ItemUri;
                    pby.Value = (object?)row.AddedBy ?? DBNull.Value;
                    pat.Value = row.AddedAt;
                    ins.ExecuteNonQuery();
                }
            }
            using (var rev = _conn.CreateCommand())
            {
                rev.Transaction = tx;
                rev.CommandText = "INSERT INTO playlists(uri,base_rev) VALUES($p,$r) ON CONFLICT(uri) DO UPDATE SET base_rev=excluded.base_rev;";
                rev.Parameters.AddWithValue("$p", playlistUri);
                rev.Parameters.AddWithValue("$r", (object?)baseRev ?? DBNull.Value);
                rev.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public IReadOnlyList<ColdRootlistEntry> LoadRootlist()
    {
        var list = new List<ColdRootlistEntry>(64);
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT position, kind, uri, group_name, depth FROM rootlist WHERE account=$a ORDER BY position;";
            c.Parameters.AddWithValue("$a", _account);
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ColdRootlistEntry(
                    r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1), r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? 0 : r.GetInt32(4)));
        }
        return list;
    }

    public void ReplaceRootlist(IReadOnlyList<ColdRootlistEntry> entries)
    {
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM rootlist WHERE account=$a;"; del.Parameters.AddWithValue("$a", _account); del.ExecuteNonQuery(); }
            using (var ins = _conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO rootlist(account,position,kind,uri,group_name,depth) VALUES($a,$pos,$k,$u,$g,$d);";
                var pa = ins.Parameters.Add("$a", SqliteType.Text); pa.Value = _account;
                var ppos = ins.Parameters.Add("$pos", SqliteType.Integer);
                var pk = ins.Parameters.Add("$k", SqliteType.Integer);
                var pu = ins.Parameters.Add("$u", SqliteType.Text);
                var pg = ins.Parameters.Add("$g", SqliteType.Text);
                var pd = ins.Parameters.Add("$d", SqliteType.Integer);
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    ppos.Value = e.Position;
                    pk.Value = e.Kind;
                    pu.Value = e.Uri;
                    pg.Value = (object?)e.GroupName ?? DBNull.Value;
                    pd.Value = e.Depth;
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    public void UpsertEntity(string uri, EntityKind kind, byte[] payload) => _queue.Writer.TryWrite(WriteOp.Entity(uri, (int)kind, payload));
    public void UpsertVideoAssociation(string uri, byte[] payload) => _queue.Writer.TryWrite(WriteOp.VideoAssoc(uri, payload));
    public void UpsertSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs = 0) => _queue.Writer.TryWrite(WriteOp.Saved(setId, uri, saved, (int)sync, addedAtMs));
    public void SetCollectionRevision(string setId, string? revision, long syncedAt) => _queue.Writer.TryWrite(WriteOp.Revision(setId, revision, syncedAt));

    public void Flush()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_queue.Writer.TryWrite(WriteOp.FlushMarker(done))) done.Task.Wait();
    }

    async Task WriteLoopAsync()
    {
        var batch = new List<WriteOp>(512);
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < 2000 && _queue.Reader.TryRead(out var op)) batch.Add(op);
            try { WriteBatch(batch); }
            catch { /* a cache-write failure is non-fatal: the data is in memory + re-fetchable */ }
            foreach (var op in batch) op.Done?.SetResult();   // complete any flush markers (after commit)
        }
    }

    void EnsureCommands()
    {
        if (_entityCmd != null) return;
        _entityCmd = _conn.CreateCommand();
        _entityCmd.CommandText = "INSERT INTO entities(uri,kind,payload) VALUES($u,$k,$p) ON CONFLICT(uri) DO UPDATE SET kind=excluded.kind, payload=excluded.payload;";
        _eu = _entityCmd.Parameters.Add("$u", SqliteType.Text);
        _ek = _entityCmd.Parameters.Add("$k", SqliteType.Integer);
        _ep = _entityCmd.Parameters.Add("$p", SqliteType.Blob);

        _videoCmd = _conn.CreateCommand();
        _videoCmd.CommandText = "INSERT INTO video_assoc(uri,payload) VALUES($u,$p) ON CONFLICT(uri) DO UPDATE SET payload=excluded.payload;";
        _vu = _videoCmd.Parameters.Add("$u", SqliteType.Text);
        _vp = _videoCmd.Parameters.Add("$p", SqliteType.Blob);

        _savedUpCmd = _conn.CreateCommand();
        // added_at: a non-zero incoming timestamp wins; 0 preserves whatever is stored (the optimistic/fold writers don't
        // know the server timestamp — the delta/paging apply does).
        _savedUpCmd.CommandText = "INSERT INTO collection_items(account,set_id,item_uri,added_at,position,sync) VALUES($a,$s,$u,$t,NULL,$y) " +
                                  "ON CONFLICT(account,set_id,item_uri) DO UPDATE SET sync=excluded.sync, " +
                                  "added_at=CASE WHEN excluded.added_at!=0 THEN excluded.added_at ELSE collection_items.added_at END;";
        _sa = _savedUpCmd.Parameters.Add("$a", SqliteType.Text);
        _ss = _savedUpCmd.Parameters.Add("$s", SqliteType.Text);
        _su = _savedUpCmd.Parameters.Add("$u", SqliteType.Text);
        _sy = _savedUpCmd.Parameters.Add("$y", SqliteType.Integer);
        _st = _savedUpCmd.Parameters.Add("$t", SqliteType.Integer);

        _savedDelCmd = _conn.CreateCommand();
        _savedDelCmd.CommandText = "DELETE FROM collection_items WHERE account=$a AND set_id=$s AND item_uri=$u;";
        _da = _savedDelCmd.Parameters.Add("$a", SqliteType.Text);
        _ds = _savedDelCmd.Parameters.Add("$s", SqliteType.Text);
        _du = _savedDelCmd.Parameters.Add("$u", SqliteType.Text);

        _revCmd = _conn.CreateCommand();
        _revCmd.CommandText = "INSERT INTO collection_rev(account,set_id,revision,synced_at) VALUES($a,$s,$r,$t) " +
                              "ON CONFLICT(account,set_id) DO UPDATE SET revision=excluded.revision, synced_at=excluded.synced_at;";
        _ra = _revCmd.Parameters.Add("$a", SqliteType.Text);
        _rs = _revCmd.Parameters.Add("$s", SqliteType.Text);
        _rr = _revCmd.Parameters.Add("$r", SqliteType.Text);
        _rt = _revCmd.Parameters.Add("$t", SqliteType.Integer);
    }

    void WriteBatch(List<WriteOp> batch)
    {
        lock (_connLock)
        {
            EnsureCommands();
            using var tx = _conn.BeginTransaction();
            _entityCmd!.Transaction = tx;
            _videoCmd!.Transaction = tx;
            _savedUpCmd!.Transaction = tx;
            _savedDelCmd!.Transaction = tx;
            _revCmd!.Transaction = tx;
            foreach (var op in batch)
            {
                switch (op.Op)
                {
                    case OpKind.Entity: _eu.Value = op.A; _ek.Value = op.Kind; _ep.Value = op.Payload!; _entityCmd.ExecuteNonQuery(); break;
                    case OpKind.VideoAssoc: _vu.Value = op.A; _vp.Value = op.Payload!; _videoCmd.ExecuteNonQuery(); break;
                    case OpKind.SavedSet: _sa.Value = _account; _ss.Value = op.A; _su.Value = op.B!; _sy.Value = op.Kind; _st.Value = op.L; _savedUpCmd.ExecuteNonQuery(); break;
                    case OpKind.SavedRemove: _da.Value = _account; _ds.Value = op.A; _du.Value = op.B!; _savedDelCmd.ExecuteNonQuery(); break;
                    case OpKind.Revision: _ra.Value = _account; _rs.Value = op.A; _rr.Value = (object?)op.B ?? DBNull.Value; _rt.Value = op.L; _revCmd.ExecuteNonQuery(); break;
                    case OpKind.Flush: break;
                }
            }
            tx.Commit();
        }
    }

    // ── IMutationOutbox (durable, synchronous — pending intents must be on disk before the call returns) ──
    public IReadOnlyList<OutboxOp> Load()
    {
        var list = new List<OutboxOp>();
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT id, type, entity_key, set_id, target_saved, op, base_rev, attempts FROM outbox ORDER BY id;";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                long id = r.GetInt64(0);
                string type = r.GetString(1);
                string entityKey = r.GetString(2);
                string setId = r.IsDBNull(3) ? "" : r.GetString(3);
                bool targetSaved = !r.IsDBNull(4) && r.GetInt64(4) != 0;
                byte[]? opBlob = r.IsDBNull(5) ? null : r.GetFieldValue<byte[]>(5);
                byte[]? baseRev = r.IsDBNull(6) ? null : r.GetFieldValue<byte[]>(6);
                int attempts = r.IsDBNull(7) ? 0 : r.GetInt32(7);
                IReadOnlyList<PlaylistOp>? ops = null;
                if (type == "oprebase" && opBlob is not null)
                {
                    var parsed = PlaylistWireMapper.ParseChanges(opBlob);
                    ops = parsed.Ops;
                    baseRev ??= parsed.BaseRev;
                }
                list.Add(new OutboxOp(id, type, entityKey, setId, targetSaved, id, attempts, ops, baseRev));
            }
        }
        return list;
    }

    public void Save(OutboxOp op)
    {
        byte[]? opBlob = op.Type == "oprebase" && op.Ops is not null ? PlaylistWireMapper.BuildChanges(op.BaseRev, op.Ops) : null;
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "INSERT INTO outbox(id,type,entity_key,set_id,target_saved,op,base_rev,attempts) VALUES($i,$t,$e,$s,$ts,$op,$br,$a) " +
                            "ON CONFLICT(id) DO UPDATE SET attempts=excluded.attempts, target_saved=excluded.target_saved, op=excluded.op, base_rev=excluded.base_rev;";
            c.Parameters.AddWithValue("$i", op.Id);
            c.Parameters.AddWithValue("$t", op.Type);
            c.Parameters.AddWithValue("$e", op.EntityKey);
            c.Parameters.AddWithValue("$s", op.SetId);
            c.Parameters.AddWithValue("$ts", op.TargetSaved ? 1 : 0);
            c.Parameters.AddWithValue("$op", (object?)opBlob ?? DBNull.Value);
            c.Parameters.AddWithValue("$br", (object?)op.BaseRev ?? DBNull.Value);
            c.Parameters.AddWithValue("$a", op.Attempts);
            c.ExecuteNonQuery();
        }
    }

    public void Remove(long id)
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "DELETE FROM outbox WHERE id=$i;";
            c.Parameters.AddWithValue("$i", id);
            c.ExecuteNonQuery();
        }
    }

    public void DeadLetter(OutboxOp op, string reason)
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "INSERT OR REPLACE INTO dead_letter(id,type,entity_key,reason,created_at) VALUES($i,$t,$e,$r,0);";
            c.Parameters.AddWithValue("$i", op.Id);
            c.Parameters.AddWithValue("$t", op.Type);
            c.Parameters.AddWithValue("$e", op.EntityKey);
            c.Parameters.AddWithValue("$r", reason);
            c.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();   // no more writes — the writer drains the backlog then exits
        bool drained;
        try { drained = _writer.Wait(TimeSpan.FromSeconds(30)); } catch { drained = true; }   // faulted → already stopped
        // If the writer is STILL running (pathological backlog / stuck), do NOT dispose the connection + commands under it —
        // a mid-ExecuteNonQuery dispose corrupts/crashes. Leaking on shutdown is the safer choice (the process is exiting).
        if (!drained) return;
        _entityCmd?.Dispose();
        _videoCmd?.Dispose();
        _savedUpCmd?.Dispose();
        _savedDelCmd?.Dispose();
        _revCmd?.Dispose();
        _conn.Dispose();
    }

    enum OpKind : byte { Entity, VideoAssoc, SavedSet, SavedRemove, Revision, Flush }

    readonly struct WriteOp
    {
        public readonly OpKind Op;
        public readonly string A;            // entity uri, or set_id
        public readonly string? B;           // saved item_uri, or the revision token (nullable)
        public readonly int Kind;            // EntityKind, or SyncState
        public readonly long L;              // revision synced_at
        public readonly byte[]? Payload;
        public readonly TaskCompletionSource? Done;

        WriteOp(OpKind op, string a, string? b, int kind, long l, byte[]? payload, TaskCompletionSource? done)
        { Op = op; A = a; B = b; Kind = kind; L = l; Payload = payload; Done = done; }

        public static WriteOp Entity(string uri, int kind, byte[] payload) => new(OpKind.Entity, uri, null, kind, 0, payload, null);
        public static WriteOp VideoAssoc(string uri, byte[] payload) => new(OpKind.VideoAssoc, uri, null, 0, 0, payload, null);
        public static WriteOp Saved(string set, string uri, bool saved, int sync, long addedAtMs = 0) => new(saved ? OpKind.SavedSet : OpKind.SavedRemove, set, uri, sync, addedAtMs, null, null);
        public static WriteOp Revision(string setId, string? revision, long syncedAt) => new(OpKind.Revision, setId, revision, 0, syncedAt, null, null);
        public static WriteOp FlushMarker(TaskCompletionSource done) => new(OpKind.Flush, "", null, 0, 0, null, done);
    }
}
