using System;
using System.IO;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Inline playlist metadata editing on the detail rail — read-only hero text by default; hover eases in a
/// pill chrome (fill + hairline border, engine-eased — no re-render) plus a fading pencil, click swaps to an
/// <see cref="EditableText"/> with save/cancel buttons (focus-preserving) and an animated saving→saved chip.
/// Cover: hover/drag cross-fades an overlay; click picks, drop replaces. Commits resolve uri from the live
/// <see cref="Loadable{T}"/> at event time.</summary>
static class PlaylistInlineEdit
{
    internal static Element Cover(Loadable<DetailModel> full, float size)
        => Embed.Comp(() => new EditableCover(full, size)) with { Key = $"pl-edit-cover:{(int)size}" };

    internal static Element Title(Loadable<DetailModel> full, float width, float titleSize)
        => Embed.Comp(() => new EditableTitle(full, width, titleSize)) with { Key = $"pl-edit-title:{(int)width}:{(int)titleSize}" };

    internal static Element Description(Loadable<DetailModel> full, float width, int maxLines, DetailHandlers h)
        => Embed.Comp(() => new EditableDescription(full, width, maxLines, h)) with { Key = $"pl-edit-desc:{(int)width}:{maxLines}" };

    internal static Element Collaborative(Loadable<DetailModel> full, float width)
        => Embed.Comp(() => new CollaborativeToggle(full, width)) with { Key = $"pl-edit-collab:{(int)width}" };

    // ── save plumbing ────────────────────────────────────────────────────────────────────────────────────────────

    const int StatusIdle = 0, StatusSaving = 1, StatusSaved = 2;

    /// <summary>The persistent read↔edit swap shell: the SAME node across both branches, so its height change (text
    /// block ↔ field box) TWEENS through layout (<see cref="MotionRecipes.CardResize"/>) instead of snapping — the
    /// branch roots' Enter fades ride inside it.</summary>
    static Element AnimatedSwap(float width, Element body) => new BoxEl
    {
        Width = width, Animate = MotionRecipes.CardResize,
        Children = [body],
    };

    static async Task<bool> SaveDetailsAsync(LibraryBridge lib, string uri, string? name, string? description, bool? collaborative)
    {
        try
        {
            await lib.UpdatePlaylistDetailsAsync(uri, name, description, collaborative).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Toasts.Show(ex.Message, ToastSeverity.Critical);
            return false;
        }
    }

    static async Task ApplyCoverJpegAsync(LibraryBridge lib, string uri, byte[] jpeg)
    {
        try
        {
            await lib.SetPlaylistCoverJpegAsync(uri, jpeg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Toasts.Show(ex.Message, ToastSeverity.Critical);
        }
    }

    static async Task TryCoverPathAsync(LibraryBridge lib, string uri, string path)
    {
        if (path is not { Length: > 0 } || !File.Exists(path)) return;
        string ext = Path.GetExtension(path);
        if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            Toasts.Show(Loc.Get(Strings.Detail.Edit.PickCover), ToastSeverity.Caution);
            return;
        }
        await ApplyCoverJpegAsync(lib, uri, await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false);
    }

