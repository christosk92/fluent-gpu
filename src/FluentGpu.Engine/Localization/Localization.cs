using System.Collections.Generic;
using System.IO;
using FluentGpu.Signals;

namespace FluentGpu.Localization;

/// <summary>
/// The engine's localization (i18n) facade — a static, signal-backed string resolver. It is a deliberate, modern
/// alternative to WinUI's <c>ResourceManager</c>/<c>.resw</c> (forbidden: reflection + satellite assemblies are
/// AOT/trim-hostile). Strings live in per-culture JSON files (<see cref="JsonResourceReader"/>); resolution applies a
/// per-key fallback chain, named-placeholder + ICU plural/select formatting (<see cref="MessageFormatter"/>), and an
/// optional pseudo-localization QA transform (<see cref="PseudoLocalizer"/>).
///
/// <para><b>Reactive, no re-render.</b> Resolution reads a single culture-epoch <see cref="Signal{T}"/>
/// (<see cref="CultureEpoch"/>); <see cref="SetCulture"/> bumps it. A text node binds its label as
/// <c>Text = Prop.Of(() =&gt; Loc.Get(key))</c> — the engine's <c>Prop&lt;T&gt;</c> wires the thunk into a mount-time
/// bind effect (see <c>Foundation/Signals/Prop.cs</c>), so reading the epoch inside <c>Get</c> subscribes ONLY that
/// node's bind effect. A culture switch therefore re-resolves exactly the bound text nodes — not the component tree
/// (the binding-not-re-render win the render-purity work established). See the hooks <c>Loc.L</c>/<c>Loc.Lf</c> sugar.</para>
///
/// <para><b>Invariant-globalization-safe.</b> Cultures are keyed by NAME string (<c>"en-US"</c>, <c>"fr"</c>) — never a
/// <c>CultureInfo</c> (none exists under <c>InvariantGlobalization=true</c>). Plural categories come from the hand-rolled
/// <see cref="PluralRules"/>. OS UI-culture detection is injected by the host (<see cref="OsCultureProvider"/>) so the
/// portable engine stays free of Win32; the Windows app wires <c>GetUserDefaultLocaleName</c> into it.</para>
///
/// <para>Thread affinity: like the rest of the reactive core, mutations (<see cref="SetCulture"/>,
/// <see cref="LoadFolder"/>, <see cref="AddStrings"/>) run on the UI thread. The fallback table is rebuilt under a lock
/// only when cultures change, and reads take a snapshot reference, so <see cref="Get"/> is allocation-light.</para>
/// </summary>
public static class Localization
{
    private static readonly object Gate = new();

    // culture name → its flat dotted-key table. Keyed ordinal/case-insensitively-via-normalization (we normalize names
    // on the way in, so "EN-us" and "en-US" coalesce).
    private static readonly Dictionary<string, Dictionary<string, string>> Tables = new(System.StringComparer.OrdinalIgnoreCase);

    // The active resolution chain (snapshot of tables to consult in order), rebuilt on any culture/data change.
    private static Dictionary<string, string>[] _chain = System.Array.Empty<Dictionary<string, string>>();
    private static string _current = "en-US";
    private static string _default = "en-US";
    private static bool _pseudo;

    // The NEUTRAL fallback floor: the built-in, ship-with-the-assembly neutral strings a library registers for its OWN
    // user-facing text (the control kit does this — see FluentGpu.Controls, whose generated module initializer calls
    // RegisterNeutral). It is consulted LAST in every resolution (after the active culture, its parent, and the default
    // culture), so a key ALWAYS resolves to its neutral string even when NO culture table is loaded — the zero-config
    // contract that keeps a library's text visible for an app that never touches localization. It is NOT a culture (it
    // never appears in AvailableCultures) and Clear() never wipes it (the built-in floor is permanent). Because it is
    // the terminal link, a translation for the same key in any real culture table always wins.
    private static Dictionary<string, string> _neutral = new(System.StringComparer.Ordinal);

