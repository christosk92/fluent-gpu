using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The one global shell-shortcut editor. It sits before the Curated outline but is not one of its sections, so
/// its count, selection and reorder indices can never leak into the sidebar document's section operations.</summary>
sealed class SidebarTopBarCard : Component
{
    const string DragKind = "wavee.customizer.topbar";

    readonly SidebarCustomizerPage _page;
    readonly Signal<int> _dragEpoch = new(0);
    NodeHandle _focusNode;
    bool _focusApplied;

    public SidebarTopBarCard(SidebarCustomizerPage page) => _page = page;

    public override Element Render()
    {
        var prefs = _page.Prefs;
        int version = prefs?.LayoutVersion.Value ?? 0;
        _ = _dragEpoch.Value;
        var band = prefs?.TopBar ?? SidebarCustomLayout.DefaultTopBar;
        bool full = band.Count >= SidebarLayoutReducer.MaxTopBarItems;
        var hooks = UseContext(InputHooks.Current);

        var reorder = UseMemo(static () => new Reorderable(DragKind)
        {
            LiveProject = false,
            ShowInsertionLine = true,
        }, DepKey.Empty);
        Configure(reorder, band.Count);

        UseLayoutEffect(() =>
        {
            if (_focusApplied || !_page.FocusTopBarRequested || _focusNode.IsNull) return;
            _focusApplied = true;
            hooks.FocusNode?.Invoke(_focusNode, true);
        }, DepKey.From(version, _page.FocusTopBarRequested ? 1 : 0));

        var content = new List<Element>(band.Count + 1);
        if (band.Count == 0)
            content.Add(new BoxEl
            {
                Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.M),
                Children =
                [
                    new TextEl(Loc.Get(CzLoc.TopBarEmpty))
                    {
                        Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2,
                    },
                ],
            });
        else
            content.Add(Strip(reorder, band));

        if (full)
            content.Add(new TextEl(Loc.Get(TopBarLoc.CapReached))
            {
                Size = 11f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2,
            });

        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = Spacing.S,
            Padding = Edges4.All(Spacing.M),
            Corners = Radii.CardAll, Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f,
                            Children =
                            [
                                new TextEl(Loc.Get(CzLoc.TopBar))
                                {
                                    Size = 13f, Weight = 600, Color = Tok.TextPrimary,
                                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                },
                                new TextEl(Loc.Get(CzLoc.TopBarGlobal))
                                {
                                    Size = 11f, Color = Tok.TextTertiary,
                                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                },
                            ],
                        },
                        new TextEl(Loc.Format(CzLoc.SectionCount,
                            ("used", band.Count), ("max", SidebarLayoutReducer.MaxTopBarItems)))
                        {
                            Size = 11f, Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
                        },
                        Embed.Comp(() => new TopBarAddButton(_page, !full,
                            band.Count == 0 ? CaptureFocus : null)) with { Key = full ? "topbar-add:full" : "topbar-add" },
                    ],
                },
                new BoxEl { Direction = 1, Shrink = 0f, Gap = Spacing.XS, Children = [.. content] },
            ],
        };
    }

    Element Strip(Reorderable reorder, IReadOnlyList<SidebarItemSpec> band)
    {
        var list = (BoxEl)reorder.List(new BoxEl
        {
            Direction = 0, Gap = Spacing.S,
            Children = [.. Rows(reorder, band)],
        });
        return ScrollView(list with { Grow = 0f, Shrink = 0f }, horizontal: true) with
        {
            Grow = 0f, Height = CzItemRow.TopBarItemHeight,
            AutoEdgeFade = true, SuppressScrollBar = true,
            ScrollKey = "customizer.topbar",
        };
    }

    List<Element> Rows(Reorderable reorder, IReadOnlyList<SidebarItemSpec> band)
    {
        var rows = new List<Element>(band.Count);
        for (int i = 0; i < band.Count; i++)
        {
            string id = band[i].Id;
            Element row = Embed.Comp(() => new CzItemRow(
                _page, SidebarIds.TopBarSection, id, compact: true)) with { Key = "topbar-item:" + id };
            var wrapped = (BoxEl)reorder.Item(i, row, key: id);
            wrapped = wrapped with { Width = CzItemRow.TopBarItemWidth, Shrink = 0f };
            if (i == 0)
            {
                var prior = wrapped.OnRealized;
                wrapped = wrapped with
                {
                    OnRealized = handle =>
                    {
                        prior?.Invoke(handle);
                        CaptureFocus(handle);
                    },
                };
            }
            rows.Add(wrapped);
        }
        return rows;
    }

    void Configure(Reorderable reorder, int count)
    {
        reorder.Scene = Context.Scene;
        reorder.RequestRender = Bump;
        reorder.ItemCount = count;
        reorder.ItemExtent = CzItemRow.TopBarItemWidth;
        reorder.Spacing = Spacing.S;
        reorder.Horizontal = true;
        reorder.ExtentOf = null;
        reorder.ItemOf = null;
        reorder.OnReorder = (from, to) => _page.DispatchTopBar(new MoveTopBarItem(from, to));
    }

    void CaptureFocus(NodeHandle handle) => _focusNode = handle;

    void Bump()
    {
        _dragEpoch.Value = _dragEpoch.Peek() + 1;
        Context.RequestRerender();
    }
}

