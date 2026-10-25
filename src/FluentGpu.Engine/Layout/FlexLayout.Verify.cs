using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Layout;

/// <summary>
/// Operation ultra-fast GPU engine, P4 — the DEBUG-only correctness oracles for incremental layout. Two of them:
/// <list type="number">
/// <item><b><c>--fg layout-verify</c> parity oracle</b> — after a real solve, re-solve the SAME root from scratch with
/// every incremental short-circuit disabled (the cross-pass Measure ring, the Arrange early-out,
/// <see cref="TryResolveSizeStable"/>), compare every node's <c>Bounds</c>, then RESTORE the original rects. A
/// divergence is the signature of a missing dirty mark — the class of bug where the screen keeps last frame's geometry
/// forever and nothing throws.</item>
/// <item><b>the unmarked-LayoutInput tripwire</b> — every node the Arrange early-out SKIPS is re-hashed against the
/// signature recorded at its last real arrange (its whole subtree, since the early-out is a claim about the subtree,
/// not just the node). A mismatch means someone wrote a layout-affecting field without
/// <c>SceneStore.Mark(…, NodeFlags.LayoutDirty)</c>. Always counted in DEBUG
/// (<see cref="DiagUnmarkedLayoutWrites"/>, asserted by <c>gate.layout.dirty-mark-tripwire</c>); logged per-node only
/// under <c>--fg layout-verify</c>.</item>
/// </list>
/// <para><b>Diagnostics only — never a behaviour switch.</b> Both are compiled out of Release entirely. The oracle
/// runs under a <c>_verifying</c> latch that suppresses every side effect a second solve would otherwise repeat
/// (OnBoundsChanged delivery, the <c>_arranged</c>/ArrangedValid columns, viewport <c>SetFrame</c> posts, scroll-bind
/// baking, realize/paint marks) and restores the diag counters, so an app with the variable set produces byte-identical
/// frames to one without it — only slower, plus stderr output when something is actually wrong.</para>
/// <para>Scope, stated honestly: the text measure cache is deliberately left LIVE across the re-solve (it is a pure
/// function of (text, style, maxWidth), so it cannot manufacture a layout divergence, and re-shaping every run twice
/// per frame would make the oracle unusable on a real page). The oracle targets the incremental machinery — the ring,
/// the early-out, the subtree-dirty propagation — which is what P4 introduced.</para>
/// </summary>
public sealed partial class FlexLayout
{
    // ── the verification latch ────────────────────────────────────────────────────────────────────────────────────
    // In DEBUG a plain field: the guarded sites are cold (viewport arrange, bounds-changed delivery, the early-out), so
    // a predictable never-taken branch there is cheaper than duplicating the whole solver. In Release it is a `const
    // false`, so every `if (!_verifying)` guard folds away and the oracle leaves no trace in the shipping solver at all.
#if DEBUG || FLUENTGPU_DIAG
    private bool _verifyingField;
    private bool Verifying => _verifyingField;
#else
    // A PROPERTY, not a const: a const would make every `if (Verifying) return;` guard body unreachable (CS0162, which
    // TreatWarningsAsErrors turns into a Release-only build break). The JIT folds this to the same nothing.
    private static bool Verifying => false;
#endif

    /// <summary>Nodes whose layout-affecting inputs changed while the Arrange early-out believed their subtree clean —
    /// i.e. a <c>LayoutInput</c> writer that skipped <c>Mark(LayoutDirty)</c>. Always 0 in Release (compiled out) and,
    /// in a correct DEBUG build, always 0 too. Per-frame; reset by <see cref="ResetFrameDiagCounters"/>.</summary>
    public int DiagUnmarkedLayoutWrites => _dUnmarkedLayoutWrites;
    private int _dUnmarkedLayoutWrites;

#if DEBUG || FLUENTGPU_DIAG
    /// <summary>Whether the DEBUG oracles are compiled in at all (false in Release — the whole class of check is
    /// <c>[Conditional]</c>-style erased, per the repo's "production safety == CI coverage" rule).</summary>
    public const bool VerifyCompiledIn = true;

