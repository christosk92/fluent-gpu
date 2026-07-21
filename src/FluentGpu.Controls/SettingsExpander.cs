using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// CommunityToolkit-style SettingsExpander: an Expander whose header is a SettingsCard and whose body hosts compact
/// SettingsCard rows. The generic Expander owns motion; this type owns settings layout, indentation, and row density.
/// </summary>
public static partial class SettingsExpander
{
    public const string PartHeaderCard = "HeaderCard";
    public const string PartItems = "Items";
    public const string PartExpanderHeader = "ExpanderHeader";
    public const string PartExpanderChevron = "ExpanderChevron";
    public const string PartExpanderContent = "ExpanderContent";

    public const float HeaderChevronWidth = 32f;
    public const float ItemMinHeight = 52f;
    public static readonly Edges4 HeaderPadding = new(16f, 16f, 4f, 16f);
    public static readonly Edges4 ItemPadding = new(58f, 8f, 44f, 8f);
    public static readonly Edges4 ClickableItemPadding = new(58f, 8f, 16f, 8f);

    public sealed record Style
    {
        public SettingsCard.Style HeaderCardStyle { get; init; } = SettingsCard.DefaultStyle with
        {
            Background = ColorF.Transparent,
            BackgroundPointerOver = ColorF.Transparent,
            BackgroundPressed = ColorF.Transparent,
            BackgroundDisabled = ColorF.Transparent,
            BorderWidth = 0f,
            Padding = HeaderPadding,
            MinHeight = SettingsCard.MinHeight,
        };

        public SettingsCard.Style ItemCardStyle { get; init; } = SettingsCard.DefaultStyle with
        {
            Padding = ItemPadding,
            MinHeight = ItemMinHeight,
            CornerRadius = 0f,
            WrapThreshold = 0f,
            WrapNoIconThreshold = 0f,
        };

        public SettingsCard.Style ClickableItemCardStyle { get; init; } = SettingsCard.DefaultStyle with
        {
            Padding = ClickableItemPadding,
            MinHeight = ItemMinHeight,
            CornerRadius = 0f,
            WrapThreshold = 0f,
            WrapNoIconThreshold = 0f,
        };
    }

    public sealed record Options
    {
        public string? Header { get; init; }
        public string? Description { get; init; }
        public string? HeaderIcon { get; init; }
        public Element? Content { get; init; }
        public IReadOnlyList<Element> Items { get; init; } = [];
        public Element? ItemsHeader { get; init; }
        public Element? ItemsFooter { get; init; }
        public bool InitiallyExpanded { get; init; }
        public Signal<bool>? IsExpanded { get; init; }
        /// <summary>Optional <c>onChange</c> sugar — fired with the new open state on a header toggle.</summary>
        public Action<bool>? OnChange { get; init; }
        public Style? Style { get; init; }
        public TemplateParts? Parts { get; init; }
    }

    public static Element Create(Options options)
        => Embed.Comp(options, () => new SettingsExpanderCore());

    public static Element Item(string header, string? description, Element? content = null,
                               SettingsCard.ContentAlignment alignment = SettingsCard.ContentAlignment.Right,
                               bool isEnabled = true, bool isClickEnabled = false, Action? onClick = null,
                               string? headerIcon = null, Style? style = null)
    {
        var s = style ?? new Style();
        return SettingsCard.Create(new SettingsCard.Options
        {
            Header = header,
            Description = description,
            HeaderIcon = headerIcon,
            Content = content,
            Alignment = alignment,
            IsEnabled = isEnabled,
            IsClickEnabled = isClickEnabled,
            IsActionIconVisible = isClickEnabled,
            OnClick = onClick,
            Style = isClickEnabled ? s.ClickableItemCardStyle : s.ItemCardStyle,
        });
    }
}

sealed class SettingsExpanderCore : Component
{
    public override Element Render()
    {
        var o = UseProps<SettingsExpander.Options>();
        var s = o.Style ?? new SettingsExpander.Style();

        var headerCard = SettingsCard.Create(new SettingsCard.Options
        {
            Header = o.Header,
            Description = o.Description,
            HeaderIcon = o.HeaderIcon,
            Content = o.Content,
            IsActionIconVisible = false,
            Style = s.HeaderCardStyle,
            Parts = o.Parts,
        });

        headerCard = o.Parts.Apply(SettingsExpander.PartHeaderCard, headerCard);

        var itemKids = new List<Element>(o.Items.Count * 2 + 2);
        if (o.ItemsHeader is not null) itemKids.Add(o.ItemsHeader);
        for (int i = 0; i < o.Items.Count; i++)
        {
            if (i > 0 || o.ItemsHeader is not null)
                itemKids.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault });
            itemKids.Add(o.Items[i]);
        }
        if (o.ItemsFooter is not null) itemKids.Add(o.ItemsFooter);

        var items = o.Parts.Apply(SettingsExpander.PartItems, new BoxEl
        {
            Direction = 1,
            Children = itemKids.ToArray(),
        });

        var parts = new TemplateParts
        {
            [Expander.PartHeader] = h =>
            {
                var styled = o.Parts.Apply(SettingsExpander.PartExpanderHeader, h);
                return styled with
                {
                    MinHeight = SettingsCard.MinHeight,
                    Padding = Edges4.All(0f),
                };
            },
            [Expander.PartChevron] = c =>
            {
                var styled = o.Parts.Apply(SettingsExpander.PartExpanderChevron, c);
                return styled with
                {
                    Width = SettingsExpander.HeaderChevronWidth,
                    Height = SettingsExpander.HeaderChevronWidth,
                    Margin = new Edges4(0f, 0f, 8f, 0f),
                };
            },
            [Expander.PartContent] = c =>
            {
                var styled = c with
                {
                    Padding = Edges4.All(0f),
                    MinHeight = 16f,
                    Children = [items],
                };
                return o.Parts.Apply(SettingsExpander.PartExpanderContent, styled) with { Children = [items] };
            },
        };

        // Deliver the (freshly rebuilt) header/content/parts to the Expander as RE-PUSHED props — NOT as constructor
        // args. The Expander is an autonomous component whose factory is discarded on reuse, so field values are frozen
        // at first mount; re-pushing the slots keeps dynamic content (e.g. a live equalizer curve) reactive across
        // parent re-renders. Only the stable open-state stays on the (frozen) fields.
        return Embed.Comp(new Expander.ExpanderSlots(headerCard, items, parts),
            () => new Expander
            {
                InitiallyExpanded = o.InitiallyExpanded,
                IsExpanded = o.IsExpanded,
                OnChange = o.OnChange,
            });
    }
}
