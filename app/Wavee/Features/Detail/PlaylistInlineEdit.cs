using System;
using System.Collections.Generic;
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

    internal static Element OwnerRow(Loadable<DetailModel> full, float width)
        => Embed.Comp(() => new PlaylistOwnerRow(full, width)) with { Key = $"pl-owner-row:{(int)width}" };

    internal static Element OwnerMenu(Loadable<DetailModel> full, DetailHandlers h)
        => Embed.Comp(() => new OwnerOverflowMenu(full, h)) with { Key = "pl-owner-menu" };

    internal static Element ShareButton(Loadable<DetailModel> full)
        => Embed.Comp(() => new PlaylistShareButton(full)) with { Key = "pl-share" };

    /// <summary>The shared adaptive invite affordance (owner row + collaborator pile): a labeled pill when the row is
    /// wide, a round icon button + tooltip when narrow. Clicking opens the Invite &amp; access flyout anchored to it.
    /// Self-gated (returns an empty box when the caller can't administer permissions), so both call sites just embed it.</summary>
    internal static Element InviteButton(Loadable<DetailModel> full, float availableWidth)
        => Embed.Comp(() => new PlaylistInviteControl(full, availableWidth)) with { Key = $"pl-invite:{(int)availableWidth}" };

    /// <summary>Live Spotify playlist owner mutations (permission, delete, invite) — false when fake backend or logged out.</summary>
    internal static bool SpotifyEditsLive(Services? svc)
        => svc?.RealPlaylistMutations is not null && svc.Session.Status == AuthStatus.Authenticated;

    static void PatchDetail(Loadable<DetailModel> full, Func<DetailModel, DetailModel> patch)
    {
        if ((LoadState)full.State.Peek() != LoadState.Ready) return;
        full.SetReady(patch(full.Value.Peek()));
    }

    static async Task RefreshPlaylistDetailAsync(Services? svc, Loadable<DetailModel> full, string uri, CancellationToken ct = default)
    {
        if (svc is null || !SpotifyEditsLive(svc)) return;
        var fresh = await DetailPage.ReloadPlaylistDetailAsync(svc, uri, ct).ConfigureAwait(false);
        if (fresh is not null) full.SetReady(fresh);
    }

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

    static async Task<bool> SaveDetailsAsync(LibraryBridge lib, string uri, string? name, string? description, bool? collaborative, string? previousName = null)
    {
        try
        {
            await lib.UpdatePlaylistDetailsAsync(uri, name, description, collaborative, previousName).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            PlaylistEditErrors.Toast(ex);
            return false;
        }
    }

    static async Task<bool> ApplyCoverJpegAsync(LibraryBridge lib, string uri, byte[] jpeg)
    {
        try
        {
            await lib.SetPlaylistCoverJpegAsync(uri, jpeg).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            PlaylistEditErrors.Toast(ex);
            return false;
        }
    }

    static async Task<bool> TryCoverPathAsync(LibraryBridge lib, string uri, string path)
    {
        if (path is not { Length: > 0 } || !File.Exists(path)) return false;
        string ext = Path.GetExtension(path);
        if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            Toast.Show(Loc.Get(Strings.Detail.Edit.PickCover), new ToastOptions { Severity = InfoBarSeverity.Warning });
            return false;
        }
        return await ApplyCoverJpegAsync(lib, uri, await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false);
    }

    // ── shared visuals ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The transient saving→saved chip. Mount-gated by the caller (status != idle); keyed by state so the
    /// saving→saved swap replays the enter fade, and unmount fades out via Exit.</summary>
    static Element StatusChip(int status) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
        Padding = new Edges4(8f, 3f, 10f, 3f), Corners = CornerRadius4.All(12f),
        Fill = Tok.FillSubtleSecondary,
        Key = "pl-edit-status",
        Animate = MotionRecipes.TextSwap,
        Children =
        [
            ZStack(new BoxEl
            {
                Key = "pl-status-icon:" + status,
                Width = 16f, Height = 16f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Animate = MotionRecipes.IconSwap,
                Children = [status == StatusSaving
                    ? ProgressRing.Indeterminate(16f, true, Tok.TextSecondary)
                    : Ui.Icon(Icons.Accept, 12f, Tok.AccentTextPrimary)],
            }),
            ZStack(new BoxEl
            {
                Key = "pl-status-text:" + status,
                Animate = MotionRecipes.TextSwap,
                Children = [new TextEl(status == StatusSaving
                    ? Loc.Get(Strings.Detail.Edit.Saving)
                    : Loc.Get(Strings.Detail.Edit.Saved))
                { Size = 11f, Weight = 600, Color = Tok.TextSecondary }],
            }),
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
        Direction = 0, Gap = Spacing.S, Justify = FlexJustify.End, Shrink = 0f,
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
        Direction = 1, Width = width, Gap = Spacing.S,
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
        }, DepKey.From(HashCode.Combine(editing.Value, uriKey, draftSnapshot)));
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
        readonly Signal<int> _status = new(StatusIdle);
        readonly Ref<DropTargetSpec?> _dropSpec = new(null);
        int _saveEpoch;

        public EditableCover(Loadable<DetailModel> full, float size) { _full = full; _size = size; }

        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            var svc = UseContext(Services.Slot);
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
                        _ = ApplyPathAsync(lib, svc, uri, files.Paths[0]);
                });

            return new BoxEl
            {
                Width = _size, Height = _size, Corners = CornerRadius4.All(Radii.Card),
                Shadow = Elevation.Card, ClipToBounds = true, ZStack = true,
                Cursor = CursorId.Hand,
                DropTarget = _dropSpec.Value,
                OnHoverMove = _ => { if (!_hovered.Peek()) _hovered.Value = true; },
                OnPointerExit = () => { if (_hovered.Peek()) _hovered.Value = false; },
                OnClick = () => _ = PickCoverAsync(lib, svc, uri),
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
                Opacity = Prop.Of(() => _status.Value != StatusIdle || _hovered.Value || _dropOver.Value || dropActive ? 1f : 0f),
                Transition = MotionTok.ControlNormal,
                Children = _status.Value != StatusIdle
                    ?
                    [
                        StatusChip(_status.Peek()),
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

        async Task ApplyPathAsync(LibraryBridge lib, Services? svc, string uri, string path)
        {
            await RunSaveAsync(
                async () =>
                {
                    bool saved = await TryCoverPathAsync(lib, uri, path).ConfigureAwait(false);
                    if (saved && svc?.RealStore?.GetPlaylist(uri) is { } header)
                        PatchDetail(_full, m => m with { Cover = header.Cover });
                    return saved;
                },
                _status, () => ++_saveEpoch, () => _saveEpoch).ConfigureAwait(false);
        }

        async Task PickCoverAsync(LibraryBridge lib, Services? svc, string uri)
        {
            string? path = FilePicker.OpenFile(FluentApp.WindowHandle, Loc.Get(Strings.Detail.Edit.PickCover),
                new[] { ("JPEG", "*.jpg;*.jpeg") });
            if (path is null) return;
            await ApplyPathAsync(lib, svc, uri, path).ConfigureAwait(false);
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
            UseLayoutEffect(() => { if (!_editing.Peek()) _draft.Value = m.Title; }, (uri ?? "", m.Title));
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
            Element titleRow = new BoxEl
            {
                Direction = 0, Width = _width + 16f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Margin = new Edges4(-8f, -4f, -8f, -4f), Padding = new Edges4(8f, 4f, 8f, 4f),
                Corners = CornerRadius4.All(Radii.Control),
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
                ],
            };
            // The transient status must never participate in the hero title's horizontal measure. Putting the Saved
            // chip in titleRow stole width for 1.8 s and forced a live wrap exactly as the editor branch was being
            // replaced; the retained old glyph run and the newly-wrapped last character then painted together. A stable
            // column keeps the title width invariant and gives status its own trailing row.
            return AnimatedSwap(_width, new BoxEl
            {
                Direction = 1, Width = _width, Gap = status == StatusIdle ? 0f : 4f,
                Children =
                [
                    titleRow,
                    status != StatusIdle
                        ? new BoxEl { Direction = 0, Width = _width, Justify = FlexJustify.End, Children = [StatusChip(status)] }
                        : new BoxEl { Width = _width, Height = 0f },
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
            _ = RunSaveAsync(async () =>
            {
                bool saved = await SaveDetailsAsync(lib, uri, next, null, null, cur.Title).ConfigureAwait(false);
                if (saved) PatchDetail(_full, m => m with { Title = next });
                return saved;
            }, _status, () => ++_saveEpoch, () => _saveEpoch);
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
            UseLayoutEffect(() => { if (!_editing.Peek()) _draft.Value = m.Description ?? ""; }, (uri ?? "", m.Description ?? ""));
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
            Element descriptionRow = new BoxEl
            {
                Direction = 0, Width = _width + 16f, Gap = Spacing.S, AlignItems = FlexAlign.Start,
                Margin = new Edges4(-8f, -4f, -8f, -4f), Padding = new Edges4(8f, 4f, 8f, 4f),
                Corners = CornerRadius4.All(Radii.Control),
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
                ],
            };
            // Same invariant as the title editor: status feedback is vertical chrome, never an inline sibling that
            // changes the wrapping width of live rich text during the save transition.
            return AnimatedSwap(_width, new BoxEl
            {
                Direction = 1, Width = _width, Gap = status == StatusIdle ? 0f : 4f,
                Children =
                [
                    descriptionRow,
                    status != StatusIdle
                        ? new BoxEl { Direction = 0, Width = _width, Justify = FlexJustify.End, Children = [StatusChip(status)] }
                        : new BoxEl { Width = _width, Height = 0f },
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
            _ = RunSaveAsync(async () =>
            {
                bool saved = await SaveDetailsAsync(lib, uri, null, next, null).ConfigureAwait(false);
                if (saved) PatchDetail(_full, m => m with { Description = next.Length == 0 ? null : next });
                return saved;
            }, _status, () => ++_saveEpoch, () => _saveEpoch);
        }
    }

    // ── owner row + invite ───────────────────────────────────────────────────────────────────────────────────────

    sealed class PlaylistOwnerRow : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly float _width;

        public PlaylistOwnerRow(Loadable<DetailModel> full, float width) { _full = full; _width = width; }

        public override Element Render()
        {
            var m = _full.Value.Value;
            string owner = m.OwnerName ?? "";
            // Flex row: avatar + invite are Shrink=0, the name Grows/trims into whatever is left — nothing ever clips at
            // rail width (the old hand-computed MaxWidth = _width - 120f under-reserved for the pill and clipped).
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MaxWidth = _width,
                Children =
                [
                    PersonPicture.Create("", 24f, displayName: owner, imageSourcePath: m.OwnerImage?.Url),
                    WaveeType.TrackTitle(owner) with { Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    InviteButton(_full, _width),
                ],
            };
        }
    }

    // ── share ────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class PlaylistShareButton : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly Signal<bool> _copied = new(false);
        TimerHandle _copyReset;   // one-shot: flips the "copied ✓" glyph back after 1600ms (frame-clock UseTimeout, generation-guarded)
        public PlaylistShareButton(Loadable<DetailModel> full) => _full = full;

        public override Element Render()
        {
            _copyReset = UseTimeout(() => _copied.Value = false, 1600f);
            var m = _full.Value.Value;
            bool copied = _copied.Value;
            return new BoxEl
            {
                Width = 40f, Height = 40f,
                Children =
                [
                    new BoxEl
                    {
                        Width = 40f, Height = 40f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = CornerRadius4.All(20f),
                        HoverScale = 1.06f, PressScale = 0.94f,
                        Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
                        OnClick = () => Share(m),
                        Children = [new BoxEl
                        {
                            Key = copied ? "share-check" : "share-link",
                            Animate = MotionRecipes.IconSwap,
                            Children = [Icon(copied ? Icons.Accept : Icons.Share, 16f,
                                copied ? Tok.AccentTextPrimary : Tok.TextSecondary)],
                        }],
                    }.Interactive(Interaction.Subtle),
                ],
            };
        }

        void Share(DetailModel m)
        {
            if (m.ContextUri is not { } uri) return;
            var url = m.ShareUrl ?? DetailPage.SpotifyPlaylistWebUrl(uri);
            if (InputHooks.Current.Default.Clipboard is { } clip)
            {
                try { clip.SetText(url); }
                catch (Exception ex) { PlaylistEditErrors.Toast(ex); return; }
                InputHooks.Current.Default.Announce?.Invoke(Loc.Get(Strings.Auth.Copied), false);
                _copied.Value = true;
                _copyReset.Restart();   // reset the ✓ back to the share glyph after 1600ms (generation-guarded handle)
            }
            else InputHooks.Current.Default.OpenUri?.Invoke(url);
        }
    }

    internal static async Task<bool> CopyContributorInviteAsync(LibraryBridge lib, DetailModel m, Loadable<DetailModel>? full = null, Services? svc = null)
    {
        if (m.ContextUri is not { } uri) return false;
        try
        {
            var token = await lib.CreateContributorInviteAsync(uri).ConfigureAwait(false);
            var url = $"{m.ShareUrl ?? DetailPage.SpotifyPlaylistWebUrl(uri)}?pt={token}";
            var clip = InputHooks.Current.Default.Clipboard;
            bool copied = clip is not null;
            if (clip is not null)
            {
                clip.SetText(url);
                InputHooks.Current.Default.Announce?.Invoke(Loc.Get(Strings.Auth.Copied), false);
            }
            else InputHooks.Current.Default.OpenUri?.Invoke(url);
            if (full is not null) await RefreshPlaylistDetailAsync(svc, full, uri).ConfigureAwait(false);
            return copied;
        }
        catch (Exception ex) { PlaylistEditErrors.Toast(ex); return false; }
    }

    static async Task<bool> SetCollaborativeAsync(LibraryBridge lib, Loadable<DetailModel> full, Services? svc, string uri, bool collaborative)
    {
        if (!await SaveDetailsAsync(lib, uri, null, null, collaborative).ConfigureAwait(false)) return false;
        PatchDetail(full, m => m with { Capabilities = m.Capabilities with { IsCollaborative = collaborative } });
        await RefreshPlaylistDetailAsync(svc, full, uri).ConfigureAwait(false);
        return true;
    }

    static async Task<bool> SetVisibilityAsync(LibraryBridge lib, Loadable<DetailModel> full, Services? svc, string uri, bool isPublic)
    {
        try
        {
            await lib.SetPlaylistVisibilityAsync(uri, isPublic).ConfigureAwait(false);
            PatchDetail(full, m => m with { IsPublic = isPublic });
            await RefreshPlaylistDetailAsync(svc, full, uri).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) { PlaylistEditErrors.Toast(ex); return false; }
    }

    // ── invite affordance + access flyout ────────────────────────────────────────────────────────────────────────

    /// <summary>Open (or toggle) the Invite &amp; access flyout anchored to <paramref name="anchor"/>. Raw acrylic card,
    /// focus-trapped, light-dismiss — the CollaboratorFacePile flyout template.</summary>
    static void OpenAccessFlyout(IOverlayService? overlay, LibraryBridge lib, Services? svc, Loadable<DetailModel> full,
        Func<NodeHandle> anchor, Ref<OverlayHandle?> handle, FlyoutPlacement placement)
    {
        if (overlay is null) return;
        if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
        handle.Value = overlay.Open(
            anchor,
            () => Embed.Comp(() => new PlaylistAccessFlyout(full, lib, svc)),
            placement,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Raw) { ConstrainToRootBounds = false });
        handle.Value.ClosedAction = () => handle.Value = null;
    }

    /// <summary>The shared adaptive invite affordance. Wide rows get a bordered pill (glyph + label); narrow rows get a
    /// 28px round icon button + tooltip, so the owner name always keeps room. Both open the access flyout on click.</summary>
    sealed class PlaylistInviteControl : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly float _availableWidth;

        public PlaylistInviteControl(Loadable<DetailModel> full, float availableWidth)
        { _full = full; _availableWidth = availableWidth; }

        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            var svc = UseContext(Services.Slot);
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);

            var m = _full.Value.Value;
            if (lib is null || !SpotifyEditsLive(svc) || !m.Capabilities.CanAdministratePermissions || m.ContextUri is null)
                return new BoxEl { Shrink = 0f };

            void Toggle() => OpenAccessFlyout(overlay, lib, svc, _full, () => anchor.Value, handle, FlyoutPlacement.BottomEdgeAlignedLeft);

            // Below ~260px a full pill would starve the owner name → collapse to a round icon (still readable via tooltip).
            bool compact = _availableWidth < 260f;
            if (compact)
                return ToolTip.Wrap(new BoxEl
                {
                    Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(14f),
                    HoverScale = 1.06f, PressScale = 0.94f,
                    Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
                    OnClick = Toggle, OnRealized = h => anchor.Value = h,
                    Children = [Icon(Icons.Friends, 14f, Tok.TextSecondary)],
                }.Interactive(Interaction.Subtle), Loc.Get(Strings.Detail.Edit.InviteCollaborators));

            return new BoxEl
            {
                Direction = 0, Shrink = 0f, Height = 28f, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = new Edges4(10f, 0f, 12f, 0f), Corners = CornerRadius4.All(14f),
                BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                HoverScale = 1.03f, PressScale = 0.97f,
                Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
                OnClick = Toggle, OnRealized = h => anchor.Value = h,
                Children =
                [
                    Icon(Icons.Friends, 12f, Tok.TextPrimary),
                    new TextEl(Loc.Get(Strings.Detail.Edit.InviteCollaborators)) { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
                ],
            }.Interactive(Interaction.Subtle);
        }
    }

    /// <summary>The "Invite &amp; access" flyout body: copy-invite CTA + collaborative/public toggles. A Component that
    /// reads live state from the <see cref="Loadable{T}"/> (component props freeze at mount), so the controlled toggles
    /// re-render when the optimistic <c>PatchDetail</c> lands. <paramref name="lib"/>/<paramref name="svc"/> are stable
    /// service instances — safe to capture at mount.</summary>
    sealed class PlaylistAccessFlyout : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly LibraryBridge _lib;
        readonly Services? _svc;
        readonly Signal<int> _inviteStatus = new(StatusIdle);
        readonly Signal<int> _collaborativeStatus = new(StatusIdle);
        readonly Signal<int> _publicStatus = new(StatusIdle);
        int _inviteEpoch, _collaborativeEpoch, _publicEpoch;

        public PlaylistAccessFlyout(Loadable<DetailModel> full, LibraryBridge lib, Services? svc)
        { _full = full; _lib = lib; _svc = svc; }

        public override Element Render()
        {
            var m = _full.Value.Value;              // live — re-renders on PatchDetail (controlled toggles)
            var caps = m.Capabilities;
            string? uri = m.ContextUri;
            int inviteStatus = _inviteStatus.Value;
            int collaborativeStatus = _collaborativeStatus.Value;
            int publicStatus = _publicStatus.Value;

            var content = new List<Element>(5)
            {
                new BoxEl
                {
                    Direction = 1, Gap = 4f,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Detail.Edit.InviteCollaborators)) { Size = 14f, Weight = 700, Color = Tok.TextPrimary },
                        new TextEl(Loc.Get(Strings.Detail.Edit.InviteHint)) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3 },
                    ],
                },
            };

            if (caps.CanAdministratePermissions && uri is not null)
                content.Add(CopyInviteCta(inviteStatus));

            if (caps.CanEditMetadata && caps.CanAdministratePermissions && uri is { } u)
            {
                content.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(0f, 2f, 0f, 2f) });
                content.Add(ToggleRow(
                    Loc.Get(Strings.Detail.Edit.Collaborative), Loc.Get(Strings.Detail.Edit.CollaborativeHint),
                    caps.IsCollaborative, collaborativeStatus,
                    () =>
                    {
                        var c = _full.Value.Peek();
                        _ = RunSaveAsync(
                            () => SetCollaborativeAsync(_lib, _full, _svc, u, !c.Capabilities.IsCollaborative),
                            _collaborativeStatus, () => ++_collaborativeEpoch, () => _collaborativeEpoch);
                    }));
                content.Add(ToggleRow(
                    Loc.Get(Strings.Detail.Edit.PublicPlaylist), Loc.Get(Strings.Detail.Edit.PublicHint),
                    m.IsPublic, publicStatus,
                    () =>
                    {
                        var c = _full.Value.Peek();
                        _ = RunSaveAsync(
                            () => SetVisibilityAsync(_lib, _full, _svc, u, !c.IsPublic),
                            _publicStatus, () => ++_publicEpoch, () => _publicEpoch);
                    }));
            }

            return new BoxEl
            {
                Direction = 1, Width = 300f, Gap = 12f, Padding = Edges4.All(16f),
                Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true, Shadow = Elevation.Flyout,
                Acrylic = Tok.AcrylicFlyout, BorderWidth = 1f, BorderColor = Tok.StrokeFlyoutDefault,
                Children = content.ToArray(),
            };
        }

        Element CopyInviteCta(int status)
        {
            var accent = Tok.AccentDefault;
            var ink = ColorContrast.PickContrast(accent);
            return new BoxEl
            {
                Direction = 0, Height = 34f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(17f), Fill = accent,
                HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,
                HoverScale = 1.02f, PressScale = 0.98f,
                Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
                IsEnabled = status != StatusSaving,
                OnClick = status == StatusSaving ? null : () => _ = RunSaveAsync(
                    () => CopyContributorInviteAsync(_lib, _full.Value.Peek(), _full, _svc),
                    _inviteStatus, () => ++_inviteEpoch, () => _inviteEpoch),
                Children =
                [
                    new BoxEl
                    {
                        Key = "invite-icon:" + status,
                        Animate = MotionRecipes.IconSwap,
                        Children = [status == StatusSaving
                            ? ProgressRing.Indeterminate(14f, true, ink)
                            : Icon(status == StatusSaved ? Icons.Accept : Icons.Link, 14f, ink)],
                    },
                    new BoxEl
                    {
                        Key = "invite-text:" + status,
                        Animate = MotionRecipes.TextSwap,
                        Children = [new TextEl(status == StatusSaved
                            ? Loc.Get(Strings.Auth.Copied)
                            : Loc.Get(Strings.Detail.Edit.CopyInviteLink))
                        { Size = 13f, Weight = 600, Color = ink }],
                    },
                ],
            };
        }

        static Element ToggleRow(string label, string caption, bool isOn, int status, Action onToggle) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                    Children =
                    [
                        new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextPrimary },
                        new TextEl(caption) { Size = 11.5f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2 },
                    ],
                },
                status != StatusIdle ? StatusChip(status) : new BoxEl { Width = 0f },
                ToggleSwitch.Create(new Signal<bool>(isOn), onChange: _ => onToggle(), isEnabled: status != StatusSaving, style: SettingsCard.CompactToggleStyle()),
            ],
        };
    }

    // ── owner overflow (invite / delete) ─────────────────────────────────────────────────────────────────────────

    sealed class OwnerOverflowMenu : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly DetailHandlers _h;

        public OwnerOverflowMenu(Loadable<DetailModel> full, DetailHandlers h) { _full = full; _h = h; }

        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            var svc = UseContext(Services.Slot);
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var accessHandle = UseRef<OverlayHandle?>(null);

            var m = _full.Value.Value;
            if (lib is null || !SpotifyEditsLive(svc) || m.ContextUri is not { } uri || !m.Capabilities.IsOwner)
                return new BoxEl();

            void Toggle()
            {
                if (overlay is null) return;
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = new List<MenuFlyoutItem>();
                AppendOwnerItems(items, overlay, lib, svc, _full, _h, () => anchor.Value, accessHandle);
                if (items.Count == 0) return;
                handle.Value = overlay.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Width = 40f, Height = 40f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(20f),
                HoverScale = 1.06f, PressScale = 0.94f,
                OnClick = Toggle,
                OnRealized = h => anchor.Value = h,
                Children = [Icon(Icons.More, 16f, Tok.TextSecondary)],
            }.Interactive(Interaction.Subtle);
        }
    }

    /// <summary>Append the playlist-owner overflow items (Invite collaborators · Delete playlist) to <paramref name="items"/>.
    /// Fully capability-gated (SpotifyEditsLive ∧ IsOwner), so both the rail's owner ⋯ menu and the vertical hero's More
    /// menu call it unconditionally and get identical rows. Invite reuses the shared access flyout; delete confirms then
    /// navigates home via <paramref name="h"/>. Every row carries a glyph so the menu's icon column stays consistent.</summary>
    internal static void AppendOwnerItems(List<MenuFlyoutItem> items, IOverlayService? overlay, LibraryBridge? lib,
        Services? svc, Loadable<DetailModel> full, DetailHandlers h, Func<NodeHandle> anchor, Ref<OverlayHandle?> accessHandle)
    {
        var m = full.Value.Peek();
        if (overlay is null || lib is null || !SpotifyEditsLive(svc) || m.ContextUri is not { } uri || !m.Capabilities.IsOwner)
            return;
        if (m.Capabilities.CanAdministratePermissions)
        {
            items.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.Edit.InviteCollaborators), Icons.Friends,
                Invoke: () => OpenAccessFlyout(overlay, lib, svc, full, anchor, accessHandle, FlyoutPlacement.BottomEdgeAlignedRight)));
            items.Add(MenuFlyoutItem.Separator);
        }
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.Edit.DeletePlaylist), Icons.Delete,
            Invoke: () => SettingsShared.Confirm(overlay,
                Loc.Get(Strings.Detail.Edit.DeletePlaylist),
                Loc.Get(Strings.Detail.Edit.DeletePlaylistConfirm),
                Loc.Get(Strings.Detail.Edit.DeletePlaylist),
                () => _ = DeletePlaylistAsync(lib, h, uri))));
    }

    static async Task DeletePlaylistAsync(LibraryBridge lib, DetailHandlers h, string uri)
    {
        try
        {
            await lib.DeletePlaylistAsync(uri).ConfigureAwait(false);
            h.Go("home", null);
        }
        catch (Exception ex) { PlaylistEditErrors.Toast(ex); }
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
