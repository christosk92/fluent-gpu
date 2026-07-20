using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>WinUI <c>NumberBoxSpinButtonPlacementMode</c>: where (if anywhere) the up/down spin buttons appear.</summary>
public enum NumberBoxSpinButtonPlacementMode : byte
{
    Hidden,   // no spin buttons (WinUI default) — editable field only
    Compact,  // a popup of up/down buttons that opens while the field is focused
    Inline,   // a stacked up/down column at the trailing edge of the field
}

/// <summary>WinUI <c>NumberBoxValidationMode</c>: what happens to invalid / out-of-range input on commit.</summary>
public enum NumberBoxValidationMode : byte
{
    InvalidInputOverwritten,  // invalid text reverts to the current value; out-of-range values are clamped
    Disabled,                 // no validation — accept whatever parses (NaN if it doesn't)
}

/// <summary>
/// WinUI <c>NumberBox</c>: an editable numeric <see cref="EditableText"/> field with an optional spin affix. The numeric
/// model is a caller-owned <see cref="Value"/> signal (<c>double.NaN</c> = cleared); the visible text is derived from it
/// through the <see cref="Formatter"/> and re-parsed on commit. Spin buttons step by <see cref="SmallChange"/> and are
/// <see cref="NumberBoxSpinButtonPlacementMode.Hidden"/> by default. <see cref="NumberBoxSpinButtonPlacementMode.Inline"/>
/// puts the two side-by-side repeat buttons (▲E70E / ▼E70D, NumberBox.xaml:174–175) at the trailing edge of the field;
/// <see cref="NumberBoxSpinButtonPlacementMode.Compact"/> shows the EC8F popup-indicator chevron (NumberBox.xaml:365)
/// and opens the up/down buttons in an Overlay popup while focused (the popup auto-opens on focus and closes on blur —
/// NumberBox.cpp:414–438; the indicator is a non-interactive TextBlock, not a toggle). Keyboard stepping follows
/// OnNumberBoxKeyDown (NumberBox.cpp:533–558): Up/Down = ±SmallChange, PageUp/PageDown = ±LargeChange, handled on key
/// DOWN so OS auto-repeat repeats the step. Validation (clamp / revert) runs on Enter and on blur per
/// <see cref="ValidationMode"/>; with <see cref="AcceptsExpression"/> a typed arithmetic expression is evaluated on
/// commit (shunting-yard over + - * / ^ and parentheses — NumberBoxParser port). Mouse-wheel over the field while it
/// is focused also steps ±SmallChange and consumes the event before any enclosing viewport scrolls (PointerWheelChanged
/// → OnNumberBoxScroll, NumberBox.cpp:40 + :578–597).
/// </summary>
public sealed class NumberBox : Component
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The field row wrapper hosting the InputBox part — the Compact popup's anchor and the
    /// Up/Down/PageUp/PageDown stepping host. Owned: OnRealized (anchor capture, chained), OnKeyDown (step keys),
    /// Children (the EditableText mount).</summary>
    public const string PartField = "Field";
    /// <summary>One spin RepeatButton — applied to BOTH the inline pair (UpSpinButton/DownSpinButton) and the Compact
    /// popup pair (PopupUpSpinButton/PopupDownSpinButton), like WinUI's shared spin-button style. The enabled-state
    /// fills are computed per render BEFORE the modifier. Owned: OnClick (step), Repeats (auto-repeat), Role.</summary>
    public const string PartSpinButton = "SpinButton";
    /// <summary>The caret glyph TextEl inside each spin button (style via <c>Parts.Set&lt;TextEl&gt;(PartSpinGlyph, …)</c>;
    /// the state colors are computed before the modifier). Owned: none.</summary>
    public const string PartSpinGlyph = "SpinGlyph";

    public Signal<double>? Value;          // caller-owned numeric value (NaN = cleared); null -> internal seeded from Initial
    public double Initial = double.NaN;
    public double Minimum = double.MinValue;
    public double Maximum = double.MaxValue;
    public double SmallChange = 1;
    public double LargeChange = 10;
    public bool IsWrapEnabled = false;
    public bool AcceptsExpression = false;
    public NumberBoxSpinButtonPlacementMode SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden;
    public NumberBoxValidationMode ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten;
    public string PlaceholderText = "";
    public string? Header = null;
    public string? Description = null;
    public float Width = 120f;             // NumberBoxMinWidth = 120
    public bool IsEnabled = true;
    /// <summary>The WinUI <c>NumberBox.Text</c> property (microsoft.ui.xaml.controls idl): the FORMATTED string, two-way.
    /// Caller-owned signal; external (programmatic) writes are validated immediately — OnTextPropertyChanged →
    /// UpdateValueToText → ValidateInput (NumberBox.cpp:333–339). Null → an internal signal.</summary>
    public Signal<string>? Text;
    /// <summary>Value → display string (the <c>INumberFormatter2.FormatDouble</c> seam, NumberBox.cpp:644). Default =
    /// invariant round-trip after rounding to 10 significant digits (<c>m_displayRounder.SignificantDigits(10)</c>,
    /// NumberBox.cpp:216 + :643).</summary>
    public Func<double, string>? Formatter;
    /// <summary>Display string → value (the <c>INumberParser.ParseDouble</c> seam, NumberBox.cpp:493–497). Default =
    /// invariant <c>double.TryParse</c> (null = parse failure). With <see cref="AcceptsExpression"/> the expression
    /// evaluator runs instead (operands parse invariantly), mirroring NumberBoxParser::Compute.</summary>
    public Func<string, double?>? Parser;
    public Action<double, double>? OnValueChanged;   // (oldValue, newValue) — WinUI ValueChangedEventArgs
    public Field<double>? Field;                     // form-validation.md: invalid border + touched-on-blur + message row
    public Style? StyleOverride;
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    /// <summary>Dims from NumberBox.xaml / NumberBox_themeresources.xaml.</summary>
    public sealed record Style
    {
        public float MinWidth { get; init; } = 120f;          // NumberBoxMinWidth (NumberBox_themeresources.xaml:35)
        public float FieldHeight { get; init; } = 32f;        // TextControlThemeMinHeight (generic.xaml:96)
        public float SpinButtonWidth { get; init; } = 32f;    // NumberBoxSpinButtonStyle MinWidth=32 (NumberBox.xaml:185)
        public float SpinGlyphSize { get; init; } = 12f;      // NumberBoxSpinButtonStyle FontSize=12 (NumberBox.xaml:189)
        public float SpinButtonMargin { get; init; } = 4f;    // UpSpinButton Margin=4 / DownSpinButton 0,4,4,4 (NumberBox.xaml:174–175)
        public float PopupSpinButtonSize { get; init; } = 36f;// NumberBoxPopupSpinButtonStyle 36x36 (NumberBox.xaml:197–198)
        public float PopupSpinGlyphSize { get; init; } = 16f; // popup FontSize 16 (NumberBox.xaml:201)
        public float PopupPadding { get; init; } = 6f;        // PopupContentRoot Padding=6 (NumberBox.xaml:116)
        public string UpGlyph { get; init; } = Icons.CaretUpSolid;     // E70E (NumberBox.xaml:174)
        public string DownGlyph { get; init; } = Icons.CaretDownSolid; // E70D (NumberBox.xaml:175)
        // Compact in-field PopupIndicator: EC8F @ FontSize 12, Margin = NumberBoxPopupIndicatorMargin 0,0,8,0,
        // Foreground = NumberBoxPopupIndicatorForeground = TextFillColorSecondary (NumberBox.xaml:365 +
        // NumberBox_themeresources.xaml:5/14 + :37).
        public string PopupIndicatorGlyph { get; init; } = Icons.NumberBoxPopupIndicator;
        public float PopupIndicatorGlyphSize { get; init; } = 12f;
        // Spin buttons take the TextControlButton/RepeatButton ramp (NumberBox.xaml:65–98 remaps RepeatButton* →
        // TextControlButton*): rest = TextControlButtonBackground = transparent (generic.xaml:889), PointerOver =
        // SubtleFillColorSecondary #0FFFFFFF dark / #09000000 light, Pressed = SubtleFillColorTertiary #0AFFFFFF /
        // #06000000 (TextBox_themeresources.xaml:40–41/147–148).
        public ColorF SpinHoverFill { get; init; } = Tok.FillSubtleSecondary;
        public ColorF SpinPressedFill { get; init; } = Tok.FillSubtleTertiary;
        // Glyph = TextControlButtonForeground = TextFillColorSecondary #C5FFFFFF / #9E000000 → Pressed =
        // TextFillColorTertiary #87FFFFFF / #72000000 (TextBox_themeresources.xaml:45–47/152–154).
        public ColorF SpinGlyphColor { get; init; } = Tok.TextSecondary;
        public ColorF SpinGlyphPressedColor { get; init; } = Tok.TextTertiary;
        public ColorF SpinGlyphDisabledColor { get; init; } = Tok.TextDisabled;
    }

    public static Style? GlobalStyle;
    public static Style DefaultStyle => GlobalStyle ?? new Style();

    // ── Factories ────────────────────────────────────────────────────────────────────────────────────────────────
    // The full factory leads with `Signal<double>? value` so it never collides with the legacy (double,double) overloads.

    /// <summary>LIVE enabled flag re-pushed to the mounted core (<c>Embed.Comp(props, …)</c>): <see cref="IsEnabled"/>
    /// is a plain field that freezes at mount via a propless <c>Embed.Comp</c>, so a toggle enabling/disabling the box
    /// would be dropped. <see cref="Create"/> routes it through re-pushed props; the frozen field is the fallback for
    /// direct callers. A tiny record carries the bool because re-pushed props are class-typed.</summary>
    internal sealed record EnabledProps(bool IsEnabled);

    /// <summary>Full WinUI-aligned factory.</summary>
    public static Element Create(
        Signal<double>? value = null, double initial = double.NaN,
        double minimum = double.MinValue, double maximum = double.MaxValue,
        double smallChange = 1, double largeChange = 10,
        NumberBoxSpinButtonPlacementMode spinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
        NumberBoxValidationMode validationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        bool isWrapEnabled = false, bool acceptsExpression = false,
        string placeholderText = "", string? header = null, string? description = null,
        float width = 120f, Func<double, string>? formatter = null, Action<double, double>? onValueChanged = null,
        Signal<string>? text = null, Func<string, double?>? parser = null, bool isEnabled = true,
        Field<double>? field = null)
        => Embed.Comp(new EnabledProps(isEnabled), () => new NumberBox
        {
            Value = value, Initial = initial, Minimum = minimum, Maximum = maximum,
            SmallChange = smallChange, LargeChange = largeChange,
            SpinButtonPlacementMode = spinButtonPlacementMode, ValidationMode = validationMode,
            IsWrapEnabled = isWrapEnabled, AcceptsExpression = acceptsExpression,
            PlaceholderText = placeholderText, Header = header, Description = description,
            Width = width, Formatter = formatter, OnValueChanged = onValueChanged,
            Text = text, Parser = parser, IsEnabled = isEnabled, Field = field,
        });

    /// <summary>Legacy: an editable numeric field with NO spin buttons (WinUI default). <paramref name="step"/> maps to SmallChange.</summary>
    public static Element Create(double initial = 0, double step = 1)
        => Create(value: null, initial: initial, smallChange: step,
                  spinButtonPlacementMode: NumberBoxSpinButtonPlacementMode.Hidden);

    /// <summary>Legacy: an editable numeric field with an inline up/down spin column.</summary>
    public static Element CreateWithSpinners(double initial = 0, double step = 1)
        => Create(value: null, initial: initial, smallChange: step,
                  spinButtonPlacementMode: NumberBoxSpinButtonPlacementMode.Inline);

    // ── Numeric helpers ──────────────────────────────────────────────────────────────────────────────────────────

    string FormatToText(double v)
    {
        if (double.IsNaN(v)) return "";
        if (Formatter is not null) return Formatter(v);
        // WinUI rounds to 10 SIGNIFICANT digits before formatting (m_displayRounder.SignificantDigits(10),
        // NumberBox.cpp:216, applied in UpdateTextToValue NumberBox.cpp:643) — significant digits, not decimal places.
        return RoundSignificant(v, 10).ToString("0.##########", CultureInfo.InvariantCulture);
    }

    static double RoundSignificant(double v, int digits)
    {
        if (v == 0 || !double.IsFinite(v)) return v;
        double mag = Math.Pow(10, digits - 1 - (int)Math.Floor(Math.Log10(Math.Abs(v))));
        return Math.Round(v * mag) / mag;
    }

    /// <summary>Coerce a numeric value into [Min,Max] when InvalidInputOverwritten; NaN passes through (cleared).</summary>
    double Coerce(double v)
    {
        if (double.IsNaN(v)) return v;
        double min = Math.Min(Minimum, Maximum), max = Math.Max(Minimum, Maximum);
        if (ValidationMode == NumberBoxValidationMode.InvalidInputOverwritten)
        {
            if (v > max) return max;
            if (v < min) return min;
        }
        return v;
    }

    static bool NumEquals(double a, double b) => (double.IsNaN(a) && double.IsNaN(b)) || a == b;

    public override Element Render()
    {
        // Reactive enabled flag (provider wins over the frozen field); shadowing local so downstream reads are live.
        bool IsEnabled = UsePropsOrDefault<EnabledProps>()?.IsEnabled ?? this.IsEnabled;
        var s = StyleOverride ?? DefaultStyle;
        var fallbackValue = UseSignal(Initial);
        var value = Value ?? fallbackValue;
        // In-progress edit text (EditableText writes here on every keystroke; we reformat from Value on commit/step).
        var fallbackText = UseSignal(FormatToText(Coerce(Initial)));
        var text = Text ?? fallbackText;
        // Guards our OWN text writes so the OnTextChanged validation below only reacts to EXTERNAL programmatic
        // writes of the Text signal (WinUI m_textUpdating, NumberBox.cpp:651–654).
        var updatingText = UseRef(false);

        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        double current = value.Value;   // subscribe → re-render (and reformat text) when the numeric value changes

        void SetText(string v)
        {
            updatingText.Value = true;
            try { text.Value = v; }
            finally { updatingText.Value = false; }
        }

        // Write a new numeric value: coerce, set the signal, reformat the field text, fire ValueChanged on real change.
        void SetValue(double next)
        {
            next = Coerce(next);
            double old = value.Peek();
            if (!NumEquals(old, next))
            {
                value.Value = next;
                OnValueChanged?.Invoke(old, next);
            }
            SetText(FormatToText(next));
        }

        // Value → Text one-way sync (WinUI OnValuePropertyChanged → UpdateTextToValue + the initial template sync):
        // re-formats the field text whenever the VALUE changes outside the text-edit path (an external value.Value
        // write), and at mount so a caller-provided empty Text signal picks up the seeded Value. Guarded writes —
        // never triggers the programmatic-text validation below.
        UseEffect(() => SetText(FormatToText(value.Peek())), current);

        // Parse the current field text into a number (expression-aware), returning NaN on empty and null on parse failure.
        // The Parser seam = INumberParser.ParseDouble (NumberBox.cpp:493–497); expressions go through the
        // NumberBoxParser::Compute port (operands parse invariantly).
        double? ParseText(string raw)
        {
            string t = raw.Trim();
            if (t.Length == 0) return double.NaN;
            if (AcceptsExpression)
                return NumberBoxExpression.TryEvaluate(t);   // null on malformed, NaN on divide-by-zero
            if (Parser is not null) return Parser(t);
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }

        // WinUI ValidateInput (NumberBox.cpp:478–521): empty → NaN (cleared); parse failure → revert the text to the
        // current value under InvalidInputOverwritten, or leave BOTH text and value untouched under Disabled
        // (NumberBox.cpp:499–506 has no else branch); success → SetValue (which re-formats, covering "1+2" → "3").
        void ValidateInput()
        {
            double? parsed = ParseText(text.Peek());
            if (parsed is double d)
            {
                SetValue(d);   // also re-formats the text when the value is unchanged ("1+2" → "3", cpp:509–513)
            }
            else if (ValidationMode == NumberBoxValidationMode.InvalidInputOverwritten)
            {
                SetText(FormatToText(value.Peek()));   // revert to the last good value (cpp:501–505)
            }
            // Disabled mode + unparsable: leave the text AND value as they are (cpp:499–506).
        }

        // WinUI StepValue (NumberBox.cpp:599–630): validate the typed text first; a NaN value does NOT step (the cpp
        // guards with isnan); else add the delta, wrap if enabled, commit (the Value setter clamps via Coerce).
        void Step(double delta)
        {
            ValidateInput();
            double v = value.Peek();
            if (double.IsNaN(v)) return;
            double min = Math.Min(Minimum, Maximum), max = Math.Max(Minimum, Maximum);
            double next = v + delta;
            if (IsWrapEnabled)
            {
                if (next > max) next = min;
                else if (next < min) next = max;
            }
            SetValue(next);
        }

        // Keyboard stepping (OnNumberBoxKeyDown, NumberBox.cpp:533–558): Up/Down = ±SmallChange, PageUp/PageDown =
        // ±LargeChange, handled on key DOWN so OS auto-repeat repeats the step. The single-line EditableText leaves
        // these keys unmapped (TextEditKeymap returns None) so they bubble up to the wrapper's OnKeyDown here.
        void HandleStepKeys(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up: Step(SmallChange); e.Handled = true; break;
                case Keys.Down: Step(-SmallChange); e.Handled = true; break;
                case Keys.PageUp: Step(LargeChange); e.Handled = true; break;
                case Keys.PageDown: Step(-LargeChange); e.Handled = true; break;
            }
        }

        // WinUI UpdateSpinButtonEnabled: NaN → both off; wrap or non-clamping mode → both on; else gate at the bounds.
        bool spinEnabled = !double.IsNaN(current);
        bool upEnabled = spinEnabled &&
            (IsWrapEnabled || ValidationMode != NumberBoxValidationMode.InvalidInputOverwritten || current < Math.Max(Minimum, Maximum));
        bool downEnabled = spinEnabled &&
            (IsWrapEnabled || ValidationMode != NumberBoxValidationMode.InvalidInputOverwritten || current > Math.Min(Minimum, Maximum));

        // ── Sanitize: live keystroke filter ─────────────────────────────────────────────────────────────────────
        // Plain numeric mode strips to a signed decimal (matches the old behavior). Expression mode is permissive
        // (operators + parens + spaces) because WinUI validates expressions on commit, not per keystroke.
        Func<string, string> sanitize = AcceptsExpression ? SanitizeExpression : SanitizeNumeric;

        // ── Compact popup ───────────────────────────────────────────────────────────────────────────────────────
        Element PopupBody()
        {
            // The host FlyoutSurface already supplies acrylic + 1px stroke + OverlayCornerRadius + shadow.
            BoxEl PopupSpinButton(string glyph, double delta)
            {
                Action step = () => Step(delta);
                var b = new BoxEl
                {
                    Width = s.PopupSpinButtonSize, Height = s.PopupSpinButtonSize,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = Radii.ControlAll, Repeats = true, Role = AutomationRole.Button,
                    HoverFill = s.SpinHoverFill, PressedFill = s.SpinPressedFill,
                    OnClick = step,
                    // Popup spin glyph: FontSize 16 (NumberBox.xaml:201), TextControlButtonForeground → pressed Tertiary
                    // (NumberBox.xaml:125–146 RepeatButton* remap).
                    Children = [Parts.Apply(PartSpinGlyph,
                        new TextEl(glyph) { Size = s.PopupSpinGlyphSize, FontFamily = Theme.IconFont, Color = s.SpinGlyphColor, PressedColor = s.SpinGlyphPressedColor })],
                };
                // Parts: restyle the button; the step mechanics (click + auto-repeat) always win.
                if (Parts is { } sp)
                    b = sp.Apply(PartSpinButton, b) with { OnClick = step, Repeats = true, Role = AutomationRole.Button };
                return b;
            }
            return new BoxEl
            {
                // PopupUpSpinButton Margin 0,0,0,4 + PopupContentRoot Padding 6 (NumberBox.xaml:163 + :116).
                Direction = 1, Gap = s.SpinButtonMargin, Padding = Edges4.All(s.PopupPadding),
                Children = [PopupSpinButton(s.UpGlyph, SmallChange), PopupSpinButton(s.DownGlyph, -SmallChange)],
            };
        }

        void OpenPopup()
        {
            if (handle.Value is { IsOpen: true }) return;
            handle.Value = svc.Open(() => anchor.Value, PopupBody, FlyoutPlacement.BottomLeft);
        }
        void ClosePopup() => handle.Value?.Close();

        // ── Field affix (Inline spin pair / Compact popup indicator / Hidden none) ──────────────────────────────
        Element? affix = null;
        if (SpinButtonPlacementMode == NumberBoxSpinButtonPlacementMode.Inline)
        {
            // WinUI inline spin buttons are two SIDE-BY-SIDE RepeatButtons overlaying the field's trailing edge
            // (NumberBox.xaml:174–175): NumberBoxSpinButtonStyle MinWidth=32, FontSize=12, VerticalAlignment=Stretch
            // (NumberBox.xaml:185–189), UpSpinButton Margin="4", DownSpinButton Margin="0,4,4,4", CornerRadius =
            // ControlCornerRadius (4). State ramp = the RepeatButton*→TextControlButton* remap (NumberBox.xaml:65–98).
            BoxEl SpinCell(string glyph, double delta, bool enabled, Edges4 margin)
            {
                Action? step = enabled ? (Action)(() => Step(delta)) : null;
                var b = new BoxEl
                {
                    Width = s.SpinButtonWidth, Margin = margin,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    HoverFill = enabled ? s.SpinHoverFill : ColorF.Transparent,
                    PressedFill = enabled ? s.SpinPressedFill : ColorF.Transparent,
                    Corners = Radii.ControlAll,
                    Repeats = enabled,
                    Role = AutomationRole.Button,
                    // The inline spin pair mounts INSIDE the EditableText root (its I-beam would inherit down):
                    // explicit Arrow masks it — WinUI's spin RepeatButtons are siblings of the TextBox part
                    // (NumberBox.xaml:174-175) so they get the arrow naturally; ours needs the forced mask, same
                    // as the TextBox delete button (TextBox_Partial.cpp:884).
                    Cursor = CursorId.Arrow,
                    OnClick = step,
                    Children = [Parts.Apply(PartSpinGlyph, new TextEl(glyph)
                    {
                        Size = s.SpinGlyphSize, FontFamily = Theme.IconFont,   // FontSize 12 (NumberBox.xaml:189)
                        Color = enabled ? s.SpinGlyphColor : s.SpinGlyphDisabledColor,
                        PressedColor = enabled ? s.SpinGlyphPressedColor : s.SpinGlyphDisabledColor,
                    })],
                };
                // Parts: restyle the button (the enabled-state fills above are pre-modifier); step mechanics + the
                // I-beam-masking Arrow always win.
                if (Parts is { } sp)
                    b = sp.Apply(PartSpinButton, b) with { OnClick = step, Repeats = enabled, Role = AutomationRole.Button, Cursor = CursorId.Arrow };
                return b;
            }
            affix = new BoxEl
            {
                Direction = 0, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    SpinCell(s.UpGlyph, SmallChange, upEnabled, Edges4.All(s.SpinButtonMargin)),                       // Margin 4 (NumberBox.xaml:174)
                    SpinCell(s.DownGlyph, -SmallChange, downEnabled, new Edges4(0, s.SpinButtonMargin, s.SpinButtonMargin, s.SpinButtonMargin)), // Margin 0,4,4,4 (:175)
                ],
            };
        }
        else if (SpinButtonPlacementMode == NumberBoxSpinButtonPlacementMode.Compact)
        {
            // The in-field PopupIndicator (NumberBox.xaml:365): a NON-interactive TextBlock — EC8F @ FontSize 12,
            // Margin = NumberBoxPopupIndicatorMargin 0,0,8,0 (NumberBox_themeresources.xaml:37), Foreground =
            // NumberBoxPopupIndicatorForeground = TextFillColorSecondary (NumberBox_themeresources.xaml:5/14).
            // The popup itself opens on focus and closes on blur (OnNumberBoxGotFocus/LostFocus, NumberBox.cpp:414–438)
            // — WinUI does NOT toggle it from the indicator.
            affix = new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HitTestVisible = false,
                Children = [new TextEl(s.PopupIndicatorGlyph)
                {
                    Size = s.PopupIndicatorGlyphSize, FontFamily = Theme.IconFont, Color = s.SpinGlyphColor,
                    Margin = new Edges4(0, 0, 8f, 0),
                }],
            };
        }

        // ── Focus: validate-on-blur + the Compact popup opens on focus / closes on blur (NumberBox.cpp:414–438) ──
        var fieldFocused = UseRef(false);
        void OnFocusChanged(bool focused)
        {
            fieldFocused.Value = focused;
            if (focused)
            {
                if (SpinButtonPlacementMode == NumberBoxSpinButtonPlacementMode.Compact) OpenPopup();
            }
            else
            {
                ValidateInput();
                if (SpinButtonPlacementMode == NumberBoxSpinButtonPlacementMode.Compact) ClosePopup();
            }
        }

        var field = Embed.Comp(() =>
        {
            var e = new EditableText
            {
                Text = text,
                Width = Width,
                Height = s.FieldHeight,
                Placeholder = PlaceholderText,
                Sanitize = sanitize,
                RightAffix = affix,
                IsEnabled = IsEnabled,
                OnCommit = _ => ValidateInput(),                       // Enter (KeyUp ValidateInput, NumberBox.cpp:564–568)
                OnCancel = () => SetText(FormatToText(value.Peek())),  // Escape → UpdateTextToValue (NumberBox.cpp:570–574)
                OnFocusChanged = OnFocusChanged,
                Field = Field?.Binding,                                // form-validation.md: invalid border + touched-on-blur
            };
            // External programmatic Text writes validate immediately: OnTextPropertyChanged → UpdateValueToText →
            // ValidateInput (NumberBox.cpp:325–340). Our own SetText writes are guarded out (m_textUpdating-style),
            // and FOCUSED programmatic changes (EditableText's Escape revert) defer to the validate-on-blur that
            // immediately follows — WinUI's Escape path is UpdateTextToValue, never a commit (NumberBox.cpp:570–574).
            e.OnTextChanged = _ =>
            {
                if (!updatingText.Value && !fieldFocused.Value
                    && e.LastChangeReason == TextChangeReason.ProgrammaticChange) ValidateInput();
            };
            return e;
        });

        // The field wrapper: the Compact popup's anchor node + the keyboard stepping handler (Up/Down/PageUp/PageDown
        // bubble out of the single-line EditableText to here).
        Action<NodeHandle> anchorCapture = h => anchor.Value = h;
        Element[] fieldKids = [field];
        var fieldBox = new BoxEl
        {
            Direction = 0, Width = Width,
            OnRealized = anchorCapture,
            OnKeyDown = IsEnabled ? HandleStepKeys : null,
            Children = fieldKids,
        };
        // Parts: restyle the wrapper; the anchor capture, stepping keys and EditableText mount always win.
        Element fieldRow = fieldBox;
        if (Parts is { } fp)
        {
            var m = fp.Apply(PartField, fieldBox);
            fieldRow = m with
            {
                OnRealized = TemplateParts.Chain(anchorCapture, m.OnRealized),
                OnKeyDown = IsEnabled ? (Action<KeyEventArgs>)HandleStepKeys : null,
                Children = fieldKids,
            };
        }

        // ── Header / Description wrapper (NumberBox.xaml:113 + :176) ─────────────────────────────────────────────
        if (Header is null && Description is null && Field is null) return fieldRow;

        var stack = new List<Element>(3);
        if (Header is not null)
            // HeaderContentPresenter: TextControlHeaderForeground (generic.xaml:886) → Disabled =
            // TextControlHeaderForegroundDisabled (NumberBox.xaml:20–24); Margin = TextBoxTopHeaderMargin 0,0,0,8
            // (NumberBox.xaml:113); FontSize inherits ControlContentThemeFontSize 14.
            stack.Add(new TextEl(Header)
            {
                Size = 14f,
                Color = IsEnabled ? Tok.TextControlHeaderForeground : Tok.TextControlHeaderForegroundDisabled,
                Margin = new Edges4(0, 0, 0, 8f),
            });
        stack.Add(fieldRow);
        if (Description is not null)
            // DescriptionPresenter (NumberBox.xaml:176): SystemControlDescriptionTextForegroundBrush (BaseMedium,
            // generic.xaml:327+209/4134); FontSize inherits 14; no extra margin in the template.
            stack.Add(new TextEl(Description) { Size = 14f, Color = Tok.TextControlDescriptionForeground });
        // form-validation.md: the error message row (reveal-animated; zero space when valid). NumberBox.ValidationMode
        // (parse/clamp) is orthogonal — a Field can show "must be ≥ 18" while the mode still clamps the parsed value.
        if (Field is { } vf) stack.Add(FieldVisuals.MessageRow(vf.Error));

        return new BoxEl { Direction = 1, Children = stack.ToArray() };
    }

    // ── Sanitizers (static so the closures don't capture this) ───────────────────────────────────────────────────

    static string SanitizeNumeric(string str)
    {
        if (str.Length == 0) return str;
        Span<char> buf = stackalloc char[str.Length];
        int n = 0; bool dot = false;
        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];
            if (c == '-' && n == 0) buf[n++] = c;
            else if (c == '.' && !dot) { dot = true; buf[n++] = c; }
            else if (c >= '0' && c <= '9') buf[n++] = c;
        }
        return new string(buf[..n]);
    }

    static string SanitizeExpression(string str)
    {
        if (str.Length == 0) return str;
        Span<char> buf = stackalloc char[str.Length];
        int n = 0;
        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];
            bool ok = (c >= '0' && c <= '9') || c == '.' || c == ' '
                      || c == '+' || c == '-' || c == '*' || c == '/' || c == '^'
                      || c == '(' || c == ')';
            if (ok) buf[n++] = c;
        }
        return new string(buf[..n]);
    }
}