    /// <summary>The culture-epoch signal: bumped by <see cref="SetCulture"/> / data changes. <see cref="Get"/> reads it
    /// (subscribing the calling thunk), so a bound text node re-resolves when the culture changes — with no re-render.
    /// The host's <c>ReactiveRuntime</c> drains the resulting bind-effect updates on the next frame.</summary>
    public static readonly Signal<int> CultureEpoch = new(0);

    /// <summary>Host-injected OS UI-culture provider (returns a BCP-47 name like <c>"en-US"</c>, or null/empty when
    /// unavailable). The portable engine has no Win32; the Windows app sets this to <c>GetUserDefaultLocaleName</c>.
    /// Consumed by <see cref="DetectOsCulture"/> / <see cref="UseOsCulture"/>.</summary>
    public static System.Func<string?>? OsCultureProvider;

    /// <summary>The active culture name (e.g. <c>"fr-FR"</c>). Reading inside a thunk subscribes via the epoch; this
    /// property itself returns the snapshot value.</summary>
    public static string CurrentCulture { get { lock (Gate) return _current; } }

    /// <summary>The terminal fallback culture (default <c>"en-US"</c>): consulted after the active culture and its
    /// parent, before the key itself.</summary>
    public static string DefaultCulture
    {
        get { lock (Gate) return _default; }
        set { lock (Gate) { _default = Normalize(value); RebuildChain(); } BumpEpoch(); }
    }

    /// <summary>Whether the pseudo-localization transform is applied to every resolved string (dev QA). Auto-enabled
    /// when <see cref="SetCulture"/> selects <see cref="PseudoLocalizer.PseudoCulture"/>.</summary>
    public static bool PseudoLocalize
    {
        get { lock (Gate) return _pseudo; }
        set { lock (Gate) _pseudo = value; BumpEpoch(); }
    }

    /// <summary>The set of cultures that have a loaded table (the language picker's options).</summary>
    public static IReadOnlyList<string> AvailableCultures
    {
        get { lock (Gate) { var a = new string[Tables.Count]; Tables.Keys.CopyTo(a, 0); return a; } }
    }

    // ── Mutation API ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Switch the active culture by NAME and bump the epoch (re-resolving every bound text node, no re-render).
    /// Selecting <see cref="PseudoLocalizer.PseudoCulture"/> also turns the pseudo transform on; selecting any other
    /// culture turns it off.</summary>
    public static void SetCulture(string culture)
    {
        lock (Gate)
        {
            _current = Normalize(culture);
            _pseudo = string.Equals(_current, PseudoLocalizer.PseudoCulture, System.StringComparison.OrdinalIgnoreCase);
            RebuildChain();
        }
        BumpEpoch();
    }

    /// <summary>Resolve <paramref name="culture"/> against the loaded tables and select it only when an exact table,
    /// parent table, or one unambiguous regional table for a neutral language exists. Returns false without changing the
    /// active culture when the requested language is unsupported.</summary>
    public static bool TrySetCulture(string? culture)
    {
        string? resolved;
        lock (Gate)
        {
            resolved = ResolveLoadedCulture(culture);
            if (resolved is null) return false;
            _current = resolved;
            _pseudo = string.Equals(_current, PseudoLocalizer.PseudoCulture, System.StringComparison.OrdinalIgnoreCase);
            RebuildChain();
        }
        BumpEpoch();
        return true;
    }

    /// <summary>Detect the OS UI culture via the host-injected <see cref="OsCultureProvider"/> and set it as the active
    /// culture iff a table for it (or a fallback ancestor) exists; otherwise leaves the current culture. Returns the
    /// detected name (may be empty when no provider is wired). Called once at startup by the host/app.</summary>
    public static string UseOsCulture()
    {
        string detected = DetectOsCulture();
        if (!string.IsNullOrEmpty(detected)) TrySetCulture(detected);
        return detected;
    }

