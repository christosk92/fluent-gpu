using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The rich, immediate-apply detail-list filter surface. Each selector owns a concrete signal for the lifetime
/// of one flyout session; its callback projects the changed facet back into the aggregate list filter signal.</summary>
sealed class TrackFilterFlyout : Component
{
    readonly IReadSignal<TrackFilterState> _filters;
    readonly Action<TrackFilterState> _setFilters;
    readonly TrackFilterCapabilities _caps;
    readonly Signal<int> _scope;
    readonly Signal<int> _explicit;
    readonly Signal<int> _video;
    readonly Signal<bool> _liked;
    readonly Signal<bool> _playable;
    readonly Signal<int> _duration;
    readonly Signal<int> _added;
    readonly Signal<int> _origin;
    readonly Signal<bool> _durationOpen = new(false);
    readonly Signal<bool> _addedOpen = new(false);
    readonly Signal<bool> _originOpen = new(false);

    static readonly TemplateParts DisclosureParts = new()
    {
        [Expander.PartHeader] = static h => h with
        {
            MinHeight = 42f,
            Padding = new Edges4(8f, 0f, 0f, 0f),
            Fill = ColorF.Transparent,
            BorderWidth = 0f,
            Corners = Radii.ControlAll,
        },
        [Expander.PartChevron] = static c => c with
        {
            Width = 28f,
            Height = 28f,
            Margin = new Edges4(8f, 0f, 4f, 0f),
        },
        [Expander.PartContent] = static c => c with
        {
            Padding = new Edges4(10f, 2f, 8f, 10f),
            MinHeight = 0f,
            Fill = ColorF.Transparent,
            BorderWidth = 0f,
            Margin = default,
            Corners = default,
        },
    };

    public TrackFilterFlyout(IReadSignal<TrackFilterState> filters, Action<TrackFilterState> setFilters,
        TrackFilterCapabilities caps)
    {
        _filters = filters;
        _setFilters = setFilters;
        _caps = caps;
        var f = filters.Peek();
        _scope = new((int)f.SearchScope);
        _explicit = new((int)f.ExplicitMode);
        _video = new((int)f.VideoMode);
        _liked = new(f.LikedOnly);
        _playable = new(f.PlayableOnly);
        _duration = new((int)f.Duration);
        _added = new((int)f.Added);
        _origin = new((int)f.Origin);
    }

