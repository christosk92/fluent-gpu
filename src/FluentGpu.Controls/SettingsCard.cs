using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI CommunityToolkit SettingsCard parity surface. The template metrics and states are lifted from
/// components/SettingsControls/src/SettingsCard: a 68px minimum card with optional header icon, header/description,
/// right/left/vertical content alignment, optional clickable action chevron, and the 476/286px wrap states.
/// </summary>
public static partial class SettingsCard
{
    public const string PartRoot = "Root";
    public const string PartHeaderIcon = "HeaderIcon";
    public const string PartHeader = "Header";
    public const string PartDescription = "Description";
    public const string PartContent = "Content";
    public const string PartActionIcon = "ActionIcon";

    public const float MinWidth = 148f;
    public const float MinHeight = 68f;
    public const float Padding = 16f;
    public const float ContentMinWidth = 120f;
    public const float HeaderIconMaxSize = 20f;
    public const float ActionIconMaxSize = 13f;
    public const float VerticalHeaderContentSpacing = 8f;
    public const float WrapThreshold = 476f;
    public const float WrapNoIconThreshold = 286f;

    static readonly string s_defaultActionIcon = ((char)0xE974).ToString();

    public enum ContentAlignment : byte
    {
        Right,
        Left,
        Vertical,
    }

    public sealed record Style
    {
        public ColorF Background { get; init; }
        public ColorF BackgroundPointerOver { get; init; }
        public ColorF BackgroundPressed { get; init; }
        public ColorF BackgroundDisabled { get; init; }
        public ColorF Foreground { get; init; }
        public ColorF ForegroundPointerOver { get; init; }
        public ColorF ForegroundPressed { get; init; }
        public ColorF ForegroundDisabled { get; init; }
        public ColorF DescriptionForeground { get; init; }
        public ColorF Border { get; init; }
        public GradientSpec? BorderPointerOver { get; init; }
        public ColorF BorderPressed { get; init; }
        public ColorF BorderDisabled { get; init; }
        public float CornerRadius { get; init; } = Radii.Control;
        public Edges4 Padding { get; init; } = Edges4.All(SettingsCard.Padding);
        public float MinWidth { get; init; } = SettingsCard.MinWidth;
        public float MinHeight { get; init; } = SettingsCard.MinHeight;
        public float HeaderIconMarginRight { get; init; } = 20f;
        public float HeaderMarginRight { get; init; } = 24f;
        public float ActionIconMarginLeft { get; init; } = 14f;
        public float BorderWidth { get; init; } = 1f;
        public float HeaderFontSize { get; init; } = 14f;
        public float DescriptionFontSize { get; init; } = 12f;
        public float BrushTransitionMs { get; init; } = 83f;
    }

    public sealed record Options
    {
        public string? Header { get; init; }
        public string? Description { get; init; }
        public string? HeaderIcon { get; init; }
        public Element? Content { get; init; }
        public ContentAlignment Alignment { get; init; } = ContentAlignment.Right;
        public bool IsClickEnabled { get; init; }
        public Action? OnClick { get; init; }
        public string? ActionIcon { get; init; }
        public string? ActionIconToolTip { get; init; }
        public bool IsActionIconVisible { get; init; } = true;
        public bool IsEnabled { get; init; } = true;
        public Style? Style { get; init; }
        public TemplateParts? Parts { get; init; }
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Background = Tok.FillCardDefault,
        BackgroundPointerOver = Tok.FillControlSecondary,
        BackgroundPressed = Tok.FillControlTertiary,
        BackgroundDisabled = Tok.FillControlDisabled,
        Foreground = Tok.TextPrimary,
        ForegroundPointerOver = Tok.TextPrimary,
        ForegroundPressed = Tok.TextSecondary,
        ForegroundDisabled = Tok.TextDisabled,
        DescriptionForeground = Tok.TextSecondary,
        Border = Tok.StrokeCardDefault,
        BorderPointerOver = Tok.ControlElevationBorder,
        BorderPressed = Tok.StrokeControlDefault,
        BorderDisabled = Tok.StrokeControlDefault,
    };

    internal static readonly Context<Options?> OptionsContext = new(null);

    public static Element Create(Options options)
        => Ctx.Provide(OptionsContext, options, Embed.Comp(() => new SettingsCardCore()));

    public static ToggleSwitch.Style CompactToggleStyle() => ToggleSwitch.DefaultStyle with
    {
        MinWidth = 0f,
        MinHeight = 36f,
    };

    internal static Element Build(Options o, float width)
    {
        var s = o.Style ?? DefaultStyle;
        if (o.Alignment == ContentAlignment.Left)
            return Root(o, s, BuildLeftContent(o, s));

        bool hasContent = o.Content is not null;
        bool wrap = width < WrapThreshold || o.Alignment == ContentAlignment.Vertical;
        bool hideIcon = width < WrapNoIconThreshold && o.Alignment == ContentAlignment.Right;

        Element header = BuildHeader(o, s, hideIcon);
        Element? content = hasContent ? BuildContent(o, wrap) : null;

        Element[] kids = wrap
            ? content is null
                ? [header]
                : [header, new BoxEl { Height = VerticalHeaderContentSpacing }, content]
            : [BuildRightRow(o, s, header, content)];

        return Root(o, s, new BoxEl { Direction = 1, Children = kids });
    }