/// <summary>The top-bar add menu: route/library picker, current track, or action picker.</summary>
sealed class TopBarAddButton : Component
{
    readonly SidebarCustomizerPage _page;
    readonly bool _enabled;
    readonly Action<NodeHandle>? _realized;

    public TopBarAddButton(SidebarCustomizerPage page, bool enabled, Action<NodeHandle>? realized)
    {
        _page = page;
        _enabled = enabled;
        _realized = realized;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var overlay = UseContext(Overlay.Service);

        void Open()
        {
            if (!_enabled || overlay is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(Items(), () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss,
                    Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        var button = new BoxEl
        {
            Width = 28f, Height = 28f, Shrink = 0f, Corners = Radii.ControlAll,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Role = AutomationRole.Button, Focusable = _enabled, IsEnabled = _enabled,
            Cursor = _enabled ? CursorId.Hand : CursorId.Arrow,
            OnRealized = node => { anchor.Value = node; _realized?.Invoke(node); },
            OnClick = Open,
            Children = [Icon(Icons.Add, 14f, _enabled ? Tok.TextSecondary : Tok.TextDisabled)],
        };
        return ToolTip.Wrap(_enabled ? button.Interactive(Interaction.Subtle) : button,
            Loc.Get(_enabled ? CzLoc.ItemAdd : TopBarLoc.CapReached));
    }

    IReadOnlyList<MenuFlyoutItem> Items()
    {
        bool hasTrack = _page.Acts?.Playback?.CurrentTrack.Peek() is { Uri.Length: > 0 };
        return
        [
            new MenuFlyoutItem(Loc.Get(CzLoc.TopBarAddItem), Icons.Add, true,
                () => SidebarPickers.OpenItem(_page, Add)),
            new MenuFlyoutItem(Loc.Get(CzLoc.TopBarAddTrack), Icons.MusicNote, hasTrack, AddTrack),
            new MenuFlyoutItem(Loc.Get(CzLoc.ItemAction), Icons.RefineSparkle, true, AddAction),
        ];
    }

    void Add(SidebarItemSpec item)
        => _page.DispatchTopBar(new AddTopBarItem(item, _page.Prefs?.TopBar.Count ?? 0));

    void AddTrack()
    {
        if (_page.Acts?.Playback?.CurrentTrack.Peek() is not { Uri.Length: > 0 } track) return;
        Add(new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Track, track.Uri,
            SidebarEntityKind.Track, FallbackTitle: track.Title, FallbackImageUrl: track.Image?.Url));
    }

    void AddAction()
        => SidebarActionPicker.Open(_page, null, binding => Add(new SidebarItemSpec(
            SidebarIds.NewItem(), SidebarItemTarget.Action, binding.ActionKey, Action: binding)));
}