    /// <summary>The raw OS UI-culture name from the host provider (never throws; empty when unavailable). Public so the
    /// probe/app can assert it is non-empty on a real OS without forcing a culture switch.</summary>
    public static string DetectOsCulture() => OsCultureProvider?.Invoke() ?? string.Empty;

    /// <summary>Load every <c>*.json</c> in <paramref name="jsonDir"/> as a culture table, keyed by file name
    /// (<c>fr-FR.json → "fr-FR"</c>). Existing tables for the same culture are replaced. Bumps the epoch.</summary>
    public static void LoadFolder(string jsonDir)
    {
        if (!Directory.Exists(jsonDir)) return;
        lock (Gate)
        {
            foreach (string path in Directory.GetFiles(jsonDir, "*.json"))
            {
                string culture = Path.GetFileNameWithoutExtension(path);
                var table = JsonResourceReader.ReadFile(path, out string? declared);
                // Prefer an in-file $culture declaration over the file name if present (self-describing files).
                Tables[Normalize(declared ?? culture)] = table;
            }
            RebuildChain();
        }
        BumpEpoch();
    }

    /// <summary>Load one culture from a JSON file path (keyed by its file name, or its <c>$culture</c> if declared).</summary>
    public static void LoadFile(string jsonPath)
    {
        lock (Gate)
        {
            var table = JsonResourceReader.ReadFile(jsonPath, out string? declared);
            string culture = declared ?? Path.GetFileNameWithoutExtension(jsonPath);
            Tables[Normalize(culture)] = table;
            RebuildChain();
        }
        BumpEpoch();
    }

