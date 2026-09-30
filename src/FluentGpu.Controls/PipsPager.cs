using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>WinUI <c>PipsPagerButtonVisibility</c> (PipsPager.idl:6-11) — how the previous/next navigation buttons
/// show. Both default to <see cref="Collapsed"/> (PipsPager.idl:56-59).</summary>
public enum PipsPagerButtonVisibility : byte
{
    Visible,
    /// <summary>Hidden (Opacity 0, layout slot kept — PipsPager.xaml:19-23) until the pointer is over the pager or
    /// keyboard focus is inside it (PipsPager.cpp:568-647).</summary>
    VisibleOnPointerOver,
    Collapsed,
}

/// <summary>A WinUI PipsPager (controls\dev\PipsPager): a strip of 12x24 dot buttons clipped to
/// <c>maxVisiblePips</c> (default 5, PipsPager.idl:50-51) with an animated scroll that keeps the selected pip
/// centered (ScrollToCenterOfViewport, PipsPager.cpp:240-253), plus optional 24x24 previous/next navigation buttons
/// (glyphs EDDB/EDDC at 8px, rotated -90° in the horizontal orientation — PipsPager_themeresources.xaml:102-106,
/// PipsPager.xaml:59-79). Keyboard: Left/Up move focus to the previous pip and Right/Down to the next regardless of
/// orientation (PipsPager.cpp:161-191); focus enters the strip at the SELECTED pip (the OnPipsAreaGettingFocus
/// redirect, PipsPager.cpp:590-612 — implemented as a roving single tab stop). Controlled.
/// <para>Clicking the ALREADY-selected pip is a no-op for <c>onChange</c> (WinUI raises SelectedIndexChanged only through a
/// real DP change) — it fires the additive <c>onReselect</c> callback instead. A host whose content can sit BETWEEN pages
/// (a free-pannable paged shelf) needs that edge: the pip still reads "page 2" while the strip rests mid-page, and the
/// re-click is the user asking to be put back on the boundary.</para></summary>
public static class PipsPager
{
    // Template parts (see TemplateParts). Each part's doc lists the props the control OWNS (re-asserted after any
    // modifier — a Parts customization cannot win those).
    /// <summary>The outer pager (the returned root). Owned: Children (nav buttons + the pip viewport), Role.</summary>
    public const string PartRoot = "Root";
    /// <summary>One 12x24 pip button — the modifier runs PER DOT (a small static count, not virtualized). The
    /// selected/normal glyph sizing is stock per-render styling a modifier may override. Owned: OnClick (select),
    /// Role, OnKeyDown (arrow roving focus), OnRealized (focus-handle capture).</summary>
    public const string PartDot = "Dot";

    public static Element Create(int count, Signal<int>? selectedIndex = null, Action<int>? onChange = null, TemplateParts? parts = null,
                                 int maxVisiblePips = 5,
                                 PipsPagerButtonVisibility previousButtonVisibility = PipsPagerButtonVisibility.Collapsed,
                                 PipsPagerButtonVisibility nextButtonVisibility = PipsPagerButtonVisibility.Collapsed,
                                 bool vertical = false,
                                 Action<int>? onReselect = null)
        => Embed.Comp(new Props(count, selectedIndex, onChange, parts, maxVisiblePips,
                                previousButtonVisibility, nextButtonVisibility, vertical, onReselect),
                      () => new PipsPagerCore());

    /// <summary>Controlled props RE-PUSHED to the core (<c>Embed.Comp(props, …)</c>) — a reused ComponentEl never
    /// re-runs its factory — so props are delivered live (equality-gated); the core reads them with <c>UseProps</c>
    /// (the RadioButtons convention). The selected index is a caller <see cref="Signal{T}"/> (null ⇒ auto-materialize).</summary>
    internal sealed record Props(int Count, Signal<int>? Selected, Action<int>? OnChange, TemplateParts? Parts,
                                 int MaxVisiblePips, PipsPagerButtonVisibility PrevVisibility,
                                 PipsPagerButtonVisibility NextVisibility, bool Vertical,
                                 Action<int>? OnReselect = null);