    private static bool s_layoutVerify => FluentGpu.Hosting.EngineSwitches.LayoutVerify;   // `--fg layout-verify`

    // The layout-input signature at each node's last REAL arrange (SetArrangedBounds). Superset of LayoutSig: it also
    // folds in the text inputs (TextStyle + the run itself), which LayoutSig deliberately omits because no ANCESTOR
    // reads them — the tripwire, unlike the ring, cares about this node's own measured size too.
    private ulong[] _verifySig = [];
    private uint[] _verifySigGen = [];   // the generation the signature was recorded for: a recycled index carries a stale one
    // Parity-oracle scratch: the rects the real (incremental) solve produced, restored verbatim after the compare.
    private RectF[] _verifySaved = [];
    private NodeHandle[] _verifyNodes = [];
    private int _verifyNodeCount;
    private NodeHandle[] _verifyWalk = new NodeHandle[64];

    /// <summary>Record the signature the Arrange early-out will later be checked against. Called from
    /// <c>SetArrangedBounds</c> — i.e. exactly when a node is genuinely (re)placed.</summary>
    private void NoteVerifySig(NodeHandle node)
    {
        uint i = node.Raw.Index;
        if (_verifySig.Length <= (int)i)
        {
            int n = System.Math.Max((int)i + 1, System.Math.Max(16, _verifySig.Length * 2));
            System.Array.Resize(ref _verifySig, n); System.Array.Resize(ref _verifySigGen, n);
        }
        _verifySig[i] = VerifySig(node);
        _verifySigGen[i] = node.Raw.Gen;
        if (s_layoutVerify) NoteVerifyParts(node);
    }

    // --fg layout-verify only: the signature's parts at the last real arrange, so a tripwire hit can say WHAT changed
    // (which flag bits, the LayoutInput fields, the text inputs) instead of only that something did (PERF r3 MODEL, C1 writers).
    private uint[] _verifyFlags = [];
    private ulong[] _verifyLi = [], _verifyTxt = [];
    private uint[] _verifyGen = [];
    private int[] _verifyPass = [];
    private LayoutInput[] _verifyLiCopy = [];
    private (uint Kind, uint Text, NodeHandle Parent)[] _verifyPaint = [];
    private void NoteVerifyParts(NodeHandle node)
    {
        int i = (int)node.Raw.Index;
        if (_verifyFlags.Length <= i)
        {
            int n = System.Math.Max(i + 1, System.Math.Max(16, _verifyFlags.Length * 2));
            System.Array.Resize(ref _verifyFlags, n); System.Array.Resize(ref _verifyLi, n); System.Array.Resize(ref _verifyTxt, n);
            System.Array.Resize(ref _verifyGen, n); System.Array.Resize(ref _verifyPass, n);
            System.Array.Resize(ref _verifyLiCopy, n); System.Array.Resize(ref _verifyPaint, n);
        }
        _verifyLiCopy[i] = _scene.Layout(node);
        _verifyPaint[i] = ((uint)_scene.Paint(node).VisualKind, (uint)_scene.Paint(node).Text.Value, _scene.Parent(node));
        _verifyGen[i] = node.Raw.Gen;
        _verifyPass[i] = _pass;
        _verifyFlags[i] = (uint)_scene.Flags(node);
        _verifyLi[i] = LayoutSigNoFlags(node);
        _verifyTxt[i] = VerifyTextSig(node);
    }

    // --fg layout-verify: the last ring answer each node took from an interval (an offer other than its slot's), by pass, so a
    // divergence names the interval answers above it in the pass that diverged.
    private (int Pass, float Offer, float W, float H, float Lo, float Hi)[] _ringIv = new (int, float, float, float, float, float)[256];

