using System;
using System.Collections.Generic;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── Command palette (WS7 G8b) ─────────────────────────────────────────────────────────────────────────────────────
// The shell's Ctrl+K "go to page" palette, built on the Popup primitive over the registry-derived search index. The
// shell owns an IsOpen signal (flipped by a Ctrl+K KeyAccelerator); this component anchors a light-dismissable popup
// under a top-centre bar and mounts a fresh PaletteContent each open (so query/selection reset for free). Fuzzy
// subsequence match, Up/Down move the highlight, Enter navigates, Esc/click-outside dismiss (light-dismiss from the
// overlay host). It is also surfaced as a Patterns page later; the shell wiring is the primary use.
sealed class CommandPalette : Component
{
    public required (string Label, string Key)[] Index;
    public required Action<string> Navigate;
    public required Signal<bool> IsOpen;

    public override Element Render()
    {
        var isOpen = IsOpen;
        var index = Index;
        var navigate = Navigate;

        // A full-width, zero-height anchor at the top of the overlay column: the popup drops from just under it,
        // horizontally centred (the anchor is centred by the mounting ZStack column below).
        var anchor = new BoxEl { Width = 560f, Height = 0f };

        return Popup.Create(
            anchor,
            content: () => Embed.Comp(() => new PaletteContent
            {
                Index = index,
                Navigate = navigate,
                Close = () => isOpen.Value = false,
            }),
            isOpen: isOpen,
            placement: FlyoutPlacement.BottomEdgeAlignedLeft,
            options: new PopupOptions(FocusTrap: true, Chrome: PopupChrome.Popup));
    }

    /// <summary>Mount the palette centred near the top of the shell overlay (a ZStack lane above the page). The anchor
    /// bar hugs the top with a small inset so the dropped panel reads as a centred command bar.</summary>
    public static Element Overlay((string Label, string Key)[] index, Action<string> navigate, Signal<bool> isOpen)
        => new BoxEl
        {
            Grow = 1, Direction = 1, AlignItems = FlexAlign.Center, HitTestVisible = false,
            Padding = new Edges4(0, 64, 0, 0),
            Children = [Embed.Comp(() => new CommandPalette { Index = index, Navigate = navigate, IsOpen = isOpen })],
        };
}

/// <summary>The palette body: a search field over a ranked result list with keyboard roving. Mounted fresh on each
/// open (the overlay builds its content on open), so <c>UseSignal</c> state starts clean every time — no reset logic.</summary>
sealed class PaletteContent : Component
{
    public required (string Label, string Key)[] Index;
    public required Action<string> Navigate;
    public required Action Close;

    const int MaxResults = 8;

    public override Element Render()
    {
        var query = UseSignal("");
        var sel = UseSignal(0);

        var results = Filter(Index, query.Value);   // reading query.Value re-renders the list as the user types
        int selClamped = results.Count == 0 ? 0 : Math.Clamp(sel.Value, 0, results.Count - 1);

        void Commit(int i)
        {
            if (i < 0 || i >= results.Count) return;
            Navigate(results[i].Key);
            Close();
        }

        void OnKey(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Down:
                    if (results.Count > 0) sel.Value = (selClamped + 1) % results.Count;
                    e.Handled = true; break;
                case Keys.Up:
                    if (results.Count > 0) sel.Value = (selClamped - 1 + results.Count) % results.Count;
                    e.Handled = true; break;
                case Keys.Enter:
                    Commit(selClamped); e.Handled = true; break;
                case Keys.Escape:
                    Close(); e.Handled = true; break;
            }
        }

        var rows = new List<Element>(results.Count + 1);
        if (results.Count == 0)
            rows.Add(new BoxEl { Padding = new Edges4(14, 12, 14, 12), Children = [new TextEl("No matching pages") { Size = 13f, Color = Tok.TextTertiary }] });
        for (int i = 0; i < results.Count; i++)
        {
            int idx = i;
            bool active = idx == selClamped;
            rows.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 10f, MinHeight = 36f,
                Padding = new Edges4(14, 6, 14, 6), Corners = Radii.ControlAll,
                Fill = active ? Tok.FillSubtleSecondary : ColorF.Transparent,
                HoverFill = Tok.FillSubtleSecondary,
                OnClick = () => Commit(idx),
                Children =
                [
                    new TextEl(Icons.Grid) { Size = 14f, FontFamily = Theme.IconFont, Color = active ? Tok.AccentDefault : Tok.TextTertiary },
                    new BoxEl { Grow = 1, Children = [new TextEl(results[idx].Label) { Size = 14f, Color = Tok.TextPrimary }] },
                    active ? new TextEl("Enter") { Size = 11f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" } : new BoxEl(),
                ],
            });
        }

        return new BoxEl
        {
            Direction = 1, Width = 560f, MaxHeight = 460f, Gap = 4f, Padding = new Edges4(8, 8, 8, 8),
            OnKeyDown = OnKey,
            Children =
            [
                TextBox.Create(query, options: new TextBox.TextBoxOptions { Placeholder = "Go to a page — type to search…" }),
                new BoxEl { Height = 2f },
                new BoxEl { Direction = 1, Gap = 1f, Children = rows.ToArray() },
            ],
        };
    }

    // Case-insensitive: exact-prefix and substring rank above a scattered subsequence match; ties keep registry order.
    static List<(string Label, string Key)> Filter((string Label, string Key)[] index, string query)
    {
        var q = query.Trim();
        var scored = new List<(int Score, int Ord, (string Label, string Key) Entry)>();
        for (int i = 0; i < index.Length; i++)
        {
            int s = q.Length == 0 ? 0 : ScoreOf(index[i].Label, q);
            if (s < 0) continue;
            scored.Add((s, i, index[i]));
        }
        scored.Sort((a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.Ord.CompareTo(b.Ord));
        var outp = new List<(string, string)>(Math.Min(MaxResults, scored.Count));
        for (int i = 0; i < scored.Count && outp.Count < MaxResults; i++) outp.Add(scored[i].Entry);
        return outp;
    }

    // Lower score = better. -1 = no match.
    static int ScoreOf(string label, string q)
    {
        var l = label;
        int idx = l.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (idx == 0) return 0;         // prefix
        if (idx > 0) return 1;          // substring
        return Subsequence(l, q) ? 2 : -1;   // scattered letters in order
    }

    static bool Subsequence(string label, string q)
    {
        int j = 0;
        for (int i = 0; i < label.Length && j < q.Length; i++)
            if (char.ToLowerInvariant(label[i]) == char.ToLowerInvariant(q[j])) j++;
        return j == q.Length;
    }
}
