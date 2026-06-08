using System.Globalization;
using FluentGpu.Dsl;
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
/// stacks an up/down column (▲E70E / ▼E70D) in the field; <see cref="NumberBoxSpinButtonPlacementMode.Compact"/> shows a
/// chevron and opens the up/down buttons in an Overlay popup while focused. Validation (clamp / revert) runs on Enter and
/// on blur per <see cref="ValidationMode"/>; with <see cref="AcceptsExpression"/> a typed arithmetic expression is
/// evaluated on commit (shunting-yard over + - * / ^ and parentheses).
/// </summary>
/// <remarks>
/// Deferred (no engine seam): mouse-wheel step and arrow / Page-key step — <see cref="EditableText"/> consumes Up/Down
/// and there is no Element-level pointer-wheel handler, so stepping is via the spin buttons (press-and-hold repeat).
/// </remarks>
public sealed class NumberBox : Component
{
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
    public Func<double, string>? Formatter;
    public Action<double, double>? OnValueChanged;   // (oldValue, newValue) — WinUI ValueChangedEventArgs
    public Style? StyleOverride;

    /// <summary>Dims from NumberBox_themeresources.xaml / NumberBox.xaml.</summary>
    public sealed record Style
    {
        public float MinWidth { get; init; } = 120f;          // NumberBoxMinWidth
        public float FieldHeight { get; init; } = 32f;
        public float SpinButtonWidth { get; init; } = 32f;    // NumberBoxSpinButtonStyle MinWidth
        public float SpinGlyphSize { get; init; } = 12f;      // inline FontSize 12
        public float SpinButtonMargin { get; init; } = 4f;    // Margin 4 / 0,4,4,4
        public float PopupSpinButtonSize { get; init; } = 36f;// NumberBoxPopupSpinButtonStyle 36x36
        public float PopupSpinGlyphSize { get; init; } = 16f; // popup FontSize 16
        public float PopupPadding { get; init; } = 6f;        // popup root Padding 6
        public string UpGlyph { get; init; } = Icons.CaretUpSolid;     // E70E
        public string DownGlyph { get; init; } = Icons.CaretDownSolid; // E70D
        public string PopupIndicatorGlyph { get; init; } = Icons.ChevronDown; // Compact in-field chevron
        public ColorF SpinHoverFill { get; init; } = Tok.FillSubtleSecondary;
        public ColorF SpinPressedFill { get; init; } = Tok.FillSubtleTertiary;
        public ColorF SpinGlyphColor { get; init; } = Tok.TextSecondary;
        public ColorF SpinGlyphDisabledColor { get; init; } = Tok.TextDisabled;
    }

    public static Style? GlobalStyle;
    public static Style DefaultStyle => GlobalStyle ?? new Style();

    // ── Factories ────────────────────────────────────────────────────────────────────────────────────────────────
    // The full factory leads with `Signal<double>? value` so it never collides with the legacy (double,double) overloads.

