using System;
using System.Collections;
using System.Collections.Generic;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>
/// A minimal observable collection for the media surface (spec §6/§8): a backing list plus a <see cref="Version"/>
/// signal bumped on every structural mutation, so UI can bind to "the collection changed" without a per-item event
/// storm. Enumeration/indexing are plain; mutation is the single owner's job (the control thread).
/// </summary>
public sealed class ObservableList<T> : IReadOnlyList<T>
{
    private readonly List<T> _items;
    private readonly Signal<int> _version = new(0);

    /// <summary>Create an empty list.</summary>
    public ObservableList() => _items = new();
    /// <summary>Create a list with an initial capacity.</summary>
    public ObservableList(int capacity) => _items = new(capacity);

    /// <summary>Bumped on every structural change — bind to it to re-render on collection edits.</summary>
    public IReadSignal<int> Version => _version;
    /// <summary>The item count.</summary>
    public int Count => _items.Count;
    /// <summary>The item at <paramref name="index"/>.</summary>
    public T this[int index] => _items[index];

    /// <summary>Append an item.</summary>
    public void Add(T item) { _items.Add(item); Bump(); }
    /// <summary>Insert an item at <paramref name="index"/>.</summary>
    public void Insert(int index, T item) { _items.Insert(index, item); Bump(); }
    /// <summary>Remove an item; returns true if it was present.</summary>
    public bool Remove(T item) { if (_items.Remove(item)) { Bump(); return true; } return false; }
    /// <summary>Remove the item at <paramref name="index"/>.</summary>
    public void RemoveAt(int index) { _items.RemoveAt(index); Bump(); }
    /// <summary>Clear all items.</summary>
    public void Clear() { if (_items.Count == 0) return; _items.Clear(); Bump(); }
    /// <summary>The index of <paramref name="item"/>, or -1.</summary>
    public int IndexOf(T item) => _items.IndexOf(item);
    /// <summary>Replace the whole contents (one version bump).</summary>
    public void Reset(IEnumerable<T> items) { _items.Clear(); _items.AddRange(items); Bump(); }

    /// <inheritdoc/>
    public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private void Bump() => _version.Value = _version.Peek() + 1;
}

/// <summary>The kind of a selectable track (spec §6).</summary>
public enum TrackKind : byte { Audio, Video, Text }

/// <summary>The semantic role of a track (spec §6) — richer than a flat/opaque list.</summary>
public enum TrackRole : byte { Main, Alternate, Commentary, Descriptions, Captions, Subtitles, Sign, Karaoke }

/// <summary>One selectable track (spec §6). <see cref="IsSelected"/> is a read signal the engine flips on selection.</summary>
public sealed record MediaTrack(
    int Id, TrackKind Kind, string? Language, string Label, TrackRole Role,
    IReadSignal<bool> IsSelected, MediaContentType Codec);

/// <summary>
/// The observable track model (spec §6): audio/video/text collections + the selected-track signals. Selection is by
/// intent (<see cref="Select"/>); the engine flips <see cref="MediaTrack.IsSelected"/>. External subtitles render on the
/// engine's own GPU text stack, with per-track sync-offset correction.
/// </summary>
public sealed class TrackSet
{
    private readonly Signal<MediaTrack?> _selAudio = new(null);
    private readonly Signal<MediaTrack?> _selVideo = new(null);
    private readonly Signal<MediaTrack?> _selText = new(null);
    private readonly Dictionary<int, Signal<bool>> _selectedFlags = new();
    private readonly Dictionary<int, TimeSpan> _syncOffsets = new();
    private int _nextExternalId = 100_000;   // external subs get a high id space so they never collide with backend ids

    /// <summary>The audio tracks.</summary>
    public ObservableList<MediaTrack> Audio { get; } = new();
    /// <summary>The video tracks.</summary>
    public ObservableList<MediaTrack> Video { get; } = new();
    /// <summary>The text (subtitle/caption) tracks.</summary>
    public ObservableList<MediaTrack> Text { get; } = new();

    /// <summary>The selected audio track (null = none).</summary>
    public IReadSignal<MediaTrack?> SelectedAudio => _selAudio;
    /// <summary>The selected video track (null = none).</summary>
    public IReadSignal<MediaTrack?> SelectedVideo => _selVideo;
    /// <summary>The selected text track (null = text disabled).</summary>
    public IReadSignal<MediaTrack?> SelectedText => _selText;

    /// <summary>Select <paramref name="track"/> in its collection (the engine flips <see cref="MediaTrack.IsSelected"/>).</summary>
    public void Select(MediaTrack track)
    {
        switch (track.Kind)
        {
            case TrackKind.Audio: SetSelected(_selAudio, track); break;
            case TrackKind.Video: SetSelected(_selVideo, track); break;
            case TrackKind.Text: SetSelected(_selText, track); break;
        }
    }

    /// <summary>Disable text (deselect any subtitle/caption track).</summary>
    public void DisableText()
    {
        if (_selText.Peek() is { } prev && _selectedFlags.TryGetValue(prev.Id, out var f)) f.Value = false;
        _selText.Value = null;
    }