    /// <summary>Add/replace a culture's strings from an in-memory dotted-key dictionary (tests, embedded resources, or a
    /// server-delivered bundle). Merges into any existing table for that culture.</summary>
    public static void AddStrings(string culture, IReadOnlyDictionary<string, string> strings)
    {
        lock (Gate)
        {
            string key = Normalize(culture);
            if (!Tables.TryGetValue(key, out var table)) Tables[key] = table = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var kv in strings) table[kv.Key] = kv.Value;
            RebuildChain();
        }
        BumpEpoch();
    }

    /// <summary>Add/replace a culture's strings from a raw JSON document (UTF-8 bytes) — the embedded-resource path.</summary>
    public static void AddJson(string culture, ReadOnlySpan<byte> utf8Json)
        => AddStrings(culture, JsonResourceReader.Read(utf8Json));

    /// <summary>Register a library's built-in NEUTRAL strings as the terminal fallback floor (see <see cref="_neutral"/>).
    /// The control kit calls this from a generated module initializer so its user-facing text resolves to the neutral
    /// string with ZERO configuration — an app that never touches localization sees the kit's default English unchanged.
    /// A translation for the same key in any loaded culture table always wins (neutral is consulted last). Merges into
    /// any previously-registered neutral strings; idempotent for equal values. Does NOT bump the culture epoch: the
    /// neutral floor is the immutable baseline set once at assembly load, before any UI resolves, so no re-resolution is
    /// needed and no reactive write happens off the UI thread during module init.</summary>
    public static void RegisterNeutral(IReadOnlyDictionary<string, string> strings)
    {
        lock (Gate)
        {
            foreach (var kv in strings) _neutral[kv.Key] = kv.Value;
            RebuildChain();
        }
    }

    /// <summary>Drop all loaded cultures and reset to the empty state (tests / re-init).</summary>
    public static void Clear()
    {
        lock (Gate) { Tables.Clear(); RebuildChain(); }
        BumpEpoch();
    }

    // ── Resolution API ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Resolve <paramref name="key"/> to its raw template for the active culture, applying the per-key fallback
    /// chain (active → parent → default → the key itself). A missing key renders VISIBLY as <c>[key]</c>. Subscribes the
    /// caller to <see cref="CultureEpoch"/> so a culture switch re-resolves a bound text node (no re-render). Pseudo-loc
    /// is applied when enabled.</summary>
    public static string Get(string key)
    {
        _ = CultureEpoch.Value;   // SUBSCRIBE the calling thunk/computation — this is the reactive hook
        bool pseudo; Dictionary<string, string>[] chain;
        lock (Gate) { pseudo = _pseudo; chain = _chain; }

        string resolved = Lookup(chain, key, out bool found);
        if (!found) return $"[{key}]";        // missing key is loud, not blank
        return pseudo ? PseudoLocalizer.Transform(resolved) : resolved;
    }

    /// <summary>Resolve + format <paramref name="key"/> with named placeholders / ICU plural-select using the
    /// <c>(name, value)</c> argument pairs. Same fallback + epoch subscription + pseudo behavior as <see cref="Get"/>.
    /// A missing argument renders visibly (<c>{name}</c>), never throws.</summary>
    public static string Format(string key, params (string Name, object Value)[] args)
    {
        _ = CultureEpoch.Value;   // SUBSCRIBE
        bool pseudo; Dictionary<string, string>[] chain; string culture;
        lock (Gate) { pseudo = _pseudo; chain = _chain; culture = _current; }

        string template = Lookup(chain, key, out bool found);
        if (!found) return $"[{key}]";
        string formatted = MessageFormatter.Format(template, new MessageFormatter.Args(args), culture);
        return pseudo ? PseudoLocalizer.Transform(formatted) : formatted;
    }

    /// <summary>As <see cref="Format(string, ValueTuple{string, object}[])"/> but with a dictionary argument bag.</summary>
    public static string Format(string key, IReadOnlyDictionary<string, object> args)
    {
        _ = CultureEpoch.Value;
        bool pseudo; Dictionary<string, string>[] chain; string culture;
        lock (Gate) { pseudo = _pseudo; chain = _chain; culture = _current; }

        string template = Lookup(chain, key, out bool found);
        if (!found) return $"[{key}]";
        string formatted = MessageFormatter.Format(template, new MessageFormatter.Args(args), culture);
        return pseudo ? PseudoLocalizer.Transform(formatted) : formatted;
    }

    /// <summary>Bind <paramref name="key"/> as a live localized string for a text/tooltip channel WITHOUT re-rendering:
    /// returns a <see cref="Prop{T}"/> thunk over <see cref="Get"/>, so the engine's mount-time bind effect re-resolves
    /// ONLY that node on a culture change (the binding-not-re-render path). Usable from static control-factory code where
    /// the instance <c>L</c>/<c>Lf</c> hooks (which need a component <c>Context</c>) are unavailable — this is the control
    /// kit's canonical localized-text mechanism. Missing key falls back to the neutral floor (or renders <c>[key]</c> if
    /// no neutral was registered).</summary>
    public static Prop<string> Bind(string key) => Prop.Of(() => Get(key));

    /// <summary>Bind <paramref name="key"/> as a live localized + formatted string (named placeholders / ICU
    /// plural-select) — the <see cref="Bind(string)"/> counterpart over <see cref="Format(string, ValueTuple{string, object}[])"/>.
    /// Re-resolves on culture change with no re-render; the <paramref name="args"/> are captured by the thunk.</summary>
    public static Prop<string> BindF(string key, params (string Name, object Value)[] args)
        => Prop.Of(() => Format(key, args));

    /// <summary>True iff <paramref name="key"/> resolves in the current chain (without producing the visible-missing
    /// form). Useful for conditional UI / tests.</summary>
    public static bool Has(string key)
    {
        Dictionary<string, string>[] chain;
        lock (Gate) chain = _chain;
        Lookup(chain, key, out bool found);
        return found;
    }

    // ── Internals ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Walk the precomputed chain in order, returning the first table that has the key. The chain is
    /// active-culture-first, so resolution is per-KEY (a key missing from fr-FR but present in fr falls through to fr),
    /// not per-file.</summary>
    private static string Lookup(Dictionary<string, string>[] chain, string key, out bool found)
    {
        for (int i = 0; i < chain.Length; i++)
            if (chain[i].TryGetValue(key, out var v)) { found = true; return v; }
        found = false;
        return key;
    }

    /// <summary>Recompute the ordered resolution chain: active culture → its parent (strip the region: <c>fr-FR → fr</c>)
    /// → default culture → default's parent. Duplicates and absent tables are dropped. Called under <see cref="Gate"/>.</summary>
    private static void RebuildChain()
    {
        var order = new List<Dictionary<string, string>>(4);
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        void Add(string? culture)
        {
            if (string.IsNullOrEmpty(culture) || !seen.Add(culture)) return;
            if (Tables.TryGetValue(culture, out var t)) order.Add(t);
        }

        Add(_current);
        Add(Parent(_current));
        Add(_default);
        Add(Parent(_default));
        // The neutral floor is the terminal link: every real culture table above wins per-key, but a key missing from
        // all of them (or when none are loaded) still resolves to its built-in neutral string.
        if (_neutral.Count > 0) order.Add(_neutral);
        _chain = order.ToArray();
    }

    /// <summary>The parent culture (region stripped): <c>"fr-FR" → "fr"</c>; a bare language has no parent (null).</summary>
    private static string? Parent(string culture)
    {
        int dash = culture.IndexOf('-');
        return dash > 0 ? culture.Substring(0, dash) : null;
    }

    // Called under Gate. A full culture may use a loaded neutral parent (nl-NL -> nl). A neutral request may use a
    // single loaded regional child (ko -> ko-KR), but never guesses when multiple regional variants are installed.
    private static string? ResolveLoadedCulture(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return null;
        string requested = Normalize(culture);
        if (Tables.ContainsKey(requested)) return requested;
        if (Parent(requested) is { } parent && Tables.ContainsKey(parent)) return requested;
        if (requested.IndexOf('-') >= 0) return null;

        string? match = null;
        foreach (string loaded in Tables.Keys)
        {
            if (!string.Equals(Parent(loaded), requested, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (match is not null) return null;
            match = loaded;
        }
        return match;
    }

    /// <summary>Normalize a culture name: trim, collapse <c>_</c>→<c>-</c> (some OSes report <c>fr_FR</c>). Case is
    /// preserved for display but the table dictionary is case-insensitive.</summary>
    private static string Normalize(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return "en-US";
        return culture.Trim().Replace('_', '-');
    }

    private static void BumpEpoch() => CultureEpoch.Value = CultureEpoch.Peek() + 1;
}

/// <summary>Short alias for <see cref="Localization"/> — call sites read <c>Loc.Get("app.title")</c> /
/// <c>Loc.Format("player.added", ("name", track))</c>.</summary>
public static class Loc
{
    /// <inheritdoc cref="Localization.Get"/>
    public static string Get(string key) => Localization.Get(key);
    /// <inheritdoc cref="Localization.Format(string, ValueTuple{string, object}[])"/>
    public static string Format(string key, params (string Name, object Value)[] args) => Localization.Format(key, args);
    /// <inheritdoc cref="Localization.Bind(string)"/>
    public static FluentGpu.Signals.Prop<string> Bind(string key) => Localization.Bind(key);
    /// <inheritdoc cref="Localization.BindF(string, ValueTuple{string, object}[])"/>
    public static FluentGpu.Signals.Prop<string> BindF(string key, params (string Name, object Value)[] args) => Localization.BindF(key, args);
    /// <inheritdoc cref="Localization.CurrentCulture"/>
    public static string CurrentCulture => Localization.CurrentCulture;
    /// <inheritdoc cref="Localization.SetCulture"/>
    public static void SetCulture(string culture) => Localization.SetCulture(culture);
}