    private void NoteRingInterval(NodeHandle n, float offer, Size2 size, float lo, float hi)
    {
        int i = (int)n.Raw.Index;
        if (i >= _ringIv.Length) System.Array.Resize(ref _ringIv, System.Math.Max(_ringIv.Length * 2, i + 1));
        _ringIv[i] = (_pass, offer, size.Width, size.Height, lo, hi);
    }

    private string DescribeChain(NodeHandle n)
    {
        var sb = new System.Text.StringBuilder(" chain:");
        int k = 0;
        for (var a = n; !a.IsNull && k < 8; a = _scene.Parent(a), k++)
        {
            sb.Append(" n#").Append(a.Raw.Index).Append("[t").Append(_scene.ElementTypeId(a));
            if (_scene.DebugKeyOf(a) is { } key) sb.Append(" '").Append(key).Append("'");
            if (!_scene.IsArrangedValid(a)) sb.Append(" !arr");
            if (!_scene.IsLayoutClean(a)) sb.Append(" dirty");
            if ((_scene.Flags(a) & NodeFlags.Parked) != 0) sb.Append(" parked");
            if ((_scene.Flags(a) & NodeFlags.Exiting) != 0) sb.Append(" exiting");
            if (_scene.HasScroll(a)) sb.Append(" scroll");
            if ((_scene.Flags(a) & NodeFlags.ZStack) != 0) sb.Append(" z");
            sb.Append(" b=").Append((int)_scene.Bounds(a).W).Append('x').Append((int)_scene.Bounds(a).H);
            int ai = (int)a.Raw.Index;
            if (ai < _ringIv.Length && _ringIv[ai].Pass != 0)
                sb.Append(System.FormattableString.Invariant($" RINGIV {_pass - _ringIv[ai].Pass} passes ago offer={_ringIv[ai].Offer} size={_ringIv[ai].W}x{_ringIv[ai].H} iv=[{_ringIv[ai].Lo},{_ringIv[ai].Hi}]"));
            sb.Append(']');
        }
        return sb.ToString();
    }