    /// <summary>Value-controlled sibling of <see cref="Create"/> (the <c>ToggleButton.Controlled</c> precedent, E12
    /// — home-redesign-remediation.md §2 E12/§3.2): the selected index is a caller VALUE, not a signal, so this
    /// control writes NO selection state of its own — <see cref="PipsPagerControlledCore"/> never materializes an
    /// internal signal the way <see cref="PipsPagerCore"/>'s <c>own</c> does. A click ALWAYS calls
    /// <paramref name="onSelect"/>(i), including a re-click on the pip <paramref name="selectedIndex"/> already
    /// names — unlike <see cref="Create"/>, which folds that case into the separate <c>onReselect</c> channel and
    /// skips <c>onChange</c>. A paged shelf whose scroll can rest BETWEEN pages (free-pannable) needs exactly that
    /// edge on the ONE channel: the pip still reads "page 2" while the strip sits mid-page, and the re-click is the
    /// user asking to be put back on the boundary — <c>ShelfController.GoTo</c> re-snaps every time, so it must fire
    /// every time. <paramref name="count"/>/<paramref name="selectedIndex"/> are plain values delivered through the
    /// props-re-push contract (<c>Embed.Comp(props, factory)</c>): a caller that re-renders with a new
    /// <paramref name="selectedIndex"/> re-skins the strip in place (live, no remount — same reconciler reuse as
    /// <see cref="Create"/>'s signal bind). A caller that wants the strip to follow a value with NO PARENT re-render
    /// (e.g. <c>ShelfController.Page</c> ticking while the owning row stays still) wraps this call in its own small
    /// <c>Component</c> that reads the source signal in <c>Render()</c> — the <c>ChapterHeader</c>/<c>ShelfPips</c>
    /// precedent (home-redesign-remediation.md §3.2) — rather than this control taking a signal itself: the pip
    /// strip's own animation/focus state (the scroll-to-center glide, the roving tab stop) still needs a STABLE
    /// node regardless of who owns re-rendering, which <c>Embed.Comp</c>'s reuse already gives it.</summary>
    public static Element Controlled(int count, int selectedIndex, Action<int> onSelect, TemplateParts? parts = null,
                                     int maxVisiblePips = 5,
                                     PipsPagerButtonVisibility previousButtonVisibility = PipsPagerButtonVisibility.Collapsed,
                                     PipsPagerButtonVisibility nextButtonVisibility = PipsPagerButtonVisibility.Collapsed,
                                     bool vertical = false)
        => Embed.Comp(new ControlledProps(count, selectedIndex, onSelect, parts, maxVisiblePips,
                                          previousButtonVisibility, nextButtonVisibility, vertical),
                      () => new PipsPagerControlledCore());

    /// <summary>Controlled props RE-PUSHED to <see cref="PipsPagerControlledCore"/> — see <see cref="Controlled"/>
    /// for the value-controlled contract. No <c>OnReselect</c> channel: <c>OnSelect</c> is the only one, and it
    /// fires for every click, same index or not.</summary>
    internal sealed record ControlledProps(int Count, int SelectedIndex, Action<int> OnSelect, TemplateParts? Parts,
                                           int MaxVisiblePips, PipsPagerButtonVisibility PrevVisibility,
                                           PipsPagerButtonVisibility NextVisibility, bool Vertical);
}

/// <summary>The stateful core shared by <see cref="PipsPagerCore"/> (<see cref="PipsPager.Create"/>) and
/// <see cref="PipsPagerControlledCore"/> (<see cref="PipsPager.Controlled"/>, E12): pointer-over/focus reveal for
/// the nav buttons, the scroll-to-center strip animation, and the roving arrow-key focus across pips — all of it
/// identical regardless of who owns the selected value. The two subclasses differ only in WHERE <c>selected</c>
/// comes from and what a click does with it (<see cref="RenderPager"/>'s <c>selectDifferent</c>/<c>selectSame</c>
/// delegates) — the hooks below are declared at THIS class's call sites, so each subclass instance gets its own
/// stable per-line hook identity across renders (hooks are keyed by call site, not call order — see the
/// <c>Component</c> cell-backed-hooks note) regardless of which concrete subclass is mounted.</summary>
internal abstract class PipsPagerCoreBase : Component
{
    const string PipGlyph = "";    // PipsPagerNormalGlyph / PipsPagerSelectedGlyph (PipsPager_themeresources.xaml:100-101)
    const string PrevGlyph = "";   // PipsPagerPreviousPageButtonGlyph (PipsPager_themeresources.xaml:102)
    const string NextGlyph = "";   // PipsPagerNextPageButtonGlyph (PipsPager_themeresources.xaml:103)