    /// <summary>Full WinUI-aligned factory.</summary>
    public static Element Create(
        Signal<double>? value = null, double initial = double.NaN,
        double minimum = double.MinValue, double maximum = double.MaxValue,
        double smallChange = 1, double largeChange = 10,
        NumberBoxSpinButtonPlacementMode spinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
        NumberBoxValidationMode validationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        bool isWrapEnabled = false, bool acceptsExpression = false,
        string placeholderText = "", string? header = null, string? description = null,
        float width = 120f, Func<double, string>? formatter = null, Action<double, double>? onValueChanged = null)
        => Embed.Comp(() => new NumberBox
        {
            Value = value, Initial = initial, Minimum = minimum, Maximum = maximum,
            SmallChange = smallChange, LargeChange = largeChange,
            SpinButtonPlacementMode = spinButtonPlacementMode, ValidationMode = validationMode,
            IsWrapEnabled = isWrapEnabled, AcceptsExpression = acceptsExpression,
            PlaceholderText = placeholderText, Header = header, Description = description,
            Width = width, Formatter = formatter, OnValueChanged = onValueChanged,
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
        // WinUI rounds to ~10 significant digits before formatting; invariant 0.###### is the engine default.
        return Math.Round(v, 10).ToString("0.######", CultureInfo.InvariantCulture);
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
        var s = StyleOverride ?? DefaultStyle;
        var fallbackValue = UseSignal(Initial);
        var value = Value ?? fallbackValue;
        // In-progress edit text (EditableText writes here on every keystroke; we reformat from Value on commit/step).
        var text = UseSignal(FormatToText(Coerce(Initial)));

        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        double current = value.Value;   // subscribe → re-render (and reformat text) when the numeric value changes

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
            text.Value = FormatToText(next);
        }

        // Parse the current field text into a number (expression-aware), returning NaN on empty and null on parse failure.
        double? ParseText(string raw)
        {
            string t = raw.Trim();
            if (t.Length == 0) return double.NaN;
            if (AcceptsExpression)
                return NumberBoxExpression.TryEvaluate(t);   // null on malformed, NaN on divide-by-zero
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }

        // WinUI ValidateInput: commit the typed text. Success → SetValue; failure → revert (InvalidInputOverwritten) or
        // leave the text as-is (Disabled, where anything goes — unparsable becomes NaN/cleared).
        void ValidateInput()
        {
            double? parsed = ParseText(text.Peek());
            if (parsed is double d)
            {
                SetValue(d);
            }
            else if (ValidationMode == NumberBoxValidationMode.InvalidInputOverwritten)
            {
                text.Value = FormatToText(value.Peek());   // revert to the last good value
            }
            else
            {
                SetValue(double.NaN);                       // Disabled: unparsable clears the value
            }
        }

        // WinUI StepValue: validate the typed text first, then add the delta (wrap if enabled), then commit.
        void Step(double delta)
        {
            ValidateInput();
            double v = value.Peek();
            double min = Math.Min(Minimum, Maximum), max = Math.Max(Minimum, Maximum);
            double next = double.IsNaN(v) ? (delta >= 0 ? min : max) : v + delta;
            if (IsWrapEnabled)
            {
                if (next > max) next = min;
                else if (next < min) next = max;
            }
            SetValue(next);
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
            BoxEl PopupSpinButton(string glyph, double delta) => new()
            {
                Width = s.PopupSpinButtonSize, Height = s.PopupSpinButtonSize,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll, Repeats = true, Role = AutomationRole.Button,
                HoverFill = s.SpinHoverFill, PressedFill = s.SpinPressedFill,
                OnClick = () => Step(delta),
                Children = [new TextEl(glyph) { Size = s.PopupSpinGlyphSize, FontFamily = Theme.IconFont, Color = s.SpinGlyphColor }],
            };
            return new BoxEl
            {
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

        // ── Field affix (Inline column / Compact chevron / Hidden none) ─────────────────────────────────────────
        Element? affix = null;
        if (SpinButtonPlacementMode == NumberBoxSpinButtonPlacementMode.Inline)
        {
            BoxEl SpinCell(string glyph, double delta, bool enabled, Edges4 margin) => new()
            {
                Grow = 1f, Margin = margin,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HoverFill = enabled ? s.SpinHoverFill : ColorF.Transparent,
                PressedFill = enabled ? s.SpinPressedFill : ColorF.Transparent,
                Corners = Radii.ControlAll,
                Repeats = enabled,
                Role = AutomationRole.Button,
                OnClick = enabled ? (Action)(() => Step(delta)) : null,
                Children = [new TextEl(glyph)
                {
                    Size = 8f, FontFamily = Theme.IconFont,   // small inline carets (WinUI inline spin buttons), both stacked ▲/▼
                    Color = enabled ? s.SpinGlyphColor : s.SpinGlyphDisabledColor,
                }],
            };
            // Two stacked half-height cells (▲ top, ▼ bottom) filling the full field height; tight 2px margins.
            affix = new BoxEl
            {
                Direction = 1, AlignSelf = FlexAlign.Stretch, Width = 28f,
                Children =
                [
                    SpinCell(s.UpGlyph, SmallChange, upEnabled, new Edges4(2, 2, 3, 1)),
                    SpinCell(s.DownGlyph, -SmallChange, downEnabled, new Edges4(2, 1, 3, 2)),
                ],
            };
        }
        else if (SpinButtonPlacementMode == NumberBoxSpinButtonPlacementMode.Compact)
        {
            // The in-field PopupIndicator chevron (EC8F in WinUI; ChevronDown here). The popup is opened on focus
            // (OnFocusChanged) and clicking the chevron toggles it as a fallback affordance.
            affix = new BoxEl
            {
                Width = 30f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Role = AutomationRole.Button, HoverFill = s.SpinHoverFill,
                OnClick = () => { if (handle.Value is { IsOpen: true }) ClosePopup(); else OpenPopup(); },
                Children = [new TextEl(s.PopupIndicatorGlyph) { Size = s.SpinGlyphSize, FontFamily = Theme.IconFont, Color = s.SpinGlyphColor }],
            };
        }

        // ── Focus: validate-on-blur + open/close the Compact popup ──────────────────────────────────────────────
        void OnFocusChanged(bool focused)
        {
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

        var field = Embed.Comp(() => new EditableText
        {
            Text = text,
            Width = Width,
            Height = s.FieldHeight,
            Placeholder = PlaceholderText,
            Sanitize = sanitize,
            RightAffix = affix,
            OnCommit = _ => ValidateInput(),                       // Enter
            OnCancel = () => text.Value = FormatToText(value.Peek()), // Escape reverts to the committed value
            OnFocusChanged = OnFocusChanged,
        });

        // The Compact popup needs an anchor node; wrap the field so OnRealized can capture it (no EditableText change).
        Element fieldRow = SpinButtonPlacementMode == NumberBoxSpinButtonPlacementMode.Compact
            ? new BoxEl { Direction = 0, Width = Width, OnRealized = h => anchor.Value = h, Children = [field] }
            : field;

        // ── Header / Description wrapper (TextBox.Create header path) ────────────────────────────────────────────
        if (Header is null && Description is null) return fieldRow;

        var stack = new List<Element>(3);
        if (Header is not null)
            stack.Add(new TextEl(Header) { Size = 14f, Color = Tok.TextSecondary });
        stack.Add(fieldRow);
        if (Description is not null)
            stack.Add(new TextEl(Description) { Size = 12f, Color = Tok.TextSecondary });

        return new BoxEl { Direction = 1, Gap = 4f, Children = stack.ToArray() };
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