    private string DescribeVerifyChange(NodeHandle n)
    {
        int i = (int)n.Raw.Index;
        if (i >= _verifyFlags.Length) return "(no parts recorded)";
        var sb = new System.Text.StringBuilder();
        uint fx = (_verifyFlags[i] ^ (uint)_scene.Flags(n)) & (uint)LayoutInputFlags;
        if (fx != 0) sb.Append(" flags^=").Append(((NodeFlags)fx).ToString());
        if (_verifyLi[i] != LayoutSigNoFlags(n)) sb.Append(" layoutInput");
        if (_verifyTxt[i] != VerifyTextSig(n)) sb.Append(" text");
        {
            bool hidden = false;
            for (var a = _scene.Parent(n); !a.IsNull; a = _scene.Parent(a)) if ((_scene.Flags(a) & NodeFlags.Visible) == 0) { hidden = true; break; }
            sb.Append(hidden ? " UNDER-COLLAPSED" : " inflow");
            if ((_scene.Flags(n) & NodeFlags.Parked) != 0) sb.Append(" parked");
        }
        if (_verifyGen[i] != n.Raw.Gen)
        {
            sb.Append(" RECYCLED");
            if (!_scene.IsArrangedValid(n)) sb.Append(" neverArranged");
        }
        else
        {
            // field-level: which LayoutInput fields and paint text inputs moved (diag only; reflection is fine here)
            object a = _verifyLiCopy[i], b = _scene.Layout(n);
            foreach (var f in typeof(LayoutInput).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                if (!Equals(f.GetValue(a), f.GetValue(b))) sb.Append(" li.").Append(f.Name).Append('=').Append(f.GetValue(a)).Append("->").Append(f.GetValue(b));
            sb.Append(" flags=").Append(_scene.Flags(n)).Append(" type=").Append(_scene.ElementTypeId(n));
            var p = _verifyPaint[i];
            if (p.Kind != (uint)_scene.Paint(n).VisualKind) sb.Append(" visualKind ").Append(p.Kind).Append("->").Append((uint)_scene.Paint(n).VisualKind);
            if (p.Text != (uint)_scene.Paint(n).Text.Value)
            {
                string Str(uint id) { try { return _scene.Strings?.Resolve(new StringId((int)id)) ?? "?"; } catch { return "?"; } }
                sb.Append(" paintText '").Append(Str(p.Text)).Append("'->'").Append(Str((uint)_scene.Paint(n).Text.Value)).Append("'");

            }
            if (p.Parent != _scene.Parent(n)) sb.Append(" REPARENTED");
        }
        sb.Append(" passesAgo=").Append(_pass - _verifyPass[i]);
        sb.Append(" kind=").Append(_scene.Paint(n).VisualKind);
        return sb.ToString();
    }

    private ulong LayoutSigNoFlags(NodeHandle node)
    {
        ref LayoutInput li = ref _scene.Layout(node);
        ulong h = 14695981039346656037UL;
        MixU(ref h, _scene.IsMirrorCollapsed(node) ? 1u : 0u);
        MixU(ref h, li.Direction);
        MixF(ref h, li.Gap);
        MixE(ref h, in li.Padding);
        MixE(ref h, in li.Margin);
        MixF(ref h, li.Width); MixF(ref h, li.Height); MixF(ref h, li.AspectRatio);
        MixF(ref h, li.MinW); MixF(ref h, li.MinH); MixF(ref h, li.MaxW); MixF(ref h, li.MaxH);
        MixF(ref h, li.FlexGrow); MixF(ref h, li.FlexShrink); MixF(ref h, li.FlexBasis);
        MixU(ref h, (uint)li.AlignSelf); MixU(ref h, (uint)li.Justify); MixU(ref h, (uint)li.AlignItems);
        MixU(ref h, li.Wrap ? 1u : 0u);
        return h;
    }

    /// <summary>The Arrange early-out's tripwire: it just claimed this whole subtree is unchanged. Prove it — every
    /// descendant's layout-affecting inputs must still hash to what its last real arrange recorded.</summary>
    private void VerifyEarlyOutSubtree(NodeHandle node)
    {
        if (Verifying) return;   // the oracle's own re-solve never takes the early-out, but be explicit
        int depth = 0;
        _verifyWalk[depth++] = node;
        while (depth > 0)
        {
            var n = _verifyWalk[--depth];
            uint i = n.Raw.Index;
            // A collapsed node is flow-removed: neither it nor its subtree is measured or arranged, so their inputs are not part
            // of the result the early-out vouches for (a reveal marks the node dirty). And a recycled index's signature belongs
            // to the node that held it before: the new node was never arranged here (it is under a collapsed ancestor, or
            // collapsed itself), so there is nothing to compare (PERF r3 MODEL, C1 writers).
            if ((_scene.Flags(n) & NodeFlags.Visible) == 0) continue;
            if (i < (uint)_verifySig.Length && _verifySig[i] != 0 && _verifySigGen[i] == n.Raw.Gen)
            {
                ulong now = VerifySig(n);
                if (now != _verifySig[i])
                {
                    _dUnmarkedLayoutWrites++;
                    if (s_layoutVerify)
                    {
                        System.Console.Error.WriteLine(
                            $"[--fg layout-verify] unmarked LayoutInput write: n#{i} changed under a clean Arrange early-out " +
                            $"rooted at n#{node.Raw.Index} (sig {_verifySig[i]:x} -> {now:x}). A writer skipped Mark(LayoutDirty).{DescribeVerifyChange(n)}");
                        NoteVerifyParts(n);
                    }
                    _verifySig[i] = now;   // report each divergence ONCE — a permanent mismatch must not spam every frame
                }
            }
            for (var c = _scene.FirstChild(n); !c.IsNull; c = _scene.NextSibling(c))
            {
                if (depth == _verifyWalk.Length) System.Array.Resize(ref _verifyWalk, _verifyWalk.Length * 2);
                _verifyWalk[depth++] = c;
            }
        }
    }

    /// <summary>The node flags a layout solve READS (presence, stacking, slot placement during a size animation, the boundary
    /// choice, a viewport). The dirty bits (LayoutDirty/PaintDirty/TransformDirty: set while a node is being re-laid or
    /// repainted, cleared at the frame's end) and the interaction/paint state bits are not layout inputs: hashing them made the
    /// tripwire report a node arranged while its LayoutDirty bit was still set as "changed" once the bit cleared (PERF r3 MODEL).</summary>
    internal const NodeFlags LayoutInputFlags = NodeFlags.Visible | NodeFlags.ZStack | NodeFlags.Relayouting;

    /// <summary>The node's layout INPUTS (LayoutSig's fields with only <see cref="LayoutInputFlags"/>) plus its own text inputs —
    /// the tripwire's "did anything layout-affecting change".</summary>
    private ulong VerifySig(NodeHandle node) => LayoutSigNoFlags(node) ^ (ulong)(uint)(_scene.Flags(node) & LayoutInputFlags) * 0x9E3779B97F4A7C15UL ^ VerifyTextSig(node);

    private ulong VerifyTextSig(NodeHandle node)
    {
        ulong h = 14695981039346656037UL;
        ref LayoutInput li = ref _scene.Layout(node);
        ref NodePaint p = ref _scene.Paint(node);
        MixU(ref h, p.VisualKind == VisualKind.Text ? 1u : 0u);   // the solver only asks "is it a text run"
        MixU(ref h, (uint)p.Text.Value);
        MixU(ref h, (uint)li.TextStyle.FontFamily.Value);
        MixF(ref h, li.TextStyle.SizeDip);
        MixU(ref h, li.TextStyle.Weight);
        MixU(ref h, (uint)li.TextStyle.Wrap);
        MixU(ref h, (uint)li.TextStyle.Trim);
        MixU(ref h, (uint)li.TextStyle.MaxLines);
        MixF(ref h, li.TextStyle.CharSpacing);
        MixF(ref h, li.TextStyle.LineHeight);
        MixU(ref h, (uint)li.TextStyle.SpanRunId);
        MixF(ref h, li.TextStyle.MinSizeDip);
        MixU(ref h, (uint)li.TextStyle.Fit);
        MixU(ref h, (uint)li.TextStyle.Case);
        MixU(ref h, (uint)li.JustifySelf);
        MixU(ref h, li.MeasureUnboundedWidth ? 1u : 0u);
        return h;
    }

    /// <summary>Force ONE parity check right now, regardless of <c>--fg layout-verify</c> — the oracle's own test hook
    /// (<c>gate.layout.parity-oracle</c>): it proves the machinery re-solves, compares and RESTORES, on a scene whose
    /// answer is already known to be right. Returns the number of node rects that diverged from a from-scratch solve
    /// (0 = clean), or -1 in a Release build where the oracle is compiled out.</summary>
    public int VerifyLayoutParityNow(NodeHandle root, Size2 window)
    {
        if (root.IsNull || !_scene.IsLive(root)) return 0;
        ref LayoutInput li = ref _scene.Layout(root);
        float w = float.IsNaN(li.Width) ? window.Width : li.Width;
        float h = float.IsNaN(li.Height) ? window.Height : li.Height;
        return VerifyParityCore(root, window.Width, 0f, 0f, w, h, "VerifyLayoutParityNow", log: false);
    }

    /// <summary>The <c>--fg layout-verify</c> parity oracle. <paramref name="availW"/>/<paramref name="x"/>/… replay
    /// exactly the arguments the real solve used.</summary>
    private void VerifyParity(NodeHandle root, float availW, float x, float y, float w, float h, string site)
    {
        if (!s_layoutVerify || Verifying || root.IsNull || !_scene.IsLive(root)) return;
        VerifyParityCore(root, availW, x, y, w, h, site, log: true);
    }

    private int VerifyParityCore(NodeHandle root, float availW, float x, float y, float w, float h, string site, bool log)
    {
        if (Verifying) return 0;

        CollectVerifyNodes(root);
        for (int i = 0; i < _verifyNodeCount; i++) _verifySaved[i] = _scene.Bounds(_verifyNodes[i]);

        int m0 = _dMeasure, a0 = _dArrange, th0 = _dTextHit, tm0 = _dTextMiss, mh0 = _dMeasureMemoHit, ov0 = _dOverflow;
        _verifyingField = true;
        try
        {
            BeginMeasurePass();
            Measure(root, availW);
            Arrange(root, x, y, w, h);
        }
        finally
        {
            _verifyingField = false;
            _dMeasure = m0; _dArrange = a0; _dTextHit = th0; _dTextMiss = tm0; _dMeasureMemoHit = mh0; _dOverflow = ov0;
        }

        int mismatches = 0;
        for (int i = 0; i < _verifyNodeCount; i++)
        {
            var node = _verifyNodes[i];
            RectF fresh = _scene.Bounds(node), had = _verifySaved[i];
            if (Near(fresh.X, had.X) && Near(fresh.Y, had.Y) && Near(fresh.W, had.W) && Near(fresh.H, had.H)) continue;
            if (log && mismatches < 16)
                System.Console.Error.WriteLine(
                    $"[--fg layout-verify] {site}: n#{node.Raw.Index} incremental={had} fromScratch={fresh}{DescribeChain(node)}");
            mismatches++;
        }
        // Restore unconditionally: the oracle observes, it never decides. Even a genuine divergence leaves the frame
        // byte-identical to a run without the variable set — the report is the whole output.
        for (int i = 0; i < _verifyNodeCount; i++) _scene.Bounds(_verifyNodes[i]) = _verifySaved[i];

        if (log && mismatches > 0)
            System.Console.Error.WriteLine(
                $"[--fg layout-verify] {site}: {mismatches}/{_verifyNodeCount} node rects diverge from a from-scratch solve " +
                "— an incremental short-circuit trusted a subtree that had actually changed (a missing Mark(LayoutDirty)).");
        return mismatches;

        static bool Near(float a, float b) => a == b || System.MathF.Abs(a - b) <= 0.01f
            || (float.IsNaN(a) && float.IsNaN(b));
    }

    private void CollectVerifyNodes(NodeHandle root)
    {
        _verifyNodeCount = 0;
        int depth = 0;
        _verifyWalk[depth++] = root;
        while (depth > 0)
        {
            var n = _verifyWalk[--depth];
            if (_verifyNodes.Length == _verifyNodeCount)
            {
                System.Array.Resize(ref _verifyNodes, System.Math.Max(64, _verifyNodes.Length * 2));
                System.Array.Resize(ref _verifySaved, _verifyNodes.Length);
            }
            _verifyNodes[_verifyNodeCount++] = n;
            for (var c = _scene.FirstChild(n); !c.IsNull; c = _scene.NextSibling(c))
            {
                if (depth == _verifyWalk.Length) System.Array.Resize(ref _verifyWalk, _verifyWalk.Length * 2);
                _verifyWalk[depth++] = c;
            }
        }
        if (_verifySaved.Length < _verifyNodes.Length) System.Array.Resize(ref _verifySaved, _verifyNodes.Length);
    }
#else
    /// <summary>Whether the DEBUG oracles are compiled in at all (false in Release).</summary>
    public const bool VerifyCompiledIn = false;

    /// <summary>Release build: the oracle is compiled out entirely — always -1 ("not available"), never a silent 0.</summary>
    public int VerifyLayoutParityNow(NodeHandle root, Size2 window) => -1;

    private void NoteVerifySig(NodeHandle node) { }
    private void VerifyEarlyOutSubtree(NodeHandle node) { }
    private void VerifyParity(NodeHandle root, float availW, float x, float y, float w, float h, string site) { }
#endif
}