/// <summary>
/// A tiny port of WinUI's NumberBoxParser: tokenize <c>+ - * / ^</c> and parentheses, infix→postfix (shunting-yard,
/// precedence */ over +-, ^ highest and right-associative), evaluate. Divide-by-zero → NaN; malformed → null.
/// </summary>
internal static class NumberBoxExpression
{
    public static double? TryEvaluate(string expr)
    {
        // 1) tokenize
        var tokens = new List<Token>();
        int i = 0;
        bool prevWasValue = false;   // to distinguish unary minus from binary minus
        while (i < expr.Length)
        {
            char c = expr[i];
            if (c == ' ') { i++; continue; }
            if (c >= '0' && c <= '9' || c == '.')
            {
                int start = i;
                while (i < expr.Length && ((expr[i] >= '0' && expr[i] <= '9') || expr[i] == '.')) i++;
                if (!double.TryParse(expr.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                    return null;
                tokens.Add(Token.Num(num));
                prevWasValue = true;
                continue;
            }
            if (c == '(') { tokens.Add(Token.MakeOp('(')); prevWasValue = false; i++; continue; }
            if (c == ')') { tokens.Add(Token.MakeOp(')')); prevWasValue = true; i++; continue; }
            if (c is '+' or '-' or '*' or '/' or '^')
            {
                // A leading '-' or '-' after an operator/'(' is unary negation.
                if (c == '-' && !prevWasValue) tokens.Add(Token.MakeOp('u'));   // unary minus
                else tokens.Add(Token.MakeOp(c));
                prevWasValue = false;
                i++;
                continue;
            }
            return null;   // unexpected character
        }

        // 2) shunting-yard → postfix
        var output = new List<Token>();
        var ops = new Stack<char>();
        foreach (var t in tokens)
        {
            if (t.IsNumber) { output.Add(t); continue; }
            char op = t.Op;
            if (op == '(') { ops.Push(op); continue; }
            if (op == ')')
            {
                bool matched = false;
                while (ops.Count > 0)
                {
                    char top = ops.Pop();
                    if (top == '(') { matched = true; break; }
                    output.Add(Token.MakeOp(top));
                }
                if (!matched) return null;   // mismatched parens
                continue;
            }
            while (ops.Count > 0 && ops.Peek() != '('
                   && (Prec(ops.Peek()) > Prec(op) || (Prec(ops.Peek()) == Prec(op) && !RightAssoc(op))))
                output.Add(Token.MakeOp(ops.Pop()));
            ops.Push(op);
        }
        while (ops.Count > 0)
        {
            char top = ops.Pop();
            if (top == '(') return null;   // mismatched parens
            output.Add(Token.MakeOp(top));
        }

        // 3) evaluate postfix
        var st = new Stack<double>();
        foreach (var t in output)
        {
            if (t.IsNumber) { st.Push(t.Number); continue; }
            if (t.Op == 'u')   // unary minus
            {
                if (st.Count < 1) return null;
                st.Push(-st.Pop());
                continue;
            }
            if (st.Count < 2) return null;
            double b = st.Pop(), a = st.Pop();
            switch (t.Op)
            {
                case '+': st.Push(a + b); break;
                case '-': st.Push(a - b); break;
                case '*': st.Push(a * b); break;
                case '/': st.Push(b == 0 ? double.NaN : a / b); break;
                case '^': st.Push(Math.Pow(a, b)); break;
                default: return null;
            }
        }
        return st.Count == 1 ? st.Pop() : (double?)null;
    }

    static int Prec(char op) => op switch { 'u' => 4, '^' => 3, '*' or '/' => 2, '+' or '-' => 1, _ => 0 };
    static bool RightAssoc(char op) => op is '^' or 'u';

    readonly struct Token
    {
        public readonly bool IsNumber;
        public readonly double Number;
        public readonly char Op;
        Token(bool isNumber, double number, char op) { IsNumber = isNumber; Number = number; Op = op; }
        public static Token Num(double n) => new(true, n, '\0');
        public static Token MakeOp(char o) => new(false, 0, o);
    }
}