    const float PipMain = 12f;           // pip extent along the pager axis: PipsPagerHorizontalOrientationButtonWidth 12 /
                                         // PipsPagerVerticalOrientationButtonHeight 12 (PipsPager_themeresources.xaml:94, :97)
    const float PipCross = 24f;          // PipsPagerHorizontalOrientationButtonHeight 24 / vertical width 24 (:95-96)
    const float NavSize = 24f;           // PipsPagerNavigationButtonWidth/Height (PipsPager_themeresources.xaml:104-105)
    const float NavGlyphSize = 8f;       // PipsPagerNavigationButtonFontSize (PipsPager_themeresources.xaml:106)
    const float SelectedGlyphSize = 6f;  // PipsPagerSelectedGlyphFontSize (PipsPager_themeresources.xaml:107)
    const float NormalGlyphSize = 4f;    // PipsPagerNormalGlyphFontSize (PipsPager_themeresources.xaml:108)
    const float NavScalePressed = 0.875f;// PipsPagerNavigationButtonScalePressed (PipsPager_themeresources.xaml:109)

    /// <summary>Shared render body. <paramref name="selectedRaw"/> is the UNCLAMPED value from whichever source the
    /// subclass owns (a signal's <c>.Value</c> or the controlled value straight from props); <paramref name="selectDifferent"/>
    /// fires for a click on any OTHER pip, <paramref name="selectSame"/> for a click on the pip <paramref name="selectedRaw"/>
    /// already (clamped) names — <see cref="PipsPagerCore"/> writes its signal + <c>OnChange</c> only for the former and
    /// routes the latter to <c>OnReselect</c>; <see cref="PipsPagerControlledCore"/> routes BOTH to the single <c>OnSelect</c>
    /// channel (Controlled's whole point — see <see cref="PipsPager.Controlled"/>).</summary>
    protected Element RenderPager(int count, int selectedRaw, TemplateParts? parts, int maxVisiblePips,
        PipsPagerButtonVisibility prevVisibility, PipsPagerButtonVisibility nextVisibility, bool vertical,
        Action<int> selectDifferent, Action<int> selectSame)
    {
        // Hooks — stable order, unconditionally, before any early-out.
        var hooks = UseContext(InputHooks.Current);
        var handles = UseRef(new List<NodeHandle>()).Value;   // pip node per index (roving focus targets)
        var stripRef = UseRef<NodeHandle>(default);
        var stripSeeded = UseRef(false);
        var pointerOver = UseSignal(false);
        var focusWithin = UseSignal(false);

        count = Math.Max(0, count);
        int selected = (uint)selectedRaw < (uint)count ? selectedRaw : 0;   // clamp like OnSelectedPageIndexChanged (PipsPager.cpp:419-428)
        int maxVisible = Math.Max(0, maxVisiblePips);

        // Animated scroll-to-center: the strip translates so the selected pip is centred in the clipped viewport —
        // WinUI UpdateSelectedPip → ScrollToCenterOfViewport with AnimationDesired(true) (PipsPager.cpp:240-272).
        // 250ms on the 0,0,0,1 spline approximates the ScrollViewer bring-into-view glide.
        UseEffect(() =>
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null || stripRef.Value.IsNull || !scene.IsLive(stripRef.Value)) return;
            var ch = vertical ? AnimChannel.TranslateY : AnimChannel.TranslateX;
            float viewportMain = PipMain * Math.Min(maxVisible, count);   // CalculateScrollViewerSize: default*(n-1)+selected, both 12 (PipsPager.cpp:274-291)
            float maxScroll = MathF.Max(0f, PipMain * count - viewportMain);
            float to = -Math.Clamp(PipMain * selected + PipMain / 2f - viewportMain / 2f, 0f, maxScroll);
            if (!stripSeeded.Value)
            {
                stripSeeded.Value = true;
                anim.Animate(stripRef.Value, ch, to, to, 1f, Easing.Linear);   // seed the resting offset, no visible motion
                return;
            }
            // A mid-flight retarget starts from the LIVE offset (the bring-into-view glide never snaps on interrupt).
            float from = anim.TryGetTrackValue(stripRef.Value, ch, out float live) ? live : to;
            anim.Animate(stripRef.Value, ch, from, to, Motion.ControlNormal, Easing.FluentPopOpen);
        }, DepKey.From(HashCode.Combine(selected, count, maxVisible, vertical)));

        while (handles.Count < count) handles.Add(NodeHandle.Null);

        // Arrow roving focus: Left AND Up → previous pip, Right AND Down → next, regardless of orientation
        // (PipsPager.cpp OnKeyDown:161-191 maps both axes; the direction enum only steers the XY search). WinUI marks
        // the key handled unconditionally and its XY search may leave the control at the edges — not reachable from a
        // control factory, so the key stops here (the RadioButtons edge convention). Selection does NOT follow focus.
        void OnPipKey(int i, KeyEventArgs a)
        {
            if (a.Handled || count == 0) return;
            int target;
            switch (a.KeyCode)
            {
                case Keys.Left or Keys.Up: target = i - 1; break;      // previous (PipsPager.cpp:175-181)
                case Keys.Right or Keys.Down: target = i + 1; break;   // next (PipsPager.cpp:182-188)
                default: return;
            }
            a.Handled = true;
            if ((uint)target < (uint)count && target < handles.Count && !handles[target].IsNull)
                (hooks.MoveFocusVisual ?? hooks.RestoreFocus)?.Invoke(handles[target]);
        }

        // Pointer-over / focus-within reveal state, tracked only when a button is VisibleOnPointerOver (the signals
        // re-render the pager on transitions; m_isPointerOver/m_isFocused — PipsPager.cpp:613-647, :568-588).
        bool needsReveal = prevVisibility == PipsPagerButtonVisibility.VisibleOnPointerOver
                        || nextVisibility == PipsPagerButtonVisibility.VisibleOnPointerOver;
        bool revealOn = needsReveal && (pointerOver.Value || focusWithin.Value);
        // WinUI counts only non-pointer focus (FocusState != Pointer, PipsPager.cpp:570-583); the engine does not
        // surface the focus source, so a pointer-acquired focus also reveals — sanctioned small deviation.
        Action<bool>? trackFocus = needsReveal
            ? f => { if (focusWithin.Peek() != f) focusWithin.Value = f; }
            : null;

        Element Pip(int i)
        {
            int index = i;
            bool isSelected = index == selected;
            // A click routes to selectDifferent/selectSame — PipsPagerCore treats same-value as a no-op for the
            // VALUE channel (WinUI raises SelectedIndexChanged only through an actual DP change, PipsPager.cpp:418-448)
            // and surfaces the re-click on the separate OnReselect channel; PipsPagerControlledCore routes BOTH to
            // the single OnSelect channel (see PipsPager.Controlled).
            Action select = () =>
            {
                if (index != selected) selectDifferent(index);
                else selectSame(index);
            };
            // Glyph EA3B in the icon font; selected 6px / normal 4px (PipsPagerButtonBaseStyle + SelectedPipButtonStyle,
            // PipsPager_themeresources.xaml:209, :282). The wrapper carries the state size morph as a composited scale:
            // PointerOver → the selected size 6 (:229-231), Pressed → back to the normal size 4 (:245-247). WinUI swaps
            // FontSize in 0ms discrete keyframes; ours rides the eased hover/press progress — sanctioned deviation.
            var glyph = new BoxEl
            {
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                HoverScale = isSelected ? 1f : SelectedGlyphSize / NormalGlyphSize,
                PressScale = isSelected ? NormalGlyphSize / SelectedGlyphSize : 1f,
                Children =
                [
                    new TextEl(PipGlyph)
                    {
                        Size = isSelected ? SelectedGlyphSize : NormalGlyphSize,
                        Color = Tok.FillControlStrong,                  // PipsPagerSelectionIndicatorForeground = ControlStrongFillColorDefault (themeresources:15, :18)
                        HoverColor = Tok.TextSecondary,                 // ForegroundPointerOver = TextFillColorSecondary (:16)
                        PressedColor = Tok.TextSecondary,               // ForegroundPressed = TextFillColorSecondary (:17)
                        DisabledColor = Tok.FillControlStrongDisabled,  // ForegroundDisabled = ControlStrongFillColorDisabled (:19)
                        FontFamily = Theme.IconFont,
                    },
                ],
            };
            Action<KeyEventArgs> onKey = a => OnPipKey(index, a);
            Action<NodeHandle> capture = h => { while (handles.Count <= index) handles.Add(NodeHandle.Null); handles[index] = h; };
            var dot = new BoxEl
            {
                Key = "p" + index,
                Direction = 0,
                Width = vertical ? PipCross : PipMain,    // 12x24 horizontal / 24x12 vertical (themeresources:94-97)
                Height = vertical ? PipMain : PipCross,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,               // pip CornerRadius = ControlCornerRadius (themeresources:210)
                FocusVisualMargin = Edges4.All(0f),       // FocusVisualMargin = 0 (themeresources:207)
                // Roving single tab stop: focus entering the strip lands on the SELECTED pip (OnPipsAreaGettingFocus,
                // PipsPager.cpp:590-612); arrows move between pips from there.
                TabStop = isSelected,
                Role = AutomationRole.Pager,
                OnClick = select,
                OnKeyDown = onKey,
                OnFocusChanged = trackFocus,
                OnRealized = capture,
                Children = [glyph],
            };
            // Parts: restyle each pip (the modifier sees the per-dot selected sizing); the select/keyboard mechanics
            // always win.
            return parts.Apply(PipsPager.PartDot, dot) with
            {
                OnClick = select, Role = AutomationRole.Pager, OnKeyDown = onKey, OnRealized = capture,
            };
        }

        var dots = new Element[count];
        for (int i = 0; i < count; i++) dots[i] = Pip(i);

        // The clipped viewport + abutting pip strip: StackLayout with no Spacing → 12px pitch (PipsPager.xaml:85-87);
        // the viewport caps at maxVisiblePips pips (SetScrollViewerMaxSize, PipsPager.cpp:293-310). The AnimEngine owns
        // the strip's translate (the scroll-to-center effect above).
        float viewMain = PipMain * Math.Min(maxVisible, count);
        var strip = new BoxEl
        {
            Direction = vertical ? (byte)1 : (byte)0,
            OnRealized = h => stripRef.Value = h,
            Children = dots,
        };
        var viewport = new BoxEl
        {
            Width = vertical ? PipCross : viewMain,
            Height = vertical ? viewMain : PipCross,
            ClipToBounds = true,
            Children = [strip],
        };

        // 24x24 transparent navigation button. Foreground ControlStrong → TextSecondary hover/press, ControlStrong
        // disabled (themeresources:28-31); background/border transparent in every state (:20-27). The inner wrapper
        // carries the WinUI ScaleTransform'd Border: pressed scale 0.875 (:156-163) and the -90° rotation the
        // HORIZONTAL orientation applies to the whole (square, transparent) button (PipsPager.xaml:61-79 — the EDDB/
        // EDDC carets are authored for the vertical orientation).
        Element NavButton(string glyph, bool hiddenOnEdge, PipsPagerButtonVisibility visibility, Action onClick)
        {
            // UpdateIndividualNavigationButtonVisualState (PipsPager.cpp:193-247): no WrapMode support yet, so the
            // edge always hides; generally-visible additionally needs pages and pips to exist.
            bool generallyVisible = !hiddenOnEdge && count != 0 && maxVisible > 0;
            bool shown = (visibility == PipsPagerButtonVisibility.Visible || revealOn) && generallyVisible;
            return new BoxEl
            {
                Width = NavSize,
                Height = NavSize,                       // 24x24 (themeresources:104-105, :122-123)
                Corners = Radii.ControlAll,             // CornerRadius = ControlCornerRadius (themeresources:124)
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                // Hidden = Opacity 0 with the layout slot kept (PipsPager.xaml:19-23, :40-44); only Collapsed unmounts.
                Opacity = shown ? 1f : 0f,
                FocusVisualMargin = Edges4.All(0f),     // FocusVisualMargin = 0 (themeresources:118)
                // The edge-hidden button goes to the Disabled state so it cannot be clicked (PipsPager.cpp:213-218);
                // a merely-unrevealed VisibleOnPointerOver button stays enabled, like WinUI's opacity-0 button.
                IsEnabled = generallyVisible,
                Role = AutomationRole.Button,
                OnClick = onClick,
                OnFocusChanged = trackFocus,
                Children =
                [
                    new BoxEl
                    {
                        AlignItems = FlexAlign.Center,
                        Justify = FlexJustify.Center,
                        PressScale = NavScalePressed,
                        Rotation = vertical ? 0f : -90f,
                        Children =
                        [
                            new TextEl(glyph)
                            {
                                Size = NavGlyphSize,                            // FontSize 8 (themeresources:106)
                                FontFamily = Theme.IconFont,
                                Color = Tok.FillControlStrong,                  // NavigationButtonForeground (themeresources:28)
                                HoverColor = Tok.TextSecondary,                 // PointerOver (:29)
                                PressedColor = Tok.TextSecondary,               // Pressed (:30)
                                DisabledColor = Tok.FillControlStrongDisabled,  // Disabled (:31)
                            },
                        ],
                    },
                ],
            };
        }

        // OnPreviousButtonClicked / OnNextButtonClicked (PipsPager.cpp:522-565, sans wrap). Both land on a DIFFERENT
        // pip whenever they act at all (the ni != selected guard), so both always route through selectDifferent.
        Action prevClick = () =>
        {
            if (count <= 1) return;
            int ni = Math.Max(0, selected - 1);
            if (ni != selected) selectDifferent(ni);
        };
        Action nextClick = () =>
        {
            if (count <= 1) return;
            int ni = Math.Min(selected + 1, count - 1);
            if (ni != selected) selectDifferent(ni);
        };

        bool prevMounted = prevVisibility != PipsPagerButtonVisibility.Collapsed;
        bool nextMounted = nextVisibility != PipsPagerButtonVisibility.Collapsed;
        var children = new Element[1 + (prevMounted ? 1 : 0) + (nextMounted ? 1 : 0)];
        int ci = 0;
        if (prevMounted)
            children[ci++] = NavButton(PrevGlyph, hiddenOnEdge: selected == 0, prevVisibility, prevClick);
        children[ci++] = viewport;
        if (nextMounted)
            children[ci] = NavButton(NextGlyph, hiddenOnEdge: selected == count - 1, nextVisibility, nextClick);

        var root = new BoxEl
        {
            Direction = vertical ? (byte)1 : (byte)0,   // RootPanel StackPanel, Orientation-bound (PipsPager.xaml:15)
            AlignItems = FlexAlign.Center,
            Role = AutomationRole.Pager,
            OnHoverMove = needsReveal ? _ => { if (!pointerOver.Peek()) pointerOver.Value = true; } : null,
            OnPointerExit = needsReveal ? () => { if (pointerOver.Peek()) pointerOver.Value = false; } : null,
            Children = children,
        };
        return parts.Apply(PipsPager.PartRoot, root) with { Children = children, Role = AutomationRole.Pager };
    }
}