    /// <summary>Add an external subtitle track and return its <see cref="MediaTrack"/> handle.</summary>
    public MediaTrack AddExternalSubtitle(SubtitleSource src, string language, string label)
    {
        int id = _nextExternalId++;
        var flag = new Signal<bool>(false);
        _selectedFlags[id] = flag;
        var track = new MediaTrack(id, TrackKind.Text, language, label, TrackRole.Subtitles, flag, src.ContentType);
        Text.Add(track);
        return track;
    }

    /// <summary>Set a per-track sync offset (drift correction; can be negative to advance the cues).</summary>
    public void SetSyncOffset(MediaTrack textTrack, TimeSpan offset) => _syncOffsets[textTrack.Id] = offset;

    /// <summary>Read a track's current sync offset (zero if none set).</summary>
    public TimeSpan SyncOffsetOf(MediaTrack textTrack)
        => _syncOffsets.TryGetValue(textTrack.Id, out var o) ? o : TimeSpan.Zero;

    /// <summary>Register a backend-discovered track (populates the collection + its <see cref="MediaTrack.IsSelected"/>
    /// backing flag). Returns the track with a live selected-flag wired in.</summary>
    public MediaTrack Register(int id, TrackKind kind, string? language, string label, TrackRole role, MediaContentType codec, bool selected = false)
    {
        var flag = new Signal<bool>(selected);
        _selectedFlags[id] = flag;
        var track = new MediaTrack(id, kind, language, label, role, flag, codec);
        switch (kind)
        {
            case TrackKind.Audio: Audio.Add(track); if (selected) _selAudio.Value = track; break;
            case TrackKind.Video: Video.Add(track); if (selected) _selVideo.Value = track; break;
            case TrackKind.Text: Text.Add(track); if (selected) _selText.Value = track; break;
        }
        return track;
    }

    private void SetSelected(Signal<MediaTrack?> sel, MediaTrack track)
    {
        if (sel.Peek() is { } prev && _selectedFlags.TryGetValue(prev.Id, out var pf)) pf.Value = false;
        if (_selectedFlags.TryGetValue(track.Id, out var nf)) nf.Value = true;
        sel.Value = track;
    }
}

// ── §6.1 Structured timed cues (synced lyrics / karaoke) ─────────────────────────────────────────────────────────────

/// <summary>Per-cue styling (spec §6.1) — a value, not serialized WebVTT markup.</summary>
public readonly record struct CueStyle(float FontScale, bool Bold, uint ArgbColor)
{
    /// <summary>The neutral default style.</summary>
    public static CueStyle Default => new(1f, false, 0xFFFFFFFF);
}

/// <summary>A structured timed cue pushed with styling + a tag (spec §6.1) — directly serves synced lyrics / karaoke.</summary>
public readonly record struct TimedCue(TimeSpan Start, TimeSpan End, string Text, CueStyle Style, object? Tag);

/// <summary>
/// A timed-cue track (spec §6.1). The active cue is a frame-accurate signal (no C# event); per-cue enter/exit handlers
/// serve karaoke highlight. Slaves to the audio SAMPLE clock (via the position fed to <see cref="Advance"/>), not the
/// frame clock, so syllable timing stays correct under dropped frames.
/// </summary>
public sealed class CueTrack
{
    private readonly List<TimedCue> _cues = new();
    private readonly Signal<TimedCue?> _active = new(null);
    private readonly List<Action<TimedCue>> _onEnter = new();
    private readonly List<Action<TimedCue>> _onExit = new();
    private int _activeIndex = -1;

    /// <summary>Create an empty cue track.</summary>
    public CueTrack() { }
    /// <summary>Create a cue track seeded with cues (must be sorted by <see cref="TimedCue.Start"/>).</summary>
    public CueTrack(IEnumerable<TimedCue> cues) => _cues.AddRange(cues);

    /// <summary>The frame-accurate active cue (null when none is active).</summary>
    public IReadSignal<TimedCue?> ActiveCue => _active;
    /// <summary>Register a per-cue enter handler (karaoke highlight).</summary>
    public void OnCueEnter(Action<TimedCue> handler) => _onEnter.Add(handler);
    /// <summary>Register a per-cue exit handler.</summary>
    public void OnCueExit(Action<TimedCue> handler) => _onExit.Add(handler);

    /// <summary>Add a cue (kept sorted by start time).</summary>
    public void Add(TimedCue cue)
    {
        int i = _cues.Count;
        while (i > 0 && _cues[i - 1].Start > cue.Start) i--;
        _cues.Insert(i, cue);
    }

    /// <summary>Advance the active-cue selection to the media position <paramref name="position"/> (fed off the audio
    /// sample clock). Fires enter/exit handlers on transitions and updates <see cref="ActiveCue"/>.</summary>
    public void Advance(TimeSpan position)
    {
        int found = -1;
        for (int i = 0; i < _cues.Count; i++)
            if (position >= _cues[i].Start && position < _cues[i].End) { found = i; break; }

        if (found == _activeIndex) return;

        if (_activeIndex >= 0)
        {
            var exiting = _cues[_activeIndex];
            for (int h = 0; h < _onExit.Count; h++) _onExit[h](exiting);
        }
        _activeIndex = found;
        if (found >= 0)
        {
            var entering = _cues[found];
            _active.Value = entering;
            for (int h = 0; h < _onEnter.Count; h++) _onEnter[h](entering);
        }
        else _active.Value = null;
    }
}