    static BoxEl Root(Options o, Style s, Element child)
    {
        bool clickable = o.IsClickEnabled && o.OnClick is not null;
        var root = new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            MinWidth = s.MinWidth,
            MinHeight = s.MinHeight,
            Padding = s.Padding,
            Corners = CornerRadius4.All(s.CornerRadius),
            Fill = o.IsEnabled ? s.Background : s.BackgroundDisabled,
            HoverFill = clickable ? s.BackgroundPointerOver : s.Background,
            PressedFill = clickable ? s.BackgroundPressed : s.Background,
            BorderWidth = s.BorderWidth,
            BorderColor = o.IsEnabled ? s.Border : s.BorderDisabled,
            HoverBorderBrush = clickable ? s.BorderPointerOver : null,
            PressedBorderBrush = clickable ? GradientSpec.Solid(s.BorderPressed) : null,
            BrushTransitionMs = s.BrushTransitionMs,
            Role = clickable ? AutomationRole.Button : AutomationRole.None,
            Focusable = clickable,
            FocusVisualMargin = Edges4.All(-3f),
            IsEnabled = o.IsEnabled,
            OnClick = clickable ? o.OnClick : null,
            Children = [child],
        };

        var applied = o.Parts.Apply(PartRoot, root);
        return applied with
        {
            OnClick = clickable ? o.OnClick : null,
            Role = clickable ? AutomationRole.Button : AutomationRole.None,
            Children = root.Children,
        };
    }

    static Element BuildRightRow(Options o, Style s, Element header, Element? content)
    {
        var kids = new List<Element>(3) { header };
        if (content is not null) kids.Add(content);
        if (ActionGlyph(o, s) is { } action) kids.Add(action);
        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            Children = kids.ToArray(),
        };
    }

    static Element BuildHeader(Options o, Style s, bool hideIcon)
    {
        var kids = new List<Element>(2);
        if (!hideIcon && o.HeaderIcon is { Length: > 0 } glyph)
            kids.Add(o.Parts.Apply(PartHeaderIcon, new BoxEl
            {
                Width = HeaderIconMaxSize,
                Height = HeaderIconMaxSize,
                Margin = new Edges4(2f, 0f, s.HeaderIconMarginRight, 0f),
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Children =
                [
                    new TextEl(glyph)
                    {
                        Size = HeaderIconMaxSize,
                        FontFamily = Theme.IconFont,
                        Color = o.IsEnabled ? s.Foreground : s.ForegroundDisabled,
                        HoverColor = s.ForegroundPointerOver,
                        PressedColor = s.ForegroundPressed,
                        DisabledColor = s.ForegroundDisabled,
                    },
                ],
            }));

        var textKids = new List<Element>(2);
        if (o.Header is { Length: > 0 } h)
            textKids.Add(o.Parts.Apply(PartHeader, new TextEl(h)
            {
                Size = s.HeaderFontSize,
                Color = o.IsEnabled ? s.Foreground : s.ForegroundDisabled,
                HoverColor = s.ForegroundPointerOver,
                PressedColor = s.ForegroundPressed,
                DisabledColor = s.ForegroundDisabled,
                Wrap = TextWrap.Wrap,
            }));
        if (o.Description is { Length: > 0 } d)
            textKids.Add(o.Parts.Apply(PartDescription, new TextEl(d)
            {
                Size = s.DescriptionFontSize,
                Color = o.IsEnabled ? s.DescriptionForeground : s.ForegroundDisabled,
                HoverColor = s.DescriptionForeground,
                PressedColor = s.ForegroundPressed,
                DisabledColor = s.ForegroundDisabled,
                Wrap = TextWrap.Wrap,
            }));

        kids.Add(new BoxEl
        {
            Direction = 1,
            Gap = 1f,
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            Margin = new Edges4(0f, 0f, s.HeaderMarginRight, 0f),
            Children = textKids.ToArray(),
        });

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            Children = kids.ToArray(),
        };
    }

    static Element BuildContent(Options o, bool wrap)
    {
        var content = o.Parts.Apply(PartContent, new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Justify = wrap ? FlexJustify.Start : FlexJustify.End,
            MinWidth = ContentMinWidth,
            Grow = wrap ? 1f : 0f,
            Shrink = 0f,
            Children = o.Content is null ? [] : [o.Content],
        });

        return content;
    }

    static Element BuildLeftContent(Options o, Style s)
    {
        Element content = o.Content ?? new BoxEl();
        return o.Parts.Apply(PartContent, new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Start,
            Children = [content],
        });
    }

    static Element? ActionGlyph(Options o, Style s)
    {
        if (!o.IsClickEnabled || !o.IsActionIconVisible) return null;
        string glyph = o.ActionIcon is { Length: > 0 } g ? g : s_defaultActionIcon;
        return o.Parts.Apply(PartActionIcon, new BoxEl
        {
            Width = ActionIconMaxSize,
            Height = ActionIconMaxSize,
            Margin = new Edges4(s.ActionIconMarginLeft, 0f, 0f, 0f),
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children =
            [
                new TextEl(glyph)
                {
                    Size = ActionIconMaxSize,
                    FontFamily = Theme.IconFont,
                    Color = o.IsEnabled ? s.Foreground : s.ForegroundDisabled,
                    HoverColor = s.ForegroundPointerOver,
                    PressedColor = s.ForegroundPressed,
                    DisabledColor = s.ForegroundDisabled,
                },
            ],
        });
    }
}

sealed class SettingsCardCore : Component
{
    readonly Signal<float> _w = new(0f);

    public override Element Render()
    {
        var options = UseContext(SettingsCard.OptionsContext) ?? new SettingsCard.Options();
        float w = _w.Value;
        float effective = w > 0.5f ? w : 720f;
        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            OnBoundsChanged = r =>
            {
                if (r.W > 0f && MathF.Abs(r.W - _w.Peek()) > 0.5f)
                    _w.Value = r.W;
            },
            Children = [SettingsCard.Build(options, effective)],
        };
    }
}