/// <summary>Mounted by <see cref="PipsPager.Create"/>: the selected index lives in a caller <see cref="Signal{T}"/>
/// (or an internally auto-materialized one) — a click on a DIFFERENT pip writes it and fires <c>OnChange</c>; a
/// click on the pip already selected fires the separate <c>OnReselect</c> instead (no signal write).</summary>
internal sealed class PipsPagerCore : PipsPagerCoreBase
{
    public override Element Render()
    {
        var props = UseProps<PipsPager.Props>();
        var own = UseSignal(0);   // auto-materialize (unconditional hook)
        var sig = props.Selected ?? own;   // caller's value signal, else the internal one (one code path)
        return RenderPager(props.Count, sig.Value, props.Parts, props.MaxVisiblePips,
            props.PrevVisibility, props.NextVisibility, props.Vertical,
            selectDifferent: index => { sig.Value = index; props.OnChange?.Invoke(index); },
            selectSame: index => props.OnReselect?.Invoke(index));
    }
}

/// <summary>Mounted by <see cref="PipsPager.Controlled"/> (E12) — see that factory for the value-controlled
/// contract. Writes NO selection state at all: every click, same pip or not, only calls
/// <see cref="PipsPager.ControlledProps.OnSelect"/>.</summary>
internal sealed class PipsPagerControlledCore : PipsPagerCoreBase
{
    public override Element Render()
    {
        var props = UseProps<PipsPager.ControlledProps>();
        return RenderPager(props.Count, props.SelectedIndex, props.Parts, props.MaxVisiblePips,
            props.PrevVisibility, props.NextVisibility, props.Vertical,
            selectDifferent: props.OnSelect,
            selectSame: props.OnSelect);
    }
}