    public override Element Render()
    {
        var current = _filters.Value;
        ColorF accent = Tok.AccentTextPrimary;
        string status = current.ActiveCount == 0
            ? Loc.Get(Strings.Detail.Filter.AllTracks)
            : Strings.Detail.Filter.ActiveCount(current.ActiveCount.ToString());

        Element SectionTitle(string text) => new TextEl(text.ToUpperInvariant())
        {
            Size = 10f,
            Weight = 700,
            Color = Tok.TextTertiary,
            Margin = new Edges4(4f, 0f, 4f, 8f),
        };

        Element Section(string title, params Element[] children) => new BoxEl
        {
            Direction = 1,
            Gap = 8f,
            Padding = new Edges4(12f, 10f, 12f, 10f),
            Children = [SectionTitle(title), .. children],
        };

        Element ScopeButton(TrackSearchScope value, string label)
        {
            int index = (int)value;
            bool selected = _scope.Value == index;
            Action choose = () =>
            {
                _scope.Value = index;
                _setFilters(_filters.Peek() with { SearchScope = value });
            };
            return new BoxEl
            {
                Height = 34f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                Fill = selected ? accent with { A = 0.14f } : Tok.FillSubtleTransparent,
                HoverFill = selected ? accent with { A = 0.21f } : Tok.FillSubtleSecondary,
                PressedFill = selected ? accent with { A = 0.10f } : Tok.FillSubtleTertiary,
                BorderWidth = 1f,
                BorderColor = selected ? accent with { A = 0.34f } : Tok.StrokeControlDefault,
                BrushTransitionMs = 83f,
                PressScale = 0.98f,
                PressDurationMs = 83f,
                OnClick = choose,
                Role = AutomationRole.RadioButton,
                Children = [new TextEl(label)
                {
                    Size = 13f,
                    Weight = selected ? (ushort)600 : (ushort)450,
                    Color = selected ? accent : Tok.TextSecondary,
                }],
            };
        }

        Element TraitFacet(string glyph, string label, Signal<int> signal, Action<int> changed) => new BoxEl
        {
            Direction = 1,
            Gap = 7f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0,
                    Gap = 9f,
                    AlignItems = FlexAlign.Center,
                    Padding = new Edges4(4f, 0f, 4f, 0f),
                    Children =
                    [
                        Icon(glyph, 16f, Tok.TextTertiary),
                        new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextSecondary },
                    ],
                },
                Segmented.Create(
                [
                    new SegmentedItem(Loc.Get(Strings.Detail.Filter.All)),
                    new SegmentedItem(Loc.Get(Strings.Detail.Filter.Hide)),
                    new SegmentedItem(Loc.Get(Strings.Detail.Filter.Only)),
                ],
                signal,
                onChange: changed),
            ],
        };

        Element StatusChoice(string glyph, string label, Signal<bool> signal, Action<bool> changed)
        {
            var checkStyle = CheckBox.DefaultStyle with
            {
                MinWidth = 0f,
                MinHeight = 32f,
                FontSize = 13f,
                ContentGap = 7f,
            };
            return new BoxEl
            {
                Direction = 0,
                Gap = 8f,
                MinHeight = 38f,
                Padding = new Edges4(9f, 2f, 7f, 2f),
                AlignItems = FlexAlign.Center,
                Corners = Radii.ControlAll,
                Fill = Tok.FillSubtleTransparent,
                HoverFill = Tok.FillSubtleSecondary,
                BorderWidth = 1f,
                BorderColor = Tok.StrokeControlDefault,
                BrushTransitionMs = 83f,
                Children =
                [
                    Icon(glyph, 15f, Tok.TextTertiary),
                    CheckBox.Create(label, signal, changed, style: checkStyle),
                ],
            };
        }

        Element Disclosure(string glyph, string label, Signal<int> value, Signal<bool> open,
            IReadOnlyList<string> labels, Action<int> changed, Action<bool> opened)
        {
            var header = new BoxEl
            {
                Direction = 0,
                Gap = 9f,
                AlignItems = FlexAlign.Center,
                Children =
                [
                    Icon(glyph, 16f, Tok.TextTertiary),
                    new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextPrimary },
                    new BoxEl
                    {
                        Grow = 1f,
                        AlignItems = FlexAlign.End,
                        Children =
                        [
                            new TextEl(Prop.Of(() => labels[value.Value]))
                            { Size = 12f, Color = Tok.TextTertiary },
                        ],
                    },
                ],
            };
            var choices = RadioButtons.Create(labels, value, changed, maxColumns: 2);
            return Embed.Comp(
                new Expander.ExpanderSlots(header, choices, DisclosureParts),
                () => new Expander
            {
                IsExpanded = open,
                OnChange = opened,
            });
        }

        void SetFlag(TrackFilterFlags flag, bool enabled)
        {
            var f = _filters.Peek();
            var flags = enabled ? f.Flags | flag : f.Flags & ~flag;
            _setFilters(f with { Flags = flags });
        }

        void OpenOnly(Signal<bool> target, bool open)
        {
            if (!open) return;
            if (!ReferenceEquals(target, _durationOpen)) _durationOpen.Value = false;
            if (!ReferenceEquals(target, _addedOpen)) _addedOpen.Value = false;
            if (!ReferenceEquals(target, _originOpen)) _originOpen.Value = false;
        }

        void ClearAll()
        {
            _scope.Value = (int)TrackSearchScope.Everything;
            _explicit.Value = (int)TrackTraitMode.All;
            _video.Value = (int)TrackTraitMode.All;
            _liked.Value = false;
            _playable.Value = false;
            _duration.Value = (int)TrackDurationRange.Any;
            _added.Value = (int)TrackAddedRange.Any;
            _origin.Value = (int)TrackOriginFilter.Any;
            _durationOpen.Value = false;
            _addedOpen.Value = false;
            _originOpen.Value = false;
            _setFilters(TrackFilterState.Default);
        }

        var bodyChildren = new List<Element>(10)
        {
            Section(Loc.Get(Strings.Detail.Filter.SearchIn),
                new GridEl
                {
                    Columns = [TrackSize.Star(), TrackSize.Star()],
                    ColGap = 4f,
                    RowGap = 4f,
                    RowHeight = 34f,
                    Children =
                    [
                        ScopeButton(TrackSearchScope.Everything, Loc.Get(Strings.Detail.Filter.Everything)),
                        ScopeButton(TrackSearchScope.Title, Loc.Get(Strings.Detail.Filter.TitleOnly)),
                        ScopeButton(TrackSearchScope.Artist, Loc.Get(Strings.Detail.Filter.ArtistOnly)),
                        ScopeButton(TrackSearchScope.Album, Loc.Get(Strings.Detail.Filter.AlbumOnly)),
                    ],
                }),
            new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
            Section(Loc.Get(Strings.Detail.Filter.Content),
                TraitFacet(Icons.Important, Loc.Get(Strings.Detail.Filter.ExplicitContent), _explicit,
                    value => _setFilters(_filters.Peek() with { ExplicitMode = (TrackTraitMode)value })),
                TraitFacet(Icons.Movie, Loc.Get(Strings.Detail.Filter.VideoTracks), _video,
                    value => _setFilters(_filters.Peek() with { VideoMode = (TrackTraitMode)value }))),
        };

        var statusChoices = new List<Element>(2);
        if (_caps.HasLibrary || current.LikedOnly)
            statusChoices.Add(StatusChoice(Icons.Heart, Loc.Get(Strings.Detail.Filter.LikedOnly), _liked,
                value => SetFlag(TrackFilterFlags.LikedOnly, value)));
        if (_caps.HasUnavailable || current.PlayableOnly)
            statusChoices.Add(StatusChoice(Icons.Accept, Loc.Get(Strings.Detail.Filter.PlayableOnly), _playable,
                value => SetFlag(TrackFilterFlags.PlayableOnly, value)));
        if (statusChoices.Count > 0)
        {
            var columns = statusChoices.Count > 1
                ? new[] { TrackSize.Star(), TrackSize.Star() }
                : new[] { TrackSize.Star() };
            bodyChildren.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault });
            bodyChildren.Add(Section(Loc.Get(Strings.Detail.Filter.Status),
                new GridEl
                {
                    Columns = columns,
                    ColGap = 5f,
                    RowGap = 5f,
                    Children = statusChoices.ToArray(),
                }));
        }

        var more = new List<Element>(3)
        {
            Disclosure(Icons.Clock, Loc.Get(Strings.Detail.Filter.Duration), _duration, _durationOpen,
                [
                    Loc.Get(Strings.Detail.Filter.AnyDuration),
                    Loc.Get(Strings.Detail.Filter.UnderThree),
                    Loc.Get(Strings.Detail.Filter.ThreeToFive),
                    Loc.Get(Strings.Detail.Filter.OverFive),
                ],
                value => _setFilters(_filters.Peek() with { Duration = (TrackDurationRange)value }),
                open => OpenOnly(_durationOpen, open)),
        };
        if (_caps.HasDateAdded || current.Added != TrackAddedRange.Any)
            more.Add(Disclosure(Icons.Calendar, Loc.Get(Strings.Detail.Filter.DateAdded), _added, _addedOpen,
                [
                    Loc.Get(Strings.Detail.Filter.AnyTime),
                    Loc.Get(Strings.Detail.Filter.LastSevenDays),
                    Loc.Get(Strings.Detail.Filter.LastThirtyDays),
                    Loc.Get(Strings.Detail.Filter.LastSixMonths),
                    Loc.Get(Strings.Detail.Filter.LastYear),
                ],
                value => _setFilters(_filters.Peek() with { Added = (TrackAddedRange)value }),
                open => OpenOnly(_addedOpen, open)));
        if (_caps.HasMixedOrigin || current.Origin != TrackOriginFilter.Any)
            more.Add(Disclosure(Icons.MusicNote, Loc.Get(Strings.Detail.Filter.Source), _origin, _originOpen,
                [
                    Loc.Get(Strings.Detail.Filter.AnySource),
                    Loc.Get(Strings.Detail.Filter.Streamed),
                    Loc.Get(Strings.Detail.Filter.Local),
                ],
                value => _setFilters(_filters.Peek() with { Origin = (TrackOriginFilter)value }),
                open => OpenOnly(_originOpen, open)));
        bodyChildren.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault });
        bodyChildren.Add(Section(Loc.Get(Strings.Detail.Filter.MoreFilters), more.ToArray()));

        return new BoxEl
        {
            Direction = 1,
            Width = 368f,
            MinWidth = 320f,
            MaxWidth = 368f,
            MaxHeight = 560f,
            MinHeight = 0f,
            ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0,
                    AlignItems = FlexAlign.Center,
                    Gap = 11f,
                    Padding = new Edges4(14f, 12f, 14f, 10f),
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 34f,
                            Height = 34f,
                            AlignItems = FlexAlign.Center,
                            Justify = FlexJustify.Center,
                            Corners = CornerRadius4.All(10f),
                            Fill = accent with { A = 0.14f },
                            Children = [Icon(Icons.Filter, 17f, accent)],
                        },
                        new BoxEl
                        {
                            Direction = 1,
                            Gap = 1f,
                            Grow = 1f,
                            Children =
                            [
                                new TextEl(Loc.Get(Strings.Detail.Filter.Title))
                                { Size = 15f, Weight = 650, Color = Tok.TextPrimary },
                                new TextEl(status) { Size = 12f, Color = Tok.TextSecondary },
                            ],
                        },
                    ],
                },
                new BoxEl { Height = 1f, Margin = new Edges4(8f, 0f, 8f, 0f), Fill = Tok.StrokeDividerDefault },
                new ScrollEl
                {
                    ContentSized = true,
                    Grow = 1f,
                    MinHeight = 0f,
                    MaxHeight = 430f,
                    Content = new BoxEl
                    {
                        Direction = 1,
                        MinWidth = 0f,
                        Children = bodyChildren.ToArray(),
                    },
                },
                new BoxEl { Height = 1f, Margin = new Edges4(8f, 0f, 8f, 0f), Fill = Tok.StrokeDividerDefault },
                new BoxEl
                {
                    Direction = 0,
                    Height = 46f,
                    Padding = new Edges4(14f, 6f, 10f, 6f),
                    AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(current.IsDefault
                            ? Loc.Get(Strings.Detail.Filter.NoFiltersApplied)
                            : status)
                        {
                            Size = 11f,
                            Color = Tok.TextTertiary,
                            Grow = 1f,
                            MinWidth = 0f,
                            MaxLines = 1,
                            Trim = TextTrim.CharacterEllipsis,
                        },
                        Button.Standard(
                            Loc.Get(Strings.Detail.Filter.ClearFilters),
                            ClearAll,
                            isEnabled: !current.IsDefault),
                    ],
                },
            ],
        };
    }
}