    // ── shared visuals ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The transient saving→saved chip. Mount-gated by the caller (status != idle); keyed by state so the
    /// saving→saved swap replays the enter fade, and unmount fades out via Exit.</summary>
    static Element StatusChip(int status) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
        Padding = new Edges4(8f, 3f, 10f, 3f), Corners = CornerRadius4.All(12f),
        Fill = Tok.FillSubtleSecondary,
        Enter = new EnterExit(Dx: -6f, Opacity: 0f, Active: true),
        Exit = new EnterExit(Opacity: 0f, Active: true),
        Key = "pl-edit-status:" + status,
        Children = status == StatusSaving
            ?
            [
                ProgressRing.Indeterminate(16f, true, Tok.TextSecondary),
                new TextEl(Loc.Get(Strings.Detail.Edit.Saving)) { Size = 11f, Weight = 600, Color = Tok.TextSecondary },
            ]
            :
            [
                Ui.Icon(Icons.Accept, 12f, Tok.AccentTextPrimary),
                new TextEl(Loc.Get(Strings.Detail.Edit.Saved)) { Size = 11f, Weight = 600, Color = Tok.TextSecondary },
            ],
    };

    /// <summary>A labeled save/cancel pill (no enter-fade — must be visible the instant edit mode mounts).</summary>
    static Element EditFieldBtn(string glyph, string label, bool accent, Action onClick) => new BoxEl
    {
        Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Height = 32f, Shrink = 0f,
        Padding = new Edges4(10f, 0f, 12f, 0f), Corners = CornerRadius4.All(16f),
        Fill = accent ? Tok.AccentTextPrimary with { A = 0.16f } : ColorF.Transparent,
        HoverFill = accent ? Tok.AccentTextPrimary with { A = 0.26f } : Tok.FillSubtleSecondary,
        PressedFill = accent ? Tok.AccentTextPrimary with { A = 0.12f } : Tok.FillSubtleTertiary,
        Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
        Role = AutomationRole.Button, OnClick = onClick,
        Children =
        [
            Ui.Icon(glyph, 13f, accent ? Tok.AccentTextPrimary : Tok.TextSecondary),
            new TextEl(label) { Size = 12f, Weight = 600, Color = accent ? Tok.AccentTextPrimary : Tok.TextSecondary },
        ],
    };

    /// <summary>Save/cancel row below the field — always visible, never clipped by the field chrome.</summary>
    static Element SaveCancelRow(Action save, Action cancel) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.S, Justify = FlexJustify.End, Shrink = 0f,
        Children =
        [
            EditFieldBtn(Icons.Accept, Loc.Get(Strings.Detail.Edit.Save), accent: true, save),
            EditFieldBtn(Icons.Cancel, Loc.Get(Strings.Detail.Edit.Cancel), accent: false, cancel),
        ],
    };

    /// <summary>Field + save/cancel row; <paramref name="shell"/> captures the outer box for bring-into-view (field +
    /// buttons, so the rail scrolls enough to show both).</summary>
    static Element EditChrome(Element field, float width, Action save, Action cancel, Ref<NodeHandle> shell,
        RenderContext ctx, Action<Action> post) => new BoxEl
    {
        Direction = 1, Width = width, Gap = WaveeSpace.S,
        OnRealized = h =>
        {
            shell.Value = h;
            // Double-post: first pump realizes the field, second pump has final layout bounds for scroll math.
            post(() => post(() => BringIntoView(ctx, h, margin: 40f)));
        },
        Children = [field, SaveCancelRow(save, cancel)],
    };

    /// <summary>A round icon-only edit-action (status chip etc.).</summary>
    static Element EditActionBtn(string glyph, bool accent, Action onClick, float size = 30f)
    {
        ColorF a = Tok.AccentTextPrimary;
        return new BoxEl
        {
            Width = size, Height = size, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(size / 2f),
            Fill = accent ? a with { A = 0.16f } : ColorF.Transparent,
            HoverFill = accent ? a with { A = 0.26f } : Tok.FillSubtleSecondary,
            PressedFill = accent ? a with { A = 0.12f } : Tok.FillSubtleTertiary,
            HoverScale = 1.06f, PressScale = 0.94f,
            Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
            Role = AutomationRole.Button, OnClick = onClick,
            Enter = new EnterExit(Sx: 0.8f, Sy: 0.8f, Opacity: 0f, Active: true),
            Children = [Ui.Icon(glyph, MathF.Round(size * 0.42f), accent ? a : Tok.TextSecondary)],
        };
    }

    /// <summary>Focus the field when its root realizes (deferred one pump). visual:false = pointer-style focus —
    /// caret at end, no select-all (the inline-edit affordance; select-all is for Tab keyboard focus only).</summary>
    static TemplateParts FocusOnMount(InputHooks hooks, Action<Action> post)
    {
        var parts = new TemplateParts();
        parts[EditableText.PartRoot] = b => b with
        {
            OnRealized = h => post(() => hooks.FocusNode?.Invoke(h, false)),
        };
        return parts;
    }

    /// <summary>Re-scroll the nearest vertical viewport whenever edit chrome grows (enter edit, draft wraps, status
    /// chip mounts) so the field + save row stay in view.</summary>
    static void UseEditScroll(Component c, Action<Action> post, Signal<bool> editing, Ref<NodeHandle> shell,
        string uriKey, string draftSnapshot)
    {
        c.Context.UseLayoutEffect(() =>
        {
            if (!editing.Peek() || shell.Value.IsNull) return;
            post(() => BringIntoView(c.Context, shell.Value, margin: 32f));
        }, editing.Value, uriKey, draftSnapshot);
    }

    /// <summary>Snap-scroll a node into its nearest vertical viewport (viewport-relative bounds — reliable inside nested
    /// scroll content). Immediate offset write (no smooth chase) so edit-enter lands before the user types.</summary>
    static void BringIntoView(RenderContext ctx, NodeHandle node, float margin = 16f)
    {
        var scene = ctx.Scene;
        if (scene is null || node.IsNull || !scene.IsLive(node)) return;

        var vp = scene.Parent(node);
        while (!vp.IsNull && !scene.HasScroll(vp)) vp = scene.Parent(vp);
        if (vp.IsNull) return;
        ref ScrollState sc = ref scene.ScrollRef(vp);
        if (sc.Orientation != 0 || sc.ViewportH <= 1f) return;

        RectF nodeAbs = scene.AbsoluteRect(node);
        RectF vpAbs = scene.AbsoluteRect(vp);
        float top = nodeAbs.Y - vpAbs.Y;
        float bottom = top + nodeAbs.H;

        float target = sc.OffsetY;
        if (top < margin) target = sc.OffsetY + top - margin;
        else if (bottom > sc.ViewportH - margin) target = sc.OffsetY + bottom - sc.ViewportH + margin;
        else return;

        target = Math.Clamp(target, 0f, MathF.Max(0f, sc.ContentH - sc.ViewportH));
        if (MathF.Abs(target - sc.OffsetY) < 0.5f) return;

        sc.OffsetY = target;
        sc.TargetY = target;
        sc.PendingTargetY = float.NaN;
        sc.Phase = ScrollIntegrator.Idle;
        sc.PhaseFlags = 0;
        sc.FlingVelocity = 0f;

        var content = sc.ContentNode;
        if (!content.IsNull && scene.IsLive(content))
        {
            scene.Paint(content).LocalTransform = Affine2D.Translation(0f, -target);
            scene.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
        scene.Mark(vp, NodeFlags.PaintDirty);
        ctx.RequestRerender();
    }

    // ── cover ────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class EditableCover : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly float _size;
        readonly Signal<bool> _hovered = new(false);
        readonly Signal<bool> _dropOver = new(false);
        readonly Signal<bool> _busy = new(false);
        readonly Ref<DropTargetSpec?> _dropSpec = new(null);

        public EditableCover(Loadable<DetailModel> full, float size) { _full = full; _size = size; }

        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            var m = _full.Value.Value;
            if (lib is null || !m.Capabilities.CanEditMetadata || m.ContextUri is not { } uri)
                return DetailRail.HeroArtwork(m, _size);

            var drag = UseDragState();
            bool compatibleDrag = drag.Active && drag.Kind == DropKinds.Files;
            bool dropCue = compatibleDrag || _dropOver.Value;

            _dropSpec.Value ??= new DropTargetSpec(
                [DropKinds.Files],
                OnEnter: _ => _dropOver.Value = true,
                OnLeave: _ => _dropOver.Value = false,
                OnDrop: s =>
                {
                    _dropOver.Value = false;
                    if (s.Payload is FileDropData { Count: > 0 } files)
                        _ = ApplyPathAsync(lib, uri, files.Paths[0]);
                });

            return new BoxEl
            {
                Width = _size, Height = _size, Corners = CornerRadius4.All(WaveeRadius.Card),
                Shadow = Elevation.Card, ClipToBounds = true, ZStack = true,
                Cursor = CursorId.Hand,
                DropTarget = _dropSpec.Value,
                OnHoverMove = _ => { if (!_hovered.Peek()) _hovered.Value = true; },
                OnPointerExit = () => { if (_hovered.Peek()) _hovered.Value = false; },
                OnClick = () => _ = PickCoverAsync(lib, uri),
                Children =
                [
                    DetailRail.HeroArtwork(m, _size),
                    // Always-mounted overlay — the cross-fade is a bound-opacity transition (compositor-only).
                    CoverOverlay(dropCue),
                ],
            };
        }

        Element CoverOverlay(bool dropActive)
        {
            // Image overlays must NOT use theme fill/text tokens — artwork can be any luminance. Always a dark scrim +
            // white foreground (the same pattern as ArtistPage hero scrims).
            const float scrimA = 0.52f, scrimDropA = 0.68f;
            var onImage = ColorF.FromRgba(255, 255, 255);
            return new BoxEl
            {
                Fill = ColorF.FromRgba(0, 0, 0) with { A = dropActive ? scrimDropA : scrimA },
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 6f,
                HitTestVisible = false,
                Opacity = Prop.Of(() => _busy.Value || _hovered.Value || _dropOver.Value || dropActive ? 1f : 0f),
                Transition = MotionTok.ControlNormal,
                Children = _busy.Value
                    ?
                    [
                        ProgressRing.Indeterminate(28f, true, onImage),
                        new TextEl(Loc.Get(Strings.Detail.Edit.Saving)) { Size = 13f, Weight = 600, Color = onImage },
                    ]
                    :
                    [
                        Ui.Icon(Icons.Camera, 32f, onImage),
                        new TextEl(dropActive
                            ? Loc.Get(Strings.Detail.Edit.DropCover)
                            : Loc.Get(Strings.Detail.Edit.ChangeCover))
                        {
                            Size = 12f, Weight = 600, Color = onImage,
                            Wrap = TextWrap.Wrap, MaxLines = 2, Width = 120f,
                        },
                    ],
            };
        }

        async Task ApplyPathAsync(LibraryBridge lib, string uri, string path)
        {
            _busy.Value = true;
            try { await TryCoverPathAsync(lib, uri, path).ConfigureAwait(false); }
            finally { _busy.Value = false; }
        }

        async Task PickCoverAsync(LibraryBridge lib, string uri)
        {
            string? path = FilePicker.OpenFile(FluentApp.WindowHandle, Loc.Get(Strings.Detail.Edit.PickCover),
                new[] { ("JPEG", "*.jpg;*.jpeg") });
            if (path is null) return;
            await ApplyPathAsync(lib, uri, path).ConfigureAwait(false);
        }
    }

    // ── title ────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class EditableTitle : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly float _width;
        readonly float _titleSize;
        readonly Signal<string> _draft = new("");
        readonly Signal<bool> _editing = new(false);
        readonly Signal<bool> _hovered = new(false);
        readonly Signal<int> _status = new(StatusIdle);
        readonly Ref<NodeHandle> _editShell = new(NodeHandle.Null);
        int _saveEpoch;

        public EditableTitle(Loadable<DetailModel> full, float width, float titleSize)
        { _full = full; _width = width; _titleSize = titleSize; }

        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            var m = _full.Value.Value;
            string? uri = m.ContextUri;
            UseLayoutEffect(() => { if (!_editing.Peek()) _draft.Value = m.Title; }, uri ?? "", m.Title);
            UseEditScroll(this, post, _editing, _editShell, uri ?? "", _draft.Value);

            if (lib is null || !m.Capabilities.CanEditMetadata || uri is null)
            {
                return WaveeType.PageHero(m.Title) with
                {
                    Size = _titleSize, MinSize = 18f, Weight = 900, Width = _width, LineHeight = float.NaN,
                    Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
                };
            }

            if (_editing.Value)
            {
                float fieldH = MathF.Max(40f, _titleSize + 12f);
                return AnimatedSwap(_width, EditChrome(
                    Embed.Comp(() => new EditableText
                    {
                        Text = _draft,
                        Width = _width,
                        Height = fieldH,
                        FontSize = _titleSize,
                        PlaceCaretAtEndOnFocus = true,
                        Placeholder = Loc.Get(Strings.Detail.Edit.NamePlaceholder),
                        OnCommit = _ => EndEdit(lib, commit: true),
                        OnCancel = () => EndEdit(lib, commit: false),
                        OnFocusChanged = gained => { if (!gained) EndEdit(lib, commit: true); },
                        Parts = FocusOnMount(hooks, post),
                    }) with { Key = uri },
                    _width, () => EndEdit(lib, commit: true), () => EndEdit(lib, commit: false), _editShell, Context, post));
            }

            int status = _status.Value;
            string title = string.IsNullOrWhiteSpace(m.Title) ? Loc.Get(Strings.Detail.Edit.NamePlaceholder) : m.Title;
            // The hover pill: padding + compensating negative margin keep the hero text at its resting x/y; the fill
            // and hairline border ease in engine-side (HoverFade channel — no re-render), cursor is always I-beam.
            return AnimatedSwap(_width, new BoxEl
            {
                Direction = 0, Width = _width + 16f, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
                Margin = new Edges4(-8f, -4f, -8f, -4f), Padding = new Edges4(8f, 4f, 8f, 4f),
                Corners = CornerRadius4.All(WaveeRadius.Control),
                HoverFill = Tok.FillSubtleSecondary,
                BorderWidth = 1f, BorderColor = ColorF.Transparent, HoverBorderColor = Tok.StrokeControlDefault,
                Cursor = CursorId.IBeam,
                OnHoverMove = _ => { if (!_hovered.Peek()) _hovered.Value = true; },
                OnPointerExit = () => { if (_hovered.Peek()) _hovered.Value = false; },
                OnClick = () => _editing.Value = true,
                Enter = new EnterExit(Opacity: 0f, Active: true),
                Children =
                [
                    WaveeType.PageHero(title) with
                    {
                        Size = _titleSize, MinSize = 18f, Weight = 900, Grow = 1f, LineHeight = float.NaN,
                        Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
                        Color = string.IsNullOrWhiteSpace(m.Title) ? Tok.TextTertiary : Tok.TextPrimary,
                    },
                    // Always-mounted pencil — fades via a bound-opacity transition (no discrete pop).
                    new BoxEl
                    {
                        Width = 20f, Height = 20f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Opacity = Prop.Of(() => _hovered.Value && _status.Value == StatusIdle ? 1f : 0f),
                        Transition = MotionTok.ControlFast,
                        Children = [Ui.Icon(Icons.Edit, MathF.Max(14f, _titleSize * 0.4f), Tok.TextSecondary)],
                    },
                    status != StatusIdle ? StatusChip(status) : new BoxEl { Width = 0f },
                ],
            });
        }

        void EndEdit(LibraryBridge lib, bool commit)
        {
            if (!_editing.Peek()) return;
            _editing.Value = false;
            var cur = _full.Value.Peek();
            if (!commit) { _draft.Value = cur.Title; return; }
            if (cur.ContextUri is not { } uri) return;
            string next = _draft.Peek().Trim();
            if (next.Length == 0 || next == cur.Title) { _draft.Value = cur.Title; return; }
            _ = RunSaveAsync(() => SaveDetailsAsync(lib, uri, next, null, null), _status, () => ++_saveEpoch, () => _saveEpoch);
        }
    }

    // ── description ──────────────────────────────────────────────────────────────────────────────────────────────

    sealed class EditableDescription : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly float _width;
        readonly int _maxLines;
        readonly DetailHandlers _h;
        readonly Signal<string> _draft = new("");
        readonly Signal<bool> _editing = new(false);
        readonly Signal<bool> _hovered = new(false);
        readonly Signal<int> _status = new(StatusIdle);
        readonly Ref<NodeHandle> _editShell = new(NodeHandle.Null);
        readonly Ref<NodeHandle> _readShell = new(NodeHandle.Null);
        int _saveEpoch;

        public EditableDescription(Loadable<DetailModel> full, float width, int maxLines, DetailHandlers h)
        { _full = full; _width = width; _maxLines = maxLines; _h = h; }

        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            var m = _full.Value.Value;
            string? uri = m.ContextUri;
            UseLayoutEffect(() => { if (!_editing.Peek()) _draft.Value = m.Description ?? ""; }, uri ?? "", m.Description ?? "");
            UseEditScroll(this, post, _editing, _editShell, uri ?? "", _draft.Value);

            if (_maxLines <= 0 || lib is null || !m.Capabilities.CanEditMetadata || uri is null)
                return new BoxEl();

            // Fixed edit height (~3 lines): never derive from the read-state line cap (can be huge on a tall rail) and
            // never grow with wrapped text — overflow scrolls inside the clipped field; the rail scrolls to the shell.
            const float fieldH = 72f;
            bool hasDesc = m.Description is { Length: > 0 };

            if (_editing.Value)
            {
                return AnimatedSwap(_width, EditChrome(
                    Embed.Comp(() => new EditableText
                    {
                        Text = _draft,
                        Width = _width,
                        Height = fieldH,
                        AcceptsReturn = true,
                        Placeholder = Loc.Get(Strings.Detail.Edit.DescriptionPlaceholder),
                        OnCommit = _ => EndEdit(lib, commit: true),
                        OnCancel = () => EndEdit(lib, commit: false),
                        OnFocusChanged = gained => { if (!gained) EndEdit(lib, commit: true); },
                        Parts = FocusOnMount(hooks, post),
                    }) with { Key = uri },
                    _width, () => EndEdit(lib, commit: true), () => EndEdit(lib, commit: false), _editShell, Context, post));
            }

            int status = _status.Value;
            return AnimatedSwap(_width, new BoxEl
            {
                Direction = 0, Width = _width + 16f, Gap = WaveeSpace.S, AlignItems = FlexAlign.Start,
                Margin = new Edges4(-8f, -4f, -8f, -4f), Padding = new Edges4(8f, 4f, 8f, 4f),
                Corners = CornerRadius4.All(WaveeRadius.Control),
                HoverFill = Tok.FillSubtleSecondary,
                BorderWidth = 1f, BorderColor = ColorF.Transparent, HoverBorderColor = Tok.StrokeControlDefault,
                Cursor = CursorId.IBeam,
                OnRealized = h => _readShell.Value = h,
                OnHoverMove = _ => { if (!_hovered.Peek()) _hovered.Value = true; },
                OnPointerExit = () => { if (_hovered.Peek()) _hovered.Value = false; },
                OnClick = () =>
                {
                    _editing.Value = true;
                    post(() => BringIntoView(Context, _readShell.Value, margin: 48f));
                },
                Enter = new EnterExit(Opacity: 0f, Active: true),
                Children =
                [
                    // FLEX text (Grow/Basis=0): wraps to whatever width the row leaves after the pencil/chip take
                    // their intrinsic space — no hand-computed width reservations (the title row's exact model).
                    hasDesc
                        ? RichText.OfFlex(m.Description!, 12f, Tok.TextSecondary, Tok.AccentTextPrimary, _maxLines,
                            u => { if (RichText.RouteForUri(u) is { } k) _h.Go(k, null); })
                        : new TextEl(Loc.Get(Strings.Detail.Edit.DescriptionPlaceholder))
                        {
                            Size = 12f, Color = Tok.TextTertiary, Grow = 1f, Basis = 0f,
                            Wrap = TextWrap.Wrap, MaxLines = _maxLines,
                        },
                    // Pencil pinned to the first text line (18px line box), fading with the hover state.
                    new BoxEl
                    {
                        Width = 16f, Height = 18f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Opacity = Prop.Of(() => _hovered.Value && _status.Value == StatusIdle ? 1f : 0f),
                        Transition = MotionTok.ControlFast,
                        Children = [Ui.Icon(Icons.Edit, 13f, Tok.TextSecondary)],
                    },
                    status != StatusIdle ? StatusChip(status) : new BoxEl { Width = 0f },
                ],
            });
        }

        void EndEdit(LibraryBridge lib, bool commit)
        {
            if (!_editing.Peek()) return;
            _editing.Value = false;
            var cur = _full.Value.Peek();
            if (!commit) { _draft.Value = cur.Description ?? ""; return; }
            if (cur.ContextUri is not { } uri) return;
            string next = _draft.Peek().Trim();
            if (next == (cur.Description ?? "")) return;
            _ = RunSaveAsync(() => SaveDetailsAsync(lib, uri, null, next, null), _status, () => ++_saveEpoch, () => _saveEpoch);
        }
    }

    // ── collaborative ────────────────────────────────────────────────────────────────────────────────────────────

    sealed class CollaborativeToggle : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly float _width;
        readonly Signal<bool> _on = new(false);

        public CollaborativeToggle(Loadable<DetailModel> full, float width) { _full = full; _width = width; }

        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            var m = _full.Value.Value;
            string? uri = m.ContextUri;
            UseLayoutEffect(() => _on.Value = m.Capabilities.IsCollaborative, uri ?? "", m.Capabilities.IsCollaborative);

            if (lib is null || !m.Capabilities.CanEditMetadata || uri is null
                || !m.Capabilities.CanAdministratePermissions)
                return new BoxEl();

            return new BoxEl
            {
                Width = _width,
                Children =
                [
                    CheckBox.Create(Loc.Get(Strings.Detail.Edit.Collaborative), _on.Value, () =>
                    {
                        bool next = !_on.Peek();
                        _on.Value = next;
                        _ = ToggleAsync(lib, next);
                    }),
                ],
            };
        }

        async Task ToggleAsync(LibraryBridge lib, bool next)
        {
            var cur = _full.Value.Peek();
            if (cur.ContextUri is not { } uri) { _on.Value = !next; return; }
            if (!await SaveDetailsAsync(lib, uri, null, null, next).ConfigureAwait(false))
                _on.Value = !next;
        }
    }

    // ── shared hooks/plumbing ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Drive the saving→saved→idle status lifecycle around one save call; a newer save (epoch bump)
    /// abandons the stale tail so overlapping edits never fight over the chip.</summary>
    static async Task RunSaveAsync(Func<Task<bool>> save, Signal<int> status, Func<int> bumpEpoch, Func<int> curEpoch)
    {
        int epoch = bumpEpoch();
        status.Value = StatusSaving;
        bool ok = await save().ConfigureAwait(false);
        if (curEpoch() != epoch) return;
        if (!ok) { status.Value = StatusIdle; return; }
        status.Value = StatusSaved;
        await Task.Delay(1800).ConfigureAwait(false);
        if (curEpoch() == epoch && status.Peek() == StatusSaved) status.Value = StatusIdle;
    }
}
